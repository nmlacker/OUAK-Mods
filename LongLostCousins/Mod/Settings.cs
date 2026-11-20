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
    /// The player's choice: which cousin and style the mod dresses their cousin as, or none (the default), and whether
    /// the cousin was picked on the game's own select screen. Kept in UserData/LongLostCousins/settings.json, never in
    /// the game's save. The style is kept when the cousin is turned off, so the select screen shows her in the last
    /// style picked. The test flag decides the choice in memory only.
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

        private sealed class Stored
        {
            [JsonPropertyName("cousin")] public string Cousin { get; set; }
            [JsonPropertyName("style")] public string Style { get; set; }
            [JsonPropertyName("picked")] public string Picked { get; set; }
        }

        private static string _cousinId, _style = DefaultStyle;
        private static bool _onScreen, _memoryOnly;

        /// <summary>A cousin is chosen in the settings (see <see cref="Chosen"/> for whether she is worn now).</summary>
        public static bool Active => _cousinId != null;
        public static string CousinId => _cousinId;
        /// <summary>The chosen style, or the last one picked while no cousin is chosen.</summary>
        public static string Style => _style;
        public static CousinEntry Cousin => Catalog.Find(_cousinId);
        /// <summary>True when the cousin was picked on the game's select screen (she then needs the Prince in the save).</summary>
        public static bool OnScreen => Active && _onScreen;
        /// <summary>True while the test flag decides the choice (nothing is written).</summary>
        public static bool MemoryOnly => _memoryOnly;

        public static string Describe() => Active && Cousin != null ? Cousin.StyleLabel(_style) : "Off";

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
            try
            {
                if (!File.Exists(FilePath)) return;
                var s = JsonSerializer.Deserialize<Stored>(File.ReadAllText(FilePath));
                if (s == null) return;
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
            }
            catch (Exception e) { Log.Warn("settings.json could not be read; staying off: " + e.Message); }
        }

        /// <summary>
        /// Chooses a cousin and style (null cousin: off, the style is kept) and writes settings.json, unless the test
        /// decides. <paramref name="onScreen"/> says the cousin was picked on the game's select screen.
        /// </summary>
        public static void Choose(string cousinId, string style, bool onScreen = false)
        {
            _cousinId = cousinId;
            if (!string.IsNullOrEmpty(style)) _style = style;
            _onScreen = cousinId != null && onScreen;
            if (_memoryOnly) { Log.Info($"the test is running: {Describe()} is kept in memory only"); return; }
            try
            {
                Directory.CreateDirectory(Folder);
                string tmp = FilePath + ".tmp";
                var stored = new Stored { Cousin = _cousinId, Style = _style, Picked = _onScreen ? PickedOnScreen : null };
                File.WriteAllText(tmp, JsonSerializer.Serialize(stored, Json));
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
                Log.Info("chosen: " + Describe() + (_onScreen ? " (on the select screen)" : ""));
            }
            catch (Exception e) { Log.Warn("settings.json could not be written: " + e.Message); }
        }

        /// <summary>The test flag: this cousin (or none) and style for this session, never written.</summary>
        public static void ChooseInMemory(string cousinId, string style)
        {
            _memoryOnly = true;
            _cousinId = cousinId;
            if (!string.IsNullOrEmpty(style)) _style = style;
            _onScreen = false;
        }
    }
}
