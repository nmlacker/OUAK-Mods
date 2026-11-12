using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MelonLoader;
using MelonLoader.Utils;

namespace OUAKModAPI
{
    /// <summary>
    /// A mod's log: every line goes to the MelonLoader console and Latest.log (tagged with the mod's name), to the
    /// session logs (<see cref="SessionLog"/>: UserData/OUAKModAPI/Logs/Sessions/&lt;date_time&gt;/, kept across sessions)
    /// and to the mod's own file, UserData/OUAKModAPI/Logs/&lt;name&gt;.log (the previous session's file is kept as .old.log).
    /// <code>static readonly ModLog Log = ModLog.For("My Mod");  Log.Info("hello");
    /// static readonly ModLog Net = Log.In("Net");          Net.Warn("lost the host");   // [My Mod/Net] in the session logs</code>
    /// Levels: <see cref="Debug"/>, <see cref="Info"/>, <see cref="Warn"/>, <see cref="Error"/>, <see cref="Exception"/>.
    /// Safe to call from any thread.
    /// </summary>
    public sealed class ModLog
    {
        private static readonly Dictionary<string, ModLog> All = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object Gate = new();

        /// <summary>Also print <see cref="Debug"/> lines (MelonPreferences: OUAKModAPI / VerboseLogs).</summary>
        public static bool Verbose { get; set; }

        /// <summary>Where the log files go.</summary>
        public static string Folder => Path.Combine(MelonEnvironment.UserDataDirectory, "OUAKModAPI", "Logs");

        public string Name { get; }
        /// <summary>The category (null for the mod's main log, see <see cref="In"/>).</summary>
        public string Category { get; }
        /// <summary>This log's file (shared by its categories).</summary>
        public string FilePath { get; }

        private readonly MelonLogger.Instance _melon;
        private readonly ModLog _root;
        private readonly Dictionary<string, ModLog> _categories;
        private StreamWriter _file;
        private bool _fileFailed;

        /// <summary>The log for <paramref name="name"/> (the same object every time for the same name).</summary>
        public static ModLog For(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) name = "Mod";
            lock (Gate)
            {
                if (!All.TryGetValue(name, out var log)) All[name] = log = new ModLog(name);
                return log;
            }
        }

        private ModLog(string name)
        {
            Name = name;
            FilePath = Path.Combine(Folder, Safe(name) + ".log");
            _melon = new MelonLogger.Instance(name);
            _root = this;
            _categories = new Dictionary<string, ModLog>(StringComparer.OrdinalIgnoreCase);
        }

        private ModLog(ModLog root, string category)
        {
            Name = root.Name;
            Category = category;
            FilePath = root.FilePath;
            _melon = root._melon;
            _root = root;
        }

        /// <summary>
        /// This mod's log for one category (the same object every time): its lines read "[Category] ..." in the console
        /// and "[Mod/Category]" in the session logs, e.g. <c>Log.In("Net")</c>, <c>Log.In("Scene")</c>.
        /// </summary>
        public ModLog In(string category)
        {
            if (string.IsNullOrWhiteSpace(category)) return _root;
            lock (Gate)
            {
                if (!_root._categories.TryGetValue(category, out var log)) _root._categories[category] = log = new ModLog(_root, category.Trim());
                return log;
            }
        }

        public void Info(string msg) { Show(SessionLog.Level.Info, msg); Write("INFO", msg); }
        public void Warn(string msg) { Show(SessionLog.Level.Warn, msg); Write("WARN", msg); }
        public void Error(string msg) { Show(SessionLog.Level.Error, msg); Write("ERROR", msg); }

        /// <summary>Shown in the console only when <see cref="Verbose"/> is on (always written to the files).</summary>
        public void Debug(string msg)
        {
            if (Verbose) Show(SessionLog.Level.Debug, "[debug] " + msg);
            Write("DEBUG", msg);
        }

        private void Show(SessionLog.Level level, string msg)
        {
            string text = Category == null ? msg : $"[{Category}] {msg}";
            SessionLog.FromModLog++;
            try
            {
                if (level == SessionLog.Level.Warn) _melon.Warning(text);
                else if (level >= SessionLog.Level.Error) _melon.Error(text);
                else _melon.Msg(text);
            }
            finally { SessionLog.FromModLog--; }
        }

        /// <summary>Logs an exception with what was being done.</summary>
        public void Exception(Exception e, string doing = null) =>
            Error(doing == null ? e.ToString() : $"{doing}: {e}");

        private void Write(string level, string msg)
        {
            SessionLog.Write(Name, Category, level switch
            {
                "DEBUG" => SessionLog.Level.Debug,
                "WARN" => SessionLog.Level.Warn,
                "ERROR" => SessionLog.Level.Error,
                _ => SessionLog.Level.Info,
            }, msg);
            if (Category != null) { _root.Write(level, $"[{Category}] {msg}", false); return; }
            Write(level, msg, false);
        }

        private void Write(string level, string msg, bool _)
        {
            if (_fileFailed) return;
            lock (Gate)
            {
                try
                {
                    if (_file == null)
                    {
                        Directory.CreateDirectory(Folder);
                        string old = Path.ChangeExtension(FilePath, ".old.log");
                        if (File.Exists(FilePath))
                        {
                            if (File.Exists(old)) File.Delete(old);
                            File.Move(FilePath, old);
                        }
                        _file = new StreamWriter(new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
                        {
                            AutoFlush = true,
                        };
                    }
                    _file.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {level,-5} {msg}");
                }
                catch
                {
                    _fileFailed = true;
                }
            }
        }

        internal static void CloseAll()
        {
            lock (Gate)
            {
                foreach (var log in All.Values)
                {
                    try { log._file?.Dispose(); } catch { }
                    log._file = null;
                }
            }
        }

        private static string Safe(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name) sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 || c == ' ' ? '_' : c);
            return sb.ToString();
        }
    }
}
