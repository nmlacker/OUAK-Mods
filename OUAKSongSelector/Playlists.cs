using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OUAKSongSelector
{
    /// <summary>What happens when a playlist reaches its last song.</summary>
    public enum RepeatMode { All, One, Off }

    /// <summary>A named list of songs. Each song is a <see cref="TrackRef"/> string: "game:N" or "file:relative/path.wav".</summary>
    public sealed class Playlist
    {
        public string Name { get; set; } = "New playlist";
        public List<string> Tracks { get; set; } = new();
        public bool Shuffle { get; set; }
        public RepeatMode Repeat { get; set; } = RepeatMode.All;
    }

    /// <summary>Which playlist plays where. Empty = nothing chosen there (the next more general choice, then the game's own music).</summary>
    public sealed class Assignments
    {
        /// <summary>Everywhere the mod plays music (story stages and the Plaza) unless something more specific is set.</summary>
        public string Global { get; set; } = "";
        /// <summary>Every story stage.</summary>
        public string Stages { get; set; } = "";
        /// <summary>The Plaza (every era).</summary>
        public string Plaza { get; set; } = "";
        /// <summary>One story stage (key: the eStage name, e.g. "Stage_Big1").</summary>
        public Dictionary<string, string> PerStage { get; set; } = new();
    }

    public sealed class SongSettings
    {
        public int Version { get; set; } = 1;
        public List<Playlist> Playlists { get; set; } = new();
        public Assignments Use { get; set; } = new();
        /// <summary>Your own songs' loudness next to the game's (1 = as loud as a full-scale WAV at the game's music volume).</summary>
        public float CustomVolume { get; set; } = 0.8f;
        /// <summary>Show the song's name on screen when a playlist song starts.</summary>
        public bool ShowNowPlaying { get; set; } = true;
        /// <summary>Let playlists use base-game songs this save hasn't unlocked yet (DLC songs always need the pack).</summary>
        public bool AllowLockedSongs { get; set; }
    }

    /// <summary>The playlists file: UserData/OUAKSongSelector/playlists.json. Saved on every change.</summary>
    public static class Store
    {
        private static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };

        public static string Path => System.IO.Path.Combine(SongSelectorMod.Dir, "playlists.json");
        public static SongSettings Settings { get; private set; } = new();
        public static string LastError { get; private set; } = "";
        /// <summary>Bumped on every change (the player re-checks what should play).</summary>
        public static int Version { get; private set; }

        public static void Load()
        {
            try
            {
                if (File.Exists(Path))
                {
                    string text = File.ReadAllText(Path);
                    Settings = JsonSerializer.Deserialize<SongSettings>(text, Json) ?? new SongSettings();
                    _lastText = text;
                    LastError = "";
                }
                else Settings = new SongSettings();
            }
            catch (Exception e)
            {
                LastError = $"playlists.json couldn't be read ({e.Message}); it was copied to playlists.bad.json and new settings started.";
                SongSelectorMod.Log.Warn(LastError);
                try { File.Copy(Path, System.IO.Path.ChangeExtension(Path, ".bad.json"), true); } catch { }
                Settings = new SongSettings();
            }
            Normalize();
            Version++;
        }

        public static void Save()
        {
            Normalize();
            Version++;
            try
            {
                Directory.CreateDirectory(SongSelectorMod.Dir);
                string tmp = Path + ".tmp";
                string text = JsonSerializer.Serialize(Settings, Json);
                _lastText = text;
                File.WriteAllText(tmp, text);
                if (File.Exists(Path)) File.Replace(tmp, Path, null);
                else File.Move(tmp, Path);
                LastError = "";
            }
            catch (Exception e) { LastError = "Couldn't save playlists.json: " + e.Message; SongSelectorMod.Log.Warn(LastError); }
        }

        private static void Normalize()
        {
            var s = Settings;
            s.Playlists ??= new();
            s.Use ??= new();
            s.Use.PerStage ??= new();
            foreach (var p in s.Playlists) { p.Tracks ??= new(); p.Name = string.IsNullOrWhiteSpace(p.Name) ? "Playlist" : p.Name.Trim(); }
            s.CustomVolume = Math.Clamp(s.CustomVolume, 0f, 2f);
        }

        private static string _lastText;

        /// <summary>
        /// playlists.json was changed outside the game (another editor, a copied file): read it again. A file that doesn't read
        /// (half saved, a typo) leaves the playlists in use alone and says why; the next change is tried again.
        /// Returns true when new settings were taken.
        /// </summary>
        public static bool TryReloadFromDisk()
        {
            string text;
            try { text = File.Exists(Path) ? File.ReadAllText(Path) : null; }
            catch { return false; }   // still being written
            if (text == null || text == _lastText) return false;
            try
            {
                var s = JsonSerializer.Deserialize<SongSettings>(text, Json);
                if (s == null) throw new JsonException("empty file");
                Settings = s;
                _lastText = text;
                LastError = "";
                Normalize();
                Version++;
                SongSelectorMod.Log.Info($"playlists.json changed outside the game: {Settings.Playlists.Count} playlist(s) read again.");
                return true;
            }
            catch (Exception e)
            {
                _lastText = text;   // don't report the same broken text every 2 s
                LastError = $"playlists.json was changed but can't be read ({e.Message}); the playlists in use stay until it's fixed.";
                SongSelectorMod.Log.Warn(LastError);
                return false;
            }
        }

        public static Playlist Find(string name) =>
            string.IsNullOrEmpty(name) ? null : Settings.Playlists.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>A name not used yet, based on <paramref name="wanted"/>.</summary>
        public static string FreeName(string wanted, Playlist except = null)
        {
            wanted = string.IsNullOrWhiteSpace(wanted) ? "Playlist" : wanted.Trim();
            string n = wanted;
            for (int i = 2; Settings.Playlists.Any(p => p != except && string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase)); i++) n = $"{wanted} ({i})";
            return n;
        }

        /// <summary>Renames a playlist and every place it's chosen.</summary>
        public static void Rename(Playlist p, string name)
        {
            string old = p.Name;
            p.Name = FreeName(name, p);
            var u = Settings.Use;
            if (Same(u.Global, old)) u.Global = p.Name;
            if (Same(u.Stages, old)) u.Stages = p.Name;
            if (Same(u.Plaza, old)) u.Plaza = p.Name;
            foreach (var k in u.PerStage.Keys.ToList()) if (Same(u.PerStage[k], old)) u.PerStage[k] = p.Name;
            Save();
        }

        /// <summary>Deletes a playlist and clears every place it was chosen.</summary>
        public static void Delete(Playlist p)
        {
            Settings.Playlists.Remove(p);
            var u = Settings.Use;
            if (Same(u.Global, p.Name)) u.Global = "";
            if (Same(u.Stages, p.Name)) u.Stages = "";
            if (Same(u.Plaza, p.Name)) u.Plaza = "";
            foreach (var k in u.PerStage.Keys.ToList()) if (Same(u.PerStage[k], p.Name)) u.PerStage.Remove(k);
            Save();
        }

        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
