using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace LongLostCousins.Data
{
    /// <summary>A cousin of cousins.json: its id, name and styles.</summary>
    public sealed class CousinEntry
    {
        public string Id, Name, Notes;
        /// <summary>Style key ("new", "old") to the .llcm path relative to Cousins/, in the file's order.</summary>
        public List<KeyValuePair<string, string>> Styles = new();
        /// <summary>Style key to the words the tab shows after the name.</summary>
        public Dictionary<string, string> StyleNames = new(StringComparer.Ordinal);

        public string StylePath(string key) => Styles.FirstOrDefault(s => s.Key == key).Value;
        public string StyleLabel(string key) => $"{Name}, {(StyleNames.TryGetValue(key, out var s) ? s : key + " style")}";
    }

    /// <summary>
    /// The cousins the mod knows, from Cousins/cousins.json inside the DLL, and their .llcm files read on first use.
    /// No Unity or game types here (the reader check runs outside the game).
    /// </summary>
    public static class Catalog
    {
        private static List<CousinEntry> _cousins;
        private static readonly Dictionary<string, LlcmModel> Models = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> Failed = new(StringComparer.Ordinal);

        /// <summary>Where resources come from: the mod's own assembly unless a check outside the game sets a folder.</summary>
        public static Func<string, byte[]> Source = FromAssembly;

        /// <summary>The error that kept cousins.json from loading, or null.</summary>
        public static string Error { get; private set; }

        public static IReadOnlyList<CousinEntry> Cousins
        {
            get
            {
                if (_cousins == null) _cousins = Load();
                return _cousins;
            }
        }

        public static CousinEntry Find(string id) => id == null ? null : Cousins.FirstOrDefault(c => c.Id == id);

        /// <summary>The model of a cousin's style, read once; null when it cannot be read (<see cref="ModelError"/> says why).</summary>
        public static LlcmModel Model(CousinEntry c, string style)
        {
            string path = c?.StylePath(style);
            if (path == null) return null;
            if (Models.TryGetValue(path, out var m)) return m;
            if (Failed.ContainsKey(path)) return null;
            try
            {
                var bytes = Source("Cousins/" + path) ?? throw new FileNotFoundException("not in the mod: Cousins/" + path);
                m = LlcmReader.Read(bytes);
                if (m.Id != c.Id || m.Style != style)
                    throw new InvalidDataException($"the file is {m.Id} ({m.Style}), cousins.json says {c.Id} ({style})");
                Models[path] = m;
                return m;
            }
            catch (Exception e)
            {
                Failed[path] = e.Message;
                return null;
            }
        }

        public static string ModelError(CousinEntry c, string style)
        {
            string path = c?.StylePath(style);
            return path != null && Failed.TryGetValue(path, out var e) ? e : null;
        }

        private static List<CousinEntry> Load()
        {
            var list = new List<CousinEntry>();
            try
            {
                var bytes = Source("Cousins/cousins.json") ?? throw new FileNotFoundException("Cousins/cousins.json is not in the mod");
                using var doc = JsonDocument.Parse(bytes);
                foreach (var e in doc.RootElement.GetProperty("cousins").EnumerateArray())
                {
                    var c = new CousinEntry
                    {
                        Id = e.GetProperty("id").GetString(),
                        Name = e.GetProperty("name").GetString(),
                        Notes = e.TryGetProperty("notes", out var n) ? n.GetString() : null,
                    };
                    foreach (var s in e.GetProperty("styles").EnumerateObject()) c.Styles.Add(new(s.Name, s.Value.GetString()));
                    if (e.TryGetProperty("style_names", out var sn))
                        foreach (var s in sn.EnumerateObject()) c.StyleNames[s.Name] = s.Value.GetString();
                    if (string.IsNullOrEmpty(c.Id) || string.IsNullOrEmpty(c.Name) || c.Styles.Count == 0)
                        throw new InvalidDataException("a cousin needs an id, a name and at least one style");
                    list.Add(c);
                }
            }
            catch (Exception e)
            {
                Error = "cousins.json: " + e.Message;
            }
            return list;
        }

        /// <summary>A resource of the mod's DLL by its path under the mod folder ("Cousins/Burda/burda_new.llcm").</summary>
        public static byte[] FromAssembly(string path)
        {
            var asm = typeof(Catalog).Assembly;
            string want = path.Replace('\\', '/');
            string name = asm.GetManifestResourceNames().FirstOrDefault(n => n.Replace('\\', '/') == want);
            if (name == null) return null;
            using var s = asm.GetManifestResourceStream(name);
            if (s == null) return null;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }

        /// <summary>The resource names inside the DLL (for the log).</summary>
        public static string[] ResourceNames() => typeof(Catalog).Assembly.GetManifestResourceNames();
    }
}
