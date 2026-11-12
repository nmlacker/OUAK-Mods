using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MelonLoader;
using UnityEngine;

namespace OUAKModAPI
{
    /// <summary>
    /// Session logs, on by default (users can turn them off: the API's page in the mod window, or MelonPreferences
    /// [OUAKModAPI] SessionLogs = false). Every game session gets its own folder,
    /// UserData/OUAKModAPI/Logs/Sessions/&lt;yyyy-MM-dd_HH-mm-ss&gt;/, holding:
    /// <list type="bullet">
    /// <item><c>all.log</c>: every line, in order: each mod's lines (through <see cref="ModLog"/> or plain MelonLogger),
    /// MelonLoader's own, the game's errors and exceptions (Unity), scene changes and game events, crashes;</item>
    /// <item><c>errors.log</c>: only the warnings, errors and crashes;</item>
    /// <item>one file per source (<c>OUAK_Multiplayer.log</c>, <c>Game.log</c>, <c>MelonLoader.log</c>...).</item>
    /// </list>
    /// Each line reads <c>21:48:36.123 INFO  [Source/Category] message</c>; a line that starts with "[Tag] " gets Tag
    /// as its category. Lines are written at once (nothing is lost if the game crashes); a session that didn't end
    /// normally is marked so in its all.log at the next start. The newest <see cref="Keep"/> sessions are kept.
    /// MelonLoader's own Logs folder can't be relied on: it deletes by file name and its names aren't zero-padded,
    /// so it keeps old March logs and deletes the newest ones (measured 2026-11-08).
    /// <para>Mods don't need to do anything: their MelonLogger lines are captured. <see cref="ModLog"/> adds levels and
    /// categories: <c>ModLog.For("My Mod").In("Net").Info("...")</c>.</para>
    /// </summary>
    public static class SessionLog
    {
        public enum Level { Debug, Info, Warn, Error, Crash }

        /// <summary>Where the session folders go.</summary>
        public static string Root => Path.Combine(ModLog.Folder, "Sessions");
        /// <summary>This session's folder (null while session logs are off).</summary>
        public static string Folder { get; private set; }
        /// <summary>Session logs are being written.</summary>
        public static bool Running => Folder != null;
        /// <summary>The user's setting (on by default).</summary>
        public static bool Enabled
        {
            get => _enabledPref?.Value ?? true;
            set
            {
                if (_enabledPref != null && _enabledPref.Value != value) { _enabledPref.Value = value; MelonPreferences.Save(); }
                if (value) Start(); else Stop("turned off by the user");
            }
        }
        /// <summary>How many session folders are kept (the user's setting, default 30).</summary>
        public static int Keep
        {
            get => Math.Max(1, _keepPref?.Value ?? 30);
            set { if (_keepPref != null) { _keepPref.Value = Math.Max(1, value); MelonPreferences.Save(); } }
        }
        /// <summary>Lines written this session.</summary>
        public static int Lines { get; private set; }
        /// <summary>Warnings / errors written this session.</summary>
        public static int Problems { get; private set; }
        /// <summary>The session before this one, if it didn't end normally (crash or closed from outside).</summary>
        public static string PreviousAbnormal { get; private set; }

        /// <summary>A file stops growing at this size (a note says so).</summary>
        public const long MaxFileBytes = 64L * 1024 * 1024;
        /// <summary>After this many copies of the same warning / error, further copies are only counted.</summary>
        public const int RepeatLimit = 20;

        private static readonly object Gate = new();
        private static readonly Dictionary<string, Sink> Sinks = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> Repeats = new();
        private static readonly Regex Tag = new(@"^\[([^\[\]]{1,40})\] ?", RegexOptions.Compiled);
        private static MelonPreferences_Entry<bool> _enabledPref;
        private static MelonPreferences_Entry<int> _keepPref;
        private static Application.LogCallback _unityCallback;   // kept referenced so the IL2CPP side can keep calling it
        private static bool _hooked;
        private static string _marker;

        // Set while a ModLog line goes through MelonLogger, so the capture below doesn't write it twice.
        [ThreadStatic] internal static int FromModLog;

        private sealed class Sink
        {
            public StreamWriter W;
            public long Bytes;
            public bool Full;
        }

        internal static void Init(MelonPreferences_Category prefs)
        {
            _enabledPref = prefs.GetEntry<bool>("SessionLogs") ?? prefs.CreateEntry("SessionLogs", true, "Session logs",
                "Write a log of every game session (every mod, the game's errors, scene changes) to UserData/OUAKModAPI/Logs/Sessions.");
            _keepPref = prefs.GetEntry<int>("SessionLogsKeep") ?? prefs.CreateEntry("SessionLogsKeep", 30, "Session logs kept",
                "How many session folders to keep; older ones are deleted at start.");
            Hook();
            if (Enabled) Start();
        }

        // ---------------------------------------------------------------- writing

        /// <summary>Writes one line. <paramref name="source"/> is usually a mod's name; <paramref name="category"/> may be null.</summary>
        public static void Write(string source, string category, Level level, string message)
        {
            if (Folder == null) return;
            source = Clean(source);
            if (message == null) message = "";
            if (category == null)
            {
                var m = Tag.Match(message);
                if (m.Success) { category = m.Groups[1].Value; message = message.Substring(m.Length); }
            }
            string where = string.IsNullOrEmpty(category) ? source : source + "/" + category;
            lock (Gate)
            {
                if (Folder == null) return;
                if (level >= Level.Warn)
                {
                    string key = where + "|" + FirstLine(message);
                    Repeats.TryGetValue(key, out int n);
                    Repeats[key] = ++n;
                    if (n > RepeatLimit)
                    {
                        if (n == 100 || n == 1000 || n == 10000 || n == 100000)
                            Emit(source, level, where, $"(the line below has now come {n} times) {FirstLine(message)}");
                        return;
                    }
                    if (n == RepeatLimit) message += $"\n(this is the {RepeatLimit}th time: further copies are only counted)";
                }
                Emit(source, level, where, message);
            }
        }

        private static void Emit(string source, Level level, string where, string message)
        {
            var sb = new StringBuilder();
            string head = $"{DateTime.Now:HH:mm:ss.fff} {Name(level),-5} [{where}] ";
            string[] lines = message.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
            sb.Append(head).Append(lines[0]);
            for (int i = 1; i < lines.Length; i++) sb.Append('\n').Append(' ', 20).Append(lines[i]);
            string text = sb.ToString();
            Lines++;
            if (level >= Level.Warn) Problems++;
            Put("all", text);
            Put(FileName(source), text);
            if (level >= Level.Warn) Put("errors", text);
        }

        private static void Put(string file, string text)
        {
            try
            {
                if (!Sinks.TryGetValue(file, out var s))
                {
                    var fs = new FileStream(Path.Combine(Folder, file + ".log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    s = new Sink { W = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true }, Bytes = fs.Length };
                    Sinks[file] = s;
                }
                if (s.Full) return;
                if (s.Bytes + text.Length > MaxFileBytes)
                {
                    s.W.WriteLine($"{DateTime.Now:HH:mm:ss.fff} NOTE  [OUAK API/Logs] this file reached {MaxFileBytes / (1024 * 1024)} MB: nothing more is written to it this session.");
                    s.Full = true;
                    return;
                }
                s.W.WriteLine(text);
                s.Bytes += text.Length + 1;
            }
            catch { }
        }

        // ---------------------------------------------------------------- session folders

        private static void Start()
        {
            lock (Gate)
            {
                if (Folder != null) return;
                try
                {
                    Directory.CreateDirectory(Root);
                    MarkAbnormalEnds();
                    string name = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                    string dir = Path.Combine(Root, name);
                    for (int i = 2; Directory.Exists(dir); i++) dir = Path.Combine(Root, $"{name}_{i}");
                    Directory.CreateDirectory(dir);
                    _marker = Path.Combine(dir, "running.txt");
                    File.WriteAllText(_marker, "The game is running (or this session didn't end normally).");
                    Folder = dir;
                    Lines = Problems = 0;
                    Repeats.Clear();
                    Prune(dir);
                }
                catch (Exception e)
                {
                    Folder = null;
                    MelonLogger.Warning($"Session logs: couldn't start ({e.Message}).");
                    return;
                }
            }
            Header();
        }

        private static void Stop(string why)
        {
            lock (Gate)
            {
                if (Folder == null) return;
                Emit("OUAK API", Level.Info, "OUAK API/Logs", $"--- session log ends: {why} ({Lines} lines, {Problems} warnings / errors)");
                foreach (var s in Sinks.Values) { try { s.W.Dispose(); } catch { } }
                Sinks.Clear();
                try { if (_marker != null) File.Delete(_marker); } catch { }
                Folder = null;
                _marker = null;
            }
        }

        // A session whose running.txt is still there didn't reach OnApplicationQuit: say so in its all.log.
        private static void MarkAbnormalEnds()
        {
            foreach (var dir in Directory.GetDirectories(Root).OrderBy(d => d, StringComparer.Ordinal))
            {
                string marker = Path.Combine(dir, "running.txt");
                if (!File.Exists(marker)) continue;
                try
                {
                    string all = Path.Combine(dir, "all.log");
                    DateTime last = File.Exists(all) ? File.GetLastWriteTime(all) : Directory.GetLastWriteTime(dir);
                    File.AppendAllText(all, $"--- this session did not end normally: the game crashed or was closed from outside " +
                        $"(last line written {last:yyyy-MM-dd HH:mm:ss}; noticed at the next start, {DateTime.Now:yyyy-MM-dd HH:mm:ss}).\n");
                    File.Delete(marker);
                    PreviousAbnormal = Path.GetFileName(dir);
                }
                catch { }
            }
        }

        private static void Prune(string current)
        {
            var dirs = Directory.GetDirectories(Root)
                .Where(d => Regex.IsMatch(Path.GetFileName(d), @"^\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}"))
                .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
                .ToList();
            foreach (var d in dirs.Skip(Keep))
            {
                if (string.Equals(d, current, StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(d, true); } catch { }
            }
        }

        private static void Header()
        {
            string game = "?", unity = "?";
            try { game = Application.version; unity = Application.unityVersion; } catch { }
            Write("OUAK API", "Logs", Level.Info,
                $"--- session log, {DateTime.Now:yyyy-MM-dd HH:mm:ss}: Once Upon a KATAMARI {game} (Unity {unity}), " +
                $"MelonLoader {typeof(MelonMod).Assembly.GetName().Version}, OUAK Modding API {ApiCore.Version}, {Environment.OSVersion}.\n" +
                $"Turn session logs off on the API's page of the mod window (Mods), or in UserData/MelonPreferences.cfg: [OUAKModAPI] SessionLogs = false. " +
                $"The newest {Keep} sessions are kept in {Root}. These logs can hold player names and SteamIDs from online play.");
            if (PreviousAbnormal != null)
                Write("OUAK API", "Logs", Level.Warn, $"The previous session ({PreviousAbnormal}) did not end normally: the game crashed or was closed from outside. Its log ends where it stopped.");
            if (ApiCore.ModsListed) WriteMods();
        }

        /// <summary>The installed mods (called once every mod is loaded).</summary>
        internal static void WriteMods()
        {
            try
            {
                Write("OUAK API", "Logs", Level.Info, "Mods: " + string.Join(", ",
                    MelonMod.RegisteredMelons.Select(m => $"{m.Info.Name} {m.Info.Version}")));
            }
            catch { }
        }

        internal static void Quit() => Stop("the game quit normally");

        // ---------------------------------------------------------------- capture

        private static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            try
            {
                MelonLogger.MsgDrawingCallbackHandler += (_, _, name, msg) => FromMelon(Level.Info, name, msg);
                MelonLogger.WarningCallbackHandler += (name, msg) => FromMelon(Level.Warn, name, msg);
                MelonLogger.ErrorCallbackHandler += (name, msg) => FromMelon(Level.Error, name, msg);
            }
            catch (Exception e) { MelonLogger.Warning($"Session logs: MelonLoader capture failed ({e.Message})."); }
            try
            {
                _unityCallback = new Action<string, string, LogType>(FromUnity);
                Application.add_logMessageReceivedThreaded(_unityCallback);
            }
            catch (Exception e) { MelonLogger.Warning($"Session logs: Unity capture failed ({e.Message})."); }
            AppDomain.CurrentDomain.UnhandledException += OnUnhandled;

            GameEvents.StageLoaded += gm => Write("Game", "Events", Level.Info, $"stage loaded: scene {GameRefs.SceneName}, " +
                $"{(GameRefs.IsMultiplay ? "multiplayer" : "single-player")}, launcher stage {Safe(() => GameRefs.Global.launcherStage.ToString())}");
            GameEvents.StagePlaying += _ => Write("Game", "Events", Level.Info, "stage playing");
            GameEvents.StageRestarted += _ => Write("Game", "Events", Level.Info, "stage restarted");
            GameEvents.StageEnded += (_, cleared) => Write("Game", "Events", Level.Info, cleared ? "stage ended: cleared" : "stage ended: not cleared");
            GameEvents.StageLeft += () => Write("Game", "Events", Level.Info, "stage left");
            GameEvents.PlazaEntered += scene => Write("Game", "Events", Level.Info, $"plaza entered: {scene}");
            GameEvents.ShipEntered += () => Write("Game", "Events", Level.Info, "ship entered");
            GameEvents.PauseChanged += paused => Write("Game", "Events", Level.Debug, paused ? "paused" : "unpaused");
        }

        private static void OnUnhandled(object sender, UnhandledExceptionEventArgs e) =>
            Write("Game", "Crash", Level.Crash, $"unhandled .NET exception{(e.IsTerminating ? " (the game is closing)" : "")}: {e.ExceptionObject}");

        private static void FromMelon(Level level, string name, string msg)
        {
            if (FromModLog > 0) return;
            Write(string.IsNullOrWhiteSpace(name) ? "MelonLoader" : name, null, level, msg);
        }

        // Called on whichever thread logged.
        private static void FromUnity(string condition, string stackTrace, LogType type)
        {
            if (type == LogType.Log) return;
            Level level = type == LogType.Warning ? Level.Warn : Level.Error;
            string text = string.IsNullOrWhiteSpace(stackTrace) ? condition : condition + "\n" + stackTrace.TrimEnd();
            Write("Game", type == LogType.Warning ? "Unity" : "Unity " + type, level, text);
        }

        /// <summary>Scene changes, from <see cref="ApiCore"/>.</summary>
        internal static void Scene(string what, string scene, int index) =>
            Write("Game", "Scene", Level.Info, $"{what}: {scene} (#{index})");

        // ---------------------------------------------------------------- helpers

        private static string Clean(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return "Mod";
            return source.Replace('_', ' ').Trim();
        }

        private static string FileName(string source)
        {
            var sb = new StringBuilder();
            foreach (char c in source) sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 || c == ' ' ? '_' : c);
            string f = sb.ToString();
            return f is "all" or "errors" ? f + "_" : f;
        }

        private static string FirstLine(string s)
        {
            int cut = s.IndexOf('\n');
            return cut < 0 ? s : s.Substring(0, cut).TrimEnd('\r');
        }

        private static string Name(Level l) => l switch
        {
            Level.Debug => "DEBUG",
            Level.Info => "INFO",
            Level.Warn => "WARN",
            Level.Error => "ERROR",
            _ => "CRASH",
        };

        private static string Safe(Func<string> f)
        {
            try { return f(); } catch { return "?"; }
        }
    }
}
