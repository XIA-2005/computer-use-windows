// web.cs - browser automation over the Chrome DevTools Protocol (compiled with cu.cs by cu.ps1)
//   * Dedicated browser instance per profile (state\web\edge|chrome; debug port 127.0.0.1:9462|9461):
//     never attaches to the user's daily windows; logins persist across runs
//   * One long-lived browser WebSocket per browser (flatten mode); page work runs on an attached target session
//   * DOM locate (text / selector / numbered element) + real CDP mouse/keyboard input; in-page waits; browser-side scaled shots
//   * The in-page helper lives in win\web-lib.js (injected per command, cached per document)
// C# 5 / .NET 4 csc compatible: no $"", no ?., no async/await, no nameof, no expression bodies.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CU
{
    public static class Web
    {
        class Conn
        {
            public ClientWebSocket ws;
            public string wsUrl = "";
            public string targetId = "";
            public string sessionId = "";
            public int nextId = 1;
            /** Environment.TickCount of the last successful session call; a fresh success skips the tab liveness probe. */
            public int lastOk = Stale();
        }

        static readonly Dictionary<string, Conn> _conns = new Dictionary<string, Conn>();
        /** a tick value far enough in the past that every freshness check fails (TickCount may be negative) */
        static int Stale() { return unchecked(Environment.TickCount - 1000000); }
        static readonly object _lk = new object();
        static readonly JavaScriptSerializer _ser = NewSer();

        static JavaScriptSerializer NewSer()
        {
            JavaScriptSerializer s = new JavaScriptSerializer();
            s.MaxJsonLength = int.MaxValue;
            s.RecursionLimit = 200;
            return s;
        }

        // ---------------------------------------------------------------- basics
        public static string Norm(string b)
        {
            b = (b ?? "").Trim().ToLowerInvariant();
            if (b == "") return "edge";
            if (b == "msedge" || b == "microsoftedge" || b == "edge") return "edge";
            if (b == "chrome" || b == "googlechrome" || b == "chromium") return "chrome";
            return "";
        }

        // Default profile retains the v6.1 ports. An explicit CU_SESSION
        // gets its own deterministic port/profile/target/frame, so separate
        // tasks cannot silently navigate one another's browser tabs.
        static string SessionSuffix()
        {
            string s = (Environment.GetEnvironmentVariable("CU_SESSION") ?? "").Trim();
            if (s == "") return "";
            using (SHA256 hash = SHA256.Create())
            {
                byte[] h = hash.ComputeHash(Encoding.UTF8.GetBytes(s));
                return BitConverter.ToString(h, 0, 8).Replace("-", "").ToLowerInvariant();
            }
        }
        public static int Port(string b)
        {
            string suffix = SessionSuffix();
            if (suffix == "") return b == "chrome" ? 9461 : 9462;
            using (SHA256 hash = SHA256.Create())
            {
                byte[] h = hash.ComputeHash(Encoding.UTF8.GetBytes(b + ":" + suffix));
                uint n = ((uint)h[0] << 24) | ((uint)h[1] << 16) | ((uint)h[2] << 8) | h[3];
                return 20000 + (int)(n % 30000); // collision => explicit ERR_PORT_CONFLICT, never attach
            }
        }

        static string StateDir()
        {
            string s = Environment.GetEnvironmentVariable("CU_STATE");
            if (!string.IsNullOrEmpty(s)) return s;
            string dll = typeof(Web).Assembly.Location;                       // ...\computer-use\win\bin\cu-xxxx.dll
            string win = Path.GetDirectoryName(Path.GetDirectoryName(dll));   // ...\computer-use\win
            return Path.GetFullPath(Path.Combine(win, "..", "state"));
        }

        static string WebDir() { return Path.Combine(StateDir(), "web"); }
        static string BrowserKey(string b) { string s = SessionSuffix(); return s == "" ? b : b + "-" + s; }
        static string ProfileDir(string b) { return Path.Combine(WebDir(), BrowserKey(b)); }
        static string LastWeb() { string s = SessionSuffix(); return Path.Combine(StateDir(), s == "" ? "last.web.json" : "last.web-" + s + ".json"); }
        static string TargetFile(string b) { return Path.Combine(WebDir(), BrowserKey(b) + ".target"); }

        static string LoadTarget(string b)
        {
            try { string f = TargetFile(b); if (File.Exists(f)) return File.ReadAllText(f).Trim(); }
            catch { }
            return "";
        }

        static void SaveTarget(string b, string id)
        {
            try { Directory.CreateDirectory(WebDir()); File.WriteAllText(TargetFile(b), id ?? ""); }
            catch { }
        }

        static string ExeOf(string b)
        {
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf8 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            if (string.IsNullOrEmpty(pf8)) pf8 = pf;
            string la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] cands;
            if (b == "chrome")
                cands = new string[] {
                    Path.Combine(pf, @"Google\Chrome\Application\chrome.exe"),
                    Path.Combine(pf8, @"Google\Chrome\Application\chrome.exe"),
                    Path.Combine(la, @"Google\Chrome\Application\chrome.exe") };
            else
                cands = new string[] {
                    Path.Combine(pf8, @"Microsoft\Edge\Application\msedge.exe"),
                    Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe"),
                    Path.Combine(la, @"Microsoft\Edge\Application\msedge.exe") };
            foreach (string c in cands) if (File.Exists(c)) return c;
            return "";
        }

        static string NormalizeUrl(string u)
        {
            if (u == null) return "";
            u = u.Trim();
            if (u == "") return u;
            if (Regex.IsMatch(u, @"^[a-zA-Z][a-zA-Z0-9+.\-]*:")) return u;   // has a scheme: http:, file:, about:, ...
            return "https://" + u;
        }

        static string ArgQ(string s) { return "\"" + s.Replace("\"", "\\\"") + "\""; }

        static string Trunc(string s, int n)
        {
            if (s == null) return "";
            return s.Length <= n ? s : s.Substring(0, n) + "...";
        }

        static string RootMsg(Exception e)
        {
            try { return e.GetBaseException().Message; } catch { return e.Message; }
        }

        static Dictionary<string, object> ParseD(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try { return _ser.DeserializeObject(json) as Dictionary<string, object>; }
            catch { return null; }
        }

        static double DN(Dictionary<string, object> d, string k)
        {
            object o;
            if (d == null || !d.TryGetValue(k, out o) || o == null) return 0;
            if (o is int) return (int)o;
            if (o is long) return (long)o;
            if (o is decimal) return (double)(decimal)o;
            if (o is double) return (double)o;
            if (o is float) return (float)o;
            double r;
            double.TryParse(Convert.ToString(o, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out r);
            return r;
        }

        static string SN(Dictionary<string, object> d, string k)
        {
            object o;
            if (d == null || !d.TryGetValue(k, out o) || o == null) return "";
            return Convert.ToString(o, CultureInfo.InvariantCulture);
        }

        static bool BN(object o) { return o is bool && (bool)o; }

        static string ExMsg(object od)
        {
            Dictionary<string, object> d = od as Dictionary<string, object>;
            if (d == null) return "unknown exception";
            object ex;
            if (d.TryGetValue("exception", out ex) && ex is Dictionary<string, object>)
            {
                Dictionary<string, object> e2 = (Dictionary<string, object>)ex;
                object desc;
                if (e2.TryGetValue("description", out desc) && desc != null) return Trunc(desc.ToString(), 500);
                if (e2.TryGetValue("value", out desc) && desc != null) return Trunc(Convert.ToString(desc), 500);
            }
            object t;
            if (d.TryGetValue("text", out t) && t != null) return t.ToString();
            return "evaluate exception";
        }

        // ---------------------------------------------------------------- HTTP (CDP REST)
        static string HttpGet(int port, string path, int ms)
        {
            try
            {
                HttpWebRequest r = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + path);
                r.Timeout = ms; r.Proxy = null; r.ReadWriteTimeout = ms;
                using (HttpWebResponse resp = (HttpWebResponse)r.GetResponse())
                using (Stream st = resp.GetResponseStream())
                using (StreamReader rd = new StreamReader(st, Encoding.UTF8))
                    return rd.ReadToEnd();
            }
            catch { return ""; }
        }

        // Verify the actual TCP LISTENING owner's PID, not merely that some
        // process responds with Chromium /json/version on our fixed port.
        // MIB_TCPROW_OWNER_PID is 24 bytes; IPv4 table class OWNER_PID_LISTENER.
        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint GetExtendedTcpTable(IntPtr table, ref int length, bool order, int family, int tcpTableClass, uint reserved);

        static int ListenerPid(int port)
        {
            int bytes = 0;
            uint first = GetExtendedTcpTable(IntPtr.Zero, ref bytes, false, 2, 3, 0);
            if ((first != 122 && first != 0) || bytes < 4 || bytes > 16 * 1024 * 1024) return -1;
            IntPtr buf = Marshal.AllocHGlobal(bytes);
            try
            {
                if (GetExtendedTcpTable(buf, ref bytes, false, 2, 3, 0) != 0) return -1;
                int count = Marshal.ReadInt32(buf);
                if (count < 0 || count > (bytes - 4) / 24) return -1;
                for (int i = 0; i < count; i++)
                {
                    int offset = 4 + i * 24;
                    if (Marshal.ReadInt32(buf, offset) != 2) continue; // LISTEN
                    int raw = Marshal.ReadInt32(buf, offset + 8);
                    int localPort = ((raw & 255) << 8) | ((raw >> 8) & 255);
                    if (localPort != port) continue;
                    uint addr = unchecked((uint)Marshal.ReadInt32(buf, offset + 4));
                    if (addr == 0x0100007f || addr == 0) return Marshal.ReadInt32(buf, offset + 20);
                }
                return 0;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        static bool OwnsListener(string b, int pid)
        {
            if (pid <= 0) return false;
            try
            {
                string expectedExe = Path.GetFullPath(ExeOf(b));
                string expectedProfile = Path.GetFullPath(ProfileDir(b)).TrimEnd('\\');
                using (ManagementObjectSearcher q = new ManagementObjectSearcher(
                    "SELECT Name,ExecutablePath,CommandLine FROM Win32_Process WHERE ProcessId=" +
                    pid.ToString(CultureInfo.InvariantCulture)))
                using (ManagementObjectCollection procs = q.Get())
                {
                    foreach (ManagementObject proc in procs)
                    {
                        string cmd = Convert.ToString(proc["CommandLine"]) ?? "";
                        string exe = Convert.ToString(proc["ExecutablePath"]) ?? "";
                        if (!string.Equals(Path.GetFullPath(exe), expectedExe, StringComparison.OrdinalIgnoreCase)) continue;
                        Match mp = Regex.Match(cmd, @"(?:^|\s)--remote-debugging-port=(\d+)(?=\s|$)", RegexOptions.IgnoreCase);
                        if (!mp.Success || mp.Groups[1].Value != Port(b).ToString(CultureInfo.InvariantCulture)) continue;
                        Match mf = Regex.Match(cmd, "(?:^|\\s)--user-data-dir=(?:\\\"([^\\\"]+)\\\"|(\\S+))", RegexOptions.IgnoreCase);
                        if (!mf.Success) continue;
                        string actualProfile = mf.Groups[1].Success ? mf.Groups[1].Value : mf.Groups[2].Value;
                        if (string.Equals(Path.GetFullPath(actualProfile).TrimEnd('\\'), expectedProfile,
                                          StringComparison.OrdinalIgnoreCase)) return true;
                    }
                }
            }
            catch { } // fail closed on missing process info / inaccessible WMI
            return false;
        }

        static string ListenerError(string b)
        {
            int pid = ListenerPid(Port(b));
            if (pid < 0) return J.Err("ERR_BROWSER_IDENTITY", "cannot inspect TCP listener ownership; refusing to attach");
            if (pid > 0 && !OwnsListener(b, pid))
                return J.Err("ERR_PORT_CONFLICT", "CDP port " + Port(b) + " belongs to a different browser/process (pid " + pid + "); refusing to attach");
            return null;
        }

        public static bool Running(string b)
        {
            b = Norm(b);
            if (b == "") return false;
            // Fast path only for our previously verified live WebSocket.
            lock (_lk)
            {
                Conn c0;
                if (_conns.TryGetValue(b, out c0) && c0.ws != null && c0.ws.State == WebSocketState.Open &&
                    unchecked(Environment.TickCount - c0.lastOk) < 15000) return true;
            }
            int pid = ListenerPid(Port(b));
            if (pid <= 0 || !OwnsListener(b, pid)) return false;
            string v = HttpGet(Port(b), "/json/version", 800);
            return v.Contains("webSocketDebuggerUrl");
        }

        // pages from REST /json/list (type=page, no devtools://)
        static List<Dictionary<string, object>> ListPages(string b)
        {
            string j = HttpGet(Port(b), "/json/list", 1500);
            if (j == "") return null;
            List<Dictionary<string, object>> all;
            try { all = _ser.Deserialize<List<Dictionary<string, object>>>(j); }
            catch { return null; }
            List<Dictionary<string, object>> pages = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> p in all)
            {
                string ty = SN(p, "type");
                string url = SN(p, "url");
                if (ty != "page") continue;
                if (url.StartsWith("devtools://")) continue;
                pages.Add(p);
            }
            return pages;
        }

        // ---------------------------------------------------------------- WebSocket
        static Conn Get(string b, out string err)
        {
            err = null;
            Conn c;
            if (_conns.TryGetValue(b, out c) && c.ws != null && c.ws.State == WebSocketState.Open)
                return c;
            if (c != null) { try { if (c.ws != null) c.ws.Dispose(); } catch { } _conns.Remove(b); }
            string identityError = ListenerError(b);
            if (identityError != null) { err = identityError; return null; }
            if (!Running(b)) { err = J.Err("ERR_NOT_RUNNING", b + " is not running (web start)"); return null; }
            string ver = HttpGet(Port(b), "/json/version", 1500);
            Match m = Regex.Match(ver, "\"webSocketDebuggerUrl\"\\s*:\\s*\"([^\"]+)\"");
            if (!m.Success) { err = J.Err("ERR_CDP", "cannot read webSocketDebuggerUrl"); return null; }
            string wsUrl = m.Groups[1].Value.Replace("\\/", "/");
            ClientWebSocket ws = new ClientWebSocket();
            try { ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None).Wait(4000); }
            catch (Exception e) { try { ws.Dispose(); } catch { } err = J.Err("ERR_WS", "ws connect failed: " + RootMsg(e)); return null; }
            c = new Conn();
            c.ws = ws; c.wsUrl = wsUrl;
            _conns[b] = c;
            return c;
        }

        // send payload; wait for the response with our id (events / other ids are discarded)
        static string CallOnce(Conn c, int id, string payload, int timeoutMs, out string raw)
        {
            raw = null;
            try
            {
                Stopwatch sw = Stopwatch.StartNew();
                byte[] outb = Encoding.UTF8.GetBytes(payload);
                Task t1 = c.ws.SendAsync(new ArraySegment<byte>(outb), WebSocketMessageType.Text, true, CancellationToken.None);
                if (!t1.Wait(timeoutMs)) return "ERR_TIMEOUT";
                byte[] buf = new byte[1 << 16];
                StringBuilder sb = new StringBuilder(4096);
                while (true)
                {
                    int left = timeoutMs - (int)sw.ElapsedMilliseconds;
                    if (left < 50) return "ERR_TIMEOUT";
                    Task<WebSocketReceiveResult> t2 = c.ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                    if (!t2.Wait(left)) return "ERR_TIMEOUT";
                    WebSocketReceiveResult res = t2.Result;
                    if (res.MessageType == WebSocketMessageType.Close) return "ERR_CLOSED";
                    sb.Append(Encoding.UTF8.GetString(buf, 0, res.Count));
                    if (!res.EndOfMessage) continue;
                    string msg = sb.ToString();
                    Match mid = Regex.Match(msg, "\"id\"\\s*:\\s*(\\d+)");
                    if (mid.Success && int.Parse(mid.Groups[1].Value, CultureInfo.InvariantCulture) == id)
                    { raw = msg; return null; }
                    sb.Length = 0;   // event or stale response
                }
            }
            catch (AggregateException ae) { return "WS:" + RootMsg(ae); }
            catch (Exception e) { return "WS:" + e.Message; }
        }

        // core: one CDP call with retries (session attach / reconnect). returns null on success.
        static string CoreCall(string b, bool useSession, string method, string paramsJson, int timeoutMs, out string resultJson)
        {
            return CoreCallEx(b, useSession, method, paramsJson, timeoutMs, true, out resultJson);
        }

        // Input.* events must never be re-sent after a timeout (a second mousePressed is a second click): the event is
        // already queued in the browser, only its ack is late (navigation started by the click, busy main thread).
        static bool _lateAck = false;
        static string InputCall(string b, string method, string paramsJson)
        {
            string res;
            string e = CoreCallEx(b, true, method, paramsJson, 1500, false, out res);
            if (e != null && e.Contains("ERR_TIMEOUT")) { _lateAck = true; return null; }
            return e;
        }

        // Runtime.evaluate must never be re-sent after a timeout either: the snippet may already have run
        // (a DOM click, a form submit, a scroll) and a second execution would double the side effect.
        // Session/target re-attach and WebSocket reconnect still retry; only the timeout resend is off.
        static string EvalCall(string b, string paramsJson, int timeoutMs, out string resultJson)
        {
            return CoreCallEx(b, true, "Runtime.evaluate", paramsJson, timeoutMs, false, out resultJson);
        }

        static string CoreCallEx(string b, bool useSession, string method, string paramsJson, int timeoutMs, bool retryOnTimeout, out string resultJson)
        {
            resultJson = null;
            lock (_lk)
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    string gerr;
                    Conn c = Get(b, out gerr);
                    if (c == null) return gerr;
                    if (useSession)
                    {
                        string se = EnsureSession(c, b);
                        if (se != null) { if (attempt < 2) continue; return se; }
                    }
                    int id = c.nextId++;
                    StringBuilder payload = new StringBuilder(256);
                    payload.Append("{\"id\":").Append(id);
                    if (useSession && c.sessionId != null && c.sessionId != "")
                        payload.Append(",\"sessionId\":").Append(J.Q(c.sessionId));
                    payload.Append(",\"method\":").Append(J.Q(method));
                    if (!string.IsNullOrEmpty(paramsJson)) payload.Append(",\"params\":").Append(paramsJson);
                    payload.Append("}");
                    string raw;
                    string e = CallOnce(c, id, payload.ToString(), timeoutMs, out raw);
                    if (e == null)
                    {
                        Dictionary<string, object> rd = ParseD(raw);
                        if (rd == null) return J.Err("ERR_CDP", "unparseable CDP response");
                        if (rd.ContainsKey("error"))
                        {
                            string em = "cdp error";
                            Dictionary<string, object> ed = rd["error"] as Dictionary<string, object>;
                            if (ed != null) em = SN(ed, "message");
                            else em = Convert.ToString(rd["error"]);
                            if (em.IndexOf("Session", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                em.IndexOf("Target", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                em.IndexOf("context", StringComparison.OrdinalIgnoreCase) >= 0)
                            { c.sessionId = ""; c.lastOk = Stale(); if (attempt < 2) continue; }
                            return J.Err("ERR_CDP", em);
                        }
                        object res;
                        rd.TryGetValue("result", out res);
                        resultJson = res == null ? "{}" : _ser.Serialize(res);
                        if (useSession) c.lastOk = Environment.TickCount;
                        return null;
                    }
                    if (e == "ERR_CLOSED" || e.StartsWith("WS:"))
                    {
                        try { c.ws.Dispose(); } catch { }
                        lock (_lk) { if (_conns.ContainsKey(b)) _conns.Remove(b); }
                        if (attempt < 2) { Thread.Sleep(60); continue; }
                        return J.Err("ERR_WS", e);
                    }
                    if (e == "ERR_TIMEOUT")
                    {
                        if (retryOnTimeout && attempt < 1) continue;
                        return J.Err("ERR_TIMEOUT", "no CDP response within " + timeoutMs + "ms");
                    }
                    return J.Err("ERR_CDP", e);
                }
                return J.Err("ERR_CDP", "retries exhausted");
            }
        }

        static string EnsureSession(Conn c, string b)
        {
            if (string.IsNullOrEmpty(c.targetId)) c.targetId = LoadTarget(b);   // survives daemon restarts
            if (!string.IsNullOrEmpty(c.sessionId) && !string.IsNullOrEmpty(c.targetId))
            {
                // a session that answered within the last few seconds is alive; a dead one fails the call and is re-attached by the retry loop
                if (unchecked(Environment.TickCount - c.lastOk) < 5000) return null;
                List<Dictionary<string, object>> live = ListPages(b);
                if (live != null)
                {
                    foreach (Dictionary<string, object> p in live)
                        if (SN(p, "id") == c.targetId) return null;   // still attached to a live tab
                }
                c.sessionId = "";
            }
            List<Dictionary<string, object>> pp = ListPages(b);
            Dictionary<string, object> pick = null;
            if (pp != null)
            {
                if (!string.IsNullOrEmpty(c.targetId))
                    foreach (Dictionary<string, object> p in pp)
                        if (SN(p, "id") == c.targetId) { pick = p; break; }
                if (pick == null && pp.Count > 0) pick = pp[0];
            }
            if (pick == null)
            {
                string cr;
                string e = CoreCallEx(b, false, "Target.createTarget", "{\"url\":\"about:blank\"}", 5000, false, out cr);
                if (e != null) return e;
                Dictionary<string, object> cd = ParseD(cr);
                c.targetId = cd == null ? "" : SN(cd, "targetId");
                if (c.targetId == "") return J.Err("ERR_ATTACH", "createTarget failed");
            }
            else c.targetId = SN(pick, "id");
            string ar;
            string ae = CoreCall(b, false, "Target.attachToTarget", "{\"targetId\":" + J.Q(c.targetId) + ",\"flatten\":true}", 5000, out ar);
            if (ae != null) return ae;
            Dictionary<string, object> ad = ParseD(ar);
            string sid = ad == null ? "" : SN(ad, "sessionId");
            if (sid == "") return J.Err("ERR_ATTACH", "attachToTarget returned no sessionId");
            c.sessionId = sid;
            SaveTarget(b, c.targetId);
            return null;
        }

        // evaluate JS on the current page. value = JSON value of the result; errJson = full error line or null.
        static string EvalTo(string b, string expression, int timeoutMs, out object value, out string errJson)
        {
            value = null; errJson = null;
            string p = "{\"expression\":" + J.Q(expression) + ",\"returnByValue\":true,\"awaitPromise\":true,\"timeout\":" +
                       Math.Min(Math.Max(timeoutMs, 500), 30000).ToString(CultureInfo.InvariantCulture) + "}";
            string res;
            string e = EvalCall(b, p, Math.Max(timeoutMs, 1000) + 1000, out res);
            if (e != null) { errJson = e; return null; }
            Dictionary<string, object> d = ParseD(res);
            if (d == null) { errJson = J.Err("ERR_CDP", "bad evaluate result"); return null; }
            if (d.ContainsKey("exceptionDetails"))
            { errJson = J.Err("ERR_JS", ExMsg(d["exceptionDetails"])); return null; }
            object ro;
            if (!d.TryGetValue("result", out ro) || ro == null) { errJson = J.Err("ERR_JS", "no result"); return null; }
            Dictionary<string, object> rod = ro as Dictionary<string, object>;
            if (rod == null) { errJson = J.Err("ERR_JS", "bad result"); return null; }
            object v;
            if (rod.TryGetValue("value", out v)) value = v;
            else { object desc; rod.TryGetValue("description", out desc); value = desc; }
            return null;
        }

        // evaluate a snippet that returns an object. returns a full error line on transport/JS failure,
        // null on success (caller then inspects the object with ObjErr / BN(d["ok"])).
        static string EvalObj(string b, string expr, int timeoutMs, out Dictionary<string, object> obj, out string errJson)
        {
            object v; string ee;
            EvalTo(b, expr, timeoutMs, out v, out ee);
            errJson = ee;
            obj = v as Dictionary<string, object>;
            if (errJson == null && obj == null)
                errJson = J.Err("ERR_JS", "snippet did not return an object (" + (v == null ? "null" : v.GetType().Name) + ")");
            return errJson;
        }

        // snippet-level {ok:false,err,msg} -> full error line; null when ok:true
        static string ObjErr(Dictionary<string, object> d)
        {
            if (d == null) return J.Err("ERR_JS", "null result");
            if (BN(d.ContainsKey("ok") ? d["ok"] : null)) return null;
            string code = SN(d, "err"); if (code == "") code = "ERR_JS";
            string msg = SN(d, "msg"); if (msg == "") msg = "failed";
            return J.Err(code, msg);
        }

        // ---------------------------------------------------------------- bot-check / risk pages
        // Reported, never bypassed: an agent that keeps retrying a challenge page only burns its budget. The
        // tag goes into error replies and into els/text/open results so the caller can stop and ask the user.
        const string WallHint =
            "this page is a bot-check / risk-control page, not the real content: stop retrying - ask the user to pass the check once in the dedicated browser window (the clearance cookie is kept in state\\web\\<browser>), or use the user's own browser";
        const string LoginHint =
            "this is the site's sign-in page: the dedicated profile has no login for this site - ask the user whether to log in once in the dedicated browser window (the cookie stays in state\\web\\<browser>) or to use another source";
        static string WallHintFor(string w) { return w == "login" ? LoginHint : WallHint; }
        static string WallTag(string b)
        {
            Dictionary<string, object> d;
            if (LibEval(b, "(()=>({ok:true,wall:String(__cu.wall()||'')}))()", 2500, out d) != null) return null;
            string w = SN(d, "wall");
            return string.IsNullOrEmpty(w) ? null : w;
        }
        static string InjectJson(string json, string frag)
        {
            if (string.IsNullOrEmpty(json) || json[json.Length - 1] != '}') return json;
            return json.Substring(0, json.Length - 1) + "," + frag + "}";
        }
        // decorate an error reply with the wall tag when the current page is a challenge page
        static string WithWall(string b, string errJson)
        {
            if (string.IsNullOrEmpty(errJson)) return errJson;
            string w = WallTag(b);
            if (w == null) return errJson;
            return InjectJson(errJson, "\"wall\":" + J.Q(w) + ",\"hint\":" + J.Q(WallHintFor(w)));
        }
        static void AddWall(string b, Dictionary<string, object> d)
        {
            if (d == null) return;
            string w = WallTag(b);
            if (w == null) return;
            d["wall"] = w;
            if (!d.ContainsKey("hint")) d["hint"] = WallHintFor(w);
        }

        // ---------------------------------------------------------------- launch / stop / status
        public static string Start(string b, string url)
        {
            b = Norm(b);
            if (b == "") return J.Err("ERR_ARGS", "-Browser must be edge or chrome");
            int port = Port(b);
            string identityError = ListenerError(b);
            if (identityError != null) return identityError;
            if (Running(b))
                return "{\"ok\":true,\"browser\":" + J.Q(b) + ",\"port\":" + port + ",\"running\":true,\"started\":false}";
            string exe = ExeOf(b);
            if (exe == "") return J.Err("ERR_BROWSER", b + " install not found");
            string prof = ProfileDir(b);
            try { Directory.CreateDirectory(prof); } catch (Exception e) { return J.Err("ERR_PROFILE", e.Message); }
            if (string.IsNullOrEmpty(url)) url = "about:blank"; else url = NormalizeUrl(url);
            string args = "--remote-debugging-address=127.0.0.1 --remote-debugging-port=" + port.ToString(CultureInfo.InvariantCulture) +
                " --user-data-dir=" + ArgQ(prof) +
                " --no-first-run --no-default-browser-check --disable-session-crashed-bubble --hide-crash-restore-bubble" +
                " --disable-sync" +
                (Environment.GetEnvironmentVariable("CU_HEADLESS") == "1" ? " --headless=new --disable-gpu" : "") +
                // keep frames flowing while the window sits behind other windows: CDP mouse/wheel input is rAF-aligned and
                // would otherwise stall for seconds (verified 2026-09-26: occluded window -> document.hidden, no rAF)
                " --disable-backgrounding-occluded-windows --disable-renderer-backgrounding --disable-background-timer-throttling" +
                " --disable-features=CalculateNativeWinOcclusion,IntensiveWakeUpThrottling" +
                " " + ArgQ(url);
            Process p;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                // A long-lived browser must not inherit the caller's stdout
                // or stderr pipe: otherwise Bash $(cu.exe web start) can hang
                // until the browser is closed even after the JSON reply.
                psi.UseShellExecute = true;
                p = Process.Start(psi);
            }
            catch (Exception e) { return J.Err("ERR_START", e.Message); }
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 12000)
            {
                identityError = ListenerError(b);
                if (identityError != null) return identityError;
                if (Running(b))
                {
                    lock (_lk)
                    {
                        Conn c;
                        if (_conns.TryGetValue(b, out c)) { try { if (c.ws != null) c.ws.Dispose(); } catch { } _conns.Remove(b); }
                    }
                    try { Directory.CreateDirectory(WebDir()); File.WriteAllText(Path.Combine(WebDir(), BrowserKey(b) + ".pid"), p.Id.ToString(CultureInfo.InvariantCulture)); }
                    catch { }
                    return "{\"ok\":true,\"browser\":" + J.Q(b) + ",\"port\":" + port + ",\"pid\":" + p.Id +
                           ",\"running\":true,\"started\":true,\"ms\":" + sw.ElapsedMilliseconds + "}";
                }
                if (p.HasExited) return J.Err("ERR_START", "browser exited early (code " + p.ExitCode + ")");
                Thread.Sleep(sw.ElapsedMilliseconds < 2000 ? 60 : 150);
            }
            return J.Err("ERR_TIMEOUT", "debug port " + port + " did not open within 12s");
        }

        static List<uint> ProfilePids(string b)
        {
            List<uint> ids = new List<uint>();
            string prof = ProfileDir(b);
            string exeName = b == "chrome" ? "chrome.exe" : "msedge.exe";
            try
            {
                // NOTE: WQL LIKE treats backslash as an escape character - never put raw backslashes
                // in the pattern. Escape %/_ as [%]/[_], then turn path separators into wildcards.
                string safe = prof.Replace("%", "[%]").Replace("_", "[_]").Replace("\\", "%").Replace("'", "''");
                string wql = "SELECT ProcessId,CommandLine FROM Win32_Process WHERE Name='" + exeName +
                             "' AND CommandLine LIKE '%user-data-dir=%" + safe + "%'";
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(wql))
                using (ManagementObjectCollection set = s.Get())
                {
                    foreach (ManagementObject o in set)
                    {
                        try
                        {
                            int id = Convert.ToInt32(o["ProcessId"]);
                            if (OwnsListener(b, id)) ids.Add((uint)id);
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return ids;
        }

        public static string Stop(string b)
        {
            b = Norm(b);
            if (b == "") return J.Err("ERR_ARGS", "-Browser must be edge or chrome");
            string identityError = ListenerError(b);
            if (identityError != null) return identityError; // never kill another process on our port
            lock (_lk)
            {
                Conn c;
                if (_conns.TryGetValue(b, out c)) { try { if (c.ws != null) c.ws.Dispose(); } catch { } _conns.Remove(b); }
            }
            int killed = 0;
            for (int pass = 0; pass < 3; pass++)
            {
                List<uint> ids = ProfilePids(b);
                if (ids.Count == 0) break;
                foreach (uint id in ids)
                {
                    try { Process.GetProcessById((int)id).Kill(); killed++; } catch { }
                }
                Thread.Sleep(250);
            }
            Thread.Sleep(300);
            bool still = Running(b);
            return "{\"ok\":true,\"browser\":" + J.Q(b) + ",\"killed\":" + killed + ",\"running\":" + (still ? "true" : "false") + "}";
        }

        public static string Status(string b)
        {
            b = Norm(b);
            if (b == "") return J.Err("ERR_ARGS", "-Browser must be edge or chrome");
            string identityError = ListenerError(b);
            if (identityError != null) return identityError;
            StringBuilder sb = new StringBuilder(512);
            sb.Append("{\"ok\":true,\"browser\":").Append(J.Q(b))
              .Append(",\"port\":").Append(Port(b))
              .Append(",\"exe\":").Append(J.Q(ExeOf(b)))
              .Append(",\"profile\":").Append(J.Q(ProfileDir(b)));
            bool run = Running(b);
            sb.Append(",\"running\":").Append(run ? "true" : "false");
            if (!run) return sb.Append("}").ToString();
            try
            {
                string pf = Path.Combine(WebDir(), BrowserKey(b) + ".pid");
                if (File.Exists(pf)) sb.Append(",\"pid\":").Append(File.ReadAllText(pf).Trim());
            }
            catch { }
            List<Dictionary<string, object>> pages = ListPages(b);
            int activeIdx = 0; int i = 0;
            StringBuilder tabs = new StringBuilder();
            string connTarget = "";
            lock (_lk) { Conn c; if (_conns.TryGetValue(b, out c)) connTarget = c.targetId; }
            if (pages != null)
            {
                foreach (Dictionary<string, object> p in pages)
                {
                    i++;
                    if (connTarget != "" && SN(p, "id") == connTarget) activeIdx = i;
                    if (tabs.Length > 0) tabs.Append(',');
                    tabs.Append("{\"i\":").Append(i)
                        .Append(",\"title\":").Append(J.Q(Trunc(SN(p, "title"), 120)))
                        .Append(",\"url\":").Append(J.Q(Trunc(SN(p, "url"), 300))).Append('}');
                }
            }
            if (activeIdx == 0 && i > 0) activeIdx = 1;
            sb.Append(",\"count\":").Append(i)
              .Append(",\"active\":").Append(activeIdx)
              .Append(",\"tabs\":[").Append(tabs).Append(']');
            return sb.Append("}").ToString();
        }

        // ---------------------------------------------------------------- navigation / tabs
        // Mark the outgoing document; the new document has no mark, so even a same-URL navigation is detected.
        // Returns the href seen before navigating.
        static string MarkNav(string b)
        {
            object v; string ej;
            EvalTo(b, "(function(){window.__cuNav=1;return String(location.href);})()", 2000, out v, out ej);
            return v == null ? "" : Convert.ToString(v);
        }

        static bool SameDocument(string prev, string next)
        {
            if (string.IsNullOrEmpty(prev) || string.IsNullOrEmpty(next)) return false;
            int a = prev.IndexOf('#'), c = next.IndexOf('#');
            string pa = a < 0 ? prev : prev.Substring(0, a), pc = c < 0 ? next : next.Substring(0, c);
            return pa == pc && c >= 0;   // only the fragment differs: no new document will appear
        }

        // prevHref == null: reload / new tab (no fragment special case). Waits for a new document that is complete;
        // a document that has been interactive (DOM ready) for a while but whose load event is held up by slow
        // resources (trackers, long polls) is reported as ready="interactive" instead of failing.
        const int INTERACTIVE_GRACE_MS = 1200;
        static string WaitReadyCore(string b, int timeoutMs, string prevHref, out string readyState)
        {
            readyState = "";
            Stopwatch sw = Stopwatch.StartNew();
            bool marked = prevHref != null;
            long interactiveAt = -1;
            while (true)
            {
                object v; string ej;
                EvalTo(b, "({h:String(location.href),r:document.readyState,n:window.__cuNav||0})", Math.Max(1500, timeoutMs), out v, out ej);
                Dictionary<string, object> d = v as Dictionary<string, object>;
                if (ej == null && d != null)
                {
                    string h = SN(d, "h"); string r = SN(d, "r");
                    bool fresh = DN(d, "n") == 0;                                 // the marked document is gone
                    if (!marked) fresh = true;
                    if (marked && !fresh && SameDocument(prevHref, h) && h != prevHref) fresh = true;   // fragment navigation
                    if (marked && !fresh && sw.ElapsedMilliseconds > 2500) fresh = true;                // unload never happened: do not hang
                    if (fresh && r == "complete")
                    {
                        Thread.Sleep(25);    // one paint (60 Hz frame = 16 ms)
                        readyState = "complete";
                        return null;
                    }
                    if (fresh && r == "interactive")
                    {
                        if (interactiveAt < 0) interactiveAt = sw.ElapsedMilliseconds;
                        else if (sw.ElapsedMilliseconds - interactiveAt >= INTERACTIVE_GRACE_MS || sw.ElapsedMilliseconds >= timeoutMs - 60)
                        { readyState = "interactive"; return null; }
                    }
                    else interactiveAt = -1;
                }
                if (sw.ElapsedMilliseconds >= timeoutMs)
                    return J.Err("ERR_TIMEOUT", "page not ready within " + timeoutMs + "ms");
                Thread.Sleep(15);
            }
        }

        static string NavResult(string b, string readyState, Stopwatch sw, string extra)
        {
            object v; string ej;
            EvalTo(b, "({h:String(location.href),t:String(document.title)})", 2000, out v, out ej);
            Dictionary<string, object> d = v as Dictionary<string, object>;
            string href = d == null ? "" : SN(d, "h"), title = d == null ? "" : SN(d, "t");
            StringBuilder sb = new StringBuilder(256);
            sb.Append("{\"ok\":true,\"url\":").Append(J.Q(href)).Append(",\"title\":").Append(J.Q(Trunc(title, 120)))
              .Append(",\"ready\":").Append(J.Q(readyState));
            if (readyState == "interactive") sb.Append(",\"warn\":\"DOM is ready but the load event has not fired yet (slow resources); the page is usable\"");
            if (!string.IsNullOrEmpty(extra)) sb.Append(',').Append(extra);
            string w = WallTag(b);
            if (w != null) sb.Append(",\"wall\":").Append(J.Q(w)).Append(",\"hint\":").Append(J.Q(WallHintFor(w)));
            sb.Append(",\"ms\":").Append(sw.ElapsedMilliseconds).Append('}');
            return sb.ToString();
        }

        static string EvalHref(string b)
        {
            object v; string ej;
            EvalTo(b, "String(location.href)", 2000, out v, out ej);
            return v == null ? "" : Convert.ToString(v);
        }

        public static string Navigate(string b, string url, int timeoutMs)
        {
            b = Norm(b);
            if (b == "") return J.Err("ERR_ARGS", "-Browser must be edge or chrome");
            url = NormalizeUrl(url);
            if (url == "") return J.Err("ERR_ARGS", "web open needs -Url");
            Stopwatch sw = Stopwatch.StartNew();
            string prev = MarkNav(b);
            string res;
            string e = CoreCall(b, true, "Page.navigate", "{\"url\":" + J.Q(url) + "}", 10000, out res);
            if (e != null) return e;
            Dictionary<string, object> rd = ParseD(res);
            if (rd != null && rd.ContainsKey("errorText"))
                return J.Err("ERR_NAV", SN(rd, "errorText"));
            string rs;
            string we = WaitReadyCore(b, timeoutMs, prev, out rs);
            if (we != null) return we;
            EnsureVisible(b);
            return NavResult(b, rs, sw, null);
        }

        public static string NewTab(string b, string url, int timeoutMs)
        {
            b = Norm(b);
            if (b == "") return J.Err("ERR_ARGS", "-Browser must be edge or chrome");
            url = NormalizeUrl(url);
            if (url == "") return J.Err("ERR_ARGS", "web open needs -Url");
            Stopwatch sw = Stopwatch.StartNew();
            string cr;
            string e = CoreCallEx(b, false, "Target.createTarget", "{\"url\":" + J.Q(url) + "}", 8000, false, out cr);
            if (e != null) return e;
            Dictionary<string, object> cd = ParseD(cr);
            string tid = cd == null ? "" : SN(cd, "targetId");
            if (tid == "") return J.Err("ERR_CDP", "createTarget failed");
            string ar;
            string ae = CoreCall(b, false, "Target.activateTarget", "{\"targetId\":" + J.Q(tid) + "}", 5000, out ar);
            if (ae != null) return ae;
            lock (_lk)
            {
                Conn c;
                if (_conns.TryGetValue(b, out c)) { c.targetId = tid; c.sessionId = ""; c.lastOk = Stale(); }
            }
            SaveTarget(b, tid);
            string rs;
            string we = WaitReadyCore(b, timeoutMs, null, out rs);
            if (we != null) return we;
            return NavResult(b, rs, sw, "\"tabId\":" + J.Q(tid));
        }

        public static string Reload(string b, int timeoutMs)
        {
            b = Norm(b);
            if (b == "") return J.Err("ERR_ARGS", "-Browser must be edge or chrome");
            Stopwatch sw = Stopwatch.StartNew();
            string prev = MarkNav(b);
            string res;
            string e = CoreCall(b, true, "Page.reload", "{}", 10000, out res);
            if (e != null) return e;
            string rs;
            string we = WaitReadyCore(b, timeoutMs, prev + "#__reload__", out rs);   // marked; a reload always yields a new document
            if (we != null) return we;
            EnsureVisible(b);
            return NavResult(b, rs, sw, null);
        }

        public static string History(string b, int dir)
        {
            b = Norm(b);
            if (b == "") return J.Err("ERR_ARGS", "-Browser must be edge or chrome");
            object v; string ej;
            EvalTo(b, dir < 0 ? "history.back()||1" : "history.forward()||1", 3000, out v, out ej);
            if (ej != null) return ej;
            Thread.Sleep(80);
            return "{\"ok\":true,\"url\":" + J.Q(EvalHref(b)) + "}";
        }

        public static string Tabs(string b)
        {
            b = Norm(b);
            if (b == "") return J.Err("ERR_ARGS", "-Browser must be edge or chrome");
            if (!Running(b)) return J.Err("ERR_NOT_RUNNING", b + " is not running (web start)");
            List<Dictionary<string, object>> pages = ListPages(b);
            if (pages == null) return J.Err("ERR_CDP", "cannot read /json/list");
            string connTarget = "";
            lock (_lk) { Conn c; if (_conns.TryGetValue(b, out c)) connTarget = c.targetId; }
            StringBuilder tabs = new StringBuilder();
            int i = 0, active = 0;
            foreach (Dictionary<string, object> p in pages)
            {
                i++;
                if (connTarget != "" && SN(p, "id") == connTarget) active = i;
                if (tabs.Length > 0) tabs.Append(',');
                tabs.Append("{\"i\":").Append(i)
                    .Append(",\"id\":").Append(J.Q(SN(p, "id")))
                    .Append(",\"title\":").Append(J.Q(Trunc(SN(p, "title"), 160)))
                    .Append(",\"url\":").Append(J.Q(Trunc(SN(p, "url"), 400))).Append('}');
            }
            if (active == 0 && i > 0) active = 1;
            return "{\"ok\":true,\"count\":" + i + ",\"active\":" + active + ",\"tabs\":[" + tabs + "]}";
        }

        public static string Tab(string b, int index, string urlSub, string titleSub)
        {
            b = Norm(b);
            if (b == "") return J.Err("ERR_ARGS", "-Browser must be edge or chrome");
            if (!Running(b)) return J.Err("ERR_NOT_RUNNING", b + " is not running (web start)");
            Dictionary<string, object> pick = PickTab(b, index, urlSub, titleSub);
            if (pick == null) return J.Err("ERR_NOT_FOUND", "no matching tab (use web tabs)");
            string tid = SN(pick, "id");
            string ar;
            string ae = CoreCall(b, false, "Target.activateTarget", "{\"targetId\":" + J.Q(tid) + "}", 5000, out ar);
            if (ae != null) return ae;
            lock (_lk)
            {
                Conn c;
                if (_conns.TryGetValue(b, out c)) { c.targetId = tid; c.sessionId = ""; c.lastOk = Stale(); }
            }
            SaveTarget(b, tid);
            return "{\"ok\":true,\"id\":" + J.Q(tid) + ",\"title\":" + J.Q(Trunc(SN(pick, "title"), 160)) +
                   ",\"url\":" + J.Q(Trunc(SN(pick, "url"), 400)) + "}";
        }

        // pick a tab: -Index (1-based) / -Url substring / -Title substring; null selectors = current attached tab
        static Dictionary<string, object> PickTab(string b, int index, string urlSub, string titleSub)
        {
            List<Dictionary<string, object>> pages = ListPages(b);
            if (pages == null || pages.Count == 0) return null;
            if (index < 1 && string.IsNullOrEmpty(urlSub) && string.IsNullOrEmpty(titleSub))
            {
                string cur = "";
                lock (_lk) { Conn c; if (_conns.TryGetValue(b, out c)) cur = c.targetId; }
                if (cur == "") cur = LoadTarget(b);
                if (cur != "")
                    foreach (Dictionary<string, object> p in pages)
                        if (SN(p, "id") == cur) return p;
                return pages[0];
            }
            Dictionary<string, object> pick = null;
            if (index >= 1 && index <= pages.Count) pick = pages[index - 1];
            else if (!string.IsNullOrEmpty(urlSub))
            {
                foreach (Dictionary<string, object> p in pages)
                    if (SN(p, "url").IndexOf(urlSub, StringComparison.OrdinalIgnoreCase) >= 0) { pick = p; break; }
            }
            else if (!string.IsNullOrEmpty(titleSub))
            {
                foreach (Dictionary<string, object> p in pages)
                    if (SN(p, "title").IndexOf(titleSub, StringComparison.OrdinalIgnoreCase) >= 0) { pick = p; break; }
            }
            return pick;
        }

        // close current (or matching) tab; the next EnsureSession picks a fresh one
        public static string CloseTab(string b, int index, string urlSub, string titleSub)
        {
            b = Norm(b);
            if (b == "") return J.Err("ERR_ARGS", "-Browser must be edge or chrome");
            if (!Running(b)) return J.Err("ERR_NOT_RUNNING", b + " is not running (web start)");
            Dictionary<string, object> pick = PickTab(b, index, urlSub, titleSub);
            if (pick == null) return J.Err("ERR_NOT_FOUND", "no matching tab");
            string tid = SN(pick, "id");
            string ar;
            string ae = CoreCall(b, false, "Target.closeTarget", "{\"targetId\":" + J.Q(tid) + "}", 5000, out ar);
            if (ae != null) return ae;
            lock (_lk)
            {
                Conn c;
                if (_conns.TryGetValue(b, out c)) { if (c.targetId == tid) { c.targetId = ""; c.sessionId = ""; c.lastOk = Stale(); } }
            }
            try { if (LoadTarget(b) == tid) SaveTarget(b, ""); } catch { }
            return "{\"ok\":true,\"closed\":" + J.Q(tid) + ",\"title\":" + J.Q(Trunc(SN(pick, "title"), 160)) +
                   ",\"url\":" + J.Q(Trunc(SN(pick, "url"), 400)) + "}";
        }

        // ---------------------------------------------------------------- in-page helper library (win\web-lib.js)
        static string _lib = "";
        static DateTime _libTime = DateTime.MinValue;
        static string _libErr = null;

        static string LibPath()
        {
            string dll = typeof(Web).Assembly.Location;                        // ...\computer-use\win\bin\cu-xxxx.dll
            string win = Path.GetDirectoryName(Path.GetDirectoryName(dll));    // ...\computer-use\win
            return Path.Combine(win, "web-lib.js");
        }

        // cached by file time: editing web-lib.js takes effect on the next command, no daemon restart needed
        static string Lib()
        {
            string p = LibPath();
            try
            {
                DateTime t = File.GetLastWriteTimeUtc(p);
                if (t != _libTime || _lib == "")
                {
                    _lib = File.ReadAllText(p, Encoding.UTF8);
                    _libTime = t;
                    _libErr = null;
                }
            }
            catch (Exception e) { _libErr = "web-lib.js not readable: " + e.Message; return ""; }
            return _lib;
        }

        // full expression: install the helper if this document has not seen it yet, then run expr
        static string WithLib(string expr)
        {
            string l = Lib();
            if (l == "") return null;
            return l + "\n;" + expr;
        }

        static string LibEval(string b, string expr, int timeoutMs, out Dictionary<string, object> d)
        {
            d = null;
            string full = WithLib(expr);
            if (full == null) return J.Err("ERR_LOAD", _libErr ?? "web-lib.js missing");
            string unused;
            string er = EvalObj(b, full, timeoutMs, out d, out unused);
            if (er != null) return er;
            return ObjErr(d);
        }

        // locator object for __cu.locate
        static string LocQ(string sel, string text, int index, bool exact, int id, bool raw)
        {
            return "{sel:" + J.Q(sel ?? "") + ",text:" + J.Q(text ?? "") + ",index:" + (index < 1 ? 1 : index).ToString(CultureInfo.InvariantCulture) +
                   ",exact:" + (exact ? "true" : "false") + ",id:" + (id < 0 ? 0 : id).ToString(CultureInfo.InvariantCulture) +
                   ",raw:" + (raw ? "true" : "false") + "}";
        }

        static string ArgErr(string b, bool needTarget, string sel, string text, int id, string what)
        {
            if (b == "") return J.Err("ERR_ARGS", "-Browser must be edge or chrome");
            if (needTarget && string.IsNullOrEmpty(sel) && string.IsNullOrEmpty(text) && id <= 0)
                return J.Err("ERR_ARGS", what + " needs -Sel, -Text/-Find or -Id");
            return null;
        }

        // locate + describe + pick the click point. doClick=true also fires el.click() in the same round trip.
        // probe=true additionally reports d.frames (compositor alive) / d.hidden so mouse input is only used when it will be processed now
        static string LocateExpr(string q, string action, bool probe)
        {
            return "(function(){var L=__cu.locate(" + q + ");if(!L.ok)return L;var el=L.el;var p=__cu.point(el);var d=__cu.describe(el);" +
                   "d.ok=true;d.count=L.count;if(L.alts)d.alts=L.alts;d.x=Math.round(p.x*100)/100;d.y=Math.round(p.y*100)/100;d.hit=p.hit;if(p.cover)d.cover=p.cover;" +
                   action + (probe ? "d.tabHint=__cu.opensTab(el);return __cu.probe(d);" : "return d;") + "})()";
        }

        static bool Frames(Dictionary<string, object> d) { return BN(d.ContainsKey("frames") ? d["frames"] : null); }

        // A background tab is document.hidden: no frames, and many sites ignore keys there. Bring our tab to the
        // front of its window (Target.activateTarget, no OS focus change) and wait for visibility. False = still
        // hidden (window minimized).
        static bool EnsureVisible(string b)
        {
            object v; string ej;
            EvalTo(b, "document.hidden", 1500, out v, out ej);
            if (!(v is bool) || !(bool)v) return true;
            string tid;
            lock (_lk) { Conn c; tid = _conns.TryGetValue(b, out c) ? c.targetId : ""; }
            if (tid == "") return false;
            string r;
            CoreCall(b, false, "Target.activateTarget", "{\"targetId\":" + J.Q(tid) + "}", 3000, out r);
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 500)
            {
                Thread.Sleep(40);
                EvalTo(b, "document.hidden", 1500, out v, out ej);
                if (v is bool && !(bool)v) return true;
            }
            return false;
        }
        const string NO_FRAMES = "browser window is hidden/minimized (no frames): real mouse input would stall";

        // ---------------------------------------------------------------- mouse / keys via CDP Input
        static string MouseEvent(string type, double x, double y, string button, int clicks, int mods)
        {
            StringBuilder sb = new StringBuilder(160);
            sb.Append("{\"type\":\"").Append(type).Append("\",\"x\":").Append(J.F(x)).Append(",\"y\":").Append(J.F(y));
            if (button != null) sb.Append(",\"button\":\"").Append(button).Append('"');
            if (clicks > 0) sb.Append(",\"clickCount\":").Append(clicks);
            if (mods != 0) sb.Append(",\"modifiers\":").Append(mods);
            sb.Append(",\"pointerType\":\"mouse\"}");
            return sb.ToString();
        }

        // move -> press -> release (twice for a double click). Hover handlers see the move like with a real pointer.
        static string DispatchMouse(string b, double x, double y, string button, bool dbl)
        {
            string bt = button == "right" ? "right" : (button == "middle" ? "middle" : "left");
            string e;
            e = InputCall(b, "Input.dispatchMouseEvent", MouseEvent("mouseMoved", x, y, "none", 0, 0)); if (e != null) return e;
            int n = dbl ? 2 : 1;
            for (int i = 1; i <= n; i++)
            {
                e = InputCall(b, "Input.dispatchMouseEvent", MouseEvent("mousePressed", x, y, bt, i, 0)); if (e != null) return e;
                e = InputCall(b, "Input.dispatchMouseEvent", MouseEvent("mouseReleased", x, y, bt, i, 0)); if (e != null) return e;
            }
            return null;
        }

        static string EnterKey(string b)
        {
            string ke;
            string kd = "{\"type\":\"keyDown\",\"windowsVirtualKeyCode\":13,\"nativeVirtualKeyCode\":13," +
                        "\"code\":\"Enter\",\"key\":\"Enter\",\"text\":\"\\r\",\"unmodifiedText\":\"\\r\"}";
            string ku = "{\"type\":\"keyUp\",\"windowsVirtualKeyCode\":13,\"nativeVirtualKeyCode\":13,\"code\":\"Enter\",\"key\":\"Enter\"}";
            ke = InputCall(b, "Input.dispatchKeyEvent", kd); if (ke != null) return ke;
            ke = InputCall(b, "Input.dispatchKeyEvent", ku); if (ke != null) return ke;
            return null;
        }

        // After "-Enter", verify the key actually went somewhere: measured on Bing (its search box is a textarea
        // whose submit handler is not attached for the first moments after load) the keydown text lands in the
        // value - the query keeps a stray CRLF and nothing is submitted. When that is detected, drop the newline
        // and press the form's own submit button with a real mouse click: exactly the workaround SKILL.md used to
        // document by hand. Forms without a submit control are left untouched (only a note, no extra events).
        static void VerifyEnter(string b, Dictionary<string, object> d)
        {
            Dictionary<string, object> de;
            if (LibEval(b, "(()=>({ok:true,swallowed:__cu.enterSwallowed()}))()", 2500, out de) != null) return;
            if (!BN(de.ContainsKey("swallowed") ? de["swallowed"] : null)) return;
            Dictionary<string, object> df;
            if (LibEval(b, "(()=>{var p=__cu.enterFallback();return{ok:true,p:p};})()", 3000, out df) != null) return;
            Dictionary<string, object> p = df.ContainsKey("p") ? df["p"] as Dictionary<string, object> : null;
            if (p == null || !BN(p.ContainsKey("ok") ? p["ok"] : null))
            {
                d["enterNote"] = "the Enter text landed in the field and no submit control was found on its form - if the page needs an explicit submit, click its search/send button";
                return;
            }
            string me = DispatchMouse(b, DN(p, "x"), DN(p, "y"), "left", false);
            if (me != null) return;
            d["enterVia"] = "submit-click";
            d["enterFallback"] = SN(p, "tag") + (SN(p, "id") != "" ? "#" + SN(p, "id") : "");
        }

        // ---------------------------------------------------------------- find / click / hover / scroll
        public static string Find(string b, string sel, string text, int index, bool exact, int id)
        {
            b = Norm(b);
            string ae = ArgErr(b, true, sel, text, id, "web find"); if (ae != null) return ae;
            Stopwatch sw = Stopwatch.StartNew();
            Dictionary<string, object> d;
            string er = LibEval(b, LocateExpr(LocQ(sel, text, index, exact, id, false), "d.method='locate';", false), 6000, out d);
            if (er != null) return WithWall(b, er);
            d["ms"] = sw.ElapsedMilliseconds;
            return _ser.Serialize(d);
        }

        // method: auto (real mouse when the point really hits the element, else JS click), mouse (strict), js, double
        public static string Click(string b, string sel, string text, int index, bool exact, string method, string button, int id)
        {
            b = Norm(b);
            string ae = ArgErr(b, true, sel, text, id, "web click"); if (ae != null) return ae;
            if (string.IsNullOrEmpty(button)) button = "left";
            button = button.ToLowerInvariant();
            method = (method ?? "auto").ToLowerInvariant();
            bool dbl = method == "double" || method == "dbl";
            bool js = method == "js";
            bool strictMouse = method == "mouse" || dbl || button != "left";
            Stopwatch sw = Stopwatch.StartNew();
            _lateAck = false;
            List<string> before = PageIds(b);
            Dictionary<string, object> d;
            string action = js ? "try{el.click();d.method='js';}catch(e){return{ok:false,err:'ERR_CLICK',msg:String(e.message)}}" : "";
            string er = LibEval(b, LocateExpr(LocQ(sel, text, index, exact, id, false), action, true), 6000, out d);
            if (er != null) return WithWall(b, er);
            if (!js)
            {
                bool hit = BN(d.ContainsKey("hit") ? d["hit"] : null);
                bool frames = Frames(d);
                if (!frames && EnsureVisible(b)) { frames = true; d["frames"] = true; d["hidden"] = false; d["activated"] = true; }
                if (strictMouse)
                {
                    if (!hit)
                        return J.Err("ERR_OCCLUDED", "element is covered by " + SN(d, "cover") + " (use -Method js to click through, or close the overlay)");
                    if (!frames) return J.Err("ERR_HIDDEN", NO_FRAMES + "; restore the window or use -Method js");
                }
                if (hit && frames)
                {
                    string me = DispatchMouse(b, DN(d, "x"), DN(d, "y"), button, dbl);
                    if (me != null) return me;
                    d["method"] = dbl ? "double" : "mouse";
                    VerifyClick(b, sel, text, index, exact, id, dbl, button, method, d);
                }
                else
                {
                    // covered (overlay / sticky bar) or no frames: a DOM click still reaches the handler; say so
                    Dictionary<string, object> d2;
                    string e2 = LibEval(b, "(function(){var el=__cu.last;if(!el)return{ok:false,err:'ERR_STALE',msg:'lost element'};try{el.click();}catch(e){return{ok:false,err:'ERR_CLICK',msg:String(e.message)}}return{ok:true};})()", 4000, out d2);
                    if (e2 != null) return e2;
                    d["method"] = "js";
                    d["warn"] = !hit ? "point covered by " + SN(d, "cover") + "; used DOM click" : NO_FRAMES + "; used DOM click";
                }
            }
            if (_lateAck) d["ackLate"] = true;
            FollowNewTab(b, before, BN(d.ContainsKey("tabHint") ? d["tabHint"] : null), d);
            d.Remove("tabHint");
            d["ms"] = sw.ElapsedMilliseconds;
            return _ser.Serialize(d);
        }

        // page target ids (browser-level call, ~1-3 ms)
        static List<string> PageIds(string b)
        {
            List<string> ids = new List<string>();
            string res;
            if (CoreCall(b, false, "Target.getTargets", "{}", 3000, out res) != null) return ids;
            Dictionary<string, object> d = ParseD(res);
            object arr; if (d == null || !d.TryGetValue("targetInfos", out arr)) return ids;
            foreach (object o in (arr as object[]) ?? new object[0])
            {
                Dictionary<string, object> ti = o as Dictionary<string, object>;
                if (ti == null || SN(ti, "type") != "page" || SN(ti, "url").StartsWith("devtools://")) continue;
                ids.Add(SN(ti, "targetId"));
            }
            return ids;
        }

        // A click that opened another tab: the browser shows that tab, so the session follows it too
        // (web tab -Index N goes back). hinted = the element had target=_blank, so poll a little longer.
        static void FollowNewTab(string b, List<string> before, bool hinted, Dictionary<string, object> d)
        {
            if (before == null) return;
            Stopwatch sw = Stopwatch.StartNew();
            // no hint: one immediate check (a window.open in the click handler has already created its target by the time
            // the mouse events are acked); target=_blank: the navigation may take a moment, poll up to 700 ms
            int budget = hinted ? 700 : 0;
            string fresh = null;
            while (true)
            {
                List<string> now = PageIds(b);
                foreach (string id in now) if (!before.Contains(id)) { fresh = id; break; }
                if (fresh != null || sw.ElapsedMilliseconds >= budget) break;
                Thread.Sleep(50);
            }
            if (fresh == null) return;
            string ar;
            CoreCall(b, false, "Target.activateTarget", "{\"targetId\":" + J.Q(fresh) + "}", 3000, out ar);
            lock (_lk)
            {
                Conn c;
                if (_conns.TryGetValue(b, out c)) { c.targetId = fresh; c.sessionId = ""; c.lastOk = Stale(); }
            }
            SaveTarget(b, fresh);
            string rs;
            WaitReadyCore(b, 5000, null, out rs);
            object v; string ej;
            EvalTo(b, "({h:String(location.href),t:String(document.title)})", 2000, out v, out ej);
            Dictionary<string, object> nd = v as Dictionary<string, object>;
            Dictionary<string, object> nt = new Dictionary<string, object>();
            nt["id"] = fresh; nt["url"] = nd == null ? "" : SN(nd, "h"); nt["title"] = nd == null ? "" : Trunc(SN(nd, "t"), 120); nt["ready"] = rs ?? "";
            d["newTab"] = nt;
            d["followed"] = true;
        }

        // after a dispatched mouse click: does the point still hit the element? (cu.check in web-lib.js)
        static void VerifyClick(string b, string sel, string text, int index, bool exact, int id, bool dbl, string button, string method,
                                Dictionary<string, object> d)
        {
            Dictionary<string, object> d3;
            if (LibEval(b, "({ok:true,v:__cu.check(__cu.last)})", 2500, out d3) != null) return;
            Dictionary<string, object> v3 = d3.ContainsKey("v") ? d3["v"] as Dictionary<string, object> : null;
            if (v3 == null) return;
            bool hit = BN(v3.ContainsKey("hit") ? v3["hit"] : null);
            bool present = BN(v3.ContainsKey("present") ? v3["present"] : null);
            bool moved = BN(v3.ContainsKey("moved") ? v3["moved"] : null);
            bool off = BN(v3.ContainsKey("offscreen") ? v3["offscreen"] : null);
            d["verified"] = hit;
            if (!hit && v3.ContainsKey("cover")) d["cover"] = v3["cover"];
            if (hit) return;
            // the element left the document: a navigation (or a removal) right after the click - that is the
            // click WORKING (measured: clicking a link that navigates used to come back with a scary warn)
            if (!present) { d["gone"] = true; return; }
            // a layout shift moved the element after we located it: one single retry is safe. A merely covered
            // point is NOT retried - the second click would land on whatever covers it (often a just-opened modal).
            bool retryable = moved && !off && method == "auto" && !dbl && button == "left";
            if (retryable)
            {
                bool ran = false;
                Dictionary<string, object> d4;
                if (LibEval(b, LocateExpr(LocQ(sel, text, index, exact, id, false), "", true), 6000, out d4) == null &&
                    BN(d4.ContainsKey("hit") ? d4["hit"] : null) && Frames(d4))
                {
                    string me = DispatchMouse(b, DN(d4, "x"), DN(d4, "y"), button, false);
                    if (me != null) return;
                    d["retried"] = true; ran = true;
                    Dictionary<string, object> d5;
                    if (LibEval(b, "({ok:true,v:__cu.check(__cu.last)})", 2500, out d5) == null)
                    {
                        Dictionary<string, object> v5 = d5.ContainsKey("v") ? d5["v"] as Dictionary<string, object> : null;
                        if (v5 != null) d["verified"] = BN(v5.ContainsKey("hit") ? v5["hit"] : null);
                    }
                }
                if (!BN(d["verified"]))
                    d["warn"] = ran
                        ? "click did not land on the target even after one re-locate + retry: the point is covered or the page keeps moving - verify the result before continuing"
                        : "click point does not hit the target and the element could not be re-located: verify the result before continuing";
                return;
            }
            d["warn"] = "click point no longer hits the target element (verified=false; not retried - covered, off-screen, or a static miss): verify the result before continuing";
        }

        public static string ClickXY(string b, double x, double y, string button)
        {
            b = Norm(b);
            if (b == "") return J.Err("ERR_ARGS", "-Browser must be edge or chrome");
            if (string.IsNullOrEmpty(button)) button = "left";
            string path = LastWeb();
            if (!File.Exists(path))
                return J.Err("ERR_NO_FRAME", "no last web shot - run web shot first, or use -Sel/-Text");
            string j;
            try { j = File.ReadAllText(path, Encoding.UTF8); }
            catch (Exception e) { return J.Err("ERR_NO_FRAME", e.Message); }
            double s = 1;
            Match m = Regex.Match(j, "\"s\":(-?[0-9][0-9.eE+-]*)");
            if (m.Success) double.TryParse(m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out s);
            if (s <= 0) s = 1;
            bool full = j.IndexOf("\"full\":true") >= 0;
            double px = x / s, py = y / s;
            double vx = px, vy = py;
            if (full)
            {
                object v; string ej;
                EvalTo(b, "(()=>{window.scrollTo(0,Math.max(0," + J.F(py) + "-innerHeight/2));return{ok:true};})()", 3000, out v, out ej);
                Thread.Sleep(90);
                object v2; string ej2;
                EvalTo(b, "({sx:Math.round(scrollX),sy:Math.round(scrollY)})", 3000, out v2, out ej2);
                Dictionary<string, object> sd = v2 as Dictionary<string, object>;
                if (sd != null) { vx = px - DN(sd, "sx"); vy = py - DN(sd, "sy"); }
            }
            else
            {
                // the frame remembers the scroll position it was taken at; follow the page if it moved since
                double fsx = 0, fsy = 0;
                Match mx = Regex.Match(j, "\"sx\":(-?[0-9][0-9.eE+-]*)"), my = Regex.Match(j, "\"sy\":(-?[0-9][0-9.eE+-]*)");
                if (mx.Success) double.TryParse(mx.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out fsx);
                if (my.Success) double.TryParse(my.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out fsy);
                object v2; string ej2;
                EvalTo(b, "({sx:Math.round(scrollX),sy:Math.round(scrollY)})", 3000, out v2, out ej2);
                Dictionary<string, object> sd = v2 as Dictionary<string, object>;
                if (sd != null) { vx = px + fsx - DN(sd, "sx"); vy = py + fsy - DN(sd, "sy"); }
            }
            if (vx < 0) vx = 0;
            if (vy < 0) vy = 0;
            Stopwatch sw = Stopwatch.StartNew();
            // what is under the point, so the caller can see what it hit
            Dictionary<string, object> hd;
            LibEval(b, "(function(){var e=document.elementFromPoint(" + J.F(vx) + "," + J.F(vy) + ");if(!e)return{ok:true};var d=__cu.describe(e);d.ok=true;return d;})()", 3000, out hd);
            string me = DispatchMouse(b, vx, vy, button, false);
            if (me != null) return me;
            string on = hd == null ? "" : (SN(hd, "tag") + (SN(hd, "id") != "" ? "#" + SN(hd, "id") : "") + (SN(hd, "text") != "" ? " " + Trunc(SN(hd, "text"), 40) : ""));
            return "{\"ok\":true,\"method\":\"mouse\",\"x\":" + J.F(vx) + ",\"y\":" + J.F(vy) +
                   ",\"scale\":" + J.F(s) + ",\"button\":" + J.Q(button) + ",\"on\":" + J.Q(on) + ",\"ms\":" + sw.ElapsedMilliseconds + "}";
        }

        public static string Hover(string b, string sel, string text, int index, bool exact, int id)
        {
            b = Norm(b);
            string ae = ArgErr(b, true, sel, text, id, "web hover"); if (ae != null) return ae;
            Stopwatch sw = Stopwatch.StartNew();
            _lateAck = false;
            Dictionary<string, object> d;
            string er = LibEval(b, LocateExpr(LocQ(sel, text, index, exact, id, false), "", true), 6000, out d);
            if (er != null) return er;
            d.Remove("tabHint");
            if (!Frames(d) && EnsureVisible(b)) { d["frames"] = true; d["hidden"] = false; d["activated"] = true; }
            if (!Frames(d))
            {
                // no compositor frames: synthesize the hover events in the DOM instead of stalling on real input
                Dictionary<string, object> d2;
                string e2 = LibEval(b, "(function(){var el=__cu.last;if(!el)return{ok:false,err:'ERR_STALE',msg:'lost element'};['pointerover','pointerenter','mouseover','mouseenter','pointermove','mousemove'].forEach(function(t){try{el.dispatchEvent(new MouseEvent(t,{bubbles:t!=='mouseenter'&&t!=='pointerenter',cancelable:true,view:window}));}catch(e){}});return{ok:true};})()", 4000, out d2);
                if (e2 != null) return e2;
                d["method"] = "hover-dom"; d["warn"] = NO_FRAMES + "; dispatched DOM hover events";
            }
            else
            {
                string e = InputCall(b, "Input.dispatchMouseEvent", MouseEvent("mouseMoved", DN(d, "x"), DN(d, "y"), "none", 0, 0));
                if (e != null) return e;
                d["method"] = "hover";
            }
            if (_lateAck) d["ackLate"] = true;
            d["ms"] = sw.ElapsedMilliseconds;
            return _ser.Serialize(d);
        }

        // -Sel/-Text/-Id: bring the element into view; else -X/-Y pixels (window.scrollBy) or -Wheel notches (negative = down)
        public static string Scroll(string b, string sel, string text, int index, bool exact, int id, double dx, double dy, int wheel)
        {
            b = Norm(b);
            if (b == "") return J.Err("ERR_ARGS", "-Browser must be edge or chrome");
            Stopwatch sw = Stopwatch.StartNew();
            Dictionary<string, object> d;
            if (!string.IsNullOrEmpty(sel) || !string.IsNullOrEmpty(text) || id > 0)
            {
                string er = LibEval(b, "(function(){var L=__cu.locate(" + LocQ(sel, text, index, exact, id, false) + ");if(!L.ok)return L;" +
                    "try{L.el.scrollIntoView({block:'center',inline:'nearest'});}catch(e){L.el.scrollIntoView();}" +
                    "var d=__cu.describe(L.el);d.ok=true;d.count=L.count;d.sx=Math.round(scrollX);d.sy=Math.round(scrollY);return d;})()", 6000, out d);
                if (er != null) return er;
            }
            else if (wheel != 0)
            {
                Dictionary<string, object> c;
                string pe = LibEval(b, "__cu.probe({ok:true,x:innerWidth/2,y:innerHeight/2})", 3000, out c);
                if (pe != null) return pe;
                if (!Frames(c) && EnsureVisible(b)) c["frames"] = true;
                if (Frames(c))
                {
                    string e = InputCall(b, "Input.dispatchMouseEvent", "{\"type\":\"mouseWheel\",\"x\":" + J.F(DN(c, "x")) + ",\"y\":" + J.F(DN(c, "y")) +
                        ",\"deltaX\":0,\"deltaY\":" + J.F(-wheel * 100) + "}");
                    if (e != null) return e;
                    Thread.Sleep(80);
                    string er = LibEval(b, "({ok:true,method:'wheel',sx:Math.round(scrollX),sy:Math.round(scrollY)})", 3000, out d);
                    if (er != null) return er;
                }
                else
                {
                    string er = LibEval(b, "(function(){window.scrollBy(0," + J.F(-wheel * 100) + ");return{ok:true,method:'scrollBy',warn:" + J.Q(NO_FRAMES + "; used window.scrollBy") + ",sx:Math.round(scrollX),sy:Math.round(scrollY)};})()", 3000, out d);
                    if (er != null) return er;
                }
            }
            else
            {
                string er = LibEval(b, "(function(){window.scrollBy(" + J.F(dx) + "," + J.F(dy) + ");return{ok:true,sx:Math.round(scrollX),sy:Math.round(scrollY)};})()", 3000, out d);
                if (er != null) return er;
            }
            d["ms"] = sw.ElapsedMilliseconds;
            return _ser.Serialize(d);
        }

        // ---------------------------------------------------------------- type / keys
        // method: auto = trusted Input.insertText for visible input/textarea/contenteditable (what a person's typing looks
        //         like to the page: focus, beforeinput/input, autosuggest state, Enter submits), value setter for <select>,
        //         hidden targets and very long texts; value / keys force one path
        public static string Type(string b, string sel, string text, bool append, bool enter, bool verify, string method, int id)
        {
            b = Norm(b);
            if (b == "") return J.Err("ERR_ARGS", "-Browser must be edge or chrome");
            if (text == null) text = "";
            method = (method ?? "auto").ToLowerInvariant();
            if (method != "value" && method != "keys") method = "auto";
            Stopwatch sw = Stopwatch.StartNew();
            bool hasTarget = !string.IsNullOrEmpty(sel) || id > 0;
            string expr = "(function(){var el=null;" +
                (hasTarget ? "var L=__cu.locate(" + LocQ(sel, "", 1, false, id, true) + ");if(!L.ok)return L;el=L.el;"
                           : "el=document.activeElement;while(el&&el.shadowRoot&&el.shadowRoot.activeElement)el=el.shadowRoot.activeElement;" +
                             "if(!el||el===document.body||el===document.documentElement)return{ok:false,err:'ERR_NO_FOCUS',msg:'no focused editable element, pass -Sel or -Id'};") +
                "__cu.last=el;if(el.disabled)return{ok:false,err:'ERR_DISABLED',msg:'element is disabled'};" +
                "var tag=__cu.tag(el),ce=!!el.isContentEditable,mode=" + J.Q(method) + ";" +
                "var vis=__cu.visible(el),hid=!!document.hidden;" +
                "var editable=(tag==='input'||tag==='textarea'||tag==='select'||ce);" +
                "if(!editable){var inner=null;try{inner=el.querySelector('input,textarea,[contenteditable]:not([contenteditable=false])');}catch(e){}" +
                "if(inner){el=inner;__cu.last=el;tag=__cu.tag(el);ce=!!el.isContentEditable;vis=__cu.visible(el);}else return{ok:false,err:'ERR_NOT_EDITABLE',msg:'<'+tag+'> is not editable'};}" +
                "if(mode==='auto')mode=(tag==='select'||!vis||" + J.Q(text) + ".length>4000)?'value':'keys';if(mode==='value'&&ce)mode='keys';" +
                "if(mode==='value'){__cu.focusFor(el,false);var r=__cu.setValue(el," + J.Q(text) + "," + (append ? "true" : "false") + ");if(!r.ok)return r;" +
                "return{ok:true,tag:tag,mode:r.mode,val:r.val.slice(0,200),len:r.val.length,visible:vis,hidden:hid};}" +
                "var f=__cu.focusFor(el," + (append ? "false" : "true") + ");" +
                "return{ok:true,tag:tag,mode:'keys',ce:ce,focused:!!f,visible:vis,hidden:hid};})()";
            Dictionary<string, object> d;
            string er = LibEval(b, expr, 6000, out d);
            if (er != null) return er;
            if (d.ContainsKey("visible") && !BN(d["visible"]))
                d["warn"] = "target is not visible on the page (hidden or zero-size); a person could not type here - check web els for the visible field";
            // trusted input (insertText / Enter) needs a foreground tab; bring ours to the front if it slipped behind
            if (BN(d.ContainsKey("hidden") ? d["hidden"] : null) && (SN(d, "mode") == "keys" || enter))
            {
                if (EnsureVisible(b)) { d["hidden"] = false; d["activated"] = true; }
                else d["warn"] = (d.ContainsKey("warn") ? SN(d, "warn") + "; " : "") + NO_FRAMES + "; keys may be ignored";
                // re-focus after activation (some pages reset focus on visibilitychange)
                Dictionary<string, object> fd;
                LibEval(b, "(function(){var el=__cu.last;if(el)__cu.focusFor(el," + (append || SN(d, "mode") != "keys" ? "false" : "true") + ");return{ok:true};})()", 3000, out fd);
            }
            d.Remove("hidden");
            if (SN(d, "mode") == "keys")
            {
                if (text.Length > 0)
                {
                    string res;
                    string e = CoreCallEx(b, true, "Input.insertText", "{\"text\":" + J.Q(text) + "}", 8000, false, out res);
                    if (e != null) return e;
                }
                else if (!append)
                {
                    string e = InputCall(b, "Input.dispatchKeyEvent", SendKeyJson("keyDown", 46, "Delete", "Delete", 0, null));
                    if (e != null) return e;
                    InputCall(b, "Input.dispatchKeyEvent", SendKeyJson("keyUp", 46, "Delete", "Delete", 0, null));
                }
                d["len"] = text.Length;
            }
            if (verify)
            {
                Dictionary<string, object> vd;
                string ve = LibEval(b, "(function(){var el=__cu.last;if(!el)return{ok:false,err:'ERR_STALE',msg:'lost element'};return{ok:true,value:__cu.readValue(el)};})()", 3000, out vd);
                if (ve == null)
                {
                    string got = SN(vd, "value");
                    string want = SN(d, "mode") == "select" ? SN(d, "val") : text;
                    bool same = append ? got.EndsWith(want, StringComparison.Ordinal) : (got == want || Web.NormWs(got) == Web.NormWs(want));
                    d["verify"] = same;
                    if (!same) d["got"] = Trunc(got, 200);
                }
                else { d["verify"] = false; d["verifyErr"] = Trunc(ve, 200); }
            }
            if (enter)
            {
                string ke = EnterKey(b);
                if (ke != null) return ke;
                d["enter"] = true;
                VerifyEnter(b, d);   // Bing-style swallowed Enter -> clean the value + click the form's submit button
            }
            d["ms"] = sw.ElapsedMilliseconds;
            return _ser.Serialize(d);
        }

        public static string NormWs(string s) { return Regex.Replace(s ?? "", "\\s+", " ").Trim(); }

        class KeyDef
        {
            public int vk;
            public string key;
            public string code;
            public string text;
            public KeyDef(int v, string k, string c, string t) { vk = v; key = k; code = c; text = t; }
        }

        static Dictionary<string, KeyDef> _keyMap;
        static Dictionary<string, KeyDef> KeyMap()
        {
            if (_keyMap != null) return _keyMap;
            Dictionary<string, KeyDef> m = new Dictionary<string, KeyDef>();
            m["enter"] = new KeyDef(13, "Enter", "Enter", "\r"); m["return"] = m["enter"];
            m["tab"] = new KeyDef(9, "Tab", "Tab", null);
            m["esc"] = new KeyDef(27, "Escape", "Escape", null); m["escape"] = m["esc"];
            m["space"] = new KeyDef(32, " ", "Space", " ");
            m["backspace"] = new KeyDef(8, "Backspace", "Backspace", null);
            m["delete"] = new KeyDef(46, "Delete", "Delete", null); m["del"] = m["delete"];
            m["insert"] = new KeyDef(45, "Insert", "Insert", null);
            m["home"] = new KeyDef(36, "Home", "Home", null); m["end"] = new KeyDef(35, "End", "End", null);
            m["pageup"] = new KeyDef(33, "PageUp", "PageUp", null); m["pgup"] = m["pageup"];
            m["pagedown"] = new KeyDef(34, "PageDown", "PageDown", null); m["pgdn"] = m["pagedown"];
            m["up"] = new KeyDef(38, "ArrowUp", "ArrowUp", null); m["down"] = new KeyDef(40, "ArrowDown", "ArrowDown", null);
            m["left"] = new KeyDef(37, "ArrowLeft", "ArrowLeft", null); m["right"] = new KeyDef(39, "ArrowRight", "ArrowRight", null);
            m["arrowup"] = m["up"]; m["arrowdown"] = m["down"]; m["arrowleft"] = m["left"]; m["arrowright"] = m["right"];
            for (int i = 1; i <= 12; i++) m["f" + i] = new KeyDef(111 + i, "F" + i, "F" + i, null);
            m["minus"] = new KeyDef(189, "-", "Minus", "-"); m["plus"] = new KeyDef(187, "+", "Equal", "+"); m["equal"] = new KeyDef(187, "=", "Equal", "=");
            m["comma"] = new KeyDef(188, ",", "Comma", ","); m["period"] = new KeyDef(190, ".", "Period", "."); m["slash"] = new KeyDef(191, "/", "Slash", "/");
            _keyMap = m;
            return m;
        }

        static string SendKeyJson(string type, int vk, string key, string code, int mods, string text)
        {
            StringBuilder sb = new StringBuilder(200);
            sb.Append("{\"type\":\"").Append(type).Append('"');
            if (vk != 0) { sb.Append(",\"windowsVirtualKeyCode\":").Append(vk).Append(",\"nativeVirtualKeyCode\":").Append(vk); }
            if (key != null) sb.Append(",\"key\":").Append(J.Q(key));
            if (code != null) sb.Append(",\"code\":").Append(J.Q(code));
            if (mods != 0) sb.Append(",\"modifiers\":").Append(mods);
            if (text != null) { sb.Append(",\"text\":").Append(J.Q(text)).Append(",\"unmodifiedText\":").Append(J.Q(text)); }
            sb.Append('}');
            return sb.ToString();
        }

        static string OneKey(string b, string tok)
        {
            tok = tok.Trim();
            if (tok == "") return null;
            bool nonAscii = false;
            foreach (char ch in tok) if (ch > 127) { nonAscii = true; break; }
            if (nonAscii)
            {
                string res;
                // no timeout resend (same policy as Runtime.evaluate): a late ack means the text may already be in
                return CoreCallEx(b, true, "Input.insertText", "{\"text\":" + J.Q(tok) + "}", 4000, false, out res);
            }
            string[] parts = tok.Split('+');
            int mods = 0;
            string main = parts[parts.Length - 1];
            if (main == "" && tok.EndsWith("+")) main = "+";
            for (int i = 0; i < parts.Length - 1; i++)
            {
                string p = parts[i].ToLowerInvariant();
                if (p == "") continue;
                if (p == "ctrl" || p == "control") mods |= 2;
                else if (p == "shift") mods |= 8;
                else if (p == "alt") mods |= 1;
                else if (p == "meta" || p == "win" || p == "cmd") mods |= 4;
                else return J.Err("ERR_KEYS", "unknown modifier: " + parts[i]);
            }
            Dictionary<string, KeyDef> map = KeyMap();
            KeyDef kd;
            string ml = main.ToLowerInvariant();
            if (map.TryGetValue(ml, out kd))
            {
                // ctrl/alt combos never insert text
                string txt = (mods & 3) != 0 ? null : kd.text;
                string e = InputCall(b, "Input.dispatchKeyEvent", SendKeyJson(txt != null ? "keyDown" : "rawKeyDown", kd.vk, kd.key, kd.code, mods, txt));
                if (e != null) return e;
                return InputCall(b, "Input.dispatchKeyEvent", SendKeyJson("keyUp", kd.vk, kd.key, kd.code, mods, null));
            }
            if (main.Length == 1)
            {
                char ch = main[0];
                string key = char.ToString(ch);
                string code = "";
                int vk = 0;
                if (ch >= 'a' && ch <= 'z') { vk = ch - 32; code = "Key" + char.ToUpperInvariant(ch); }
                else if (ch >= 'A' && ch <= 'Z') { vk = ch; code = "Key" + ch; mods |= 8; }
                else if (ch >= '0' && ch <= '9') { vk = ch; code = "Digit" + ch; }
                else if (ch == ' ') { vk = 32; code = "Space"; key = " "; }
                else
                {
                    // US-layout punctuation: sites bind shortcuts to key/code (GitHub "/", Gmail "?"), so give them real codes
                    int pi = "-=[]\\;',./`".IndexOf(ch);
                    int[] pvk = { 189, 187, 219, 221, 220, 186, 222, 188, 190, 191, 192 };
                    string[] pcode = { "Minus", "Equal", "BracketLeft", "BracketRight", "Backslash", "Semicolon", "Quote", "Comma", "Period", "Slash", "Backquote" };
                    int si = "_+{}|:\"<>?~".IndexOf(ch);
                    if (pi >= 0) { vk = pvk[pi]; code = pcode[pi]; }
                    else if (si >= 0) { vk = pvk[si]; code = pcode[si]; mods |= 8; }
                    else if ("!@#$%^&*()".IndexOf(ch) >= 0) { vk = '0' + ((")!@#$%^&*(".IndexOf(ch)) % 10); code = "Digit" + (char)vk; mods |= 8; }
                    else { vk = 0; code = ""; }
                }
                // with ctrl/alt held the key is a shortcut (ctrl+a, ctrl+c ...): no text is inserted
                string txt = (mods & 3) != 0 ? null : char.ToString(ch);
                // shortcuts need the Windows "commands" mapping too so editing shortcuts work in every element
                string extra = "";
                if ((mods & 2) != 0 && vk != 0)
                {
                    string cmd = null;
                    if (ch == 'a' || ch == 'A') cmd = "selectAll"; else if (ch == 'c' || ch == 'C') cmd = "copy"; else if (ch == 'v' || ch == 'V') cmd = "paste";
                    else if (ch == 'x' || ch == 'X') cmd = "cut"; else if (ch == 'z' || ch == 'Z') cmd = (mods & 8) != 0 ? "redo" : "undo"; else if (ch == 'y' || ch == 'Y') cmd = "redo";
                    if (cmd != null) extra = ",\"commands\":[\"" + cmd + "\"]";
                }
                string down = SendKeyJson(txt != null ? "keyDown" : "rawKeyDown", vk, key, code, mods, txt);
                if (extra != "") down = down.Substring(0, down.Length - 1) + extra + "}";
                string e = InputCall(b, "Input.dispatchKeyEvent", down);
                if (e != null) return e;
                return InputCall(b, "Input.dispatchKeyEvent", SendKeyJson("keyUp", vk, key, code, mods, null));
            }
            // unknown multi-char token: insert as text (no timeout resend - see above)
            string r2;
            return CoreCallEx(b, true, "Input.insertText", "{\"text\":" + J.Q(tok) + "}", 4000, false, out r2);
        }

        // optional -Sel/-Id focuses the target first, so the keys land where intended
        public static string Keys(string b, string seq, int repeat, string sel, int id)
        {
            b = Norm(b);
            if (b == "") return J.Err("ERR_ARGS", "-Browser must be edge or chrome");
            if (string.IsNullOrEmpty(seq) || seq.Trim() == "") return J.Err("ERR_ARGS", "web keys needs -Keys");
            if (repeat < 1) repeat = 1;
            Stopwatch sw = Stopwatch.StartNew();
            _lateAck = false;
            if (!string.IsNullOrEmpty(sel) || id > 0)
            {
                Dictionary<string, object> fd;
                string fe = LibEval(b, "(function(){var L=__cu.locate(" + LocQ(sel, "", 1, false, id, true) + ");if(!L.ok)return L;__cu.focusFor(L.el,false);return{ok:true};})()", 5000, out fd);
                if (fe != null) return fe;
            }
            bool visibleNow = EnsureVisible(b);
            string[] toks = seq.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            int n = 0;
            for (int rp = 0; rp < repeat; rp++)
            {
                foreach (string t in toks)
                {
                    string e = OneKey(b, t);
                    if (e != null && e.StartsWith("{\"ok\":false")) return e;
                    n++;
                    if (toks.Length > 1 || repeat > 1) Thread.Sleep(5);
                }
            }
            return "{\"ok\":true,\"keys\":" + n.ToString(CultureInfo.InvariantCulture) + (visibleNow ? "" : ",\"warn\":" + J.Q(NO_FRAMES + "; keys may be ignored")) +
                   (_lateAck ? ",\"ackLate\":true" : "") + ",\"ms\":" + sw.ElapsedMilliseconds + "}";
        }

        // ---------------------------------------------------------------- read / eval / info / els
        static string TextExpr(string sel, int max)
        {
            int cap = max > 0 ? max : 20000;   // token budget: 100k chars of page text is ~30k tokens in one reply
            return "(()=>{" +
                "try{" +
                "var el=" + (string.IsNullOrEmpty(sel) ? "document.body" : "document.querySelector(" + J.Q(sel) + ")") + ";" +
                "if(!el)return{ok:false,err:'ERR_NOT_FOUND',msg:" + (string.IsNullOrEmpty(sel) ? "'document has no body yet (still loading?)'" : "'selector not found: '+" + J.Q(sel)) + "};" +
                "var t=el.innerText||el.textContent||'';var tr=false;var full=t.length;" +
                "if(t.length>" + cap.ToString(CultureInfo.InvariantCulture) + "){t=t.slice(0," + cap.ToString(CultureInfo.InvariantCulture) + ");tr=true;}" +
                "return{ok:true,url:String(location.href),title:String(document.title),text:t,len:full,chars:t.length,truncated:tr};" +
                "}catch(e){return{ok:false,err:'ERR_SEL',msg:String(e.message)}}})()";
        }

        public static string Text(string b, string sel, int max)
        {
            b = Norm(b);
            if (b == "") return J.Err("ERR_ARGS", "-Browser must be edge or chrome");
            Dictionary<string, object> d = null; string unused;
            string er = null, oe = null;
            Stopwatch sw = Stopwatch.StartNew();
            // right after a navigation the new document may not have a <body> yet: give it up to a second
            while (true)
            {
                er = EvalObj(b, TextExpr(sel, max), 8000, out d, out unused);
                oe = er == null ? ObjErr(d) : null;
                if (er == null && oe == null) break;
                if (sw.ElapsedMilliseconds > 1000) break;
                Thread.Sleep(100);
            }
            if (er != null) return WithWall(b, er);
            if (oe != null) return WithWall(b, oe);
            AddWall(b, d);
            return _ser.Serialize(d);
        }

        public static string Value(string b, string sel, int id)
        {
            b = Norm(b);
            if (b == "") return J.Err("ERR_ARGS", "-Browser must be edge or chrome");
            Dictionary<string, object> d;
            string expr = (string.IsNullOrEmpty(sel) && id <= 0)
                ? "(function(){var el=document.activeElement||document.body;return{ok:true,value:__cu.readValue(el)};})()"
                : "(function(){var L=__cu.locate(" + LocQ(sel, "", 1, false, id, true) + ");if(!L.ok)return L;return{ok:true,value:__cu.readValue(L.el)};})()";
            string er = LibEval(b, expr, 4000, out d);
            if (er != null) return er;
            return "{\"ok\":true,\"value\":" + J.Q(SN(d, "value")) + "}";
        }

        public static string Info(string b)
        {
            b = Norm(b);
            if (b == "") return J.Err("ERR_ARGS", "-Browser must be edge or chrome");
            // LibEval (not EvalObj): the wall check needs __cu, and after a site-initiated redirect this may be
            // the FIRST command in the new document - the lib is auto-injected here
            Dictionary<string, object> d;
            string er = LibEval(b, "(()=>({ok:true,url:String(location.href),title:String(document.title)," +
                "ready:document.readyState==='complete',w:innerWidth,h:innerHeight,dpr:devicePixelRatio," +
                "x:Math.round(scrollX),y:Math.round(scrollY),wall:String(__cu.wall()||'')," +
                "dh:Math.max(document.documentElement.scrollHeight,document.body?document.body.scrollHeight:0)}))()", 4000, out d);
            if (er != null) return er;
            string w = SN(d, "wall");
            d.Remove("wall");
            if (w != "") { d["wall"] = w; if (!d.ContainsKey("hint")) d["hint"] = WallHintFor(w); }   // info: orient before acting
            return _ser.Serialize(d);
        }

        public static string Eval(string b, string js, int timeoutMs)
        {
            b = Norm(b);
            if (b == "") return J.Err("ERR_ARGS", "-Browser must be edge or chrome");
            if (string.IsNullOrEmpty(js)) return J.Err("ERR_ARGS", "web eval needs -Js");
            Stopwatch sw = Stopwatch.StartNew();
            object v; string ej;
            string full = WithLib(js);    // __cu is available to user scripts too
            EvalTo(b, full ?? js, timeoutMs, out v, out ej);
            if (ej != null) return ej;
            Dictionary<string, object> d = v as Dictionary<string, object>;
            if (d != null && d.ContainsKey("ok")) { d["ms"] = sw.ElapsedMilliseconds; return _ser.Serialize(d); }
            string ty;
            if (v == null) ty = "undefined";
            else if (v is string) ty = "string";
            else if (v is bool) ty = "boolean";
            else if (v is int || v is long || v is double || v is decimal || v is float) ty = "number";
            else if (v is object[] || v is Dictionary<string, object>) ty = "object";
            else ty = v.GetType().Name.ToLowerInvariant();
            return "{\"ok\":true,\"type\":" + J.Q(ty) + ",\"value\":" + (v == null ? "null" : _ser.Serialize(v)) +
                   ",\"ms\":" + sw.ElapsedMilliseconds + "}";
        }

        // numbered list of interactive elements: [i,kind,text,x,y,w,h] in viewport CSS px; ids feed click/type/hover -Id
        public static string Els(string b, string sel, bool all, int max)
        {
            b = Norm(b);
            if (b == "") return J.Err("ERR_ARGS", "-Browser must be edge or chrome");
            if (max <= 0) max = 300;
            Stopwatch sw = Stopwatch.StartNew();
            Dictionary<string, object> d;
            string er = LibEval(b, "(function(){var l=__cu.collect(" + J.Q(sel ?? "") + "," + (all ? "true" : "false") + "," + max + ",false);" +
                "if(!l)return{ok:false,err:'ERR_NOT_FOUND',msg:'scope selector not found: '+" + J.Q(sel ?? "") + "};" +
                "return{ok:true,count:l.length,url:String(location.href),elements:l};})()", 8000, out d);
            if (er != null) return WithWall(b, er);
            d["ms"] = sw.ElapsedMilliseconds;
            AddWall(b, d);        // a challenge page has almost no elements: say why instead of "count 2"
            return _ser.Serialize(d);
        }

        // ---------------------------------------------------------------- wait
        // one in-page waiter per slice (MutationObserver + 100 ms poll); the loop only exists to survive navigations
        public static string Wait(string b, string sel, string text, string urlSub, bool ready, int timeoutMs, int fixedMs, int stableMs, string gone)
        {
            b = Norm(b);
            if (b == "") return J.Err("ERR_ARGS", "-Browser must be edge or chrome");
            bool none = string.IsNullOrEmpty(sel) && string.IsNullOrEmpty(text) && string.IsNullOrEmpty(urlSub) && !ready && stableMs <= 0 && string.IsNullOrEmpty(gone);
            if (none)
            {
                if (fixedMs < 0) fixedMs = 0;
                Stopwatch fs = Stopwatch.StartNew();
                Thread.Sleep(fixedMs);
                return "{\"ok\":true,\"ms\":" + fs.ElapsedMilliseconds + "}";
            }
            if (timeoutMs < 100) timeoutMs = 100;
            Stopwatch sw = Stopwatch.StartNew();
            string q = "{sel:" + J.Q(sel ?? "") + ",text:" + J.Q(text ?? "") + ",url:" + J.Q(urlSub ?? "") + ",ready:" + (ready ? "true" : "false") +
                       ",stable:" + (stableMs > 0 ? stableMs : 0).ToString(CultureInfo.InvariantCulture) + ",gone:" + J.Q(gone ?? "") + "}";
            int transient = 0;
            while (true)
            {
                int left = timeoutMs - (int)sw.ElapsedMilliseconds;
                if (left <= 0) return WithWall(b, J.Err("ERR_TIMEOUT", "condition not met within " + timeoutMs + "ms"));
                // a navigation destroys the document our waiter lives in and its promise never settles; keep the
                // slices short when a navigation is what we are waiting for (-Url / -Ready) so that costs little
                int slice = Math.Min(left, (!string.IsNullOrEmpty(urlSub) || ready) ? 600 : 2500);
                Dictionary<string, object> d;
                string full = WithLib("__cu.waitFor(__cu.condition(" + q + ")," + slice.ToString(CultureInfo.InvariantCulture) + ")");
                if (full == null) return J.Err("ERR_LOAD", _libErr ?? "web-lib.js missing");
                object v; string ej;
                EvalTo(b, full, slice + 1500, out v, out ej);
                d = v as Dictionary<string, object>;
                if (ej == null && d != null)
                {
                    if (BN(d.ContainsKey("ok") ? d["ok"] : null))
                        return "{\"ok\":true,\"found\":true,\"ms\":" + sw.ElapsedMilliseconds + "}";
                    if (SN(d, "err") != "") return J.Err(SN(d, "err"), SN(d, "msg"));
                    continue;   // slice timed out inside the page: loop for the remaining time
                }
                // transient errors during navigation (context destroyed / session gone) are retried
                if (ej != null && !ej.Contains("ERR_CDP") && !ej.Contains("ERR_WS") && !ej.Contains("ERR_TIMEOUT") && !ej.Contains("ERR_JS"))
                    return ej;
                if (++transient > 200) return WithWall(b, ej ?? J.Err("ERR_TIMEOUT", "wait gave up"));
                Thread.Sleep(35);
            }
        }

        // ---------------------------------------------------------------- screenshot
        // the browser scales the capture itself (clip.scale): one JPEG, no decode/re-encode round trip
        public static string Shot(string b, string outFile, bool full, int quality, int maxSide, bool marks, string markSel)
        {
            b = Norm(b);
            if (b == "") return J.Err("ERR_ARGS", "-Browser must be edge or chrome");
            if (quality < 10) quality = 10;
            if (quality > 100) quality = 100;
            if (string.IsNullOrEmpty(outFile)) outFile = Path.Combine(StateDir(), "web.jpg");
            Stopwatch sw = Stopwatch.StartNew();
            string markExpr = marks ? "var l=__cu.collect(" + J.Q(markSel ?? "") + ",false,300," + (full ? "false" : "true") + ");if(!l)l=[];var mk=__cu.mark(true);" : "var l=null,mk=0;";
            Dictionary<string, object> vd;
            string er = LibEval(b, "(function(){" + markExpr + "return{ok:true,sx:Math.round(scrollX),sy:Math.round(scrollY),iw:innerWidth,ih:innerHeight,dpr:devicePixelRatio," +
                   "dw:Math.max(document.documentElement.scrollWidth,document.body?document.body.scrollWidth:0)," +
                   "dh:Math.max(document.documentElement.scrollHeight,document.body?document.body.scrollHeight:0),els:l,marks:mk};})()", 8000, out vd);
            if (er != null) return er;
            double sx = DN(vd, "sx"), sy = DN(vd, "sy"), iw = DN(vd, "iw"), ih = DN(vd, "ih"), dw = DN(vd, "dw"), dh = DN(vd, "dh"), dpr = DN(vd, "dpr");
            if (dpr <= 0) dpr = 1;
            double cssW = full ? Math.Max(dw, iw) : iw, cssH = full ? Math.Max(dh, ih) : ih;
            if (cssW <= 0 || cssH <= 0) { cssW = 1024; cssH = 768; }
            if (full && cssH > 20000) cssH = 20000;   // keep the capture sane on endless pages
            // target image px per CSS px: native (dpr) unless the longest side would exceed maxSide.
            // clip.scale is applied on top of the device scale factor, so pass scale/dpr.
            double scale = dpr;
            if (maxSide > 0 && Math.Max(cssW, cssH) * scale > maxSide) scale = maxSide / Math.Max(cssW, cssH);
            string clip = "{\"x\":" + J.F(full ? 0 : sx) + ",\"y\":" + J.F(full ? 0 : sy) + ",\"width\":" + J.F(cssW) + ",\"height\":" + J.F(cssH) + ",\"scale\":" + J.F(scale / dpr) + "}";
            string p = "{\"format\":\"jpeg\",\"quality\":" + quality.ToString(CultureInfo.InvariantCulture) +
                       ",\"fromSurface\":true,\"clip\":" + clip + (full ? ",\"captureBeyondViewport\":true" : "") + "}";
            string res;
            string e = CoreCall(b, true, "Page.captureScreenshot", p, 30000, out res);
            if (marks) { Dictionary<string, object> unused; LibEval(b, "(function(){__cu.mark(false);return{ok:true};})()", 3000, out unused); }
            if (e != null) return e;
            Dictionary<string, object> rd = ParseD(res);
            string b64 = rd == null ? "" : SN(rd, "data");
            if (b64 == "") return J.Err("ERR_SHOT", "no screenshot data");
            byte[] raw;
            try { raw = Convert.FromBase64String(b64); }
            catch (Exception ex) { return J.Err("ERR_SHOT", ex.Message); }
            string dir = Path.GetDirectoryName(outFile);
            if (!string.IsNullOrEmpty(dir)) { try { Directory.CreateDirectory(dir); } catch { } }
            int imgW = 0, imgH = 0;
            try
            {
                File.WriteAllBytes(outFile, raw);
                // JPEG SOF0/SOF2 header: the real pixel size without decoding the image
                for (int i = 2; i + 9 < raw.Length; )
                {
                    if (raw[i] != 0xFF) { i++; continue; }
                    int mk = raw[i + 1];
                    if (mk == 0xD8 || (mk >= 0xD0 && mk <= 0xD7) || mk == 0x01 || mk == 0xFF) { i += 2; continue; }
                    int len = (raw[i + 2] << 8) | raw[i + 3];
                    if (mk == 0xC0 || mk == 0xC1 || mk == 0xC2) { imgH = (raw[i + 5] << 8) | raw[i + 6]; imgW = (raw[i + 7] << 8) | raw[i + 8]; break; }
                    i += 2 + len;
                }
            }
            catch (Exception ex) { return J.Err("ERR_SHOT", ex.Message); }
            if (imgW <= 0) { imgW = (int)Math.Round(cssW * scale); imgH = (int)Math.Round(cssH * scale); }
            double sImg = cssW > 0 ? (double)imgW / cssW : scale;   // image px -> CSS px
            try
            {
                string fj = "{\"mode\":\"page\",\"s\":" + J.F(sImg) + ",\"w\":" + imgW + ",\"h\":" + imgH +
                    ",\"iw\":" + J.F(iw) + ",\"ih\":" + J.F(ih) + ",\"dw\":" + J.F(dw) + ",\"dh\":" + J.F(dh) +
                    ",\"sx\":" + J.F(sx) + ",\"sy\":" + J.F(sy) + ",\"dpr\":" + J.F(dpr) + ",\"full\":" + (full ? "true" : "false") +
                    ",\"img\":" + J.Q(outFile) + ",\"ts\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}";
                File.WriteAllText(LastWeb(), fj, Encoding.UTF8);
            }
            catch { }
            StringBuilder sb = new StringBuilder(512);
            sb.Append("{\"ok\":true,\"file\":").Append(J.Q(outFile)).Append(",\"w\":").Append(imgW).Append(",\"h\":").Append(imgH)
              .Append(",\"scale\":").Append(J.F(sImg)).Append(",\"dpr\":").Append(J.F(dpr)).Append(",\"full\":").Append(full ? "true" : "false")
              .Append(",\"vw\":").Append(J.F(iw)).Append(",\"vh\":").Append(J.F(ih)).Append(",\"sx\":").Append(J.F(sx)).Append(",\"sy\":").Append(J.F(sy));
            if (marks)
            {
                object els; vd.TryGetValue("els", out els);
                sb.Append(",\"marks\":").Append((int)DN(vd, "marks")).Append(",\"elements\":").Append(els == null ? "[]" : _ser.Serialize(els));
            }
            sb.Append(",\"ms\":").Append(sw.ElapsedMilliseconds).Append('}');
            return sb.ToString();
        }
    }
}
