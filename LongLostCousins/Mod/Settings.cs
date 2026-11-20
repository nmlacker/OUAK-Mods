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
    /// The player's choice: which cousin and style the mod dresses their cousin as, or none (the default). Kept in
    /// UserData/LongLostCousins/settings.json, never in the game's save. The test flag turns a cousin on in memory only.
    /// </summary>
    internal static class Settings
    {
        private static readonly ModLog Log = LongLostCousinsMod.Log.In("Settings");
        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

        public static string Folder => Path.Combine(MelonEnvironment.UserDataDirectory, "LongLostCousins");
        private static string FilePath => Path.Combine(Folder, "settings.json");

        private sealed class Stored
        {
            [JsonPropertyName("cousin")] public string Cousin { get; set; }
            [JsonPropertyName("style")] public string Style { get; set; }
        }

        private static string _cousinId, _style;
        private static bool _memoryOnly;

        /// <summary>A cousin is chosen.</summary>
        public static bool Active => _cousinId != null;
        public static string CousinId => _cousinId;
        public static string Style => _style;
        public static CousinEntry Cousin => Catalog.Find(_cousinId);
        /// <summary>True while the test flag decides the choice (nothing is written).</summary>
        public static bool MemoryOnly => _memoryOnly;

        public static string Describe() => Active && Cousin != null ? Cousin.StyleLabel(_style) : "Off";

        /// <summary>Reads settings.json. Off when it is missing, unreadable or names a cousin or style the mod lacks.</summary>
        public static void Load()
        {
            _cousinId = _style = null;
            try
            {
                if (!File.Exists(FilePath)) return;
                var s = JsonSerializer.Deserialize<Stored>(File.ReadAllText(FilePath));
                if (s == null || string.IsNullOrEmpty(s.Cousin)) return;
                var c = Catalog.Find(s.Cousin);
                if (c == null || c.StylePath(s.Style) == null)
                {
                    Log.Warn($"settings.json names {s.Cousin} ({s.Style}), which this version does not have; staying off");
                    return;
                }
                _cousinId = c.Id;
                _style = s.Style;
            }
            catch (Exception e) { Log.Warn("settings.json could not be read; staying off: " + e.Message); }
        }

        /// <summary>Chooses a cousin and style (null cousin: off) and writes settings.json, unless the test decides.</summary>
        public static void Choose(string cousinId, string style)
        {
            _cousinId = cousinId;
            _style = cousinId != null ? style : null;
            if (_memoryOnly) { Log.Info($"the test is running: {Describe()} is kept in memory only"); return; }
            try
            {
                Directory.CreateDirectory(Folder);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(new Stored { Cousin = _cousinId, Style = _style }, Json));
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
                Log.Info("chosen: " + Describe());
            }
            catch (Exception e) { Log.Warn("settings.json could not be written: " + e.Message); }
        }

        /// <summary>The test flag: this cousin and style for this session, never written.</summary>
        public static void ChooseInMemory(string cousinId, string style)
        {
            _memoryOnly = true;
            _cousinId = cousinId;
            _style = style;
        }
    }
}
