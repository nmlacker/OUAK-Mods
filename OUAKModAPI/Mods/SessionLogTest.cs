using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MelonLoader;

namespace OUAKModAPI
{
    /// <summary>
    /// The session logs' part of the API self-test (<see cref="ApiSelfTest"/>): the session folder and its header, scene
    /// lines, ModLog categories (once each, in all / errors / the mod's file), plain MelonLogger lines, the game's (Unity)
    /// errors, repeated warnings, multi-line messages, turning the logs off and on, marking a session that didn't end
    /// normally, and pruning old sessions. Restores the user's settings and removes its own folders.
    /// </summary>
    internal static class SessionLogTest
    {
        internal static string Run(out bool ok)
        {
            var notes = new List<string>();
            var fails = new List<string>();
            void Check(string what, bool good, string detail = null)
            {
                (good ? notes : fails).Add(detail == null ? what : $"{what} ({detail})");
                ApiCore.Log.Info($"[LogTest] {(good ? "OK  " : "FAIL")} {what}{(detail == null ? "" : ": " + detail)}");
            }

            bool wasEnabled = SessionLog.Enabled;
            int wasKeep = SessionLog.Keep;
            string id = DateTime.Now.ToString("HHmmssfff");
            string fakeAbnormal = null;
            var fakeOld = new List<string>();
            try
            {
                if (!SessionLog.Running) SessionLog.Enabled = true;
                string dir = SessionLog.Folder;
                string all = Read(dir, "all");
                Check("a session folder, still marked running", dir != null && File.Exists(Path.Combine(dir, "running.txt")), Path.GetFileName(dir));
                Check("the header: game, MelonLoader, API, how to turn it off, mods",
                    all.Contains("--- session log") && all.Contains("OUAK Modding API") && all.Contains("SessionLogs = false") && all.Contains("[OUAK API/Logs] Mods: "),
                    FirstWith(all, "Mods: "));
                Check("scene changes", all.Contains("[Game/Scene] loaded: "), FirstWith(all, "[Game/Scene]"));

                // ModLog with a category: once in all.log, also in errors.log and the mod's own file.
                var log = ModLog.For("LogTest");
                log.In("Net").Warn("probe-net-" + id);
                all = Read(dir, "all");
                Check("ModLog categories: [LogTest/Net], written once, also in errors.log and LogTest.log",
                    Count(all, "WARN  [LogTest/Net] probe-net-" + id) == 1 && Read(dir, "errors").Contains("probe-net-" + id) && Read(dir, "LogTest").Contains("probe-net-" + id),
                    FirstWith(all, "probe-net-" + id));

                // Mods that log through MelonLoader directly are captured; "[Tag] " becomes the category.
                MelonLogger.Msg("[Probe] probe-melon-" + id);
                new MelonLogger.Instance("Other_Mod").Error("probe-other-" + id);
                all = Read(dir, "all");
                Check("plain MelonLogger lines: the [Tag] is the category", Count(all, "/Probe] probe-melon-" + id) == 1, FirstWith(all, "probe-melon-" + id));
                Check("another mod's errors, in its own file", all.Contains("ERROR [Other Mod] probe-other-" + id) && Read(dir, "Other_Mod").Contains("probe-other-" + id),
                    FirstWith(all, "probe-other-" + id));

                // The game's own errors (Unity's log).
                UnityEngine.Debug.LogError("probe-unity-" + id);
                all = Read(dir, "all");
                Check("the game's (Unity) errors", all.Contains("[Game/Unity Error] probe-unity-" + id) && Read(dir, "Game").Contains("probe-unity-" + id),
                    FirstWith(all, "probe-unity-" + id));

                // Repeats: the first 20 are written, the rest counted.
                for (int i = 0; i < 30; i++) log.Warn("probe-repeat-" + id);
                all = Read(dir, "all");
                int written = Count(all, "] probe-repeat-" + id);
                Check($"a warning repeated 30x is written {SessionLog.RepeatLimit}x, then only counted", written == SessionLog.RepeatLimit && all.Contains("further copies are only counted"),
                    $"written {written}x");

                log.Info("probe-multi-" + id + "\nsecond line");
                all = Read(dir, "all");
                int at = all.IndexOf("probe-multi-" + id, StringComparison.Ordinal);
                Check("a multi-line message keeps its lines together, indented", at >= 0 && all.IndexOf("\n                    second line", at, StringComparison.Ordinal) > at);

                // Turning the logs off and on; a session that didn't end normally; pruning.
                var real = Directory.GetDirectories(SessionLog.Root).Length;
                fakeAbnormal = Path.Combine(SessionLog.Root, "2000-01-01_00-00-00");
                Directory.CreateDirectory(fakeAbnormal);
                File.WriteAllText(Path.Combine(fakeAbnormal, "running.txt"), "test");
                File.WriteAllText(Path.Combine(fakeAbnormal, "all.log"), "00:00:00.000 INFO  [Test] a session that crashed\n");
                for (int i = 1; i <= 3; i++)
                {
                    string d = Path.Combine(SessionLog.Root, $"1999-01-0{i}_00-00-00");
                    Directory.CreateDirectory(d);
                    fakeOld.Add(d);
                }
                SessionLog.Keep = real + 2;   // this session, the next one and the fake crashed one stay; the 1999 ones go

                SessionLog.Enabled = false;
                bool stopped = !SessionLog.Running && !File.Exists(Path.Combine(dir, "running.txt")) && Read(dir, "all").Contains("--- session log ends: turned off by the user");
                log.Info("probe-off-" + id);
                Check("turning it off ends the log and writes nothing more", stopped && !Read(dir, "all").Contains("probe-off-" + id) && !File.Exists(Path.Combine(dir, "running.txt")));

                SessionLog.Enabled = true;
                string next = SessionLog.Folder;
                Check("turning it on starts a new session folder", next != null && next != dir && Read(next, "all").Contains("--- session log"), Path.GetFileName(next));
                string crashed = Read(fakeAbnormal, "all");
                Check("a session that didn't end normally is marked at the next start",
                    crashed.Contains("did not end normally") && !File.Exists(Path.Combine(fakeAbnormal, "running.txt")) && SessionLog.PreviousAbnormal == "2000-01-01_00-00-00"
                    && Read(next, "all").Contains("(2000-01-01_00-00-00) did not end normally"),
                    FirstWith(crashed, "did not end normally"));
                Check($"only the newest {real + 2} sessions are kept", fakeOld.All(d => !Directory.Exists(d)) && Directory.Exists(dir) && Directory.Exists(fakeAbnormal),
                    $"{Directory.GetDirectories(SessionLog.Root).Length} folders");
            }
            catch (Exception e)
            {
                fails.Add("threw " + e.Message);
                ApiCore.Log.Error("[LogTest] " + e);
            }
            finally
            {
                SessionLog.Keep = wasKeep;
                if (SessionLog.Enabled != wasEnabled) SessionLog.Enabled = wasEnabled;
                foreach (var d in fakeOld.Append(fakeAbnormal))
                    try { if (d != null && Directory.Exists(d)) Directory.Delete(d, true); } catch { }
            }
            ok = fails.Count == 0;
            return ok ? $"session logs: {notes.Count} checks" : "session logs: " + string.Join("; ", fails);
        }

        private static string Read(string dir, string file)
        {
            try
            {
                string p = Path.Combine(dir, file + ".log");
                if (!File.Exists(p)) return "";
                using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var r = new StreamReader(fs);
                return r.ReadToEnd();
            }
            catch { return ""; }
        }

        private static int Count(string text, string part)
        {
            int n = 0;
            for (int i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal)) n++;
            return n;
        }

        private static string FirstWith(string text, string part)
        {
            int i = text.IndexOf(part, StringComparison.Ordinal);
            if (i < 0) return "not found";
            int start = text.LastIndexOf('\n', i) + 1;
            int end = text.IndexOf('\n', i);
            return text.Substring(start, (end < 0 ? text.Length : end) - start).Trim();
        }
    }
}
