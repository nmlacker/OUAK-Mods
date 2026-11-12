using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Il2CppApp.KatamariSin;
using MelonLoader;
using MelonLoader.Utils;
using OUAKModAPI;
using UnityEngine;

namespace OUAKModMenu
{
    /// <summary>
    /// Test-only: armed by UserData/OUAKModMenu/textdump.flag (comma-separated words; an entry ending in "_" is an id
    /// prefix, e.g. "TIPS_"). At the title screen it writes every loaded game text (TextCommonTable) whose id starts
    /// with a prefix or whose English contains a word to UserData/OUAKModMenu/texts.txt, then quits. Read-only.
    /// </summary>
    internal static class TextDump
    {
        private static string FlagPath => Path.Combine(MelonEnvironment.UserDataDirectory, "OUAKModMenu", "textdump.flag");
        private static string OutPath => Path.Combine(MelonEnvironment.UserDataDirectory, "OUAKModMenu", "texts.txt");
        private static string[] _words;
        private static float _at;

        public static void CheckFlag()
        {
            if (!File.Exists(FlagPath)) return;
            _words = File.ReadAllText(FlagPath).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            try { File.Delete(FlagPath); } catch { }
            MelonLogger.Msg($"[TextDump] armed: {string.Join(", ", _words)}");
        }

        public static void Tick()
        {
            if (_words == null || Time.unscaledTime < _at) return;
            _at = Time.unscaledTime + 1f;
            if (SceneLoader.ActiveScene != "SelectData" || GameRefs.Global == null) return;
            if (Time.unscaledTime < 25f) return; // let the title screen finish loading its tables
            var words = _words;
            _words = null;
            try
            {
                var lines = new List<string>();
                var seen = new HashSet<string>();
                foreach (var table in Resources.FindObjectsOfTypeAll<TextCommonTable>())
                {
                    var list = table?.datas;
                    if (list == null) continue;
                    lines.Add($"## table {table.name}: {list.Count} entries");
                    for (int i = 0; i < list.Count; i++)
                    {
                        var e = list[i];
                        if (e == null || string.IsNullOrEmpty(e.id) || !seen.Add(table.name + "/" + e.id)) continue;
                        string en = e.english ?? "";
                        bool hit = words.Any(w => w.EndsWith("_") ? e.id.StartsWith(w, StringComparison.Ordinal)
                                                                  : en.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
                        if (hit) lines.Add($"{table.name}/{e.id}: {en.Replace("\n", " / ")} | {e.japanese?.Replace("\n", " / ")}");
                    }
                }
                File.WriteAllLines(OutPath, lines);
                MelonLogger.Msg($"[TextDump] wrote {lines.Count} lines to {OutPath}");
            }
            catch (Exception e) { MelonLogger.Msg("[TextDump] failed: " + e.Message); }
            MelonLogger.Msg("[TextDump] DONE, quitting");
            Application.Quit();
        }
    }
}
