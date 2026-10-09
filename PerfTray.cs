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
        // Klickflaechen (Favoriten, Medien-Buttons) in Fensterkoordinaten, werden bei jedem Zeichnen neu gesetzt
        class HitArea { public Rectangle R; public string Key, Tip; public Action Click; }
        readonly List<HitArea> hits = new List<HitArea>();
        readonly ToolTip tip = new ToolTip();
        string hoveredKey;
        readonly MediaWatcher media = new MediaWatcher();
        bool showMedia = true, showCover = true;
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
            media.Enabled = showMedia;
            try { media.Start(); } catch { media.Available = false; }

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

        List<Item> cachedItems;
        bool cachedLight;

        // fast = Animations-Frame: Messwerte und Design vom letzten normalen Zeichnen wiederverwenden
        void Render(bool fast = false)
        {
            if (!IsHandleCreated) return;
            Rectangle tb = TaskbarRect();
            int barH = Math.Max(24, Math.Min(tb.Height, 80));
            float fontPx = Math.Max(11f, barH * 0.29f);
            var items = fast && cachedItems != null ? cachedItems : (cachedItems = BuildItems());
            int rows = barH >= fontPx * 2.5f ? 2 : 1;
            bool light = fast ? cachedLight : (cachedLight = LightTheme());
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

                // Abschnitte von links nach rechts: Favoriten | Medien | Werte
                var favs = OrderedFavorites().ToList();
                int cellW = (int)Math.Round(barH * 0.72f), cellH = (int)Math.Round(barH * 0.8f);
                float iconPx = (float)Math.Round(barH * 0.42f);
                float favW = favs.Count * cellW;

                var mi = showMedia ? media.Current : null;
                int btnW = (int)Math.Round(barH * 0.62f);
                float coverPx = (float)Math.Round(barH * 0.66f);
                string line1 = "", line2 = "";
                float textW = 0, mediaW = 0;
                if (mi != null)
                {
                    line1 = rows == 1 && mi.Artist.Length > 0 ? mi.Title + " – " + mi.Artist : mi.Title;
                    line2 = rows == 1 ? "" : mi.Artist;
                    float measured = Math.Max(pg.MeasureString(line1, font, 4000, fmt).Width,
                                              pg.MeasureString(line2, font, 4000, fmt).Width);
                    textW = (float)Math.Ceiling(Math.Min(fontPx * 11f, measured) + 2);
                    if (hudActive) textW = Math.Max(textW, (float)Math.Ceiling(fontPx * 8.5f)); // Platz fuer die Lautstaerke-Anzeige
                    mediaW = (showCover ? coverPx + fontPx * 0.5f : 0) + textW + fontPx * 0.4f + 3 * btnW;
                }
                float statsW = widths.Sum() + gap * Math.Max(0, cols.Count - 1);

                var sections = new[] { favW, mediaW, statsW }.Where(w => w > 0).ToList();
                float sectionGap = gap * 0.8f;
                int width = (int)Math.Ceiling(pad * 2 + sections.Sum() + sectionGap * Math.Max(0, sections.Count - 1));
                width = Math.Max(width, 20);
                int height = barH;
                float top = (height - rows * lineH) / 2f;
                float radius = barH * 0.12f;

                using (var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                        // fast unsichtbarer Hintergrund, damit das Fenster Mausklicks bekommt
                        g.Clear(Color.FromArgb(1, 0, 0, 0));
                        float x = pad;

                        hits.Clear();
                        int cellY = (height - cellH) / 2;

                        // Favoriten
                        for (int f = 0; f < favs.Count; f++)
                        {
                            string fav = favs[f];
                            DrawButton(g, new Rectangle((int)x + f * cellW, cellY, cellW, cellH), "fav:" + fav,
                                FavIcon(fav), iconPx, textColor, light, radius, FavName(fav), () => OpenFavorite(fav));
                        }
                        if (favW > 0) x += favW + sectionGap;

                        // Medien: Cover | Titel + Interpret | Zurueck, Play/Pause, Weiter
                        if (mi != null)
                        {
                            if (showCover)
                            {
                                var cr = new RectangleF(x, (height - coverPx) / 2f, coverPx, coverPx);
                                DrawCover(g, mi.Cover, cr, textColor, iconPx);
                                hits.Add(new HitArea { R = new Rectangle((int)x, 0, (int)coverPx, height), Key = "media:cover" });
                                x += coverPx + fontPx * 0.5f;
                            }
                            var tf = new StringFormat(StringFormat.GenericTypographic);
                            tf.Trimming = StringTrimming.EllipsisCharacter;
                            // ohne LineLimit: sonst verschwindet eine Zeile ganz, wenn das Feld minimal zu niedrig ist
                            tf.FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
                            float y1 = rows == 1 ? (height - lineH) / 2f : top;
                            float boxH = lineH + fontPx * 0.5f;
                            if (hudActive)
                                DrawVolumeHud(g, new RectangleF(x, 0, textW, height), font, textColor, iconPx, fontPx);
                            else
                            {
                                using (var br = new SolidBrush(textColor))
                                    g.DrawString(line1, font, br, new RectangleF(x, y1 + fontPx * 0.12f, textW, boxH), tf);
                                if (line2.Length > 0)
                                    using (var br = new SolidBrush(Color.FromArgb(170, textColor)))
                                        g.DrawString(line2, font, br, new RectangleF(x, top + lineH + fontPx * 0.12f, textW, boxH), tf);
                            }
                            hits.Add(new HitArea
                            {
                                R = new Rectangle((int)x, 0, (int)textW, height),
                                Key = "media:text",
                                Tip = mi.Artist.Length > 0 ? mi.Title + "\n" + mi.Artist : mi.Title
                            });
                            x += textW + fontPx * 0.4f;

                            float bIcon = (float)Math.Round(iconPx * 0.85f);
                            DrawButton(g, new Rectangle((int)x, cellY, btnW, cellH), "media:prev", "skip-back", bIcon,
                                mi.CanPrev ? textColor : Color.FromArgb(90, textColor), light, radius,
                                T("Zurück", "Previous"), mi.CanPrev ? (Action)(() => MediaCommand("prev")) : null);
                            DrawButton(g, new Rectangle((int)x + btnW, cellY, btnW, cellH), "media:play",
                                mi.Playing ? "pause" : "play", bIcon, textColor, light, radius,
                                mi.Playing ? T("Pause", "Pause") : T("Abspielen", "Play"), () => MediaCommand("toggle"));
                            DrawButton(g, new Rectangle((int)x + 2 * btnW, cellY, btnW, cellH), "media:next", "skip-forward", bIcon,
                                mi.CanNext ? textColor : Color.FromArgb(90, textColor), light, radius,
                                T("Weiter", "Next"), mi.CanNext ? (Action)(() => MediaCommand("next")) : null);
                            x += 3 * btnW + sectionGap;
                        }

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

        // Icon-Button mit Hover-Hervorhebung; registriert die Klickflaeche
        void DrawButton(Graphics g, Rectangle cell, string key, string icon, float iconPx, Color color,
                        bool light, float radius, string tipText, Action click)
        {
            if (click != null && key == hoveredKey)
            {
                using (var hb = new SolidBrush(light ? Color.FromArgb(28, 0, 0, 0) : Color.FromArgb(40, 255, 255, 255)))
                using (var rr = RoundedRect(cell, radius))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.FillPath(hb, rr);
                }
            }
            Lucide.Draw(g, icon, new RectangleF(
                cell.X + (cell.Width - iconPx) / 2f, cell.Y + (cell.Height - iconPx) / 2f, iconPx, iconPx), color);
            hits.Add(new HitArea { R = cell, Key = key, Tip = tipText, Click = click });
        }

        // Cover quadratisch zugeschnitten mit runden Ecken; ohne Cover ein Musik-Icon
        static void DrawCover(Graphics g, Bitmap cover, RectangleF r, Color color, float iconPx)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var clip = RoundedRect(Rectangle.Round(r), r.Width * 0.15f))
            {
                if (cover == null)
                {
                    using (var bg = new SolidBrush(Color.FromArgb(40, color))) g.FillPath(bg, clip);
                    float s = iconPx * 0.8f;
                    Lucide.Draw(g, "music", new RectangleF(r.X + (r.Width - s) / 2, r.Y + (r.Height - s) / 2, s, s), color);
                    return;
                }
                var state = g.Save();
                g.SetClip(clip);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                int side = Math.Min(cover.Width, cover.Height);
                var src = new Rectangle((cover.Width - side) / 2, (cover.Height - side) / 2, side, side);
                g.DrawImage(cover, Rectangle.Round(r), src, GraphicsUnit.Pixel);
                g.Restore(state);
            }
        }

        readonly System.Windows.Forms.Timer refreshSoon = new System.Windows.Forms.Timer();

        void MediaCommand(string what)
        {
            switch (what)
            {
                case "prev": media.Previous(); break;
                case "next": media.Next(); break;
                case "stop": media.StopPlayback(); break;
                case "shuffle": media.ToggleShuffle(); break;
                case "repeat": media.CycleRepeat(); break;
                case "back10": media.Seek(-10); break;
                case "fwd10": media.Seek(10); break;
                default: media.Toggle(); break;
            }
            MediaCommandRefresh();
        }

        void RefreshSoonTick(object sender, EventArgs e)
        {
            refreshSoon.Stop();
            Render();
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

            var hit = HitAt(e.Location);
            if (e.Button == MouseButtons.Left && hit != null && hit.Click != null)
            {
                hit.Click();
                return;
            }
            if (e.Button == MouseButtons.Right && hit != null && hit.Key.StartsWith("media:") && media.Current != null)
            {
                ShowTrackMenu();
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
            if (hoveredKey == null) return;
            hoveredKey = null;
            tip.Hide(this);
            Cursor = Cursors.Default;
            Render();
        }

        // Mausrad ueber dem Track = Lautstaerke des Players (wie im Windows-Lautstaerkemixer)
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            var mi = media.Current;
            var hit = HitAt(e.Location);
            if (mi == null || hit == null || !hit.Key.StartsWith("media:")) return;
            string proc = MediaWatcher.ProcessName(mi.SourceApp);
            float delta = e.Delta / 120f * 0.05f;
            float? level = AppVolume.Change(proc, delta);
            if (!level.HasValue)
            {
                string app = MediaWatcher.AppName(mi.SourceApp);
                tip.Show(T("Lautstärke von ", "Can't change volume of ") + app + T(" nicht änderbar", ""),
                    this, hit.R.X, -(int)(lastBounds.Height * 0.72f), 1500);
                hoveredKey = null;
                return;
            }
            tip.Hide(this);
            if (!hudActive)
            {
                hudShown = Math.Max(0f, Math.Min(1f, level.Value - delta)); // von der alten Lautstaerke aus animieren
                hudMuted = AppVolume.IsMuted(proc) == true;
                hudClock.Restart();
            }
            hudTarget = level.Value;
            hudKick = 1f;
            hudUntil = DateTime.UtcNow.AddMilliseconds(1600);
            hudActive = true;
            if (!hudTimer.Enabled)
            {
                hudTimer.Interval = 16;
                hudTimer.Tick -= HudTick;
                hudTimer.Tick += HudTick;
                hudTimer.Start();
            }
        }

        // ---------------- Lautstaerke-Animation ----------------

        readonly System.Windows.Forms.Timer hudTimer = new System.Windows.Forms.Timer();
        readonly Stopwatch hudClock = new Stopwatch();
        bool hudActive, hudMuted;
        float hudTarget, hudShown, hudKick;
        DateTime hudUntil;

        void HudTick(object sender, EventArgs e)
        {
            if (DateTime.UtcNow > hudUntil)
            {
                hudTimer.Stop();
                hudActive = false;
                Render();
                return;
            }
            hudShown += (hudTarget - hudShown) * 0.22f; // weich zur neuen Lautstaerke gleiten
            hudKick *= 0.9f;                            // kurzer "Puls" bei jedem Mausrad-Schritt
            Render(true);
        }

        // Lautsprecher-Icon | huepfende Equalizer-Balken (Anzahl leuchtender Balken + Hoehe = Lautstaerke) | Prozent
        void DrawVolumeHud(Graphics g, RectangleF r, Font font, Color color, float iconPx, float fontPx)
        {
            float lvl = hudMuted ? 0 : Math.Max(0f, Math.Min(1f, hudShown));
            float t = (float)hudClock.Elapsed.TotalSeconds;
            var fmt = StringFormat.GenericTypographic;

            string icon = hudMuted || hudTarget <= 0.001f ? "volume-x" : hudTarget < 0.5f ? "volume-1" : "volume-2";
            float ic = (float)Math.Round(iconPx * 0.95f);
            Lucide.Draw(g, icon, new RectangleF(r.X, r.Y + (r.Height - ic) / 2f, ic, ic), color);

            string pct = hudMuted ? T("stumm", "muted") : (int)Math.Round(hudTarget * 100) + "%";
            float pctW = Math.Max(g.MeasureString("100%", font, 400, fmt).Width, g.MeasureString(pct, font, 400, fmt).Width);
            float pctActual = g.MeasureString(pct, font, 400, fmt).Width;
            using (var br = new SolidBrush(color))
                g.DrawString(pct, font, br, r.Right - pctActual, r.Y + (r.Height - fontPx * 1.2f) / 2f, fmt);

            float bx0 = r.X + ic + fontPx * 0.55f, bx1 = r.Right - pctW - fontPx * 0.55f;
            float barW = Math.Max(2f, (float)Math.Round(fontPx * 0.2f)), step = barW * 1.9f;
            int n = Math.Max(5, (int)((bx1 - bx0 + (step - barW)) / step));
            float used = n * step - (step - barW);
            float sx = bx0 + ((bx1 - bx0) - used) / 2f;
            float maxH = r.Height * 0.62f, cy = r.Y + r.Height / 2f;

            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var on = new SolidBrush(color))
            using (var off = new SolidBrush(Color.FromArgb(70, color)))
            {
                for (int i = 0; i < n; i++)
                {
                    bool lit = (i + 0.5f) / n <= lvl + 0.0001f;
                    float wave = 0.3f + 0.7f * Math.Abs((float)(Math.Sin(t * 7.0 + i * 0.8) * Math.Cos(t * 2.7 + i * 0.33)));
                    float h = lit ? maxH * wave * (0.3f + 0.7f * lvl) * (1f + 0.35f * hudKick) : barW;
                    h = Math.Max(barW, Math.Min(h, r.Height * 0.9f));
                    var bar = new RectangleF(sx + i * step, cy - h / 2f, barW, h);
                    using (var p = RoundedRectF(bar, barW / 2f))
                        g.FillPath(lit ? on : off, p);
                }
            }
        }

        static System.Drawing.Drawing2D.GraphicsPath RoundedRectF(RectangleF r, float radius)
        {
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            var gp = new System.Drawing.Drawing2D.GraphicsPath();
            if (d <= 0.5f) { gp.AddRectangle(r); return gp; }
            gp.AddArc(r.X, r.Y, d, d, 180, 90);
            gp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            gp.CloseFigure();
            return gp;
        }

        // ---------------- Track-Menue (Rechtsklick auf den laufenden Titel) ----------------

        ContextMenuStrip trackMenu;

        static void OpenUrl(string url)
        {
            try { Process.Start(url); } catch { }
        }

        static string SongQuery(string title, string artist)
        {
            return string.IsNullOrEmpty(artist) ? title : artist + " " + title;
        }

        static string SongLabel(string title, string artist)
        {
            return string.IsNullOrEmpty(artist) ? title : artist + " – " + title;
        }

        static ToolStripMenuItem MenuItem(string text, string icon, Action click)
        {
            var it = new ToolStripMenuItem(text);
            if (icon != null) it.Image = Lucide.ToBitmap(icon, 16, SystemColors.MenuText);
            if (click != null) it.Click += (s, e) => click();
            return it;
        }

        // Kopieren, Songtext, Suchen auf ... - fuer den aktuellen Titel und fuer den Verlauf
        static void AddSongActions(ToolStripItemCollection items, string title, string artist)
        {
            string q = Uri.EscapeDataString(SongQuery(title, artist));
            items.Add(MenuItem(T("„Interpret – Titel“ kopieren", "Copy \"Artist – Title\""), "copy",
                () => { try { Clipboard.SetText(SongLabel(title, artist)); } catch { } }));
            items.Add(MenuItem(T("Songtext suchen", "Find lyrics"), "mic-vocal",
                () => OpenUrl("https://genius.com/search?q=" + q)));
            var search = MenuItem(T("Suchen auf", "Search on"), "search", null);
            search.DropDownItems.Add(MenuItem("YouTube Music", null, () => OpenUrl("https://music.youtube.com/search?q=" + q)));
            search.DropDownItems.Add(MenuItem("Spotify", null, () => OpenUrl("https://open.spotify.com/search/" + q)));
            search.DropDownItems.Add(MenuItem("YouTube", null, () => OpenUrl("https://www.youtube.com/results?search_query=" + q)));
            search.DropDownItems.Add(MenuItem("Google", null, () => OpenUrl("https://www.google.com/search?q=" + q)));
            items.Add(search);
        }

        void ShowTrackMenu()
        {
            var mi = media.Current;
            if (mi == null) return;
            if (trackMenu != null) trackMenu.Dispose();
            trackMenu = new ContextMenuStrip();
            var items = trackMenu.Items;

            var head = new ToolStripMenuItem(mi.Title) { Enabled = false };
            head.Font = new Font(head.Font, FontStyle.Bold);
            items.Add(head);
            if (mi.Artist.Length > 0) items.Add(new ToolStripMenuItem(mi.Artist) { Enabled = false });
            items.Add(new ToolStripSeparator());

            AddSongActions(items, mi.Title, mi.Artist);
            var save = MenuItem(T("Cover speichern…", "Save cover…"), "image-down", () => SaveCover(mi));
            save.Enabled = mi.Cover != null;
            items.Add(save);

            // Steuerung, die nicht jeder Player unterstuetzt - nur zeigen, was geht
            var player = new List<ToolStripItem>();
            if (mi.CanSeek)
            {
                player.Add(MenuItem(T("10 s zurück", "Back 10 s"), "rewind", () => MediaCommand("back10")));
                player.Add(MenuItem(T("10 s vor", "Forward 10 s"), "fast-forward", () => MediaCommand("fwd10")));
            }
            if (mi.CanShuffle)
            {
                var sh = MenuItem(T("Zufallswiedergabe", "Shuffle"), "shuffle", () => MediaCommand("shuffle"));
                sh.Checked = mi.Shuffle == true;
                player.Add(sh);
            }
            if (mi.CanRepeat)
            {
                string mode = mi.Repeat == 1 ? T("Titel", "Track") : mi.Repeat == 2 ? T("Alle", "All") : T("Aus", "Off");
                player.Add(MenuItem(T("Wiederholen: ", "Repeat: ") + mode, mi.Repeat == 1 ? "repeat-1" : "repeat", () => MediaCommand("repeat")));
            }
            if (mi.CanStop) player.Add(MenuItem(T("Stopp", "Stop"), "square", () => MediaCommand("stop")));
            if (player.Count > 0)
            {
                items.Add(new ToolStripSeparator());
                foreach (var p in player) items.Add(p);
            }

            // Lautstaerke des Players
            string proc = MediaWatcher.ProcessName(mi.SourceApp);
            float? vol = AppVolume.Get(proc);
            if (vol.HasValue)
            {
                items.Add(new ToolStripSeparator());
                string app = MediaWatcher.AppName(mi.SourceApp);
                items.Add(new ToolStripMenuItem(T("Lautstärke ", "Volume ") + app + ": " + (int)Math.Round(vol.Value * 100) + " %  " +
                    T("(Mausrad über dem Titel)", "(mouse wheel over the track)")) { Enabled = false, Image = Lucide.ToBitmap("volume-2", 16, SystemColors.GrayText) });
                bool muted = AppVolume.IsMuted(proc) == true;
                var mute = MenuItem(T("Stummschalten", "Mute"), null, () => AppVolume.SetMute(proc, !muted));
                mute.Checked = muted;
                items.Add(mute);
            }

            items.Add(new ToolStripSeparator());

            // Zuletzt gehoert
            var hist = MenuItem(T("Zuletzt gehört", "Recently played"), "history", null);
            foreach (var h in media.History())
            {
                var entry = new ToolStripMenuItem(SongLabel(h.Title, h.Artist) + "   (" + h.Time.ToString("HH:mm") + ", " + h.Source + ")");
                AddSongActions(entry.DropDownItems, h.Title, h.Artist);
                hist.DropDownItems.Add(entry);
            }
            if (hist.DropDownItems.Count == 0) hist.Enabled = false;
            items.Add(hist);

            // Quelle waehlen, wenn mehrere Player laufen
            var sources = media.Sources;
            var src = MenuItem(T("Quelle", "Source"), "audio-lines", null);
            var auto = MenuItem(T("Automatisch (wie Windows)", "Automatic (like Windows)"), null, () => SetSource(null));
            auto.Checked = media.PreferredAumid == null;
            src.DropDownItems.Add(auto);
            foreach (var s in sources)
            {
                string aumid = s.Aumid;
                var it = MenuItem(s.Name + (s.Title.Length > 0 ? " – " + s.Title : ""), null, () => SetSource(aumid));
                it.Checked = media.PreferredAumid == aumid;
                src.DropDownItems.Add(it);
            }
            items.Add(src);

            items.Add(new ToolStripSeparator());
            items.Add(MenuItem(T("PerfTray-Einstellungen…", "PerfTray settings…"), null,
                () => BeginInvoke((Action)(() => { SetForegroundWindow(Handle); menu.Show(Cursor.Position); }))));

            tip.Hide(this);
            SetForegroundWindow(Handle);
            trackMenu.Show(Cursor.Position);
        }

        void SetSource(string aumid)
        {
            media.PreferredAumid = aumid;
            media.Refresh();
            SaveSettings();
            MediaCommandRefresh();
        }

        // kurz darauf neu zeichnen, damit z. B. Play/Pause sofort umspringt
        void MediaCommandRefresh()
        {
            refreshSoon.Stop();
            refreshSoon.Interval = 400;
            refreshSoon.Tick -= RefreshSoonTick;
            refreshSoon.Tick += RefreshSoonTick;
            refreshSoon.Start();
        }

        void SaveCover(MediaInfo mi)
        {
            if (mi.Cover == null) return;
            string name = SongLabel(mi.Title, mi.Artist);
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            if (name.Length > 100) name = name.Substring(0, 100);
            using (var dlg = new SaveFileDialog())
            {
                dlg.Filter = "PNG|*.png";
                dlg.FileName = name + ".png";
                dlg.InitialDirectory = FavPath("PICTURES") ?? "";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try { mi.Cover.Save(dlg.FileName, ImageFormat.Png); }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "PerfTray", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        HitArea HitAt(Point p)
        {
            return hits.FirstOrDefault(h => h.R.Contains(p));
        }

        void UpdateHover(Point p)
        {
            var hit = HitAt(p);
            string key = hit == null ? null : hit.Key;
            if (key == hoveredKey) return;
            hoveredKey = key;
            Cursor = hit != null && hit.Click != null ? Cursors.Hand : Cursors.Default;
            if (hit != null && !string.IsNullOrEmpty(hit.Tip))
            {
                int lines = hit.Tip.Split('\n').Length;
                tip.Show(hit.Tip, this, hit.R.X, -(int)(lastBounds.Height * (0.4f + 0.32f * lines)), 4000);
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

            // Medien-Steuerung (YouTube Music, Spotify, ...)
            var mediaMenu = new ToolStripMenuItem(T("Medien-Steuerung", "Media controls"));
            mediaMenu.Image = Lucide.ToBitmap("music", 16, SystemColors.MenuText);
            mediaMenu.DropDown.Closing += KeepOpenOnToggle;
            if (!media.Available)
            {
                mediaMenu.DropDownItems.Add(new ToolStripMenuItem(
                    T("Auf diesem Windows nicht verfügbar", "Not available on this Windows version")) { Enabled = false });
            }
            else
            {
                var showIt = new ToolStripMenuItem(T("Anzeigen, wenn etwas läuft", "Show when something is playing")) { Checked = showMedia };
                showIt.Click += (s, e) =>
                {
                    MarkKeepOpen();
                    showMedia = !showMedia;
                    media.Enabled = showMedia;
                    ((ToolStripMenuItem)s).Checked = showMedia;
                    SaveSettings();
                    Render();
                };
                var coverIt = new ToolStripMenuItem(T("Cover anzeigen", "Show cover art")) { Checked = showCover };
                coverIt.Click += (s, e) =>
                {
                    MarkKeepOpen();
                    showCover = !showCover;
                    ((ToolStripMenuItem)s).Checked = showCover;
                    SaveSettings();
                    Render();
                };
                mediaMenu.DropDownItems.Add(showIt);
                mediaMenu.DropDownItems.Add(coverIt);
            }
            menu.Items.Add(mediaMenu);

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
            media.Stop();
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
                        else if (k == "media")
                            showMedia = v != "0";
                        else if (k == "cover")
                            showCover = v != "0";
                        else if (k == "source" && v.Length > 0)
                            media.PreferredAumid = v;
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
                    "favorites=" + string.Join("|", favorites),
                    "media=" + (showMedia ? "1" : "0"),
                    "cover=" + (showCover ? "1" : "0"),
                    "source=" + (media.PreferredAumid ?? "")
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
