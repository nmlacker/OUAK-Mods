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
        /// <summary>Style key to the select-screen icon (a PNG) relative to Cousins/.</summary>
        public Dictionary<string, string> Icons = new(StringComparer.Ordinal);

        /// <summary>Style key to how the player's presents and the stage HUD fit her in that style (cousins.json "fit"); a style without one gets <see cref="StyleFit.None"/>.</summary>
        public Dictionary<string, StyleFit> Fits = new(StringComparer.Ordinal);

        public StyleFit FitOf(string key) => key != null && Fits.TryGetValue(key, out var f) ? f : StyleFit.None;

        /// <summary>Style key to what that style needs to play Katamari Forever's motions (cousins.json "forever"); a style without one plays OUAK's.</summary>
        public Dictionary<string, ForeverRig> Forever = new(StringComparer.Ordinal);

        public ForeverRig ForeverOf(string key) => key != null && Forever.TryGetValue(key, out var r) ? r : null;
        public string StylePath(string key) => Styles.FirstOrDefault(s => s.Key == key).Value;
        public string IconPath(string key) => key != null && Icons.TryGetValue(key, out var p) ? p : null;
        public string StyleLabel(string key) => $"{Name}, {(StyleNames.TryGetValue(key, out var s) ? s : key + " style")}";
    }

    /// <summary>
    /// How the player's presents and the stage HUD fit one cousin in one style, from cousins.json "fit" (her own numbers,
    /// no game data). Offsets are in centimetres in her joint's own axes (her joints rest at identity, so x is her left,
    /// y up, z forward).
    /// </summary>
    public sealed class StyleFit
    {
        public static readonly StyleFit None = new();

        /// <summary>The game joint a present hangs on (its name) to an offset from her joint of that name.</summary>
        public Dictionary<string, float[]> JointOffsets = new(StringComparer.Ordinal);
        /// <summary>A present id to an offset added to its joint's, for the few presents that need their own.</summary>
        public Dictionary<int, float[]> PresentOffsets = new();
        /// <summary>Present ids she never shows, whatever they are.</summary>
        public HashSet<int> CannotWear = new();
        /// <summary>Her scale and offset in the stage HUD's copy of the player only.</summary>
        public float HudScale = 1f;
        public float[] HudOffset = { 0f, 0f, 0f };
        /// <summary>Words for the log: where the numbers come from.</summary>
        public string Notes;

        public float[] OffsetFor(string joint, int presentId)
        {
            var o = new float[3];
            if (joint != null && JointOffsets.TryGetValue(joint, out var j)) { o[0] += j[0]; o[1] += j[1]; o[2] += j[2]; }
            if (PresentOffsets.TryGetValue(presentId, out var p)) { o[0] += p[0]; o[1] += p[1]; o[2] += p[2]; }
            return o;
        }

        internal static StyleFit Read(JsonElement e)
        {
            var f = new StyleFit();
            if (e.TryGetProperty("joint_offsets", out var jo))
                foreach (var j in jo.EnumerateObject()) f.JointOffsets[j.Name] = Vec(j.Value);
            if (e.TryGetProperty("present_offsets", out var po))
                foreach (var p in po.EnumerateObject())
                {
                    if (!int.TryParse(p.Name, out int id) || id <= 0) throw new InvalidDataException($"present_offsets: '{p.Name}' is not a present id");
                    f.PresentOffsets[id] = Vec(p.Value);
                }
            if (e.TryGetProperty("cannot_wear", out var cw))
                foreach (var v in cw.EnumerateArray()) f.CannotWear.Add(v.GetInt32());
            if (e.TryGetProperty("hud_scale", out var hs))
            {
                f.HudScale = hs.GetSingle();
                if (!(f.HudScale >= 0.5f && f.HudScale <= 1.5f)) throw new InvalidDataException($"hud_scale {f.HudScale} is outside 0.5 to 1.5");
            }
            if (e.TryGetProperty("hud_offset", out var ho)) f.HudOffset = Vec(ho);
            if (e.TryGetProperty("notes", out var n)) f.Notes = n.GetString();
            return f;
        }

        private static float[] Vec(JsonElement e)
        {
            var v = new float[3];
            int i = 0;
            foreach (var x in e.EnumerateArray())
            {
                if (i >= 3) throw new InvalidDataException("an offset has more than 3 numbers");
                v[i++] = x.GetSingle();
                if (float.IsNaN(v[i - 1]) || Math.Abs(v[i - 1]) > 200f) throw new InvalidDataException("an offset is not a number of centimetres under 200");
            }
            if (i != 3) throw new InvalidDataException("an offset needs 3 numbers");
            return v;
        }
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
                    if (e.TryGetProperty("icons", out var ic))
                        foreach (var s in ic.EnumerateObject()) c.Icons[s.Name] = s.Value.GetString();
                    if (e.TryGetProperty("fit", out var fit))
                        foreach (var s in fit.EnumerateObject()) c.Fits[s.Name] = StyleFit.Read(s.Value);
                    if (e.TryGetProperty("forever", out var fe))
                        foreach (var s in fe.EnumerateObject()) c.Forever[s.Name] = ReadForever(s.Value);
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

        /// <summary>
        /// A style's "forever" block: scale (her cm per Forever unit), axes (3 signs), joints (her joint name to a Forever
        /// node 0..255), states (an OUAK Animator state name to [pack 1 or 2, slot 0..157]). Anything else stops cousins.json
        /// from loading.
        /// </summary>
        private static ForeverRig ReadForever(JsonElement e)
        {
            var r = new ForeverRig { Scale = e.GetProperty("scale").GetSingle() };
            if (!(r.Scale > 0f && r.Scale < 1000f)) throw new InvalidDataException($"forever: scale {r.Scale} is out of range");
            int i = 0;
            foreach (var x in e.GetProperty("axes").EnumerateArray())
            {
                if (i >= 3) throw new InvalidDataException("forever: axes has more than 3 numbers");
                float a = x.GetSingle();
                if (a != 1f && a != -1f) throw new InvalidDataException("forever: each axis is 1 or -1");
                r.Axes[i++] = a;
            }
            if (i != 3) throw new InvalidDataException("forever: axes needs 3 numbers");
            foreach (var j in e.GetProperty("joints").EnumerateObject())
            {
                int node = j.Value.GetInt32();
                if (node < 0 || node > 255) throw new InvalidDataException($"forever: {j.Name}'s node {node} is out of range");
                r.Joints[j.Name] = node;
            }
            foreach (var st in e.GetProperty("states").EnumerateObject())
            {
                var v = new List<int>();
                foreach (var x in st.Value.EnumerateArray()) v.Add(x.GetInt32());
                if (v.Count != 2 || v[0] < 1 || v[0] > 2 || v[1] < 0 || v[1] > 157)
                    throw new InvalidDataException($"forever: state {st.Name} needs [pack 1 or 2, slot 0 to 157]");
                r.States[st.Name] = (v[0], v[1]);
            }
            if (r.Joints.Count == 0 || r.States.Count == 0) throw new InvalidDataException("forever: no joints or no states");
            return r;
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
