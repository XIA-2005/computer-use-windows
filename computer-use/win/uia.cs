// uia.cs - UI Automation layer (compiled together with cu.cs)
//   Collect      : interactive elements of a window (name / type / physical rect), time-boxed on an MTA thread
//   cache (TSV)  : elements tied to one frame, used by click -Id and coordinate snapping
//   FocusedText  : value/text of the focused element (post-typing verification)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Automation;

namespace CU
{
    public class El
    {
        public int id, l, t, r, b;
        public string type = "", name = "";
        public bool enabled = true;
        public int Cx { get { return (l + r) / 2; } }
        public int Cy { get { return (t + b) / 2; } }
        public long Area { get { return (long)(r - l) * (b - t); } }
    }

    public static class Uia
    {
        static readonly ControlType[] Interactive = new ControlType[] {
            ControlType.Button, ControlType.Edit, ControlType.ListItem, ControlType.MenuItem, ControlType.TabItem,
            ControlType.CheckBox, ControlType.RadioButton, ControlType.Hyperlink, ControlType.ComboBox, ControlType.TreeItem,
            ControlType.SplitButton, ControlType.DataItem, ControlType.Slider, ControlType.Spinner, ControlType.Document };

        public static string LastStatus = "";
        static volatile bool busy = false;   // a timed-out walk may still be running on its thread
        public static long LastMs = 0;

        public static List<El> Collect(long hwnd, int timeoutMs, bool text, int max)
        {
            if (busy) { LastStatus = "busy (previous UIA walk still running)"; LastMs = 0; return new List<El>(); }
            List<El> res = null;
            string err = null;
            busy = true;
            IntPtr h = new IntPtr(hwnd);
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            Thread th = new Thread(delegate ()
            {
                try { res = Walk(h, text, max); }
                catch (Exception e) { err = e.GetType().Name + ": " + e.Message; }
                finally { busy = false; }
            });
            th.IsBackground = true;
            th.SetApartmentState(ApartmentState.MTA);
            th.Start();
            bool done = th.Join(timeoutMs);
            LastMs = sw.ElapsedMilliseconds;
            if (!done) { LastStatus = "timeout"; return new List<El>(); }
            LastStatus = err ?? "ok";
            return res ?? new List<El>();
        }

        static List<El> Walk(IntPtr h, bool text, int max)
        {
            Core.A();
            N.RECT wb = Core.Bounds(h);
            long warea = (long)(wb.R - wb.L) * (wb.B - wb.T);
            AutomationElement root = AutomationElement.FromHandle(h);
            List<Condition> cs = new List<Condition>();
            foreach (ControlType ct in Interactive) cs.Add(new PropertyCondition(AutomationElement.ControlTypeProperty, ct));
            if (text) cs.Add(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
            Condition cond = new AndCondition(new OrCondition(cs.ToArray()),
                                              new PropertyCondition(AutomationElement.IsOffscreenProperty, false));
            CacheRequest cr = new CacheRequest();
            cr.Add(AutomationElement.NameProperty);
            cr.Add(AutomationElement.ControlTypeProperty);
            cr.Add(AutomationElement.BoundingRectangleProperty);
            cr.Add(AutomationElement.IsEnabledProperty);
            cr.TreeScope = TreeScope.Element;
            cr.AutomationElementMode = AutomationElementMode.None;
            AutomationElementCollection col;
            using (cr.Activate()) col = root.FindAll(TreeScope.Descendants, cond);
            List<El> l = new List<El>();
            HashSet<string> seen = new HashSet<string>();
            foreach (AutomationElement e in col)
            {
                System.Windows.Rect rc;
                try { rc = e.Cached.BoundingRectangle; } catch { continue; }
                if (rc.IsEmpty || double.IsInfinity(rc.Width) || double.IsNaN(rc.Width)) continue;
                int L = Math.Max(wb.L, (int)Math.Round(rc.Left)), T = Math.Max(wb.T, (int)Math.Round(rc.Top));
                int R = Math.Min(wb.R, (int)Math.Round(rc.Right)), B = Math.Min(wb.B, (int)Math.Round(rc.Bottom));
                if (R - L < 4 || B - T < 4) continue;
                string type = "";
                try { type = e.Cached.ControlType.ProgrammaticName.Replace("ControlType.", ""); } catch { }
                if (type == "Document" && (long)(R - L) * (B - T) > warea * 6 / 10) continue;
                string name = "";
                try { name = (e.Cached.Name ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim(); } catch { }
                if (type == "Text" && name.Length == 0) continue;
                if (name.Length > 60) name = name.Substring(0, 60) + "\u2026";
                if (!seen.Add(L + "," + T + "," + R + "," + B + "," + type)) continue;
                El x = new El();
                x.type = type; x.name = name; x.l = L; x.t = T; x.r = R; x.b = B;
                try { x.enabled = e.Cached.IsEnabled; } catch { }
                l.Add(x);
                if (l.Count >= max) break;
            }
            l.Sort(delegate (El a, El b)
            {
                int ra = a.Cy / 40, rb = b.Cy / 40;
                if (ra != rb) return ra.CompareTo(rb);
                return a.l.CompareTo(b.l);
            });
            for (int i = 0; i < l.Count; i++) l[i].id = i + 1;
            return l;
        }

        // ---------------------------------------------------------------- cache tied to a frame
        public static void SaveCache(string path, Frame f, List<El> l)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(f.hwnd + "\t" + f.ts + "\t" + f.bl + "\t" + f.bt + "\t" + f.br + "\t" + f.bb + "\n");
            foreach (El e in l)
                sb.Append(e.id + "\t" + e.type + "\t" + e.l + "\t" + e.t + "\t" + e.r + "\t" + e.b + "\t" + (e.enabled ? 1 : 0) + "\t" + e.name + "\n");
            string d = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        // returns null if missing; hdr = [hwnd, ts, bl, bt, br, bb]
        public static List<El> LoadCache(string path, out long[] hdr)
        {
            hdr = null;
            if (!File.Exists(path)) return null;
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            if (lines.Length == 0) return null;
            string[] h = lines[0].Split('\t');
            hdr = new long[6];
            for (int i = 0; i < 6 && i < h.Length; i++) long.TryParse(h[i], out hdr[i]);
            List<El> l = new List<El>();
            for (int i = 1; i < lines.Length; i++)
            {
                string[] p = lines[i].Split(new char[] { '\t' }, 8);
                if (p.Length < 7) continue;
                El e = new El();
                int.TryParse(p[0], out e.id); e.type = p[1];
                int.TryParse(p[2], out e.l); int.TryParse(p[3], out e.t); int.TryParse(p[4], out e.r); int.TryParse(p[5], out e.b);
                e.enabled = p[6] == "1"; e.name = p.Length > 7 ? p[7] : "";
                l.Add(e);
            }
            return l;
        }

        // compact element list in frame IMAGE coordinates: [[id,"type","name",cx,cy,w,h],...]
        public static string ListJson(List<El> l, Frame f)
        {
            StringBuilder sb = new StringBuilder("[");
            for (int i = 0; i < l.Count; i++)
            {
                El e = l[i];
                double[] c = f.ToImage(e.Cx, e.Cy);
                if (i > 0) sb.Append(",");
                sb.Append("[" + e.id + "," + J.Q(e.type) + "," + J.Q(e.name) + "," + (int)c[0] + "," + (int)c[1] + "," +
                          (int)Math.Round((e.r - e.l) * f.s) + "," + (int)Math.Round((e.b - e.t) * f.s) + (e.enabled ? "" : ",0") + "]");
            }
            return sb.Append("]").ToString();
        }

        // boxes for Core.Som, in image coords: "id,l,t,r,b;..."
        public static string Boxes(List<El> l, Frame f)
        {
            StringBuilder sb = new StringBuilder();
            foreach (El e in l)
            {
                double[] a = f.ToImage(e.l, e.t), b = f.ToImage(e.r, e.b);
                if (sb.Length > 0) sb.Append(";");
                sb.Append(e.id + "," + (int)a[0] + "," + (int)a[1] + "," + (int)b[0] + "," + (int)b[1]);
            }
            return sb.ToString();
        }

        public static El ById(List<El> l, int id)
        {
            if (l == null) return null;
            foreach (El e in l) if (e.id == id) return e;
            return null;
        }

        // name match: exact (case/space-insensitive) first, then contains; prefers interactive, then smaller
        public static List<El> ByName(List<El> l, string q)
        {
            string n = Norm(q);
            List<El> exact = new List<El>(), part = new List<El>();
            foreach (El e in l)
            {
                string en = Norm(e.name);
                if (en.Length == 0) continue;
                if (en == n) exact.Add(e); else if (en.Contains(n)) part.Add(e);
            }
            Comparison<El> cmp = delegate (El a, El b)
            {
                int ta = a.type == "Text" ? 1 : 0, tb = b.type == "Text" ? 1 : 0;
                if (ta != tb) return ta.CompareTo(tb);
                return a.Area.CompareTo(b.Area);
            };
            exact.Sort(cmp); part.Sort(cmp);
            exact.AddRange(part);
            return exact;
        }
        static string Norm(string s)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in s ?? "") if (!char.IsWhiteSpace(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        // snap a physical point to the best element: inside -> smallest containing element (no move);
        // otherwise nearest element within tol px. Returns the element or null.
        public static El SnapTarget(List<El> l, int sx, int sy, int tol, out bool inside)
        {
            inside = false;
            El best = null;
            foreach (El e in l)
            {
                if (e.type == "Document") continue;
                if (sx >= e.l && sx < e.r && sy >= e.t && sy < e.b)
                {
                    if (best == null || !inside || e.Area < best.Area) { best = e; inside = true; }
                }
            }
            if (inside) return best;
            double bd = double.MaxValue;
            foreach (El e in l)
            {
                if (e.type == "Document" || e.type == "Text") continue;
                double dx = Math.Max(Math.Max(e.l - sx, 0), sx - (e.r - 1));
                double dy = Math.Max(Math.Max(e.t - sy, 0), sy - (e.b - 1));
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (d <= tol && d < bd) { bd = d; best = e; }
            }
            return best;
        }

        // ---------------------------------------------------------------- Set-of-Marks overlay
        // draws numbered boxes onto a copy of the frame image. boxes: "id,l,t,r,b;..." (image coords)
        public static string Som(string inPath, string outPath, string boxes)
        {
            try
            {
                Color[] pal = { Color.FromArgb(230, 25, 75), Color.FromArgb(0, 130, 200), Color.FromArgb(60, 180, 75), Color.FromArgb(245, 130, 48),
                                Color.FromArgb(145, 30, 180), Color.FromArgb(0, 128, 128), Color.FromArgb(170, 110, 40), Color.FromArgb(128, 0, 0) };
                using (Bitmap bmp = Core.LoadCopy(inPath))
                using (Graphics g = Graphics.FromImage(bmp))
                using (Font f = new Font("Arial", 11, FontStyle.Bold, GraphicsUnit.Pixel))
                using (SolidBrush white = new SolidBrush(Color.White))
                {
                    g.SmoothingMode = SmoothingMode.None;
                    List<RectangleF> used = new List<RectangleF>();
                    foreach (string it in (boxes ?? "").Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string[] p = it.Split(',');
                        if (p.Length < 5) continue;
                        int id = int.Parse(p[0]), l = int.Parse(p[1]), t = int.Parse(p[2]), r = int.Parse(p[3]), b = int.Parse(p[4]);
                        Color c = pal[id % pal.Length];
                        using (Pen pen = new Pen(Color.FromArgb(200, c), 1.5f)) g.DrawRectangle(pen, l, t, Math.Max(1, r - l - 1), Math.Max(1, b - t - 1));
                        string s = id.ToString();
                        SizeF z = g.MeasureString(s, f);
                        float lw = z.Width + 1, lh = z.Height - 1;
                        // label candidates: top-left outside, top-left inside, bottom-left outside, top-right inside
                        PointF[] cand = { new PointF(l, t - lh), new PointF(l, t), new PointF(l, b), new PointF(r - lw, t) };
                        PointF pos = cand[1];
                        foreach (PointF q in cand)
                        {
                            RectangleF rr = new RectangleF(q.X, q.Y, lw, lh);
                            if (rr.X < 0 || rr.Y < 0 || rr.Right > bmp.Width || rr.Bottom > bmp.Height) continue;
                            bool clash = false;
                            foreach (RectangleF u in used) if (u.IntersectsWith(rr)) { clash = true; break; }
                            if (!clash) { pos = q; break; }
                        }
                        RectangleF box = new RectangleF(pos.X, pos.Y, lw, lh);
                        used.Add(box);
                        using (SolidBrush bg = new SolidBrush(Color.FromArgb(215, c))) g.FillRectangle(bg, box);
                        g.DrawString(s, f, white, pos.X, pos.Y - 1);
                    }
                    Core.Save(bmp, outPath, 90);
                }
                return "OK";
            }
            catch (Exception ex) { return "ERR " + ex.Message; }
        }

        // ---------------------------------------------------------------- focused element text (verification)
        public static string FocusedText(int pid, int timeoutMs, out string status)
        {
            string res = null, st = "none";
            Thread th = new Thread(delegate ()
            {
                try
                {
                    Core.A();
                    AutomationElement f = AutomationElement.FocusedElement;
                    if (f == null) return;
                    if (pid != 0 && f.Current.ProcessId != pid) { st = "other-process"; return; }
                    object p;
                    if (f.TryGetCurrentPattern(ValuePattern.Pattern, out p)) { res = ((ValuePattern)p).Current.Value; st = "value"; }
                    else if (f.TryGetCurrentPattern(TextPattern.Pattern, out p)) { res = ((TextPattern)p).DocumentRange.GetText(4000); st = "text"; }
                    else st = "no-pattern";
                }
                catch (Exception e) { st = "error: " + e.Message; }
            });
            th.IsBackground = true;
            th.SetApartmentState(ApartmentState.MTA);
            th.Start();
            if (!th.Join(timeoutMs)) st = "timeout";
            status = st;
            return res;
        }
    }
}
