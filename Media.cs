// Medien-Steuerung: liest ueber die Windows-Medienschnittstelle (dieselbe wie das Lautstaerke-Popup
// und die Medientasten), was gerade laeuft - YouTube Music im Browser, Spotify, VLC usw.
// Bewusst ohne System.Runtime.WindowsRuntime, damit der Build ohne Windows-SDK funktioniert.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Windows.Foundation;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace PerfTray
{
    class MediaInfo
    {
        public string Title = "", Artist = "", SourceApp = "";
        public bool Playing, CanPrev, CanNext, CanSeek, CanStop, CanShuffle, CanRepeat;
        public bool? Shuffle;
        public int Repeat = -1; // 0 = aus, 1 = Titel, 2 = Liste, -1 = unbekannt
        public Bitmap Cover;
    }

    class MediaSource { public string Aumid, Name, Title; }

    class HistoryEntry { public string Title, Artist, Source; public DateTime Time; }

    class MediaWatcher
    {
        public volatile MediaInfo Current;      // null = nichts laeuft
        public volatile bool Available = true;  // false = Windows-Version ohne Medienschnittstelle
        public volatile bool Enabled = true;
        public volatile string PreferredAumid;  // null = was Windows als aktiv betrachtet
        public volatile List<MediaSource> Sources = new List<MediaSource>();

        volatile bool running = true;
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly List<HistoryEntry> history = new List<HistoryEntry>();
        object manager; // GlobalSystemMediaTransportControlsSessionManager (als object, damit alte Windows-Versionen nicht abstuerzen)
        string coverKey, historyKey;
        Bitmap cover;
        int coverTries;

        public void Start()
        {
            var t = new Thread(Loop);
            t.IsBackground = true;
            t.Start();
        }

        public void Stop() { running = false; wake.Set(); }
        public void Refresh() { wake.Set(); }

        public List<HistoryEntry> History()
        {
            lock (history) return history.ToList();
        }

        static T Wait<T>(IAsyncOperation<T> op)
        {
            var until = DateTime.UtcNow.AddSeconds(3);
            while (op.Status == AsyncStatus.Started && DateTime.UtcNow < until) Thread.Sleep(5);
            return op.Status == AsyncStatus.Completed ? op.GetResults() : default(T);
        }

        void Loop()
        {
            try { Init(); }
            catch { manager = null; }
            if (manager == null) { Available = false; return; }

            while (running)
            {
                if (Enabled)
                {
                    try { Poll(); } catch { Current = null; }
                }
                else Current = null;
                wake.WaitOne(1000);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void Init()
        {
            manager = Wait(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
        }

        // Gewaehlte Quelle, sonst die von Windows als aktiv gemeldete
        GlobalSystemMediaTransportControlsSession Session()
        {
            var mgr = manager as GlobalSystemMediaTransportControlsSessionManager;
            if (mgr == null) return null;
            string pref = PreferredAumid;
            if (pref != null)
            {
                var s = mgr.GetSessions().FirstOrDefault(x => x.SourceAppUserModelId == pref);
                if (s != null) return s;
            }
            return mgr.GetCurrentSession();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void Poll()
        {
            var mgr = (GlobalSystemMediaTransportControlsSessionManager)manager;
            var sources = new List<MediaSource>();
            foreach (var x in mgr.GetSessions())
            {
                string title = "";
                try { var p = Wait(x.TryGetMediaPropertiesAsync()); if (p != null) title = p.Title ?? ""; } catch { }
                sources.Add(new MediaSource { Aumid = x.SourceAppUserModelId, Name = AppName(x.SourceAppUserModelId), Title = title });
            }
            Sources = sources;

            var s = Session();
            if (s == null) { Current = null; return; }
            var props = Wait(s.TryGetMediaPropertiesAsync());
            if (props == null || string.IsNullOrEmpty(props.Title)) { Current = null; return; }

            var pb = s.GetPlaybackInfo();
            var info = new MediaInfo
            {
                Title = props.Title,
                Artist = props.Artist ?? "",
                SourceApp = s.SourceAppUserModelId,
                Playing = pb.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                CanPrev = pb.Controls.IsPreviousEnabled,
                CanNext = pb.Controls.IsNextEnabled,
                CanSeek = pb.Controls.IsPlaybackPositionEnabled,
                CanStop = pb.Controls.IsStopEnabled,
                CanShuffle = pb.Controls.IsShuffleEnabled,
                CanRepeat = pb.Controls.IsRepeatEnabled,
                Shuffle = pb.IsShuffleActive
            };
            var rep = pb.AutoRepeatMode;
            if (rep.HasValue)
                info.Repeat = rep.Value == MediaPlaybackAutoRepeatMode.Track ? 1 : rep.Value == MediaPlaybackAutoRepeatMode.List ? 2 : 0;

            // Cover nur bei neuem Titel laden; manche Player liefern es erst etwas spaeter
            string key = info.Title + "\n" + info.Artist;
            if (key != coverKey) { coverKey = key; cover = null; coverTries = 0; }
            if (cover == null && coverTries < 5 && props.Thumbnail != null)
            {
                coverTries++;
                cover = LoadCover(props.Thumbnail);
            }
            info.Cover = cover;
            AddToHistory(info);
            Current = info;
        }

        void AddToHistory(MediaInfo info)
        {
            string key = info.Title + "\n" + info.Artist;
            if (key == historyKey) return;
            historyKey = key;
            lock (history)
            {
                history.RemoveAll(h => h.Title == info.Title && h.Artist == info.Artist);
                history.Insert(0, new HistoryEntry { Title = info.Title, Artist = info.Artist, Source = AppName(info.SourceApp), Time = DateTime.Now });
                if (history.Count > 10) history.RemoveRange(10, history.Count - 10);
            }
        }

        static Bitmap LoadCover(IRandomAccessStreamReference reference)
        {
            try
            {
                var stream = Wait(reference.OpenReadAsync());
                if (stream == null || stream.Size == 0) return null;
                var reader = new DataReader(stream.GetInputStreamAt(0));
                uint n = Wait(reader.LoadAsync((uint)stream.Size));
                var bytes = new byte[n];
                reader.ReadBytes(bytes);
                using (var ms = new MemoryStream(bytes))
                using (var img = Image.FromStream(ms))
                    return new Bitmap(img);
            }
            catch { return null; }
        }

        // ---------------- Befehle (laufen im Hintergrund, danach sofort neu abfragen) ----------------

        public void Toggle() { Run("toggle", 0); }
        public void Previous() { Run("prev", 0); }
        public void Next() { Run("next", 0); }
        public void StopPlayback() { Run("stop", 0); }
        public void ToggleShuffle() { Run("shuffle", 0); }
        public void CycleRepeat() { Run("repeat", 0); }
        public void Seek(int seconds) { Run("seek", seconds); }

        void Run(string what, int arg)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Execute(what, arg); } catch { }
                Thread.Sleep(150);
                wake.Set();
            });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void Execute(string what, int arg)
        {
            var s = Session();
            if (s == null) return;
            var pb = s.GetPlaybackInfo();
            switch (what)
            {
                case "prev": Wait(s.TrySkipPreviousAsync()); break;
                case "next": Wait(s.TrySkipNextAsync()); break;
                case "stop": Wait(s.TryStopAsync()); break;
                case "shuffle": Wait(s.TryChangeShuffleActiveAsync(!(pb.IsShuffleActive ?? false))); break;
                case "repeat":
                    {
                        var mode = pb.AutoRepeatMode ?? MediaPlaybackAutoRepeatMode.None;
                        var nextMode = mode == MediaPlaybackAutoRepeatMode.None ? MediaPlaybackAutoRepeatMode.List
                                     : mode == MediaPlaybackAutoRepeatMode.List ? MediaPlaybackAutoRepeatMode.Track
                                     : MediaPlaybackAutoRepeatMode.None;
                        Wait(s.TryChangeAutoRepeatModeAsync(nextMode));
                        break;
                    }
                case "seek":
                    {
                        var tl = s.GetTimelineProperties();
                        long pos = tl.Position.Ticks;
                        // Position ist ein Schnappschuss; bei laufender Wiedergabe die vergangene Zeit dazurechnen
                        if (pb.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                            pos += (DateTimeOffset.Now - tl.LastUpdatedTime).Ticks;
                        long target = Math.Max(0, pos + arg * TimeSpan.TicksPerSecond);
                        if (tl.EndTime.Ticks > 0) target = Math.Min(target, tl.EndTime.Ticks);
                        Wait(s.TryChangePlaybackPositionAsync(target));
                        break;
                    }
                default: Wait(s.TryTogglePlayPauseAsync()); break;
            }
        }

        // ---------------- Namen der Player ----------------

        // AppUserModelID -> lesbarer Name ("Chrome", "308046B0AF4A39CB" = Firefox, "...!Spotify" = Spotify)
        public static string AppName(string aumid)
        {
            if (string.IsNullOrEmpty(aumid)) return "?";
            string id = aumid;
            int bang = id.LastIndexOf('!');
            if (bang >= 0) id = id.Substring(bang + 1);
            if (id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) id = Path.GetFileNameWithoutExtension(id);
            if (id.Length == 16 && id.All(Uri.IsHexDigit)) return "Firefox";
            if (id.Equals("MSEdge", StringComparison.OrdinalIgnoreCase)) return "Edge";
            if (id.Length > 0) id = char.ToUpper(id[0]) + id.Substring(1);
            return id;
        }

        // AppUserModelID -> Prozessname fuer die Lautstaerke-Regelung
        public static string ProcessName(string aumid)
        {
            string name = AppName(aumid);
            if (name == "Edge") return "msedge";
            return name.ToLowerInvariant();
        }
    }
}
