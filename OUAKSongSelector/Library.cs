using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace OUAKSongSelector
{
    /// <summary>
    /// Keeps the song list and the playlists up to date while the game runs, with Windows' own change notifications
    /// (<see cref="FileSystemWatcher"/>): nothing is read while nothing changes. A change to the Music folder (a file added,
    /// removed, renamed or rewritten, in any subfolder) or to playlists.json marks it; half a second after the last change
    /// (a file being copied fires many) the folder is listed once on a worker thread and the result applied on the game's
    /// thread, and playlists.json is read again (see <see cref="Store.TryReloadFromDisk"/>).
    /// Measured before this: listing the folder every 2 s cost 102 ms and 3.2 MB of garbage per round with 2,000 files.
    /// If Windows drops notifications (buffer overflow) or the folder goes away, the folder is listed again and the watch
    /// is set up anew; if no watch can be made at all, the folder is listed every 30 s instead.
    /// </summary>
    public static class Library
    {
        private const double Quiet = 0.5;          // seconds after the last change before reading
        private const float FallbackEvery = 30f;  // only when no watch can be made

        private static FileSystemWatcher _music, _json;
        private static long _musicChangedAt, _jsonChangedAt;   // DateTime ticks of the last change (0 = none pending)
        private static int _rewatch;
        private static Task<Catalog.ScanResult> _scan;
        private static float _nextFallback;
        private static bool _started;

        /// <summary>Last automatic change (for the log and the test).</summary>
        public static string LastChange { get; private set; } = "";
        public static int Updates { get; private set; }
        /// <summary>Folder listings made by the automatic update (none while nothing changes).</summary>
        public static int Scans { get; private set; }
        public static bool Watching => _music != null && _json != null;

        public static void Tick()
        {
            if (!_started) { _started = true; Watch(); }
            if (Interlocked.Exchange(ref _rewatch, 0) != 0) { Watch(); Mark(ref _musicChangedAt); }

            // A listing finished: apply it.
            if (_scan != null)
            {
                if (!_scan.IsCompleted) return;
                var r = _scan.Result;
                _scan = null;
                if (r.Error == null && r.Signature != Catalog.Signature)
                {
                    int before = Catalog.CustomSongs.Count;
                    string msg = Catalog.Apply(r);
                    Updates++;
                    LastChange = $"Music folder changed: {before} -> {Catalog.CustomSongs.Count} file(s).";
                    SongSelectorMod.Log.Info(LastChange + " " + msg);
                }
            }

            long now = DateTime.UtcNow.Ticks;
            long quiet = TimeSpan.FromSeconds(Quiet).Ticks;

            long j = Interlocked.Read(ref _jsonChangedAt);
            if (j != 0 && now - j >= quiet && Interlocked.CompareExchange(ref _jsonChangedAt, 0, j) == j)
                if (Store.TryReloadFromDisk()) { Updates++; LastChange = "playlists.json changed outside the game: read again."; }

            long m = Interlocked.Read(ref _musicChangedAt);
            if (m != 0 && now - m >= quiet && Interlocked.CompareExchange(ref _musicChangedAt, 0, m) == m) StartScan();

            if (!Watching && Time.unscaledTime >= _nextFallback)
            {
                _nextFallback = Time.unscaledTime + FallbackEvery;
                StartScan();
                if (Store.TryReloadFromDisk()) Updates++;
            }
        }

        private static void StartScan()
        {
            if (_scan != null) { Mark(ref _musicChangedAt); return; }   // one at a time; this change is read next
            Scans++;
            _scan = Task.Run(Catalog.Scan);
        }

        private static void Mark(ref long at) => Interlocked.Exchange(ref at, DateTime.UtcNow.Ticks);

        private static void Watch()
        {
            Stop();
            try
            {
                Directory.CreateDirectory(Catalog.MusicFolder);
                _music = new FileSystemWatcher(Catalog.MusicFolder)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
                _music.Created += OnMusic; _music.Deleted += OnMusic; _music.Changed += OnMusic;
                _music.Renamed += OnMusicRenamed;
                _music.Error += OnError;
                _music.EnableRaisingEvents = true;

                _json = new FileSystemWatcher(SongSelectorMod.Dir, "playlists.json")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                _json.Created += OnJson; _json.Changed += OnJson; _json.Deleted += OnJson;
                _json.Renamed += OnJsonRenamed;
                _json.Error += OnError;
                _json.EnableRaisingEvents = true;
                SongSelectorMod.Log.Info("Watching the Music folder and playlists.json for changes (Windows notifications).");
            }
            catch (Exception e)
            {
                Stop();
                SongSelectorMod.Log.Warn($"Can't watch for file changes ({e.Message}); the Music folder is checked every {FallbackEvery:0} s instead.");
            }
        }

        // Windows calls these on its own threads: they only note the time.
        private static void OnMusic(object sender, FileSystemEventArgs e) => Mark(ref _musicChangedAt);
        private static void OnMusicRenamed(object sender, RenamedEventArgs e) => Mark(ref _musicChangedAt);
        private static void OnJson(object sender, FileSystemEventArgs e) => Mark(ref _jsonChangedAt);
        private static void OnJsonRenamed(object sender, RenamedEventArgs e) => Mark(ref _jsonChangedAt);
        private static void OnError(object sender, ErrorEventArgs e)
        {
            try { SongSelectorMod.Log.Warn("File watch: " + e.GetException()?.Message + " (listing again)"); } catch { }
            Interlocked.Exchange(ref _rewatch, 1);
        }

        public static void Stop()
        {
            try { _music?.Dispose(); } catch { }
            try { _json?.Dispose(); } catch { }
            _music = null; _json = null;
        }
    }
}
