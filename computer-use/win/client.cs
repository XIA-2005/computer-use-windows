// client.cs -> cu.exe : thin, fast front-end for cu.ps1 (C# 5, .NET 4 csc)
//   cu.exe <cmd> [options]            same arguments / same one-line JSON output as cu.ps1
//   Talks to a resident cu.ps1 daemon over a per-user named pipe (starts it on demand, detached via WMI,
//   so it is not a child of the calling shell / editor). Daemon exits by itself after CU_IDLE minutes idle.
//   Falls back to running "powershell -File cu.ps1 ..." directly when the daemon cannot be used.
//   env: CU_NODAEMON=1 -> always direct;  CU_IDLE=<minutes> (default 20)
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

static class CuClient
{
    static string Here { get { return Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName); } }

    static int Main(string[] argv)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        string ps1 = Path.Combine(Here, "cu.ps1");
        if (argv.Length > 0 && (argv[0] == "--stop" || argv[0] == "stop-daemon"))
            return Stop(PipeName(ps1)) ? Out("{\"ok\":true,\"daemon\":\"stopped\"}") : Out("{\"ok\":true,\"daemon\":\"not running\"}");
        if (Environment.GetEnvironmentVariable("CU_NODAEMON") == "1") return Direct(ps1, argv);
        string pipe;
        try { pipe = PipeName(ps1); } catch { return Direct(ps1, argv); }
        string res = null;
        try
        {
            res = Ask(pipe, argv, 60);
            if (res == null)
            {
                StartDaemon(ps1, pipe);
                res = Ask(pipe, argv, 15000);
            }
        }
        catch (Exception) { res = null; }
        if (res == null) return Direct(ps1, argv);
        return Out(res);
    }

    static int Out(string line)
    {
        Stream o = Console.OpenStandardOutput();
        byte[] b = new UTF8Encoding(false).GetBytes(line + "\n");
        o.Write(b, 0, b.Length); o.Flush();
        return line.StartsWith("{\"ok\":false") ? 1 : 0;
    }

    // pipe name = user + hash of the source files: editing cu.ps1/cu.cs/uia.cs automatically gets a fresh daemon
    static string PipeName(string ps1)
    {
        StringBuilder k = new StringBuilder(Environment.UserName.ToLowerInvariant());
        foreach (string name in new string[] { "CU_STATE", "CU_SESSION", "CU_FG_AUTO", "CU_SLOW", "CU_SETTLE_QUIET", "CU_HEADLESS" })
            k.Append("|").Append(name).Append("=").Append(Environment.GetEnvironmentVariable(name) ?? "");
        foreach (string f in new string[] { "cu.ps1", "cu.cs", "uia.cs", "web.cs" })
        {
            string p = Path.Combine(Here, f);
            k.Append("|").Append(File.Exists(p) ? File.GetLastWriteTimeUtc(p).Ticks + ":" + new FileInfo(p).Length : "-");
        }
        using (MD5 m = MD5.Create())
        {
            byte[] h = m.ComputeHash(Encoding.UTF8.GetBytes(k.ToString()));
            return "cu4-" + BitConverter.ToString(h, 0, 6).Replace("-", "").ToLowerInvariant();
        }
    }

    static string Esc(string s)
    {
        StringBuilder sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            if (c == '"') sb.Append("\\\""); else if (c == '\\') sb.Append("\\\\");
            else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
        }
        return sb.Append("\"").ToString();
    }

    // one request / one response line. null = daemon not reachable (nothing was executed)
    static string Ask(string pipe, string[] argv, int connectMs)
    {
        using (NamedPipeClientStream c = new NamedPipeClientStream(".", pipe, PipeDirection.InOut))
        {
            try { c.Connect(connectMs); } catch (TimeoutException) { return null; } catch (IOException) { return null; }
            StringBuilder req = new StringBuilder("{\"cwd\":" + Esc(Environment.CurrentDirectory) + ",\"argv\":[");
            for (int i = 0; i < argv.Length; i++) { if (i > 0) req.Append(","); req.Append(Esc(argv[i])); }
            req.Append("]}\n");
            byte[] b = new UTF8Encoding(false).GetBytes(req.ToString());
            c.Write(b, 0, b.Length); c.Flush();
            StreamReader r = new StreamReader(c, new UTF8Encoding(false));
            string line = r.ReadLine();
            if (line == null) return "{\"ok\":false,\"err\":\"ERR_DAEMON\",\"msg\":\"daemon closed the connection (it may have crashed); retry, or set CU_NODAEMON=1\"}";
            return line;
        }
    }

    static bool Stop(string pipe)
    {
        string r = null;
        try { r = Ask(pipe, new string[] { "__stop" }, 300); } catch { }
        return r != null;
    }

    static void StartDaemon(string ps1, string pipe)
    {
        string cmd = "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + ps1 + "\" serve -Pipe " + pipe;
        // WMI does not inherit the caller's environment. Explicitly forward
        // session and private state to keep the resident fast path isolated.
        string state = Environment.GetEnvironmentVariable("CU_STATE");
        string session = Environment.GetEnvironmentVariable("CU_SESSION");
        if (!string.IsNullOrEmpty(state)) cmd += " -StateDir " + Quote(state);
        if (!string.IsNullOrEmpty(session)) cmd += " -Session " + Quote(session);
        string fgAuto = Environment.GetEnvironmentVariable("CU_FG_AUTO");
        string slow = Environment.GetEnvironmentVariable("CU_SLOW");
        string quiet = Environment.GetEnvironmentVariable("CU_SETTLE_QUIET");
        string headless = Environment.GetEnvironmentVariable("CU_HEADLESS");
        if (!string.IsNullOrEmpty(fgAuto)) cmd += " -EnvFgAuto " + Quote(fgAuto);
        if (!string.IsNullOrEmpty(slow)) cmd += " -EnvSlow " + Quote(slow);
        if (!string.IsNullOrEmpty(quiet)) cmd += " -EnvQuiet " + Quote(quiet);
        if (!string.IsNullOrEmpty(headless)) cmd += " -EnvHeadless " + Quote(headless);
        // a mutex keeps concurrent clients from starting several daemons
        using (Mutex mx = new Mutex(false, "Local\\" + pipe + "-start"))
        {
            bool own = false;
            try { own = mx.WaitOne(20000); } catch (AbandonedMutexException) { own = true; }
            try
            {
                using (NamedPipeClientStream probe = new NamedPipeClientStream(".", pipe, PipeDirection.InOut))
                {
                    try { probe.Connect(40); return; } catch { }   // someone else started it meanwhile
                }
                try
                {
                    // WMI: the daemon is parented to WmiPrvSE, outside this process tree / job object
                    ManagementClass startup = new ManagementClass("Win32_ProcessStartup");
                    ManagementObject si = startup.CreateInstance();
                    si["ShowWindow"] = (ushort)0;
                    ManagementClass pc = new ManagementClass("Win32_Process");
                    ManagementBaseObject inp = pc.GetMethodParameters("Create");
                    inp["CommandLine"] = cmd;
                    inp["CurrentDirectory"] = Here;
                    inp["ProcessStartupInformation"] = si;
                    ManagementBaseObject o = pc.InvokeMethod("Create", inp, null);
                    if (Convert.ToInt32(o["ReturnValue"]) == 0) return;
                }
                catch { }
                ProcessStartInfo psi = new ProcessStartInfo("powershell.exe", cmd.Substring("powershell.exe ".Length));
                psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.WorkingDirectory = Here;
                Process.Start(psi);
            }
            finally { if (own) mx.ReleaseMutex(); }
        }
    }

    static string Quote(string a)
    {
        if (a.Length > 0 && a.IndexOfAny(new char[] { ' ', '\t', '"' }) < 0) return a;
        StringBuilder sb = new StringBuilder("\"");
        int bs = 0;
        foreach (char ch in a)
        {
            if (ch == '\\') { bs++; continue; }
            if (ch == '"') { sb.Append('\\', bs * 2 + 1); sb.Append('"'); bs = 0; continue; }
            sb.Append('\\', bs); bs = 0; sb.Append(ch);
        }
        sb.Append('\\', bs * 2);
        return sb.Append("\"").ToString();
    }

    static int Direct(string ps1, string[] argv)
    {
        StringBuilder a = new StringBuilder("-NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + Quote(ps1));
        foreach (string s in argv) a.Append(" ").Append(Quote(s));
        ProcessStartInfo psi = new ProcessStartInfo("powershell.exe", a.ToString());
        psi.UseShellExecute = false; psi.RedirectStandardOutput = true; psi.CreateNoWindow = true;
        psi.StandardOutputEncoding = new UTF8Encoding(false);
        using (Process p = Process.Start(psi))
        {
            // A launched Edge/Chrome process may inherit stdout's write handle.
            // ReadToEnd then waits for the entire browser lifetime even after
            // cu.ps1 has already produced its single JSON response.
            string line = p.StandardOutput.ReadLine();
            if (line == null) line = "{\"ok\":false,\"err\":\"ERR_DAEMON\",\"msg\":\"direct CLI returned no JSON\"}";
            p.WaitForExit(3000);
            Stream so = Console.OpenStandardOutput();
            byte[] b = new UTF8Encoding(false).GetBytes(line + "\n");
            so.Write(b, 0, b.Length); so.Flush();
            return line.StartsWith("{\"ok\":false") ? 1 : 0;
        }
    }
}
