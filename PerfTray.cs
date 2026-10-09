// PerfTray - zeigt CPU/GPU/RAM/Speicher als Text direkt in der Taskleiste (links neben den Tray-Symbolen).
// Kompiliert mit dem in Windows enthaltenen C#-Compiler (siehe build.bat), keine Abhaengigkeiten.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("PerfTray")]
[assembly: AssemblyProduct("PerfTray")]

namespace PerfTray
{
    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            bool created;
            using (var mutex = new Mutex(true, "PerfTray_SingleInstance_8f3a21", out created))
            {
                if (!created) return; // laeuft schon
                try { SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new Overlay());
            }
        }
    }

    // ------------------------------------------------------------------
    // Messwerte: laeuft in einem Hintergrund-Thread, damit die Anzeige nie haengt
    // ------------------------------------------------------------------
    class Sampler
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile;
            public ulong ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX buffer);

        public volatile float Cpu = -1, Gpu = -1, DiskActivity = -1;
        public double RamTotalGB, RamUsedGB;
        public volatile int IntervalMs = 1000;

        volatile bool running = true;
        PerformanceCounter cpuCounter, diskIdleCounter;
        PerformanceCounterCategory gpuCategory;
        Dictionary<string, CounterSample> prevGpu;
        bool gpuSupported = true;

        public void Start()
        {
            var t = new Thread(Loop);
            t.IsBackground = true;
            t.Start();
        }

        public void Stop() { running = false; }

        void Loop()
        {
            cpuCounter = TryCounter("Processor Information", "% Processor Utility", "_Total")
                      ?? TryCounter("Processor", "% Processor Time", "_Total");
            diskIdleCounter = TryCounter("PhysicalDisk", "% Idle Time", "_Total");
            while (running)
            {
                try { Sample(); } catch { }
                Thread.Sleep(IntervalMs);
            }
        }

        static PerformanceCounter TryCounter(string cat, string name, string inst)
        {
            try
            {
                var c = new PerformanceCounter(cat, name, inst, true);
                c.NextValue();
                return c;
            }
            catch { return null; }
        }

        static float Clamp(float v) { return v < 0 ? 0 : (v > 100 ? 100 : v); }

        void Sample()
        {
            if (cpuCounter != null) Cpu = Clamp(cpuCounter.NextValue());
            if (diskIdleCounter != null) DiskActivity = Clamp(100f - diskIdleCounter.NextValue());

            var mem = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(mem))
            {
                const double GB = 1024.0 * 1024 * 1024;
                RamTotalGB = mem.ullTotalPhys / GB;
                RamUsedGB = (mem.ullTotalPhys - mem.ullAvailPhys) / GB;
            }

            if (gpuSupported) SampleGpu();
        }

        // GPU-Auslastung wie im Task-Manager: pro Grafikkarte und Engine-Typ (3D, Video, Copy ...)
        // summieren, dann den hoechsten Wert nehmen. Alle Instanzen mit einem einzigen Abruf lesen,
        // einzelne Zaehler waeren sehr teuer.
        void SampleGpu()
        {
            InstanceDataCollection data;
            try
            {
                if (gpuCategory == null) gpuCategory = new PerformanceCounterCategory("GPU Engine");
                data = gpuCategory.ReadCategory()["Utilization Percentage"];
            }
            catch { gpuSupported = false; Gpu = -1; return; }
            if (data == null) { gpuSupported = false; Gpu = -1; return; }

            var cur = new Dictionary<string, CounterSample>();
            var sums = new Dictionary<string, float>();
            foreach (InstanceData d in data.Values)
            {
                cur[d.InstanceName] = d.Sample;
                CounterSample old;
                if (prevGpu == null || !prevGpu.TryGetValue(d.InstanceName, out old)) continue;
                float v = CounterSample.Calculate(old, d.Sample);
                string key = GpuKey(d.InstanceName);
                float s;
                sums.TryGetValue(key, out s);
                sums[key] = s + v;
            }
            prevGpu = cur;
            Gpu = Clamp(sums.Count == 0 ? 0 : sums.Values.Max());
        }

        // "pid_1234_luid_0x0_0x1234_phys_0_eng_0_engtype_3D" -> "0x0_0x1234|3D"
        static string GpuKey(string inst)
        {
            string luid = "";
            int i = inst.IndexOf("luid_", StringComparison.Ordinal);
            int p = inst.IndexOf("_phys", StringComparison.Ordinal);
            if (i >= 0 && p > i) luid = inst.Substring(i + 5, p - i - 5);
            string eng = "";
            int e = inst.IndexOf("engtype_", StringComparison.Ordinal);
            if (e >= 0) eng = inst.Substring(e + 8);
            return luid + "|" + eng;
        }
    }

    // ------------------------------------------------------------------
    // Durchsichtiges Textfenster, das auf der Taskleiste liegt
    // ------------------------------------------------------------------
    class Overlay : Form
    {
        #region Win32
        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential)]
        struct SIZE { public int W, H; public SIZE(int w, int h) { W = w; H = h; } }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct BLENDFUNCTION { public byte Op, Flags, Alpha, Format; }

        [DllImport("user32.dll")] static extern IntPtr FindWindow(string cls, string name);
        [DllImport("user32.dll")] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string name);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
        [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("user32.dll")]
        static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dstDc, ref POINT dst, ref SIZE size,
            IntPtr srcDc, ref POINT src, int key, ref BLENDFUNCTION blend, int flags);

        [DllImport("shell32.dll")]
        static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
        const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
        #endregion

        // Sprache folgt der Windows-Anzeigesprache: Deutsch, sonst Englisch
        static readonly bool German = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de";
        static string T(string de, string en) { return German ? de : en; }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        readonly string settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PerfTray", "settings.ini");

        readonly Sampler sampler = new Sampler();
        readonly List<string> enabled = new List<string>();
        readonly List<string> favorites = new List<string>(); // Kennung wie "DOWNLOADS" oder "PATH:D:\Projekte"
        readonly List<Rectangle> favCells = new List<Rectangle>(); // Klickflaechen der Favoriten (Fensterkoordinaten)
        readonly ToolTip tip = new ToolTip();
        int hoveredFav = -1;
        readonly Dictionary<string, float> columnWidth = new Dictionary<string, float>(); // waechst nur -> kein Zappeln
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly System.Windows.Forms.Timer renderTimer = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer zTimer = new System.Windows.Forms.Timer();

        int fromRight = -1;   // Abstand rechter Rand -> rechter Rand der Taskleiste; -1 = automatisch
        bool keepMenuOpen, hiddenForFullscreen;
        bool mouseDown, dragging;
        Point dragStartCursor;
        int dragStartRight;
        Rectangle lastBounds;

        public Overlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Text = "PerfTray";

            LoadSettings();
            sampler.Start();

            menu.Opening += (s, e) => BuildMenu();
            menu.Closing += KeepOpenOnToggle;

            renderTimer.Interval = sampler.IntervalMs;
            renderTimer.Tick += (s, e) => Render();
            zTimer.Interval = 300;
            zTimer.Tick += (s, e) => KeepOnTaskbar();
        }

        // Beim An-/Abhaken bleibt das Menue offen (gilt auch fuer Untermenues)
        void KeepOpenOnToggle(object sender, ToolStripDropDownClosingEventArgs e)
        {
            if (keepMenuOpen && e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true;
        }

        void MarkKeepOpen()
        {
            keepMenuOpen = true;
            BeginInvoke((Action)(() => keepMenuOpen = false));
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80000 /*WS_EX_LAYERED*/ | 0x80 /*WS_EX_TOOLWINDOW*/ | 0x8 /*WS_EX_TOPMOST*/;
                return cp;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Render();
            renderTimer.Start();
            zTimer.Start();
        }

        // ---------------- Position ----------------

        static Rectangle TaskbarRect()
        {
            RECT r;
            IntPtr tb = FindWindow("Shell_TrayWnd", null);
            if (tb != IntPtr.Zero && GetWindowRect(tb, out r))
                return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            var s = Screen.PrimaryScreen;
            return new Rectangle(s.Bounds.Left, s.WorkingArea.Bottom, s.Bounds.Width, s.Bounds.Bottom - s.WorkingArea.Bottom);
        }

        // Rechter Rand fuer die automatische Position: direkt links neben dem Infobereich (Tray + Uhr)
        static int AutoRight(Rectangle tb)
        {
            RECT r;
            IntPtr bar = FindWindow("Shell_TrayWnd", null);
            IntPtr notify = FindWindowEx(bar, IntPtr.Zero, "TrayNotifyWnd", null);
            if (notify != IntPtr.Zero && GetWindowRect(notify, out r) && r.Right > r.Left && r.Left > tb.Left)
                return r.Left - 6;
            return tb.Right - 320;
        }

        int CurrentRight(Rectangle tb)
        {
            return fromRight < 0 ? AutoRight(tb) : tb.Right - fromRight;
        }

        // ---------------- Inhalt ----------------

        bool On(string id) { return enabled.Contains(id); }

        static string Pct(float v) { return v < 0 ? "–" : ((int)Math.Round(v)) + "%"; }

        static string GB(double gb)
        {
            if (gb >= 1000) return (gb / 1024).ToString("0.0") + " TB";
            if (gb >= 100) return gb.ToString("0") + " GB";
            return gb.ToString("0.0") + " GB";
        }

        static string Join(string a, string b)
        {
            if (a != null && b != null) return a + " / " + b;
            return a ?? b;
        }

        class Item { public string Key, Text; public bool Hot; }

        List<Item> BuildItems()
        {
            var items = new List<Item>();
            if (On("CPU")) items.Add(new Item { Key = "CPU", Text = "CPU: " + Pct(sampler.Cpu), Hot = sampler.Cpu >= 90 });
            if (On("GPU")) items.Add(new Item { Key = "GPU", Text = "GPU: " + Pct(sampler.Gpu), Hot = sampler.Gpu >= 90 });

            bool rp = On("RAM%"), rg = On("RAMGB");
            if (rp || rg)
            {
                float p = sampler.RamTotalGB > 0 ? (float)(sampler.RamUsedGB / sampler.RamTotalGB * 100) : -1;
                items.Add(new Item
                {
                    Key = "RAM",
                    Text = "RAM: " + Join(rp ? Pct(p) : null, rg ? GB(sampler.RamUsedGB) : null),
                    Hot = p >= 90
                });
            }

            var letters = enabled.Where(i => i.StartsWith("DISK%:") || i.StartsWith("DISKGB:") || i.StartsWith("DISKFREE:"))
                                 .Select(i => i.Substring(i.IndexOf(':') + 1)).Distinct().OrderBy(l => l);
            foreach (var l in letters)
            {
                string text = l + ": –";
                bool hot = false;
                try
                {
                    var d = new DriveInfo(l);
                    const double G = 1024.0 * 1024 * 1024;
                    double total = d.TotalSize / G, used = (d.TotalSize - d.TotalFreeSpace) / G;
                    float p = total > 0 ? (float)(used / total * 100) : 0;
                    string usedText = Join(On("DISK%:" + l) ? Pct(p) : null, On("DISKGB:" + l) ? GB(used) : null);
                    string freeText = On("DISKFREE:" + l) ? GB(total - used) + T(" frei", " free") : null;
                    text = l + ": " + (usedText != null && freeText != null ? usedText + " · " + freeText : usedText ?? freeText);
                    hot = p >= 90;
                }
                catch { }
                items.Add(new Item { Key = "DISK" + l, Text = text, Hot = hot });
            }

            if (On("DISKACT"))
                items.Add(new Item { Key = "ACT", Text = T("Disk-Aktivität: ", "Disk activity: ") + Pct(sampler.DiskActivity), Hot = sampler.DiskActivity >= 90 });
            return items;
        }

        // ---------------- Favoriten-Ordner ----------------

        static readonly string[] KnownFavorites = { "HOME", "DESKTOP", "DOWNLOADS", "DOCUMENTS", "PICTURES", "MUSIC", "VIDEOS", "TRASH" };

        static string KnownFolder(string guid)
        {
            IntPtr p;
            if (SHGetKnownFolderPath(new Guid(guid), 0, IntPtr.Zero, out p) != 0) return null;
            try { return Marshal.PtrToStringUni(p); }
            finally { Marshal.FreeCoTaskMem(p); }
        }

        // Pfad eines Favoriten (beruecksichtigt z. B. nach OneDrive verschobene Ordner)
        static string FavPath(string id)
        {
            switch (id)
            {
                case "HOME": return KnownFolder("5E6C858F-0E22-4760-9AFE-EA3317B67173");
                case "DESKTOP": return KnownFolder("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
                case "DOWNLOADS": return KnownFolder("374DE290-123F-4565-9164-39C4925E467B");
                case "DOCUMENTS": return KnownFolder("FDD39AD0-238F-46AF-ADB4-6C85480369C7");
                case "PICTURES": return KnownFolder("33E28130-4E1E-4676-835A-98395C3BC3BB");
                case "MUSIC": return KnownFolder("4BD8D571-6D19-48D3-BE97-422220080E43");
                case "VIDEOS": return KnownFolder("18989B1D-99B5-455B-841C-AB7C74E4DDFC");
                case "TRASH": return "shell:RecycleBinFolder";
            }
            return id.StartsWith("PATH:") ? id.Substring(5) : null;
        }

        static string FavName(string id)
        {
            switch (id)
            {
                case "HOME": return T("Benutzerordner", "User folder");
                case "DESKTOP": return "Desktop";
                case "DOWNLOADS": return "Downloads";
                case "DOCUMENTS": return T("Dokumente", "Documents");
                case "PICTURES": return T("Bilder", "Pictures");
                case "MUSIC": return T("Musik", "Music");
                case "VIDEOS": return "Videos";
                case "TRASH": return T("Papierkorb", "Recycle Bin");
            }
            string path = FavPath(id) ?? "";
            string name = Path.GetFileName(path.TrimEnd('\\'));
            return string.IsNullOrEmpty(name) ? path : name;
        }

        static string FavIcon(string id)
        {
            switch (id)
            {
                case "HOME": return "house";
                case "DESKTOP": return "monitor";
                case "DOWNLOADS": return "download";
                case "DOCUMENTS": return "file-text";
                case "PICTURES": return "image";
                case "MUSIC": return "music";
                case "VIDEOS": return "video";
                case "TRASH": return "trash-2";
            }
            string path = FavPath(id) ?? "";
            return path.Length <= 3 && path.EndsWith(":\\") ? "hard-drive" : "folder";
        }

        // Bekannte Ordner in fester Reihenfolge, eigene Ordner dahinter
        IEnumerable<string> OrderedFavorites()
        {
            return KnownFavorites.Where(favorites.Contains).Concat(favorites.Where(f => f.StartsWith("PATH:")));
        }

        void OpenFavorite(string id)
        {
            string path = FavPath(id);
            if (path == null) return;
            if (!path.StartsWith("shell:") && !Directory.Exists(path))
            {
                MessageBox.Show(T("Ordner nicht gefunden:\n", "Folder not found:\n") + path, "PerfTray",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try { Process.Start("explorer.exe", "\"" + path + "\""); } catch { }
        }

        static bool LightTheme()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return k != null && Convert.ToInt32(k.GetValue("SystemUsesLightTheme", 0)) == 1;
            }
            catch { return false; }
        }

        void Render()
        {
            if (!IsHandleCreated) return;
            Rectangle tb = TaskbarRect();
            int barH = Math.Max(24, Math.Min(tb.Height, 80));
            float fontPx = Math.Max(11f, barH * 0.29f);
            var items = BuildItems();
            int rows = barH >= fontPx * 2.5f ? 2 : 1;
            bool light = LightTheme();
            Color textColor = light ? Color.FromArgb(20, 20, 20) : Color.White;
            Color hotColor = light ? Color.FromArgb(200, 30, 30) : Color.FromArgb(255, 110, 100);

            using (var font = new Font("Segoe UI", fontPx, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var probe = new Bitmap(1, 1))
            using (var pg = Graphics.FromImage(probe))
            {
                var fmt = StringFormat.GenericTypographic;
                pg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                // Spalten von oben nach unten fuellen (2 Zeilen)
                var cols = new List<List<Item>>();
                for (int i = 0; i < items.Count; i += rows) cols.Add(items.Skip(i).Take(rows).ToList());
                var widths = new List<float>();
                for (int c = 0; c < cols.Count; c++)
                {
                    float w = cols[c].Max(it => pg.MeasureString(it.Text, font, 2000, fmt).Width);
                    string key = string.Join("+", cols[c].Select(it => it.Key));
                    float old;
                    if (columnWidth.TryGetValue(key, out old) && old > w) w = old;
                    columnWidth[key] = w;
                    widths.Add((float)Math.Ceiling(w));
                }

                float pad = fontPx * 0.5f, gap = fontPx * 1.2f, lineH = fontPx * 1.32f;

                // Favoriten links vor den Werten
                var favs = OrderedFavorites().ToList();
                int cellW = (int)Math.Round(barH * 0.72f), cellH = (int)Math.Round(barH * 0.8f);
                float iconPx = (float)Math.Round(barH * 0.42f);
                float favW = favs.Count * cellW + (favs.Count > 0 && cols.Count > 0 ? gap * 0.6f : 0);

                int width = (int)Math.Ceiling(pad * 2 + favW + widths.Sum() + gap * Math.Max(0, cols.Count - 1));
                width = Math.Max(width, 20);
                int height = barH;
                float top = (height - rows * lineH) / 2f;
                if (hoveredFav >= favs.Count) hoveredFav = -1;

                using (var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                        // fast unsichtbarer Hintergrund, damit das Fenster Mausklicks bekommt
                        g.Clear(Color.FromArgb(1, 0, 0, 0));
                        float x = pad;

                        favCells.Clear();
                        for (int f = 0; f < favs.Count; f++)
                        {
                            var cell = new Rectangle((int)x + f * cellW, (height - cellH) / 2, cellW, cellH);
                            favCells.Add(cell);
                            if (f == hoveredFav)
                            {
                                using (var hb = new SolidBrush(light ? Color.FromArgb(28, 0, 0, 0) : Color.FromArgb(40, 255, 255, 255)))
                                using (var rr = RoundedRect(cell, barH * 0.12f))
                                {
                                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                                    g.FillPath(hb, rr);
                                }
                            }
                            Lucide.Draw(g, FavIcon(favs[f]), new RectangleF(
                                cell.X + (cellW - iconPx) / 2f, cell.Y + (cellH - iconPx) / 2f, iconPx, iconPx), textColor);
                        }
                        x += favW;

                        for (int c = 0; c < cols.Count; c++)
                        {
                            for (int r = 0; r < cols[c].Count; r++)
                            {
                                var it = cols[c][r];
                                float y = rows == 1 ? (height - lineH) / 2f : top + r * lineH;
                                using (var br = new SolidBrush(it.Hot ? hotColor : textColor))
                                    g.DrawString(it.Text, font, br, x, y + fontPx * 0.12f, fmt);
                            }
                            x += widths[c] + gap;
                        }
                    }

                    int right = CurrentRight(tb);
                    int left = Math.Max(tb.Left, Math.Min(right - width, tb.Right - width));
                    int ypos = tb.Top + (tb.Height - height) / 2;
                    Present(bmp, left, ypos);
                    lastBounds = new Rectangle(left, ypos, width, height);
                }
            }
        }

        static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle r, float radius)
        {
            float d = radius * 2;
            var gp = new System.Drawing.Drawing2D.GraphicsPath();
            gp.AddArc(r.X, r.Y, d, d, 180, 90);
            gp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            gp.CloseFigure();
            return gp;
        }

        void Present(Bitmap bmp, int x, int y)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
            IntPtr oldBmp = SelectObject(memDc, hBmp);
            try
            {
                var size = new SIZE(bmp.Width, bmp.Height);
                var src = new POINT(0, 0);
                var dst = new POINT(x, y);
                var blend = new BLENDFUNCTION { Op = 0, Flags = 0, Alpha = 255, Format = 1 /*AC_SRC_ALPHA*/ };
                UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2 /*ULW_ALPHA*/);
            }
            finally
            {
                SelectObject(memDc, oldBmp);
                DeleteObject(hBmp);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        // Ueber der Taskleiste bleiben, aber bei Vollbild (Spiele, Videos) ausblenden
        void KeepOnTaskbar()
        {
            bool fs = ForegroundIsFullscreen();
            if (fs != hiddenForFullscreen)
            {
                hiddenForFullscreen = fs;
                ShowWindow(Handle, fs ? SW_HIDE : SW_SHOWNOACTIVATE);
            }
            if (!fs && !menu.Visible)
                SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        bool ForegroundIsFullscreen()
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == Handle || fg == GetShellWindow() || fg == GetDesktopWindow()) return false;
            var sb = new StringBuilder(64);
            GetClassName(fg, sb, 64);
            string cls = sb.ToString();
            if (cls == "WorkerW" || cls == "Progman" || cls == "Shell_TrayWnd") return false;
            RECT r;
            if (!GetWindowRect(fg, out r)) return false;
            var scr = Screen.FromHandle(fg);
            if (!scr.Primary) return false;
            var b = scr.Bounds;
            return r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
        }

        // ---------------- Maus: Klick = Menue, Ziehen = verschieben ----------------

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            mouseDown = true;
            dragging = false;
            dragStartCursor = Cursor.Position;
            dragStartRight = lastBounds.Right;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!mouseDown) { UpdateHover(e.Location); return; }
            int dx = Cursor.Position.X - dragStartCursor.X;
            if (!dragging && Math.Abs(dx) < 5) return;
            dragging = true;
            Rectangle tb = TaskbarRect();
            int right = Math.Max(tb.Left + lastBounds.Width, Math.Min(tb.Right, dragStartRight + dx));
            fromRight = tb.Right - right;
            Render();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            bool wasDrag = dragging;
            mouseDown = dragging = false;
            if (wasDrag) { SaveSettings(); return; }

            int fav = FavAt(e.Location);
            if (e.Button == MouseButtons.Left && fav >= 0)
            {
                var list = OrderedFavorites().ToList();
                if (fav < list.Count) OpenFavorite(list[fav]);
                return;
            }
            if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right)
            {
                tip.Hide(this);
                SetForegroundWindow(Handle);
                menu.Show(Cursor.Position);
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoveredFav < 0) return;
            hoveredFav = -1;
            tip.Hide(this);
            Cursor = Cursors.Default;
            Render();
        }

        int FavAt(Point p)
        {
            for (int i = 0; i < favCells.Count; i++)
                if (favCells[i].Contains(p)) return i;
            return -1;
        }

        void UpdateHover(Point p)
        {
            int fav = FavAt(p);
            if (fav == hoveredFav) return;
            hoveredFav = fav;
            Cursor = fav >= 0 ? Cursors.Hand : Cursors.Default;
            if (fav >= 0)
            {
                var list = OrderedFavorites().ToList();
                if (fav < list.Count)
                    tip.Show(FavName(list[fav]), this, favCells[fav].X, -(int)(lastBounds.Height * 0.7f), 2500);
            }
            else tip.Hide(this);
            Render();
        }

        // ---------------- Menue ----------------

        IEnumerable<string> FixedDriveLetters()
        {
            DriveInfo[] all;
            try { all = DriveInfo.GetDrives(); } catch { yield break; }
            foreach (var d in all)
            {
                bool ok;
                try { ok = d.DriveType == DriveType.Fixed && d.IsReady; } catch { ok = false; }
                if (ok) yield return d.Name.Substring(0, 1);
            }
        }

        static string NameFor(string id)
        {
            switch (id)
            {
                case "CPU": return T("CPU-Auslastung", "CPU usage");
                case "GPU": return T("GPU-Auslastung", "GPU usage");
                case "RAM%": return "RAM in %";
                case "RAMGB": return "RAM in GB";
                case "DISKACT": return T("Datenträger-Aktivität", "Disk activity");
            }
            string letter = id.Substring(id.IndexOf(':') + 1);
            if (id.StartsWith("DISKFREE")) return T("Laufwerk ", "Drive ") + letter + T(": frei in GB", ": free in GB");
            return T("Laufwerk ", "Drive ") + letter + T(": belegt in ", ": used in ") + (id.StartsWith("DISK%") ? "%" : "GB");
        }

        void BuildMenu()
        {
            menu.Items.Clear();

            var title = new ToolStripMenuItem(T("PerfTray – anzeigen:", "PerfTray – show:")) { Enabled = false };
            title.Font = new Font(title.Font, FontStyle.Bold);
            menu.Items.Add(title);

            foreach (var id in new[] { "CPU", "GPU", "RAM%", "RAMGB", "DISKACT" })
                menu.Items.Add(MetricItem(id));

            // Festplatten: belegt / frei pro Laufwerk
            var drives = new ToolStripMenuItem(T("Festplatten", "Drives"));
            drives.Image = Lucide.ToBitmap("hard-drive", 16, SystemColors.MenuText);
            drives.DropDown.Closing += KeepOpenOnToggle;
            bool firstDrive = true;
            foreach (var l in FixedDriveLetters())
            {
                if (!firstDrive) drives.DropDownItems.Add(new ToolStripSeparator());
                firstDrive = false;
                foreach (var kind in new[] { "DISK%:", "DISKGB:", "DISKFREE:" })
                    drives.DropDownItems.Add(MetricItem(kind + l));
            }
            menu.Items.Add(drives);

            // Favoriten-Ordner
            var favMenu = new ToolStripMenuItem(T("Favoriten-Ordner", "Favorite folders"));
            favMenu.Image = Lucide.ToBitmap("folder", 16, SystemColors.MenuText);
            favMenu.DropDown.Closing += KeepOpenOnToggle;
            var favIds = KnownFavorites.ToList();
            foreach (var l in FixedDriveLetters()) favIds.Add("PATH:" + l + ":\\");
            favIds.AddRange(favorites.Where(f => f.StartsWith("PATH:") && !favIds.Contains(f)));
            foreach (var id in favIds)
            {
                string fav = id;
                string label = id.StartsWith("PATH:") && FavIcon(id) == "hard-drive"
                    ? T("Laufwerk ", "Drive ") + FavPath(id).TrimEnd('\\')
                    : FavName(id);
                var it = new ToolStripMenuItem(label) { Checked = favorites.Contains(id) };
                it.Image = Lucide.ToBitmap(FavIcon(id), 16, SystemColors.MenuText);
                if (id.StartsWith("PATH:") && FavIcon(id) == "folder") it.ToolTipText = FavPath(id);
                it.Click += (s, e) =>
                {
                    MarkKeepOpen();
                    if (favorites.Contains(fav))
                    {
                        if (favorites.Count == 1 && enabled.Count == 0) return;
                        favorites.Remove(fav);
                    }
                    else favorites.Add(fav);
                    ((ToolStripMenuItem)s).Checked = favorites.Contains(fav);
                    SaveSettings();
                    Render();
                };
                favMenu.DropDownItems.Add(it);
            }
            favMenu.DropDownItems.Add(new ToolStripSeparator());
            var add = new ToolStripMenuItem(T("Ordner hinzufügen…", "Add folder…"));
            add.Click += (s, e) => AddCustomFolder();
            favMenu.DropDownItems.Add(add);
            menu.Items.Add(favMenu);

            menu.Items.Add(new ToolStripSeparator());

            var interval = new ToolStripMenuItem(T("Aktualisierung", "Update interval"));
            foreach (var ms in new[] { 1000, 2000, 5000 })
            {
                int v = ms;
                var it = new ToolStripMenuItem(T("alle ", "every ") + (v / 1000) + " s") { Checked = sampler.IntervalMs == v };
                it.Click += (s, e) => { sampler.IntervalMs = v; renderTimer.Interval = v; SaveSettings(); };
                interval.DropDownItems.Add(it);
            }
            menu.Items.Add(interval);

            var reset = new ToolStripMenuItem(T("Position zurücksetzen (neben die Uhr)", "Reset position (next to the clock)"));
            reset.Click += (s, e) => { fromRight = -1; SaveSettings(); Render(); };
            menu.Items.Add(reset);

            var autostart = new ToolStripMenuItem(T("Mit Windows starten", "Start with Windows")) { Checked = IsAutostart() };
            autostart.Click += (s, e) => SetAutostart(!IsAutostart());
            menu.Items.Add(autostart);

            var tm = new ToolStripMenuItem(T("Task-Manager öffnen", "Open Task Manager"));
            tm.Click += (s, e) => { try { Process.Start("taskmgr.exe"); } catch { } };
            menu.Items.Add(tm);

            menu.Items.Add(new ToolStripSeparator());
            var exit = new ToolStripMenuItem(T("Beenden", "Exit"));
            exit.Click += (s, e) => Quit();
            menu.Items.Add(exit);
        }

        ToolStripMenuItem MetricItem(string id)
        {
            var it = new ToolStripMenuItem(NameFor(id)) { Checked = On(id) };
            it.Click += (s, e) =>
            {
                MarkKeepOpen();
                if (On(id))
                {
                    // mindestens etwas muss sichtbar bleiben, sonst ist nichts mehr anklickbar
                    if (enabled.Count == 1 && favorites.Count == 0) return;
                    enabled.Remove(id);
                }
                else enabled.Add(id);
                ((ToolStripMenuItem)s).Checked = On(id);
                columnWidth.Clear();
                SaveSettings();
                Render();
            };
            return it;
        }

        void AddCustomFolder()
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = T("Ordner für die Taskleiste auswählen", "Choose a folder for the taskbar");
                if (dlg.ShowDialog(this) != DialogResult.OK || string.IsNullOrEmpty(dlg.SelectedPath)) return;
                string id = "PATH:" + dlg.SelectedPath;
                if (!favorites.Contains(id)) favorites.Add(id);
                SaveSettings();
                Render();
            }
        }

        void Quit()
        {
            renderTimer.Stop();
            zTimer.Stop();
            sampler.Stop();
            Close();
        }

        // ---------------- Einstellungen / Autostart ----------------

        void LoadSettings()
        {
            try
            {
                if (File.Exists(settingsPath))
                {
                    foreach (var line in File.ReadAllLines(settingsPath))
                    {
                        int eq = line.IndexOf('=');
                        if (eq < 0) continue;
                        string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                        int n;
                        if (k == "metrics")
                        {
                            hasMetricsSetting = true;
                            enabled.AddRange(v.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()));
                        }
                        else if (k == "interval" && int.TryParse(v, out n) && n >= 500)
                            sampler.IntervalMs = n;
                        else if (k == "fromRight" && int.TryParse(v, out n))
                            fromRight = n;
                        else if (k == "favorites")
                        {
                            hasFavoritesSetting = true;
                            // '|' kommt in Windows-Pfaden nicht vor
                            favorites.AddRange(v.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()));
                        }
                    }
                }
            }
            catch { }
            if (!hasFavoritesSetting) favorites.AddRange(new[] { "DOWNLOADS", "DOCUMENTS", "DESKTOP" });
            if (enabled.Count == 0 && !hasMetricsSetting) enabled.AddRange(new[] { "CPU", "GPU", "RAM%", "RAMGB" });
            if (enabled.Count == 0 && favorites.Count == 0) enabled.Add("CPU");
        }

        bool hasFavoritesSetting, hasMetricsSetting;

        void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
                File.WriteAllLines(settingsPath, new[]
                {
                    "metrics=" + string.Join(",", enabled),
                    "interval=" + sampler.IntervalMs,
                    "fromRight=" + fromRight,
                    "favorites=" + string.Join("|", favorites)
                });
            }
            catch { }
        }

        static bool IsAutostart()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue("PerfTray") != null;
        }

        static void SetAutostart(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue("PerfTray", "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue("PerfTray", false);
            }
        }
    }
}
