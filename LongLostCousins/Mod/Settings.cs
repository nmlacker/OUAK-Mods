using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using LongLostCousins.Data;
using MelonLoader.Utils;
using OUAKModAPI;

namespace LongLostCousins
{
    /// <summary>
    /// The player's choice: which cousin and style the mod dresses their cousin as, or none (the default), where the
    /// cousin was picked, and the cousin the game's save held when she was picked (she is worn only while the save still
    /// holds it). Kept in UserData/LongLostCousins/settings.json, never in the game's save. The style is kept when the
    /// cousin is turned off, so the select screen shows her in the last style picked. The test flag decides the choice
    /// in memory only.
    /// </summary>
    internal static class Settings
    {
        private static readonly ModLog Log = LongLostCousinsMod.Log.In("Settings");
        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

        public static string Folder => Path.Combine(MelonEnvironment.UserDataDirectory, "LongLostCousins");
        private static string FilePath => Path.Combine(Folder, "settings.json");

        /// <summary>The style used when nothing else names one.</summary>
        public const string DefaultStyle = "new";
        /// <summary>The value of "picked" for a cousin picked on the game's select screen.</summary>
        public const string PickedOnScreen = "select";
        /// <summary>No saved cousin recorded yet (taken from the save the first time she is checked with a slot loaded).</summary>
        public const int NotRecorded = -1;

        private sealed class Stored
        {
            [JsonPropertyName("cousin")] public string Cousin { get; set; }
            [JsonPropertyName("style")] public string Style { get; set; }
            [JsonPropertyName("picked")] public string Picked { get; set; }
            [JsonPropertyName("saved")] public int? Saved { get; set; }
            [JsonPropertyName("custom")] public int? Custom { get; set; }
            [JsonPropertyName("forever_folder")] public string ForeverFolder { get; set; }
        }

        private static string _cousinId, _style = DefaultStyle;
        private static bool _onScreen, _memoryOnly, _dirty;
        private static int _saved = NotRecorded, _custom = NotRecorded;
        private static string _foreverFolder;

        /// <summary>A cousin is chosen in the settings (see <see cref="Chosen"/> for whether she is worn now).</summary>
        public static bool Active => _cousinId != null;
        public static string CousinId => _cousinId;
        /// <summary>The chosen style, or the last one picked while no cousin is chosen.</summary>
        public static string Style => _style;
        public static CousinEntry Cousin => Catalog.Find(_cousinId);
        /// <summary>True when the cousin was picked on the game's select screen (for the log and the tab only).</summary>
        public static bool OnScreen => Active && _onScreen;
        /// <summary>True while the test flag decides the choice (nothing is written).</summary>
        public static bool MemoryOnly => _memoryOnly;
        /// <summary>The saved cousin (save +0x19C) recorded when she was picked, or <see cref="NotRecorded"/>.</summary>
        public static int Saved => _saved;
        /// <summary>The saved custom index (+0x1A0) recorded with it; compared only when <see cref="Saved"/> is the Cousin Creator's 999.</summary>
        public static int Custom => _custom;
        public static bool HasRecord => Active && _saved != NotRecorded;

        /// <summary>
        /// The player's Katamari Forever disc folder ("forever_folder"), or null. Never logged or shown: only whether it is
        /// set and what was found there.
        /// </summary>
        public static string ForeverFolder => _foreverFolder;

        public static string Describe() => Active && Cousin != null ? Cousin.StyleLabel(_style) : "Off";

        /// <summary>The recorded save for the log: "cousin 6", "Cousin Creator cousin 9", or "not recorded yet".</summary>
        public static string DescribeRecord() =>
            !HasRecord ? "not recorded yet" : _saved == Chosen.CreatorId ? $"Cousin Creator cousin (custom index {_custom})" : $"cousin {_saved}";

        /// <summary>The style <paramref name="c"/> is shown in: the settings' style if she has it, else her first.</summary>
        public static string StyleFor(CousinEntry c)
        {
            if (c == null) return _style;
            if (c.StylePath(_style) != null) return _style;
            return c.StylePath(DefaultStyle) != null ? DefaultStyle : (c.Styles.Count > 0 ? c.Styles[0].Key : _style);
        }

        /// <summary>Reads settings.json. Off when it is missing, unreadable or names a cousin or style the mod lacks.</summary>
        public static void Load()
        {
            _cousinId = null;
            _style = DefaultStyle;
            _onScreen = false;
            _saved = _custom = NotRecorded;
            _foreverFolder = null;
            try
            {
                if (!File.Exists(FilePath)) return;
                var s = JsonSerializer.Deserialize<Stored>(File.ReadAllText(FilePath));
                if (s == null) return;
                // Read before the cousin: the folder counts with no cousin chosen too.
                _foreverFolder = string.IsNullOrWhiteSpace(s.ForeverFolder) ? null : s.ForeverFolder.Trim();
                if (!string.IsNullOrEmpty(s.Style)) _style = s.Style;
                if (string.IsNullOrEmpty(s.Cousin)) return;
                var c = Catalog.Find(s.Cousin);
                if (c == null || c.StylePath(s.Style) == null)
                {
                    Log.Warn($"settings.json names {s.Cousin} ({s.Style}), which this version does not have; staying off");
                    return;
                }
                _cousinId = c.Id;
                _onScreen = s.Picked == PickedOnScreen;
                // A file from 0.1.0 has no saved cousin: it is taken from the save the first time a slot is loaded.
                if (s.Saved is int saved && Chosen.IsStorableId(saved))
                {
                    _saved = saved;
                    _custom = s.Custom ?? NotRecorded;
                }
            }
            catch (Exception e) { Log.Warn("settings.json could not be read; staying off: " + e.Message); }
        }

        /// <summary>
        /// Chooses a cousin and style (null cousin: off, the style is kept) and writes settings.json, unless the test
        /// decides. <paramref name="onScreen"/> says the cousin was picked on the game's select screen; <paramref name="saved"/>
        /// and <paramref name="custom"/> are the save's cousin and custom index at that moment (<see cref="NotRecorded"/>:
        /// taken from the save later).
        /// </summary>
        public static void Choose(string cousinId, string style, bool onScreen = false, int saved = NotRecorded, int custom = NotRecorded)
        {
            _cousinId = cousinId;
            if (!string.IsNullOrEmpty(style)) _style = style;
            _onScreen = cousinId != null && onScreen;
            bool record = cousinId != null && Chosen.IsStorableId(saved);
            _saved = record ? saved : NotRecorded;
            _custom = record ? custom : NotRecorded;
            Write("chosen: " + Describe() + (_onScreen ? " (on the select screen)" : "") + (Active ? ", the save's " + DescribeRecord() : ""));
        }

        /// <summary>
        /// Records the save's cousin for the chosen cousin (in memory now; the file is written by <see cref="Flush"/> from
        /// LateUpdate, since this may run inside a game call).
        /// </summary>
        public static void Record(int saved, int custom)
        {
            if (!Active || !Chosen.IsStorableId(saved)) return;
            _saved = saved;
            _custom = custom;
            _dirty = true;
        }

        /// <summary>MelonMod.OnLateUpdate: writes a record made inside a game call.</summary>
        public static void Flush()
        {
            if (!_dirty) return;
            _dirty = false;
            Write($"recorded for {Describe()}: the save's {DescribeRecord()}");
        }

        private static void Write(string what)
        {
            _dirty = false;
            if (_memoryOnly) { Log.Info($"the test is running: {what} (kept in memory only)"); return; }
            try
            {
                Directory.CreateDirectory(Folder);
                string tmp = FilePath + ".tmp";
                var stored = new Stored
                {
                    Cousin = _cousinId, Style = _style, Picked = _onScreen ? PickedOnScreen : null,
                    Saved = HasRecord ? _saved : null, Custom = HasRecord ? _custom : null, ForeverFolder = _foreverFolder,
                };
                File.WriteAllText(tmp, JsonSerializer.Serialize(stored, Json));
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
                Log.Info(what);
            }
            catch (Exception e) { Log.Warn("settings.json could not be written: " + e.Message); }
        }

        /// <summary>Sets the Katamari Forever folder (null or empty: none) and writes settings.json (in memory only while the test runs).</summary>
        public static void SetForeverFolder(string folder)
        {
            _foreverFolder = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim().Trim('"');
            Write(_foreverFolder == null ? "the Katamari Forever folder is cleared" : "a Katamari Forever folder is set");
        }

        /// <summary>The test flag: this cousin (or none) and style for this session, never written. The saved cousin is taken from the save later.</summary>
        public static void ChooseInMemory(string cousinId, string style)
        {
            _memoryOnly = true;
            _cousinId = cousinId;
            if (!string.IsNullOrEmpty(style)) _style = style;
            _onScreen = false;
            _saved = _custom = NotRecorded;
        }
    }
}
