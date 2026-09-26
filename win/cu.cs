// cu.cs - Windows Computer-Use core (compiled once to a cached DLL by cu.ps1)
//   * Per-monitor DPI aware: every coordinate is PHYSICAL screen pixels (fixes offset clicks on scaled displays)
//   * Frame mapping: every snap writes a frame (origin + scale); clicks take image coords and map back exactly
//   * Background input goes to the deepest child window under the point / the focused child (not the top-level)
//   * Capture: screen copy when the window is visible, PrintWindow when occluded; DWM visible bounds (no shadow border)
//   * Settle/diff: after an action, wait until the UI stops changing and report how much changed
// C# 5 / CodeDom compatible (PowerShell 5.1 Add-Type): no $"", no ?., no out var, no expression bodies.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace CU
{
    public static class N
    {
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Explicit)] public struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public INPUTUNION u; }
        [StructLayout(LayoutKind.Sequential)] public struct GUITHREADINFO
        {
            public int cbSize; public uint flags; public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret; public RECT rcCaret;
        }
        public delegate bool EnumProc(IntPtr h, IntPtr l);

        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool f);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr h, ref POINT p);
        [DllImport("user32.dll")] public static extern IntPtr ChildWindowFromPointEx(IntPtr h, POINT p, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll", EntryPoint = "PostMessageW")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")] public static extern IntPtr SendMessageTimeout(IntPtr h, uint m, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessageTimeoutS(IntPtr h, uint m, IntPtr w, string l, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
        [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
        [DllImport("user32.dll")] public static extern IntPtr GetWindowDpiAwarenessContext(IntPtr h);
        [DllImport("user32.dll")] public static extern int GetAwarenessFromDpiAwarenessContext(IntPtr c);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr mon, int type, out uint x, out uint y);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
        [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] public static extern int DwmGetWindowAttributeInt(IntPtr h, int attr, out int v, int size);
        [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint type);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern short VkKeyScan(char c);
        [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint tid, ref GUITHREADINFO info);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern int SetStretchBltMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll")] public static extern bool StretchBlt(IntPtr d, int dx, int dy, int dw, int dh, IntPtr s, int sx, int sy, int sw, int sh, uint rop);
    }

    public static class J
    {
        public static string Q(string s)
        {
            if (s == null) return "null";
            StringBuilder sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u" + ((int)c).ToString("x4")); else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
        public static string F(double d) { return d.ToString("0.####", CultureInfo.InvariantCulture); }
        public static string B(bool b) { return b ? "true" : "false"; }
        public static string Hex(IntPtr h) { return "0x" + h.ToInt64().ToString("X"); }
        public static string Err(string code, string msg)
        {
            return "{\"ok\":false,\"err\":" + Q(code) + (msg != null ? ",\"msg\":" + Q(msg) : "") + "}";
        }
    }

    // A frame = one screenshot + how its pixels map to the physical screen.
    //   screen_x = ox + (img_x + 0.5) / s     img_x = (screen_x - ox) * s
    public class Frame
    {
        public string img = "", method = "", title = "";
        public int w, h, pid, bl, bt, br, bb;
        public double ox, oy, s = 1;
        public long hwnd, ts;

        public string ToJson()
        {
            return "{\"img\":" + J.Q(img) + ",\"w\":" + w + ",\"h\":" + h + ",\"ox\":" + J.F(ox) + ",\"oy\":" + J.F(oy) +
                   ",\"s\":" + s.ToString("0.########", CultureInfo.InvariantCulture) + ",\"hwnd\":" + hwnd + ",\"pid\":" + pid +
                   ",\"bounds\":[" + bl + "," + bt + "," + br + "," + bb + "],\"method\":" + J.Q(method) +
                   ",\"title\":" + J.Q(title) + ",\"ts\":" + ts + "}";
        }

        public static Frame Load(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try { return Parse(File.ReadAllText(path, Encoding.UTF8)); } catch { return null; }
        }

        public static Frame Parse(string j)
        {
            Frame f = new Frame();
            f.w = (int)Num(j, "w"); f.h = (int)Num(j, "h");
            f.ox = Num(j, "ox"); f.oy = Num(j, "oy"); f.s = Num(j, "s"); if (f.s <= 0) f.s = 1;
            f.hwnd = (long)Num(j, "hwnd"); f.pid = (int)Num(j, "pid"); f.ts = (long)Num(j, "ts");
            Match m = Regex.Match(j, "\"bounds\":\\[(-?\\d+),(-?\\d+),(-?\\d+),(-?\\d+)\\]");
            if (m.Success)
            {
                f.bl = int.Parse(m.Groups[1].Value); f.bt = int.Parse(m.Groups[2].Value);
                f.br = int.Parse(m.Groups[3].Value); f.bb = int.Parse(m.Groups[4].Value);
            }
            f.img = Str(j, "img"); f.method = Str(j, "method"); f.title = Str(j, "title");
            return f;
        }
        static double Num(string j, string k)
        {
            Match m = Regex.Match(j, "\"" + k + "\":(-?[0-9][0-9.eE+-]*)");
            return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        }
        static string Str(string j, string k)
        {
            Match m = Regex.Match(j, "\"" + k + "\":\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success) return "";
            try { return Regex.Unescape(m.Groups[1].Value); } catch { return m.Groups[1].Value; }
        }

        public int[] ToScreen(double x, double y)
        {
            return new int[] { (int)Math.Floor(ox + (x + 0.5) / s), (int)Math.Floor(oy + (y + 0.5) / s) };
        }
        public double[] ToImage(double sx, double sy)
        {
            return new double[] { (sx - ox) * s, (sy - oy) * s };
        }
        public int[] RegionToScreen(double x, double y, double rw, double rh)
        {
            return new int[] { (int)Math.Floor(ox + x / s), (int)Math.Floor(oy + y / s),
                               (int)Math.Ceiling(ox + (x + rw) / s), (int)Math.Ceiling(oy + (y + rh) / s) };
        }
        public bool Inside(double x, double y) { return x >= 0 && y >= 0 && x < w && y < h; }

        // "" when the frame is still valid for its window, else an error code
        public string Check(bool force)
        {
            if (hwnd == 0) return "";
            Core.A();
            IntPtr hw = new IntPtr(hwnd);
            if (!N.IsWindow(hw)) return "ERR_WINDOW_GONE";
            if (N.IsIconic(hw)) return "ERR_MINIMIZED";
            if (force) return "";
            N.RECT r = Core.Bounds(hw);
            if (Math.Abs(r.L - bl) > 2 || Math.Abs(r.T - bt) > 2 || Math.Abs(r.R - br) > 2 || Math.Abs(r.B - bb) > 2)
                return "ERR_STALE_FRAME";
            return "";
        }
    }

    public static class Core
    {
        static readonly IntPtr PMV2 = new IntPtr(-4);

        public static void Init()
        {
            try { N.SetProcessDpiAwarenessContext(PMV2); } catch { }
            A();
        }
        // (re)assert per-monitor-v2 awareness on the calling thread; cheap, called by every entry point
        public static void A()
        {
            try { N.SetThreadDpiAwarenessContext(PMV2); }
            catch { try { N.SetProcessDPIAware(); } catch { } }
        }

        // ---------------------------------------------------------------- windows
        public class W
        {
            public IntPtr h; public string title = "", cls = "", proc = ""; public uint pid; public N.RECT r; public bool min, fg;
        }

        public static string Title(IntPtr h)
        {
            int len = N.GetWindowTextLength(h);
            StringBuilder sb = new StringBuilder(len + 2);
            N.GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }
        public static string Cls(IntPtr h)
        {
            StringBuilder sb = new StringBuilder(256);
            N.GetClassName(h, sb, 256);
            return sb.ToString();
        }

        public static N.RECT Bounds(IntPtr h)
        {
            N.RECT r;
            bool ok = false;
            try { ok = N.DwmGetWindowAttribute(h, 9, out r, Marshal.SizeOf(typeof(N.RECT))) == 0 && r.R > r.L; }
            catch { r = new N.RECT(); }
            if (!ok) N.GetWindowRect(h, out r);
            return r;
        }
        public static N.RECT Virt()
        {
            N.RECT r = new N.RECT();
            r.L = N.GetSystemMetrics(76); r.T = N.GetSystemMetrics(77);
            r.R = r.L + N.GetSystemMetrics(78); r.B = r.T + N.GetSystemMetrics(79);
            return r;
        }

        // pid -> process name, remembered for a minute: Process.GetProcessById costs 1-5 ms per window per call
        static readonly Dictionary<uint, KeyValuePair<string, int>> _procNames = new Dictionary<uint, KeyValuePair<string, int>>();
        public static string ProcName(uint pid)
        {
            int now = Environment.TickCount;
            lock (_procNames)
            {
                KeyValuePair<string, int> kv;
                if (_procNames.TryGetValue(pid, out kv) && unchecked(now - kv.Value) < 60000) return kv.Key;
            }
            string name;
            try { name = Process.GetProcessById((int)pid).ProcessName; } catch { name = "?"; }
            lock (_procNames) { _procNames[pid] = new KeyValuePair<string, int>(name, now); if (_procNames.Count > 2000) _procNames.Clear(); }
            return name;
        }

        // does a previously resolved window still fit the same -Title/-Proc? (2-3 API calls instead of a full EnumWindows)
        public static bool StillMatches(long hwnd, string title, string proc)
        {
            A();
            IntPtr h = new IntPtr(hwnd);
            if (h == IntPtr.Zero || !N.IsWindow(h) || !N.IsWindowVisible(h)) return false;
            int cloaked = 0;
            try { N.DwmGetWindowAttributeInt(h, 14, out cloaked, 4); } catch { }
            if (cloaked != 0) return false;
            if (!string.IsNullOrEmpty(title) && Title(h).IndexOf(title, StringComparison.OrdinalIgnoreCase) < 0) return false;
            string p = (proc ?? "").Trim();
            if (p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) p = p.Substring(0, p.Length - 4);
            if (p.Length > 0) { uint pid; N.GetWindowThreadProcessId(h, out pid); if (!string.Equals(ProcName(pid), p, StringComparison.OrdinalIgnoreCase)) return false; }
            return true;
        }

        public static List<W> ListWindows(bool all)
        {
            A();
            List<W> list = new List<W>();
            IntPtr fg = N.GetForegroundWindow();
            N.EnumWindows(delegate (IntPtr h, IntPtr l)
            {
                if (!N.IsWindowVisible(h)) return true;
                string t = Title(h);
                if (t.Length == 0 && !all) return true;
                int cloaked = 0;
                try { N.DwmGetWindowAttributeInt(h, 14, out cloaked, 4); } catch { }
                if (cloaked != 0 && !all) return true;
                W w = new W();
                w.h = h; w.title = t; w.cls = Cls(h);
                N.GetWindowThreadProcessId(h, out w.pid);
                w.proc = ProcName(w.pid); w.r = Bounds(h); w.min = N.IsIconic(h); w.fg = (h == fg);
                if (!all && !w.min && (w.r.R - w.r.L < 4 || w.r.B - w.r.T < 4)) return true;
                list.Add(w);
                return true;
            }, IntPtr.Zero);
            return list;
        }

        // best match: exact title > foreground > not minimized > larger. Returns JSON fragment with candidates.
        public static string Find(string title, string proc, out IntPtr best)
        {
            best = IntPtr.Zero;
            string p = (proc ?? "").Trim();
            if (p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) p = p.Substring(0, p.Length - 4);
            int bestScore = int.MinValue, n = 0;
            StringBuilder c = new StringBuilder();
            foreach (W w in ListWindows(false))
            {
                if (p.Length > 0 && !string.Equals(w.proc, p, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(title) && w.title.IndexOf(title, StringComparison.OrdinalIgnoreCase) < 0) continue;
                n++;
                int sc = 0;
                if (!string.IsNullOrEmpty(title) && string.Equals(w.title, title, StringComparison.OrdinalIgnoreCase)) sc += 1000;
                if (w.fg) sc += 100;
                if (!w.min) sc += 50;
                sc += (int)Math.Min(40L, ((long)(w.r.R - w.r.L) * (w.r.B - w.r.T)) / 100000L);
                if (sc > bestScore) { bestScore = sc; best = w.h; }
                if (n <= 8)
                {
                    if (c.Length > 0) c.Append(",");
                    c.Append("{\"hwnd\":" + J.Q(J.Hex(w.h)) + ",\"proc\":" + J.Q(w.proc) + ",\"title\":" + J.Q(w.title) + "}");
                }
            }
            return "\"matches\":" + n + (n > 1 ? ",\"candidates\":[" + c + "]" : "");
        }

        public static long RootAt(int x, int y)
        {
            A();
            IntPtr w = N.WindowFromPoint(new N.POINT(x, y));
            return w == IntPtr.Zero ? 0 : N.GetAncestor(w, 2).ToInt64();
        }

        public static string InfoJson(bool all, string filter)
        {
            A();
            N.RECT v = Virt();
            uint sdpi = 96;
            try { sdpi = N.GetDpiForSystem(); } catch { }
            N.POINT cur; N.GetCursorPos(out cur);
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"ok\":true,\"coords\":\"physical\",\"virtual_screen\":[" + v.L + "," + v.T + "," + (v.R - v.L) + "," + (v.B - v.T) + "]");
            sb.Append(",\"system_dpi\":" + sdpi + ",\"scale\":" + J.F(sdpi / 96.0) + ",\"cursor\":[" + cur.X + "," + cur.Y + "]");
            sb.Append(",\"foreground\":" + J.Q(J.Hex(N.GetForegroundWindow())) + ",\"windows\":[");
            bool first = true;
            foreach (W w in ListWindows(all))
            {
                if (!string.IsNullOrEmpty(filter) && w.title.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                    w.proc.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                uint dpi = 0;
                try { dpi = N.GetDpiForWindow(w.h); } catch { }
                if (!first) sb.Append(",");
                first = false;
                sb.Append("{\"hwnd\":" + J.Q(J.Hex(w.h)) + ",\"title\":" + J.Q(w.title) + ",\"proc\":" + J.Q(w.proc) + ",\"pid\":" + w.pid +
                          ",\"cls\":" + J.Q(w.cls) + ",\"rect\":[" + w.r.L + "," + w.r.T + "," + (w.r.R - w.r.L) + "," + (w.r.B - w.r.T) + "]" +
                          ",\"dpi\":" + dpi + ",\"min\":" + J.B(w.min) + ",\"fg\":" + J.B(w.fg) + "}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        // ---------------------------------------------------------------- capture
        static bool SameProcess(IntPtr a, IntPtr b)
        {
            uint p1, p2;
            N.GetWindowThreadProcessId(a, out p1);
            N.GetWindowThreadProcessId(b, out p2);
            return p1 == p2;
        }

        // true when the point is covered by the target (or one of its own popups/menus)
        public static bool OwnsPoint(IntPtr top, int x, int y)
        {
            IntPtr w = N.WindowFromPoint(new N.POINT(x, y));
            if (w == IntPtr.Zero) return false;
            IntPtr r = N.GetAncestor(w, 2);
            return r == top || SameProcess(r, top);
        }

        static bool Unoccluded(IntPtr h, N.RECT src)
        {
            int cloaked = 0;
            try { N.DwmGetWindowAttributeInt(h, 14, out cloaked, 4); } catch { }
            if (cloaked != 0) return false;
            N.RECT v = Virt();
            int w = src.R - src.L, hh = src.B - src.T;
            for (int i = 0; i < 5; i++)
                for (int j = 0; j < 5; j++)
                {
                    int x = src.L + w * (2 * i + 1) / 10, y = src.T + hh * (2 * j + 1) / 10;
                    if (x < v.L || y < v.T || x >= v.R || y >= v.B) return false;
                    if (!OwnsPoint(h, x, y)) return false;
                }
            return true;
        }

        static Bitmap GrabScreen(N.RECT r)
        {
            int w = r.R - r.L, h = r.B - r.T;
            if (w <= 0 || h <= 0) return null;
            Bitmap b = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(b))
                g.CopyFromScreen(r.L, r.T, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
            return b;
        }

        static Bitmap GrabPrint(IntPtr h, N.RECT wr)
        {
            int w = wr.R - wr.L, hh = wr.B - wr.T;
            if (w <= 0 || hh <= 0) return null;
            int lw = w, lh = hh;
            // DPI-unaware windows render at their logical size: capture at that size, then scale to physical
            try
            {
                IntPtr ctx = N.GetWindowDpiAwarenessContext(h);
                if (ctx != IntPtr.Zero)
                {
                    IntPtr old = N.SetThreadDpiAwarenessContext(ctx);
                    N.RECT lr;
                    if (N.GetWindowRect(h, out lr)) { lw = lr.R - lr.L; lh = lr.B - lr.T; }
                    N.SetThreadDpiAwarenessContext(old != IntPtr.Zero ? old : PMV2);
                }
            }
            catch { }
            A();
            if (lw <= 0 || lh <= 0) { lw = w; lh = hh; }
            Bitmap bmp = new Bitmap(lw, lh, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                IntPtr dc = g.GetHdc();
                bool ok = N.PrintWindow(h, dc, 2);
                g.ReleaseHdc(dc);
                if (!ok) { bmp.Dispose(); return null; }
            }
            if (lw != w || lh != hh) { Bitmap up = Resize(bmp, w, hh, true); bmp.Dispose(); return up; }
            return bmp;
        }

        // blank = (almost) one flat colour: black, white or the grey placeholder Chromium paints before its first frame
        // 24x24 sample grid read through LockBits (GetPixel costs ~10 us a call; this is ~0.1 ms for the whole grid)
        public static bool LooksBlank(Bitmap b)
        {
            if (b == null || b.Width < 4 || b.Height < 4) return true;
            Rectangle rc = new Rectangle(0, 0, b.Width, b.Height);
            BitmapData d = b.LockBits(rc, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                byte[] row = new byte[3];
                Func<int, int, int> px = delegate(int x, int y) { Marshal.Copy(new IntPtr(d.Scan0.ToInt64() + (long)y * d.Stride + x * 3), row, 0, 3); return (row[0] << 16) | (row[1] << 8) | row[2]; };
                int c0 = px(b.Width / 2, b.Height / 2), r0 = (c0 >> 16) & 255, g0 = (c0 >> 8) & 255, b0 = c0 & 255;
                int same = 0, total = 0;
                for (int i = 0; i < 24; i++)
                    for (int j = 0; j < 24; j++)
                    {
                        int c = px(b.Width * (2 * i + 1) / 48, b.Height * (2 * j + 1) / 48);
                        total++;
                        if (Math.Abs(((c >> 16) & 255) - r0) + Math.Abs(((c >> 8) & 255) - g0) + Math.Abs((c & 255) - b0) < 12) same++;
                    }
                return same * 1000 >= total * 995;
            }
            finally { b.UnlockBits(d); }
        }

        // src = physical screen rect to return (already clipped to the window bounds)
        public static Bitmap Grab(IntPtr h, N.RECT src, string method, out string used)
        {
            used = string.IsNullOrEmpty(method) ? "auto" : method.ToLowerInvariant();
            if (h == IntPtr.Zero) { used = "screen"; return GrabScreen(src); }
            if (used == "auto") used = (!N.IsIconic(h) && Unoccluded(h, src)) ? "screen" : "print";
            if (used == "screen") return GrabScreen(src);
            N.RECT wr;
            N.GetWindowRect(h, out wr);
            Bitmap full = GrabPrint(h, wr);
            if (full == null || LooksBlank(full))
            {
                // a screen copy is only the window's content when nothing covers it; otherwise keep the (blank)
                // PrintWindow result and let the caller flag it, never return some other window's pixels
                if (!N.IsIconic(h) && Unoccluded(h, src)) { if (full != null) full.Dispose(); used = "screen-fallback"; return GrabScreen(src); }
                if (full == null) return null;
            }
            Rectangle crop = new Rectangle(src.L - wr.L, src.T - wr.T, src.R - src.L, src.B - src.T);
            crop.Intersect(new Rectangle(0, 0, full.Width, full.Height));
            if (crop.Width <= 0 || crop.Height <= 0) { full.Dispose(); return null; }
            Bitmap c = full.Clone(crop, PixelFormat.Format24bppRgb);
            full.Dispose();
            return c;
        }

        public static Bitmap Resize(Bitmap src, int w, int h, bool hq)
        {
            Bitmap d = new Bitmap(Math.Max(1, w), Math.Max(1, h), PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(d))
            {
                // HighQualityBilinear prefilters when shrinking (no aliasing on small text) and is ~1.5-2x faster than bicubic
                g.InterpolationMode = hq ? InterpolationMode.HighQualityBilinear : InterpolationMode.Bilinear;
                g.PixelOffsetMode = hq ? PixelOffsetMode.HighQuality : PixelOffsetMode.HighSpeed;
                g.CompositingQuality = CompositingQuality.HighSpeed;
                using (ImageAttributes ia = new ImageAttributes())
                {
                    ia.SetWrapMode(WrapMode.TileFlipXY);
                    g.DrawImage(src, new Rectangle(0, 0, d.Width, d.Height), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
                }
            }
            return d;
        }

        // crisp pixel-exact enlargement for zoom views
        public static Bitmap Upscale(Bitmap src, int w, int h)
        {
            Bitmap d = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(d))
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel);
            }
            return d;
        }

        static ImageCodecInfo Codec(string mime)
        {
            foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders()) if (c.MimeType == mime) return c;
            return null;
        }
        public static void Save(Bitmap b, string path, int quality)
        {
            string d = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d);
            string e = Path.GetExtension(path).ToLowerInvariant();
            if (e == ".jpg" || e == ".jpeg")
            {
                using (EncoderParameters ep = new EncoderParameters(1))
                {
                    ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
                    b.Save(path, Codec("image/jpeg"), ep);
                }
            }
            else b.Save(path, ImageFormat.Png);
        }

        static void DrawGrid(Bitmap b, int step)
        {
            using (Graphics g = Graphics.FromImage(b))
            using (Pen p = new Pen(Color.FromArgb(120, 255, 0, 255), 1))
            using (Font f = new Font("Arial", 9, FontStyle.Bold, GraphicsUnit.Pixel))
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(170, 0, 0, 0)))
            using (SolidBrush fg = new SolidBrush(Color.Yellow))
            {
                for (int x = step; x < b.Width; x += step)
                {
                    g.DrawLine(p, x, 0, x, b.Height);
                    string s = x.ToString(); SizeF z = g.MeasureString(s, f);
                    g.FillRectangle(bg, x + 1, 0, z.Width, z.Height); g.DrawString(s, f, fg, x + 1, 0);
                }
                for (int y = step; y < b.Height; y += step)
                {
                    g.DrawLine(p, 0, y, b.Width, y);
                    string s = y.ToString(); SizeF z = g.MeasureString(s, f);
                    g.FillRectangle(bg, 0, y + 1, z.Width, z.Height); g.DrawString(s, f, fg, 0, y + 1);
                }
            }
        }

        // hasRegion: capture only [rl,rt,rr,rb] (physical screen coords). scale>0 forces the factor,
        // otherwise factor = min(cap, maxSide / longest side). Writes <out>.frame.json and lastFramePath.
        public static string Snap(long hwnd, bool hasRegion, int rl, int rt, int rr, int rb, int maxSide, int maxPixels, double scale, double cap,
                                  int grid, string outPath, int quality, string method, bool restore, string lastFramePath)
        {
            A();
            IntPtr h = new IntPtr(hwnd);
            N.RECT b;
            string title = "";
            uint pid = 0;
            if (h != IntPtr.Zero)
            {
                if (!N.IsWindow(h)) return J.Err("ERR_WINDOW_GONE", null);
                if (N.IsIconic(h))
                {
                    if (!restore) return J.Err("ERR_MINIMIZED", "window is minimized; pass -Restore (restores without activating)");
                    N.ShowWindow(h, 4);
                    Thread.Sleep(300);
                }
                b = Bounds(h);
                title = Title(h);
                N.GetWindowThreadProcessId(h, out pid);
            }
            else b = Virt();
            N.RECT src = b;
            if (hasRegion)
            {
                src.L = Math.Max(b.L, rl); src.T = Math.Max(b.T, rt); src.R = Math.Min(b.R, rr); src.B = Math.Min(b.B, rb);
                if (src.R - src.L < 2 || src.B - src.T < 2) return J.Err("ERR_REGION", "region is outside the window");
            }
            string used;
            Bitmap raw = Grab(h, src, method, out used);
            // a just-restored / just-uncovered Chromium window can paint a flat placeholder for a few hundred ms
            for (int tries = 0; h != IntPtr.Zero && raw != null && tries < 4 && LooksBlank(raw); tries++)
            {
                raw.Dispose();
                Thread.Sleep(250);
                raw = Grab(h, src, method, out used);
            }
            if (raw == null) return J.Err("ERR_CAPTURE", used);
            bool blank = h != IntPtr.Zero && LooksBlank(raw);
            int w = raw.Width, hh = raw.Height;
            double s = 1;
            if (scale > 0) s = scale;
            else
            {
                s = cap;
                if (maxSide > 0) s = Math.Min(s, (double)maxSide / Math.Max(w, hh));
                if (maxPixels > 0) s = Math.Min(s, Math.Sqrt((double)maxPixels / ((double)w * hh)));
            }
            Bitmap img = raw;
            int nw = (int)Math.Round(w * s), nh = (int)Math.Round(hh * s);
            if (nw != w || nh != hh) { img = s >= 2 ? Upscale(raw, nw, nh) : Resize(raw, nw, nh, true); raw.Dispose(); s = (double)nw / w; }
            if (grid > 0) DrawGrid(img, grid);
            try { Save(img, outPath, quality); }
            catch (Exception ex) { img.Dispose(); return J.Err("ERR_SAVE", ex.Message); }
            Frame f = new Frame();
            f.img = outPath; f.w = img.Width; f.h = img.Height; f.ox = src.L; f.oy = src.T; f.s = s;
            f.hwnd = hwnd; f.pid = (int)pid; f.bl = b.L; f.bt = b.T; f.br = b.R; f.bb = b.B;
            f.method = used + (blank ? "+blank" : ""); f.title = title; f.ts = DateTime.Now.Ticks / 10000;
            img.Dispose();
            string fj = f.ToJson();
            UTF8Encoding enc = new UTF8Encoding(false);
            try { File.WriteAllText(Path.ChangeExtension(outPath, ".frame.json"), fj, enc); } catch { }
            if (!string.IsNullOrEmpty(lastFramePath))
            {
                try
                {
                    string d = Path.GetDirectoryName(lastFramePath);
                    if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d);
                    File.WriteAllText(lastFramePath, fj, enc);
                }
                catch { }
            }
            if (blank) return fj.Substring(0, fj.Length - 1) + ",\"warn\":\"ERR_BLANK: window rendered a blank/flat image (Chromium/Electron apps stop painting while covered or just restored) - activate it (needs -Fg permission) or retry\"}";
            return fj;
        }

        // In-memory variant of Snap for OCR: same capture/scale rules, but returns the bitmap instead of writing
        // a PNG (a full-resolution window is 5-10 MB of PNG encode + decode per pass: 150-400 ms saved each time).
        public static Bitmap SnapMem(long hwnd, bool hasRegion, int rl, int rt, int rr, int rb, double scale, string method, bool restore,
                                     ref int ox, ref int oy, ref double sOut, ref string used, ref string err)
        {
            A();
            err = ""; used = "";
            IntPtr h = new IntPtr(hwnd);
            N.RECT b;
            if (h != IntPtr.Zero)
            {
                if (!N.IsWindow(h)) { err = "ERR_WINDOW_GONE"; return null; }
                if (N.IsIconic(h))
                {
                    if (!restore) { err = "ERR_MINIMIZED"; return null; }
                    N.ShowWindow(h, 4);
                    Thread.Sleep(300);
                }
                b = Bounds(h);
            }
            else b = Virt();
            N.RECT src = b;
            if (hasRegion)
            {
                src.L = Math.Max(b.L, rl); src.T = Math.Max(b.T, rt); src.R = Math.Min(b.R, rr); src.B = Math.Min(b.B, rb);
                if (src.R - src.L < 2 || src.B - src.T < 2) { err = "ERR_REGION"; return null; }
            }
            string u;
            Bitmap raw = Grab(h, src, method, out u);
            for (int tries = 0; h != IntPtr.Zero && raw != null && tries < 4 && LooksBlank(raw); tries++)
            {
                raw.Dispose();
                Thread.Sleep(250);
                raw = Grab(h, src, method, out u);
            }
            used = u;
            if (raw == null) { err = "ERR_CAPTURE"; return null; }
            int w = raw.Width, hh = raw.Height;
            double s = scale > 0 ? scale : 1;
            Bitmap img = raw;
            int nw = (int)Math.Round(w * s), nh = (int)Math.Round(hh * s);
            if (nw != w || nh != hh) { img = s >= 2 ? Upscale(raw, nw, nh) : Resize(raw, nw, nh, true); raw.Dispose(); s = (double)nw / w; }
            ox = src.L; oy = src.T; sOut = s;
            return img;
        }

        // 32bpp BGRA copy of a bitmap (what Windows.Graphics.Imaging.SoftwareBitmap.CreateCopyFromBuffer wants)
        public static byte[] Bgra(Bitmap b)
        {
            Rectangle rc = new Rectangle(0, 0, b.Width, b.Height);
            byte[] px = new byte[b.Width * 4 * b.Height];
            Bitmap c = b.PixelFormat == PixelFormat.Format32bppArgb ? b : b.Clone(rc, PixelFormat.Format32bppArgb);
            try
            {
                BitmapData d = c.LockBits(rc, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    int rowBytes = b.Width * 4;
                    for (int y = 0; y < b.Height; y++)
                        Marshal.Copy(new IntPtr(d.Scan0.ToInt64() + (long)y * d.Stride), px, y * rowBytes, rowBytes);
                }
                finally { c.UnlockBits(d); }
            }
            finally { if (!object.ReferenceEquals(c, b)) c.Dispose(); }
            return px;
        }

        // cheap content fingerprint (strided sum): identical screens skip a repeated OCR pass
        public static long QuickHash(byte[] b)
        {
            long h = 1469598103934665603L ^ b.Length;
            int step = Math.Max(1, b.Length / 200000);
            for (int i = 0; i < b.Length; i += step) h = (h ^ b[i]) * 1099511628211L;
            return h;
        }

        // ---------------------------------------------------------------- change detection
        // GDI screen copy straight into a (usually smaller) bitmap: a few ms even for a 4K window
        public static Bitmap FastGrab(N.RECT r, int dw, int dh)
        {
            int w = r.R - r.L, h = r.B - r.T;
            if (w <= 0 || h <= 0 || dw <= 0 || dh <= 0) return null;
            IntPtr sdc = N.GetDC(IntPtr.Zero), mdc = IntPtr.Zero, bmp = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                mdc = N.CreateCompatibleDC(sdc);
                bmp = N.CreateCompatibleBitmap(sdc, dw, dh);
                old = N.SelectObject(mdc, bmp);
                N.SetStretchBltMode(mdc, 3);   // COLORONCOLOR: fast, good enough for change detection
                if (!N.StretchBlt(mdc, 0, 0, dw, dh, sdc, r.L, r.T, w, h, 0x00CC0020)) return null;
                N.SelectObject(mdc, old); old = IntPtr.Zero;
                using (Bitmap t = Image.FromHbitmap(bmp)) return t.Clone(new Rectangle(0, 0, dw, dh), PixelFormat.Format24bppRgb);
            }
            catch { return null; }
            finally
            {
                if (old != IntPtr.Zero) N.SelectObject(mdc, old);
                if (bmp != IntPtr.Zero) N.DeleteObject(bmp);
                if (mdc != IntPtr.Zero) N.DeleteDC(mdc);
                N.ReleaseDC(IntPtr.Zero, sdc);
            }
        }

        // whole-window thumbnail (1/6 size, fixed dims regardless of capture path)
        public static Bitmap Thumb(long hwnd)
        {
            A();
            IntPtr h = new IntPtr(hwnd);
            if (h != IntPtr.Zero && (!N.IsWindow(h) || N.IsIconic(h))) return null;
            N.RECT b = h == IntPtr.Zero ? Virt() : Bounds(h);
            int tw = Math.Max(8, (b.R - b.L) / 6), th = Math.Max(8, (b.B - b.T) / 6);
            if (h == IntPtr.Zero || Unoccluded(h, b))
            {
                Bitmap q = FastGrab(b, tw, th);
                if (q != null) return q;
            }
            string u;
            Bitmap f = null;
            try { f = Grab(h, b, "print", out u); } catch { }
            if (f == null) return null;
            Bitmap t = Resize(f, tw, th, false);
            f.Dispose();
            return t;
        }

        // local region around the action point (half resolution); null when that area is not visible on screen
        public static Bitmap RoiThumb(long hwnd, int sx, int sy)
        {
            A();
            IntPtr h = new IntPtr(hwnd);
            if (h == IntPtr.Zero || !N.IsWindow(h) || N.IsIconic(h) || (sx == 0 && sy == 0)) return null;
            N.RECT b = Bounds(h), r;
            r.L = Math.Max(b.L, sx - 260); r.T = Math.Max(b.T, sy - 180); r.R = Math.Min(b.R, sx + 260); r.B = Math.Min(b.B, sy + 180);
            if (r.R - r.L < 16 || r.B - r.T < 16) return null;
            int[,] pts = { { r.L + 2, r.T + 2 }, { r.R - 3, r.T + 2 }, { r.L + 2, r.B - 3 }, { r.R - 3, r.B - 3 }, { sx, sy } };
            for (int i = 0; i < 5; i++) if (!OwnsPoint(h, pts[i, 0], pts[i, 1])) return null;
            return FastGrab(r, (r.R - r.L) / 2, (r.B - r.T) / 2);
        }

        // percent of pixels that differ noticeably
        public static double Diff(Bitmap a, Bitmap b)
        {
            if (a == null || b == null) return -1;
            if (a.Width != b.Width || a.Height != b.Height) return 100;
            Rectangle r = new Rectangle(0, 0, a.Width, a.Height);
            BitmapData da = a.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            BitmapData db = b.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                int stride = da.Stride, n = stride * a.Height;
                byte[] x = new byte[n], y = new byte[n];
                Marshal.Copy(da.Scan0, x, 0, n);
                Marshal.Copy(db.Scan0, y, 0, n);
                long cnt = 0;
                for (int row = 0; row < a.Height; row++)
                {
                    int o = row * stride;
                    for (int col = 0; col < a.Width; col++, o += 3)
                    {
                        int d = Math.Abs(x[o] - y[o]) + Math.Abs(x[o + 1] - y[o + 1]) + Math.Abs(x[o + 2] - y[o + 2]);
                        if (d > 48) cnt++;
                    }
                }
                return 100.0 * cnt / ((long)a.Width * a.Height);
            }
            finally { a.UnlockBits(da); b.UnlockBits(db); }
        }

        // after an action: poll the window + the local region around the action point.
        //   changed -> wait until both stop changing (settled)
        //   nothing changed within quietMs -> return early (the action probably had no visible effect)
        public static string Settle(long hwnd, Bitmap before, Bitmap beforeRoi, int sx, int sy, int maxMs, int quietMs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            Bitmap prev = null, prevR = null;
            double ch = 0, chR = 0;
            bool settled = false, any = false;
            if (quietMs <= 0 || quietMs > maxMs) quietMs = maxMs;
            Thread.Sleep(25);
            int polls = 0;
            while (true)
            {
                Bitmap cur = Thumb(hwnd);
                if (cur == null) break;
                Bitmap curR = beforeRoi != null ? RoiThumb(hwnd, sx, sy) : null;
                polls++;
                ch = Diff(before, cur);
                chR = (curR != null && beforeRoi != null) ? Diff(beforeRoi, curR) : 0;
                bool changed = ch > 0.15 || chR > 0.5;
                if (changed) any = true;
                bool stable = prev != null && Diff(prev, cur) < 0.05 &&
                              (curR == null || prevR == null || Diff(prevR, curR) < 0.2);
                if (prev != null) prev.Dispose();
                if (prevR != null) prevR.Dispose();
                prev = cur; prevR = curR;
                if (any && stable) { settled = true; break; }
                if (!any && sw.ElapsedMilliseconds >= quietMs) break;
                if (sw.ElapsedMilliseconds >= maxMs) break;
                Thread.Sleep(30);
            }
            if (prev != null) prev.Dispose();
            if (prevR != null) prevR.Dispose();
            return "\"changed\":" + J.F(ch) + ",\"roi_changed\":" + J.F(chR) + ",\"settled\":" + J.B(settled) +
                   ",\"settle_ms\":" + sw.ElapsedMilliseconds + ",\"polls\":" + polls;
        }
        public static string Settle(long hwnd, Bitmap before, int maxMs) { return Settle(hwnd, before, null, 0, 0, maxMs, maxMs); }

        // wait until two consecutive views are identical
        public static string WaitStable(long hwnd, int maxMs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            Bitmap prev = Thumb(hwnd);
            bool ok = false;
            while (prev != null && sw.ElapsedMilliseconds < maxMs)
            {
                Thread.Sleep(80);
                Bitmap cur = Thumb(hwnd);
                if (cur == null) break;
                double d = Diff(prev, cur);
                prev.Dispose();
                prev = cur;
                if (d < 0.05) { ok = true; break; }
            }
            if (prev != null) prev.Dispose();
            return "{\"ok\":true,\"stable\":" + J.B(ok) + ",\"ms\":" + sw.ElapsedMilliseconds + "}";
        }

        // ---------------------------------------------------------------- mouse
        // deepest enabled+visible child under a physical screen point
        public static IntPtr DeepChild(IntPtr top, int sx, int sy)
        {
            IntPtr cur = top;
            for (int i = 0; i < 32; i++)
            {
                N.POINT p = new N.POINT(sx, sy);
                N.ScreenToClient(cur, ref p);
                IntPtr c = N.ChildWindowFromPointEx(cur, p, 1 | 2 | 4);
                if (c == IntPtr.Zero || c == cur) break;
                cur = c;
            }
            return cur;
        }
        public static long ChildAt(long top, int sx, int sy) { A(); return DeepChild(new IntPtr(top), sx, sy).ToInt64(); }

        // How many physical px per logical px the target window works in (1 for per-monitor aware windows).
        public static double TargetFactor(IntPtr h)
        {
            try
            {
                int aw = N.GetAwarenessFromDpiAwarenessContext(N.GetWindowDpiAwarenessContext(h));
                if (aw == 2) return 1.0;
                uint mx = 96, my = 96;
                N.GetDpiForMonitor(N.MonitorFromWindow(h, 2), 0, out mx, out my);
                double sys = 96;
                if (aw == 1) { try { sys = N.GetDpiForSystem(); } catch { } }
                double f = mx / sys;
                return f > 0.1 ? f : 1.0;
            }
            catch { return 1.0; }
        }

        // physical screen point -> message coordinates. Measured on Win11 (2026-09): the system itself rescales
        // posted mouse-message coords for DPI-unaware targets, so we always send PHYSICAL client/screen coords.
        static N.POINT ToTarget(IntPtr h, int sx, int sy, bool clientSpace)
        {
            A();
            N.POINT p = new N.POINT(sx, sy);
            if (clientSpace) N.ScreenToClient(h, ref p);
            return p;
        }
        static IntPtr LP(N.POINT p) { return new IntPtr(((p.Y & 0xFFFF) << 16) | (p.X & 0xFFFF)); }

        static int Btn(string b, out uint down, out uint up, out uint dbl)
        {
            switch ((b ?? "left").ToLowerInvariant())
            {
                case "right": down = 0x204; up = 0x205; dbl = 0x206; return 0x2;
                case "middle": down = 0x207; up = 0x208; dbl = 0x209; return 0x10;
                default: down = 0x201; up = 0x202; dbl = 0x203; return 0x1;
            }
        }

        // action: click | double | down | up | move | drag | scroll | hscroll ; mods bit1=ctrl bit2=shift
        public static string BgMouse(long hwnd, int sx, int sy, string button, string action, int mods, int sx2, int sy2, int wheel)
        {
            A();
            IntPtr top = new IntPtr(hwnd);
            if (!N.IsWindow(top)) return J.Err("ERR_WINDOW_GONE", null);
            N.POINT before; N.GetCursorPos(out before);
            IntPtr t = DeepChild(top, sx, sy);
            uint down, up, dbl;
            int mk = Btn(button, out down, out up, out dbl);
            int mm = 0;
            if ((mods & 1) != 0) mm |= 0x8;
            if ((mods & 2) != 0) mm |= 0x4;
            N.POINT c = ToTarget(t, sx, sy, true);
            IntPtr lp = LP(c);
            string act = (action ?? "click").ToLowerInvariant();
            if (act == "scroll" || act == "hscroll")
            {
                IntPtr slp = LP(ToTarget(t, sx, sy, false));
                N.PostMessage(t, 0x200, new IntPtr(mm), lp);
                int steps = Math.Max(1, Math.Abs(wheel)), dir = wheel < 0 ? -120 : 120;
                uint msg = act == "scroll" ? 0x020Au : 0x020Eu;
                for (int i = 0; i < steps; i++)
                {
                    N.PostMessage(t, msg, new IntPtr(((dir & 0xFFFF) << 16) | mm), slp);
                    Thread.Sleep(15);
                }
            }
            else if (act == "move") N.PostMessage(t, 0x200, new IntPtr(mm), lp);
            else if (act == "down") { N.PostMessage(t, 0x200, new IntPtr(mm), lp); N.PostMessage(t, down, new IntPtr(mk | mm), lp); }
            else if (act == "up") N.PostMessage(t, up, new IntPtr(mm), lp);
            else if (act == "drag")
            {
                N.PostMessage(t, 0x200, new IntPtr(mm), lp); Thread.Sleep(10);
                N.PostMessage(t, down, new IntPtr(mk | mm), lp); Thread.Sleep(40);
                int steps = 12;
                for (int i = 1; i <= steps; i++)
                {
                    int x = sx + (sx2 - sx) * i / steps, y = sy + (sy2 - sy) * i / steps;
                    N.PostMessage(t, 0x200, new IntPtr(mk | mm), LP(ToTarget(t, x, y, true)));
                    Thread.Sleep(12);
                }
                N.PostMessage(t, up, new IntPtr(mm), LP(ToTarget(t, sx2, sy2, true)));
            }
            else
            {
                N.PostMessage(t, 0x200, new IntPtr(mm), lp); Thread.Sleep(8);
                N.PostMessage(t, down, new IntPtr(mk | mm), lp); Thread.Sleep(20);
                N.PostMessage(t, up, new IntPtr(mm), lp);
                if (act == "double")
                {
                    Thread.Sleep(20);
                    N.PostMessage(t, dbl, new IntPtr(mk | mm), lp); Thread.Sleep(20);
                    N.PostMessage(t, up, new IntPtr(mm), lp);
                }
            }
            N.POINT after; N.GetCursorPos(out after);
            return "{\"ok\":true,\"mode\":\"bg\",\"act\":" + J.Q(act) + ",\"screen\":[" + sx + "," + sy + "],\"target\":" + J.Q(J.Hex(t)) +
                   ",\"cls\":" + J.Q(Cls(t)) + ",\"client\":[" + c.X + "," + c.Y + "],\"cursor_moved\":" +
                   J.B(before.X != after.X || before.Y != after.Y) + "}";
        }

        static void SendMouse(uint flags, int data)
        {
            N.INPUT[] a = new N.INPUT[1];
            a[0].type = 0; a[0].u.mi.dwFlags = flags; a[0].u.mi.mouseData = unchecked((uint)data);
            N.SendInput(1, a, Marshal.SizeOf(typeof(N.INPUT)));
        }

        static bool SameApp(IntPtr a, IntPtr top)
        {
            if (a == IntPtr.Zero) return false;
            if (a == top) return true;
            return N.GetAncestor(a, 3) == top;
        }

        public static bool EnsureFg(IntPtr h)
        {
            if (SameApp(N.GetForegroundWindow(), h)) return true;
            if (N.IsIconic(h)) N.ShowWindow(h, 9);
            IntPtr fg = N.GetForegroundWindow();
            uint dummy;
            uint ft = N.GetWindowThreadProcessId(fg, out dummy), me = N.GetCurrentThreadId();
            bool att = ft != 0 && ft != me && N.AttachThreadInput(me, ft, true);
            N.BringWindowToTop(h);
            N.SetForegroundWindow(h);
            if (att) N.AttachThreadInput(me, ft, false);
            for (int i = 0; i < 25; i++) { if (SameApp(N.GetForegroundWindow(), h)) return true; Thread.Sleep(12); }
            SendKey(0x12, false); SendKey(0x12, true);   // ALT tap lifts the foreground lock
            N.SetForegroundWindow(h);
            for (int i = 0; i < 25; i++) { if (SameApp(N.GetForegroundWindow(), h)) return true; Thread.Sleep(12); }
            return false;
        }
        public static string Activate(long hwnd)
        {
            A();
            IntPtr h = new IntPtr(hwnd);
            if (!N.IsWindow(h)) return J.Err("ERR_WINDOW_GONE", null);
            return EnsureFg(h) ? "{\"ok\":true,\"fg\":true}" : J.Err("ERR_NOFOCUS", "could not bring window to foreground");
        }

        // real input (moves the cursor, needs foreground). Refuses when another window covers the point.
        public static string FgMouse(long hwnd, int sx, int sy, string button, string action, int mods, int sx2, int sy2, int wheel, bool restoreCursor)
        {
            A();
            IntPtr top = new IntPtr(hwnd);
            if (top != IntPtr.Zero)
            {
                if (!N.IsWindow(top)) return J.Err("ERR_WINDOW_GONE", null);
                if (!EnsureFg(top)) return J.Err("ERR_NOFOCUS", "could not bring target to foreground; nothing sent");
                if (!OwnsPoint(top, sx, sy)) return J.Err("ERR_OCCLUDED", "another window covers the target point; nothing sent");
            }
            N.POINT before; N.GetCursorPos(out before);
            string act = (action ?? "click").ToLowerInvariant();
            uint fd = 0x2, fu = 0x4;
            string b = (button ?? "left").ToLowerInvariant();
            if (b == "right") { fd = 0x8; fu = 0x10; } else if (b == "middle") { fd = 0x20; fu = 0x40; }
            if ((mods & 1) != 0) SendKey(0x11, false);
            if ((mods & 2) != 0) SendKey(0x10, false);
            N.SetCursorPos(sx, sy);
            Thread.Sleep(15);
            if (act == "scroll" || act == "hscroll") SendMouse(act == "scroll" ? 0x0800u : 0x1000u, wheel * 120);
            else if (act == "move") { }
            else if (act == "down") SendMouse(fd, 0);
            else if (act == "up") SendMouse(fu, 0);
            else if (act == "drag")
            {
                SendMouse(fd, 0); Thread.Sleep(40);
                int steps = 16;
                for (int i = 1; i <= steps; i++) { N.SetCursorPos(sx + (sx2 - sx) * i / steps, sy + (sy2 - sy) * i / steps); Thread.Sleep(12); }
                Thread.Sleep(30);
                SendMouse(fu, 0);
            }
            else
            {
                SendMouse(fd, 0); Thread.Sleep(15); SendMouse(fu, 0);
                if (act == "double") { Thread.Sleep(40); SendMouse(fd, 0); Thread.Sleep(15); SendMouse(fu, 0); }
            }
            if ((mods & 2) != 0) SendKey(0x10, true);
            if ((mods & 1) != 0) SendKey(0x11, true);
            Thread.Sleep(20);
            if (restoreCursor && act != "move") N.SetCursorPos(before.X, before.Y);
            return "{\"ok\":true,\"mode\":\"fg\",\"act\":" + J.Q(act) + ",\"screen\":[" + sx + "," + sy + "],\"cursor_restored\":" + J.B(restoreCursor) + "}";
        }

        // ---------------------------------------------------------------- keyboard
        static readonly Dictionary<string, ushort> VK = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            {"ctrl",0x11},{"control",0x11},{"shift",0x10},{"alt",0x12},{"win",0x5B},{"enter",0x0D},{"return",0x0D},
            {"tab",0x09},{"esc",0x1B},{"escape",0x1B},{"space",0x20},{"backspace",0x08},{"back",0x08},{"delete",0x2E},
            {"del",0x2E},{"insert",0x2D},{"ins",0x2D},{"home",0x24},{"end",0x23},{"pageup",0x21},{"pgup",0x21},
            {"pagedown",0x22},{"pgdn",0x22},{"left",0x25},{"up",0x26},{"right",0x27},{"down",0x28},{"apps",0x5D},
            {"menu",0x5D},{"printscreen",0x2C},{"capslock",0x14},{"numlock",0x90},{"plus",0xBB},{"minus",0xBD}
        };

        public static ushort[] Combo(string s)
        {
            List<ushort> l = new List<ushort>();
            foreach (string p0 in (s ?? "").Split('+'))
            {
                string p = p0.Trim();
                if (p.Length == 0) continue;
                ushort v;
                if (VK.TryGetValue(p, out v)) { l.Add(v); continue; }
                int n;
                if (p.Length > 1 && (p[0] == 'f' || p[0] == 'F') && int.TryParse(p.Substring(1), out n) && n >= 1 && n <= 24) { l.Add((ushort)(0x6F + n)); continue; }
                if (p.Length == 1)
                {
                    char c = char.ToUpperInvariant(p[0]);
                    if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) { l.Add((ushort)c); continue; }
                    short k = N.VkKeyScan(p[0]);
                    if (k != -1) { l.Add((ushort)(k & 0xFF)); continue; }
                }
                throw new ArgumentException("unknown key: " + p);
            }
            if (l.Count == 0) throw new ArgumentException("empty key combo");
            return l.ToArray();
        }
        static bool IsExt(ushort vk) { return (vk >= 0x21 && vk <= 0x28) || vk == 0x2D || vk == 0x2E || vk == 0x5B || vk == 0x5C || vk == 0x5D || vk == 0x6F || vk == 0x90; }

        static void SendKey(ushort vk, bool up)
        {
            N.INPUT[] a = new N.INPUT[1];
            a[0].type = 1; a[0].u.ki.wVk = vk; a[0].u.ki.wScan = (ushort)N.MapVirtualKey(vk, 0);
            a[0].u.ki.dwFlags = (up ? 2u : 0u) | (IsExt(vk) ? 1u : 0u);
            N.SendInput(1, a, Marshal.SizeOf(typeof(N.INPUT)));
        }
        static N.INPUT KI(ushort vk, ushort sc, uint flags)
        {
            N.INPUT i = new N.INPUT();
            i.type = 1; i.u.ki.wVk = vk; i.u.ki.wScan = sc; i.u.ki.dwFlags = flags;
            return i;
        }
        static IntPtr KeyLP(ushort vk, bool up, bool alt)
        {
            uint sc = N.MapVirtualKey(vk, 0);
            uint v = 1u | (sc << 16) | (IsExt(vk) ? (1u << 24) : 0u) | (alt ? (1u << 29) : 0u) | (up ? 0xC0000000u : 0u);
            return new IntPtr(unchecked((int)v));
        }

        static IntPtr FocusOf(IntPtr top)
        {
            uint pid;
            uint tid = N.GetWindowThreadProcessId(top, out pid);
            N.GUITHREADINFO gi = new N.GUITHREADINFO();
            gi.cbSize = Marshal.SizeOf(typeof(N.GUITHREADINFO));
            if (N.GetGUIThreadInfo(tid, ref gi) && gi.hwndFocus != IntPtr.Zero) return gi.hwndFocus;
            return top;
        }
        public static bool IsClassicEdit(string cls)
        {
            return cls.Equals("Edit", StringComparison.OrdinalIgnoreCase) || cls.IndexOf(".EDIT.", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   cls.IndexOf("RichEdit", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static string BgKey(long hwnd, string combo, long targetOverride, int repeat)
        {
            A();
            IntPtr top = new IntPtr(hwnd);
            if (!N.IsWindow(top)) return J.Err("ERR_WINDOW_GONE", null);
            IntPtr t = targetOverride != 0 ? new IntPtr(targetOverride) : FocusOf(top);
            ushort[] ks;
            try { ks = Combo(combo); } catch (Exception e) { return J.Err("ERR_KEY", e.Message); }
            string cls = Cls(t);
            bool ctrl = false, shift = false, alt = false, win = false;
            ushort main = 0;
            foreach (ushort k in ks)
            {
                if (k == 0x11) ctrl = true; else if (k == 0x10) shift = true; else if (k == 0x12) alt = true; else if (k == 0x5B) win = true; else main = k;
            }
            string how = "post";
            if (repeat < 1) repeat = 1;
            // classic edit controls: use real edit messages instead of simulated Ctrl combos (100% reliable)
            if (ctrl && !shift && !alt && !win && IsClassicEdit(cls))
            {
                uint m = 0; IntPtr wp = IntPtr.Zero, lp = IntPtr.Zero;
                if (main == 0x41) { m = 0x00B1; lp = new IntPtr(-1); }
                else if (main == 0x43) m = 0x0301;
                else if (main == 0x56) m = 0x0302;
                else if (main == 0x58) m = 0x0300;
                else if (main == 0x5A) m = 0x0304;
                if (m != 0)
                {
                    IntPtr res;
                    for (int i = 0; i < repeat; i++) N.SendMessageTimeout(t, m, wp, lp, 2, 2000, out res);
                    how = "msg:0x" + m.ToString("X");
                    return "{\"ok\":true,\"mode\":\"bg\",\"keys\":" + J.Q(combo) + ",\"how\":" + J.Q(how) + ",\"target\":" + J.Q(J.Hex(t)) + ",\"cls\":" + J.Q(cls) + "}";
                }
            }
            uint kd = alt ? 0x104u : 0x100u, ku = alt ? 0x105u : 0x101u;
            for (int r = 0; r < repeat; r++)
            {
                foreach (ushort k in ks) N.PostMessage(t, kd, new IntPtr(k), KeyLP(k, false, alt));
                for (int i = ks.Length - 1; i >= 0; i--) N.PostMessage(t, ku, new IntPtr(ks[i]), KeyLP(ks[i], true, alt));
                Thread.Sleep(5);
            }
            string warn = (ctrl || alt || win || shift) ? ",\"warn\":\"background modifier keys are simulated; apps that read the real key state may ignore them - verify, or use -Fg\"" : "";
            return "{\"ok\":true,\"mode\":\"bg\",\"keys\":" + J.Q(combo) + ",\"how\":" + J.Q(how) + ",\"target\":" + J.Q(J.Hex(t)) + ",\"cls\":" + J.Q(cls) + warn + "}";
        }

        public static string FgKey(long hwnd, string combo, int repeat)
        {
            A();
            IntPtr top = new IntPtr(hwnd);
            if (top != IntPtr.Zero && !EnsureFg(top)) return J.Err("ERR_NOFOCUS", "could not bring target to foreground; nothing sent");
            ushort[] ks;
            try { ks = Combo(combo); } catch (Exception e) { return J.Err("ERR_KEY", e.Message); }
            if (repeat < 1) repeat = 1;
            List<N.INPUT> l = new List<N.INPUT>();
            for (int r = 0; r < repeat; r++)
            {
                foreach (ushort k in ks) l.Add(KI(k, (ushort)N.MapVirtualKey(k, 0), IsExt(k) ? 1u : 0u));
                for (int i = ks.Length - 1; i >= 0; i--) l.Add(KI(ks[i], (ushort)N.MapVirtualKey(ks[i], 0), 2u | (IsExt(ks[i]) ? 1u : 0u)));
            }
            N.SendInput((uint)l.Count, l.ToArray(), Marshal.SizeOf(typeof(N.INPUT)));
            return "{\"ok\":true,\"mode\":\"fg\",\"keys\":" + J.Q(combo) + "}";
        }

        // method: auto | replacesel | char | paste   (paste: caller already put the text on the clipboard)
        public static string BgText(long hwnd, string text, string method, long targetOverride)
        {
            A();
            IntPtr top = new IntPtr(hwnd);
            if (!N.IsWindow(top)) return J.Err("ERR_WINDOW_GONE", null);
            IntPtr t = targetOverride != 0 ? new IntPtr(targetOverride) : FocusOf(top);
            string cls = Cls(t);
            bool classic = IsClassicEdit(cls);
            string m = (method ?? "auto").ToLowerInvariant();
            if (m == "auto") m = classic ? "replacesel" : "char";
            IntPtr res;
            if (m == "replacesel")
            {
                if (N.SendMessageTimeoutS(t, 0x00C2, new IntPtr(1), text, 2, 3000, out res) == IntPtr.Zero)
                    return J.Err("ERR_TIMEOUT", "EM_REPLACESEL timed out");
            }
            else if (m == "paste")
            {
                if (classic) N.SendMessageTimeout(t, 0x0302, IntPtr.Zero, IntPtr.Zero, 2, 3000, out res);
                else
                {
                    N.PostMessage(t, 0x100, new IntPtr(0x11), KeyLP(0x11, false, false));
                    N.PostMessage(t, 0x100, new IntPtr(0x56), KeyLP(0x56, false, false));
                    N.PostMessage(t, 0x101, new IntPtr(0x56), KeyLP(0x56, true, false));
                    N.PostMessage(t, 0x101, new IntPtr(0x11), KeyLP(0x11, true, false));
                }
            }
            else
            {
                m = "char";
                foreach (char ch in text.Replace("\r\n", "\n"))
                    N.PostMessage(t, 0x102, new IntPtr(ch == '\n' ? '\r' : ch), new IntPtr(1));
            }
            return "{\"ok\":true,\"mode\":\"bg\",\"method\":" + J.Q(m) + ",\"target\":" + J.Q(J.Hex(t)) + ",\"cls\":" + J.Q(cls) + ",\"chars\":" + text.Length + "}";
        }

        // foreground unicode typing via SendInput (no clipboard, IME-independent)
        public static string FgText(long hwnd, string text)
        {
            A();
            IntPtr top = new IntPtr(hwnd);
            if (top != IntPtr.Zero && !EnsureFg(top)) return J.Err("ERR_NOFOCUS", "could not bring target to foreground; nothing sent");
            List<N.INPUT> l = new List<N.INPUT>();
            foreach (char ch in text.Replace("\r\n", "\n"))
            {
                if (ch == '\n') { l.Add(KI(0x0D, 0x1C, 0)); l.Add(KI(0x0D, 0x1C, 2)); }
                else { l.Add(KI(0, ch, 4)); l.Add(KI(0, ch, 4 | 2)); }
            }
            int size = Marshal.SizeOf(typeof(N.INPUT));
            for (int i = 0; i < l.Count; i += 200)
            {
                if (top != IntPtr.Zero && !SameApp(N.GetForegroundWindow(), top))
                    return J.Err("ERR_FOCUS_LOST", "focus changed while typing; stopped after " + (i / 2) + " chars");
                int n = Math.Min(200, l.Count - i);
                N.INPUT[] chunk = l.GetRange(i, n).ToArray();
                N.SendInput((uint)n, chunk, size);
                Thread.Sleep(10);
            }
            return "{\"ok\":true,\"mode\":\"fg\",\"method\":\"unicode\",\"chars\":" + text.Length + "}";
        }

        // real Ctrl+V to the foreground window (for long text)
        public static string FgPaste(long hwnd)
        {
            A();
            IntPtr top = new IntPtr(hwnd);
            if (top != IntPtr.Zero && !EnsureFg(top)) return J.Err("ERR_NOFOCUS", "could not bring target to foreground; nothing sent");
            N.INPUT[] a = { KI(0x11, 0x1D, 0), KI(0x56, 0x2F, 0), KI(0x56, 0x2F, 2), KI(0x11, 0x1D, 2) };
            N.SendInput(4, a, Marshal.SizeOf(typeof(N.INPUT)));
            return "{\"ok\":true,\"mode\":\"fg\",\"method\":\"paste\"}";
        }

        // grayscale + invert copy: lets Windows OCR read light text on coloured/dark buttons
        public static string Invert(string inPath, string outPath)
        {
            try
            {
                using (Bitmap src = LoadCopy(inPath))
                using (Bitmap d = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb))
                {
                    using (Graphics g = Graphics.FromImage(d))
                    using (ImageAttributes ia = new ImageAttributes())
                    {
                        float a = -0.299f, b = -0.587f, c = -0.114f;
                        ColorMatrix m = new ColorMatrix(new float[][] {
                            new float[] { a, a, a, 0, 0 }, new float[] { b, b, b, 0, 0 }, new float[] { c, c, c, 0, 0 },
                            new float[] { 0, 0, 0, 1, 0 }, new float[] { 1, 1, 1, 0, 1 } });
                        ia.SetColorMatrix(m);
                        g.DrawImage(src, new Rectangle(0, 0, d.Width, d.Height), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
                    }
                    Save(d, outPath, 90);
                }
                return "OK";
            }
            catch (Exception ex) { return "ERR " + ex.Message; }
        }

        // OCR pre-processing: optional upscale + polarity-free local contrast: out = 255 - 6*max(0,|gray - localMean| - 15).
        // Text of either polarity (white on blue buttons, dark mode, grey on white) becomes dark on white.
        public static string Prep(string inPath, string outPath, double factor, int radius)
        {
            try
            {
                using (Bitmap src = LoadCopy(inPath))
                using (Bitmap up = PrepMem(src, factor, radius))
                    Save(up, outPath, 95);
                return "OK";
            }
            catch (Exception ex) { return "ERR " + ex.Message; }
        }

        // 2x upscale (optional) + adaptive local-mean binarisation with polarity handling; returns a new bitmap
        public static Bitmap PrepMem(Bitmap src, double factor, int radius)
        {
            {
                {
                    int w = Math.Max(1, (int)Math.Round(src.Width * factor)), h = Math.Max(1, (int)Math.Round(src.Height * factor));
                    Bitmap up = (w == src.Width && h == src.Height) ? src.Clone(new Rectangle(0, 0, src.Width, src.Height), PixelFormat.Format24bppRgb) : Resize(src, w, h, true);
                    {
                        Rectangle rc = new Rectangle(0, 0, w, h);
                        BitmapData d = up.LockBits(rc, ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
                        try
                        {
                            int stride = d.Stride, n = stride * h, W1 = w + 1;
                            byte[] px = new byte[n];
                            Marshal.Copy(d.Scan0, px, 0, n);
                            byte[] g = new byte[w * h];
                            for (int y = 0; y < h; y++)
                                for (int x = 0, o = y * stride; x < w; x++, o += 3)
                                    g[y * w + x] = (byte)((px[o] * 29 + px[o + 1] * 150 + px[o + 2] * 77) >> 8);
                            long[] I = new long[(long)W1 * (h + 1)];
                            for (int y = 0; y < h; y++)
                            {
                                long row = 0;
                                for (int x = 0; x < w; x++) { row += g[y * w + x]; I[(y + 1) * W1 + x + 1] = I[y * W1 + x + 1] + row; }
                            }
                            int r = Math.Max(4, radius);
                            for (int y = 0; y < h; y++)
                            {
                                int y0 = Math.Max(0, y - r), y1 = Math.Min(h, y + r + 1);
                                for (int x = 0, o = y * stride; x < w; x++, o += 3)
                                {
                                    int x0 = Math.Max(0, x - r), x1 = Math.Min(w, x + r + 1);
                                    long sum = I[y1 * W1 + x1] - I[y0 * W1 + x1] - I[y1 * W1 + x0] + I[y0 * W1 + x0];
                                    int m = (int)(sum / ((long)(x1 - x0) * (y1 - y0)));
                                    int v = 255 - Math.Max(0, Math.Abs(g[y * w + x] - m) - 15) * 6;
                                    byte c = (byte)(v < 0 ? 0 : v);
                                    px[o] = c; px[o + 1] = c; px[o + 2] = c;
                                }
                            }
                            Marshal.Copy(px, 0, d.Scan0, n);
                        }
                        finally { up.UnlockBits(d); }
                        return up;
                    }
                }
            }
        }

        // ---------------------------------------------------------------- mark (click preview)
        public static Bitmap LoadCopy(string path)
        {
            using (FileStream fs = File.OpenRead(path))
            using (Image im = Image.FromStream(fs))
            {
                Bitmap b = new Bitmap(im.Width, im.Height, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(b)) g.DrawImage(im, 0, 0, im.Width, im.Height);
                return b;
            }
        }
        static List<int[]> ParsePts(string pts)
        {
            List<int[]> l = new List<int[]>();
            foreach (string it in (pts ?? "").Split(new char[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] xy = it.Split(':');
                if (xy.Length != 2) throw new ArgumentException("bad point '" + it + "' (want x:y)");
                l.Add(new int[] { (int)Math.Round(double.Parse(xy[0], CultureInfo.InvariantCulture)), (int)Math.Round(double.Parse(xy[1], CultureInfo.InvariantCulture)) });
            }
            return l;
        }
        static void DrawMarker(Graphics g, int x, int y, int size, Color c, float penW)
        {
            using (Pen p = new Pen(c, penW))
            {
                g.DrawEllipse(p, x - size / 2f, y - size / 2f, size, size);
                g.DrawLine(p, x - size, y, x - size / 4f, y); g.DrawLine(p, x + size / 4f, y, x + size, y);
                g.DrawLine(p, x, y - size, x, y - size / 4f); g.DrawLine(p, x, y + size / 4f, x, y + size);
            }
        }

        // marked full image + optional zoom sheet (zoom = radius in image px; each tile magnified 4x, centre pixel boxed)
        public static string Mark(string inPath, string outPath, string pts, int size, int zoom, string zoomOut)
        {
            try
            {
                if (string.Equals(Path.GetFullPath(inPath), Path.GetFullPath(outPath), StringComparison.OrdinalIgnoreCase)) return J.Err("ERR_SAME_FILE", null);
                List<int[]> P = ParsePts(pts);
                if (P.Count == 0) return J.Err("ERR_NO_PTS", null);
                using (Bitmap orig = LoadCopy(inPath))
                using (Bitmap bmp = (Bitmap)orig.Clone())
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    using (Font f = new Font("Arial", 12, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (SolidBrush db = new SolidBrush(Color.FromArgb(220, 0, 0, 0)))
                    using (SolidBrush wb = new SolidBrush(Color.White))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        for (int i = 0; i < P.Count; i++)
                        {
                            int x = P[i][0], y = P[i][1];
                            DrawMarker(g, x, y, size + 3, Color.White, 5);
                            DrawMarker(g, x, y, size, Color.FromArgb(255, 32, 32), 2);
                            using (SolidBrush dot = new SolidBrush(Color.FromArgb(255, 32, 32))) g.FillRectangle(dot, x - 1, y - 1, 3, 3);
                            if (P.Count > 1)
                            {
                                string s = (i + 1).ToString();
                                float lx = x + size * 0.62f, ly = y - size * 0.62f - 14;
                                g.DrawString(s, f, db, lx + 1, ly + 1); g.DrawString(s, f, wb, lx, ly);
                            }
                        }
                    }
                    Save(bmp, outPath, 90);
                    string zj = "";
                    if (zoom > 0 && !string.IsNullOrEmpty(zoomOut))
                    {
                        int mag = 4, tile = zoom * 2 * mag, cols = Math.Min(4, P.Count), rows = (P.Count + cols - 1) / cols;
                        using (Bitmap sheet = new Bitmap(cols * (tile + 6) + 6, rows * (tile + 6) + 6, PixelFormat.Format24bppRgb))
                        {
                            using (Graphics g = Graphics.FromImage(sheet))
                            using (Font f = new Font("Arial", 14, FontStyle.Bold, GraphicsUnit.Pixel))
                            using (Pen red = new Pen(Color.FromArgb(255, 32, 32), 1))
                            using (Pen white = new Pen(Color.White, 3))
                            using (SolidBrush db = new SolidBrush(Color.FromArgb(200, 0, 0, 0)))
                            using (SolidBrush wb = new SolidBrush(Color.White))
                            {
                                g.Clear(Color.FromArgb(40, 40, 40));
                                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                                g.PixelOffsetMode = PixelOffsetMode.Half;
                                for (int i = 0; i < P.Count; i++)
                                {
                                    int tx = 6 + (i % cols) * (tile + 6), ty = 6 + (i / cols) * (tile + 6);
                                    int x = P[i][0], y = P[i][1];
                                    g.DrawImage(orig, new Rectangle(tx, ty, tile, tile), new Rectangle(x - zoom, y - zoom, zoom * 2, zoom * 2), GraphicsUnit.Pixel);
                                    int cx = tx + zoom * mag, cy = ty + zoom * mag, gap = mag * 2;
                                    g.PixelOffsetMode = PixelOffsetMode.Default;
                                    g.DrawLine(white, cx - tile / 4, cy + mag / 2, cx - gap, cy + mag / 2); g.DrawLine(white, cx + gap + mag, cy + mag / 2, cx + tile / 4, cy + mag / 2);
                                    g.DrawLine(white, cx + mag / 2, cy - tile / 4, cx + mag / 2, cy - gap); g.DrawLine(white, cx + mag / 2, cy + gap + mag, cx + mag / 2, cy + tile / 4);
                                    g.DrawLine(red, cx - tile / 4, cy + mag / 2, cx - gap, cy + mag / 2); g.DrawLine(red, cx + gap + mag, cy + mag / 2, cx + tile / 4, cy + mag / 2);
                                    g.DrawLine(red, cx + mag / 2, cy - tile / 4, cx + mag / 2, cy - gap); g.DrawLine(red, cx + mag / 2, cy + gap + mag, cx + mag / 2, cy + tile / 4);
                                    g.DrawRectangle(red, cx - 1, cy - 1, mag + 1, mag + 1);
                                    g.PixelOffsetMode = PixelOffsetMode.Half;
                                    string s = (i + 1) + "  (" + x + "," + y + ")";
                                    SizeF z = g.MeasureString(s, f);
                                    g.FillRectangle(db, tx, ty, z.Width + 4, z.Height + 2);
                                    g.DrawString(s, f, wb, tx + 2, ty + 1);
                                }
                            }
                            Save(sheet, zoomOut, 90);
                        }
                        zj = ",\"zoom\":" + J.Q(zoomOut);
                    }
                    return "{\"ok\":true,\"in\":" + J.Q(inPath) + ",\"out\":" + J.Q(outPath) + zj + ",\"pts\":" + J.Q(pts) + "}";
                }
            }
            catch (Exception ex) { return J.Err("ERR_MARK", ex.Message); }
        }
    }
}
