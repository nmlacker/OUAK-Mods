using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Il2Cpp;
using Il2CppApp.KatamariSin;
using OUAKModAPI;

namespace OUAKSongSelector
{
    /// <summary>One of the game's songs, as its own music player lists it (GlobalManager.FavoriteBgmTable).</summary>
    public sealed class GameSong
    {
        /// <summary>What the game passes to SoundManager.PlayBGMInStage: FavoriteBgmInfo.bgmId, +1000 for an instrumental entry.</summary>
        public int Index;
        /// <summary>FavoriteBgmInfo.bgmNumber (the save's unlock flags use it for the base game's songs).</summary>
        public int Number;
        /// <summary>0 = the base game; 1-5 = the DLC music packs (cue sheets BGM_DLC1..5).</summary>
        public int Album;
        public string Name = "", Artist = "", FirstRelease = "";
        public bool Instrumental;
        public string Ref => TrackRef.Game(Index);
        public string Label => Artist.Length > 0 && Artist != "-" ? $"{Name}  ·  {Artist}" : Name;
    }

    /// <summary>A music file in the Music folder (WAV, or a type Windows can decode).</summary>
    public sealed class CustomSong
    {
        public string RelPath = "";
        public string Name = "";
        public long Bytes;
        public string Ref => TrackRef.File(RelPath);
    }

    /// <summary>Track references stored in playlists: "game:N" or "file:relative/path.wav".</summary>
    public static class TrackRef
    {
        public static string Game(int index) => "game:" + index;
        public static string File(string relPath) => "file:" + relPath.Replace('\\', '/');
        public static bool IsGame(string r, out int index)
        {
            index = -1;
            return r != null && r.StartsWith("game:") && int.TryParse(r.Substring(5), out index);
        }
        public static bool IsFile(string r, out string relPath)
        {
            relPath = r != null && r.StartsWith("file:") ? r.Substring(5) : null;
            return relPath != null;
        }
    }

    /// <summary>The songs a playlist can use: the game's (with their names) and the WAV files in the Music folder.</summary>
    public static class Catalog
    {
        public static readonly string[] AlbumNames =
        {
            "Once Upon a KATAMARI", "DLC music pack 1", "DLC music pack 2", "DLC music pack 3", "DLC music pack 4", "DLC music pack 5",
        };
        /// <summary>The file types that play here: WAV (read by the mod) and whatever this Windows decodes (Media Foundation).</summary>
        public static IEnumerable<string> PlayableTypes => new[] { ".wav" }.Concat(MfDecoder.Extensions.OrderBy(x => x));

        private static List<GameSong> _game;
        private static Dictionary<int, GameSong> _byIndex = new();
        private static List<CustomSong> _custom = new();
        private static readonly List<string> _unsupportedFiles = new();

        public static string MusicFolder => Path.Combine(SongSelectorMod.Dir, "Music");

        /// <summary>The game's songs, or an empty list before the game's tables are loaded.</summary>
        public static IReadOnlyList<GameSong> GameSongs
        {
            get
            {
                if (_game == null || _game.Count == 0) BuildGame();
                return _game ?? (IReadOnlyList<GameSong>)Array.Empty<GameSong>();
            }
        }

        public static IReadOnlyList<CustomSong> CustomSongs => _custom;
        public static IReadOnlyList<string> UnsupportedFiles => _unsupportedFiles;

        public static GameSong FindGame(int index)
        {
            if (_game == null || _game.Count == 0) BuildGame();
            return _byIndex.TryGetValue(index, out var s) ? s : null;
        }

        public static CustomSong FindCustom(string rel) =>
            _custom.FirstOrDefault(c => string.Equals(c.RelPath, rel.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));

        private static void BuildGame()
        {
            var g = GameRefs.Global;
            var datas = g != null ? g.FavoriteBgmTable?.datas : null;
            if (datas == null) { _game = null; return; }
            var list = new List<GameSong>();
            var map = new Dictionary<int, GameSong>();
            for (int i = 0; i < datas.Count; i++)
            {
                var f = datas[i];
                if (f == null) continue;
                // FavoriteBgmPlayer.PlayBgm: an entry whose bgmNumber lies between BgmDataSet.bgmNumInstMin and bgmNumInstMax
                // plays PlayBGMInStage(bgmId + 1000) (the VocalOff label); the table's own names say "(inst ver.)".
                string loc = f.localizeId ?? "";
                bool inst = loc.EndsWith("_inst", StringComparison.OrdinalIgnoreCase);
                var s = new GameSong
                {
                    Index = f.bgmId + (inst ? 1000 : 0),
                    Number = f.bgmNumber,
                    Album = f.albumId,
                    Instrumental = inst,
                    Name = Clean(AssetUtil.Text(g.BgmTable, loc)) ?? loc,
                    Artist = Clean(AssetUtil.Text(g.BgmTable, f.song)) ?? "",
                    FirstRelease = Clean(AssetUtil.Text(g.BgmTable, f.firstReleaseTitle)) ?? "",
                };
                if (map.ContainsKey(s.Index)) continue;
                map[s.Index] = s;
                list.Add(s);
            }
            _game = list;
            _byIndex = map;
            SongSelectorMod.Log.Info($"Game songs: {list.Count} ({list.Count(x => x.Album == 0)} base game, {list.Count(x => x.Album > 0)} DLC).");
        }

        private static string Clean(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Replace("\r", " ").Replace("\n", " ").Replace("  ", " ").Trim();

        /// <summary>Whether a game song can play: base songs the save has unlocked (or all, if the setting allows), DLC packs the game reports as owned.</summary>
        public static bool Available(GameSong s, out string why)
        {
            why = "";
            if (s == null) { why = "not in this game"; return false; }
            if (s.Album > 0)
            {
                bool owned = false;
                try
                {
                    var dlc = Player.Ctr?.IsDLCAvailable;
                    owned = dlc != null && s.Album - 1 < dlc.Length && dlc[s.Album - 1];
                }
                catch { }
                if (!owned) { why = $"needs {AlbumNames[Math.Min(s.Album, AlbumNames.Length - 1)]} (DLC)"; return false; }
                return true;
            }
            if (Store.Settings.AllowLockedSongs) return true;
            try
            {
                var save = GameRefs.Save;
                var rel = save?.BgmReleasables;
                if (rel == null || !GameRefs.SaveLoaded) { why = "load a save first"; return false; }
                if (s.Number >= 0 && s.Number < rel.Length && rel[s.Number]) return true;
                why = "not unlocked in this save yet";
                return false;
            }
            catch { why = "unknown"; return false; }
        }

        /// <summary>A readable name for any track reference.</summary>
        public static string Describe(string r)
        {
            if (TrackRef.IsGame(r, out int i))
            {
                var s = FindGame(i);
                return s != null ? s.Name : $"Game song #{i}";
            }
            if (TrackRef.IsFile(r, out string rel)) return Path.GetFileNameWithoutExtension(rel);
            return r ?? "";
        }

        /// <summary>Whether a track can play now, and why not.</summary>
        public static bool Playable(string r, out string why)
        {
            why = "";
            if (TrackRef.IsGame(r, out int i)) return Available(FindGame(i), out why);
            if (TrackRef.IsFile(r, out string rel))
            {
                if (FindCustom(rel) != null) return true;
                why = "file not in the Music folder";
                return false;
            }
            why = "unknown entry";
            return false;
        }

        /// <summary>What a scan of the Music folder found (made on any thread, applied on the main one).</summary>
        public sealed class ScanResult { public List<CustomSong> Songs = new(); public List<string> Unsupported = new(); public string Error; public string Signature = ""; }

        /// <summary>Reads the Music folder (no game objects: safe on a worker thread).</summary>
        public static ScanResult Scan()
        {
            var r = new ScanResult();
            var sig = new System.Text.StringBuilder();
            try
            {
                Directory.CreateDirectory(MusicFolder);
                foreach (var f in Directory.EnumerateFiles(MusicFolder, "*", SearchOption.AllDirectories))
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    string rel = Path.GetRelativePath(MusicFolder, f).Replace('\\', '/');
                    bool song = ext == ".wav" || MfDecoder.Extensions.Contains(ext);
                    if (!song && !MfDecoder.AllCandidates.Contains(ext)) continue;
                    var info = new FileInfo(f);
                    sig.Append(rel).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
                    if (song) r.Songs.Add(new CustomSong { RelPath = rel, Name = Path.GetFileNameWithoutExtension(f), Bytes = info.Length });
                    else r.Unsupported.Add(rel);
                }
            }
            catch (Exception e) { r.Error = e.Message; }
            r.Songs = r.Songs.OrderBy(c => c.RelPath, StringComparer.OrdinalIgnoreCase).ToList();
            r.Signature = sig.ToString();
            return r;
        }

        /// <summary>The scan the song list shows now.</summary>
        public static string Signature { get; private set; } = "";

        public static string Apply(ScanResult r)
        {
            if (r.Error != null) return "Couldn't read the Music folder: " + r.Error;
            _custom = r.Songs;
            _unsupportedFiles.Clear();
            _unsupportedFiles.AddRange(r.Unsupported);
            Signature = r.Signature;
            return $"{_custom.Count} music file(s) in the Music folder" + (_unsupportedFiles.Count > 0 ? $", {_unsupportedFiles.Count} of a type this Windows can't decode" : "") + ".";
        }

        /// <summary>Lists the Music folder again.</summary>
        public static string RescanMusic() => Apply(Scan());
    }
}
