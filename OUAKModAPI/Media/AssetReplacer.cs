using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MelonLoader;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader.Utils;
using UnityEngine;

namespace OUAKModAPI
{
    /// <summary>
    /// Replaces the game's textures and sounds by name, from files, while the game runs:
    /// <list type="bullet">
    /// <item>UserData/OUAKModAPI/Assets/Textures/&lt;texture name&gt;.png replaces every loaded texture with that name.
    /// The texture's own pixels are changed (on the graphics card for the game's compressed textures), so every
    /// material, sprite and UI image that uses it changes. Pictures of another size are scaled to the original's.</item>
    /// <item>UserData/OUAKModAPI/Assets/Audio/&lt;clip name&gt;.wav replaces that sound wherever an AudioSource plays
    /// it (music, loops, voices; one-shot effects that never sit in an AudioSource can't be swapped).</item>
    /// </list>
    /// Saving a file again swaps it live. Mods can add their own folders (<see cref="AddFolder"/>, same Textures/
    /// and Audio/ layout) or replace from code (<see cref="ReplaceTexture(string, string)"/>,
    /// <see cref="ReplaceAudio(string, AudioClip)"/>). To find names, "List names" in the Mods tab writes
    /// Assets/names.txt, and "Export textures" saves the current scene's textures as PNGs to edit.
    /// <para>Cost: nothing while no replacement exists. With replacements, the loaded textures are looked through once
    /// after each scene load and when a file changes (never every frame), and each texture is tried once per file version.</para>
    /// </summary>
    public static class AssetReplacer
    {
        private sealed class TextureRepl
        {
            public string Name, Source;
            public Png.Image Image;
            public int Version;
            // This pass's outcome, reported once per name.
            public int Done, Failed;
            public string How = "", Why = "";
        }

        private sealed class AudioRepl
        {
            public string Name, Source;
            public AudioClip Clip;
            public int Version;
        }

        public static string Folder => Path.Combine(MelonEnvironment.UserDataDirectory, "OUAKModAPI", "Assets");

        /// <summary>Textures replaced since the game started.</summary>
        public static int TexturesReplaced { get; private set; }
        /// <summary>Times a sound was swapped in an AudioSource.</summary>
        public static int SoundsSwapped { get; private set; }
        public static int TextureFiles => _textures.Count;
        public static int AudioFiles => _audio.Count;
        /// <summary>What the last reload or scan did.</summary>
        public static string LastReport { get; private set; } = "";

        /// <summary>Watch the folders and swap changed files in right away.</summary>
        public static bool LiveReload
        {
            get => _livePref?.Value ?? true;
            set { if (_livePref != null) { _livePref.Value = value; MelonPreferences.Save(); } }
        }

        private static readonly Dictionary<string, TextureRepl> _textures = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, AudioRepl> _audio = new(StringComparer.OrdinalIgnoreCase);
        // Each texture is tried once per file version (instance id -> version), done or not: never in a loop.
        private static readonly Dictionary<int, int> _tried = new();
        // Textures the API made itself are never targets.
        private static readonly HashSet<int> _ours = new();
        private static readonly Dictionary<int, Sprite> _spriteSwaps = new();
        private static readonly Dictionary<string, DateTime> _stamps = new(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> _folders = new();
        private static readonly List<FileSystemWatcher> _watchers = new();
        private static MelonPreferences_Entry<bool> _livePref;
        private static volatile bool _dirty;
        private static float _dirtyAt, _nextAudio, _scanAt = -1f, _scanAgainAt = -1f;
        private static int _version;

        internal static void Init(MelonPreferences_Category cat)
        {
            _livePref = cat.GetEntry<bool>("AssetsLiveReload") ?? cat.CreateEntry("AssetsLiveReload", true, "Live asset reload",
                "Swap replacement textures/sounds in as soon as their files change.");
            Directory.CreateDirectory(Path.Combine(Folder, "Textures"));
            Directory.CreateDirectory(Path.Combine(Folder, "Audio"));
            AddFolder(Folder);
        }

        /// <summary>Adds a folder with Textures/ and Audio/ inside (a mod's own assets). Files load right away.</summary>
        public static void AddFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || _folders.Contains(folder, StringComparer.OrdinalIgnoreCase)) return;
            _folders.Add(folder);
            foreach (string sub in new[] { "Textures", "Audio" })
            {
                try
                {
                    string dir = Path.Combine(folder, sub);
                    Directory.CreateDirectory(dir);
                    // Only the replacement folders count (not Export, where the API writes textures for you to edit).
                    var w = new FileSystemWatcher(dir) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
                    FileSystemEventHandler changed = (object sender, FileSystemEventArgs args) => { _dirty = true; };
                    w.Changed += changed;
                    w.Created += changed;
                    w.Deleted += changed;
                    w.Renamed += (object sender, RenamedEventArgs args) => { _dirty = true; };
                    w.EnableRaisingEvents = true;
                    _watchers.Add(w);
                }
                catch (Exception e) { ApiCore.Log.Warn($"Can't watch {folder}\\{sub} for changes: {e.Message}"); }
            }
            ReloadFiles();
        }

        // ---------------------------------------------------------------- from code

        /// <summary>Replaces textures called <paramref name="textureName"/> with a PNG file.</summary>
        public static void ReplaceTexture(string textureName, string pngPath)
        {
            var img = Png.Decode(File.ReadAllBytes(pngPath));
            _textures[textureName] = new TextureRepl { Name = textureName, Source = pngPath, Image = img, Version = ++_version };
            ScanSoon();
        }

        /// <summary>Replaces textures called <paramref name="textureName"/> with your texture's pixels.</summary>
        public static void ReplaceTexture(string textureName, Texture2D replacement)
        {
            if (replacement == null) return;
            var px = FrameCapture.ReadTexture(replacement, out int w, out int h);
            var rgba = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    // ReadTexture gives rows from the bottom; Png.Image keeps rows from the top.
                    var c = px[(h - 1 - y) * w + x];
                    int o = (y * w + x) * 4;
                    rgba[o] = c.r; rgba[o + 1] = c.g; rgba[o + 2] = c.b; rgba[o + 3] = c.a;
                }
            _textures[textureName] = new TextureRepl { Name = textureName, Source = "(code)", Image = new Png.Image { Width = w, Height = h, Rgba = rgba }, Version = ++_version };
            ScanSoon();
        }

        /// <summary>Plays <paramref name="replacement"/> wherever the game plays a clip called <paramref name="clipName"/>.</summary>
        public static void ReplaceAudio(string clipName, AudioClip replacement)
        {
            if (replacement == null) return;
            replacement.hideFlags = HideFlags.HideAndDontSave;
            _audio[clipName] = new AudioRepl { Name = clipName, Source = "(code)", Clip = replacement, Version = ++_version };
        }

        /// <summary>Plays a WAV file wherever the game plays a clip called <paramref name="clipName"/>.</summary>
        public static void ReplaceAudio(string clipName, string wavPath)
        {
            _audio[clipName] = new AudioRepl { Name = clipName, Source = wavPath, Clip = Wav.Load(wavPath, clipName + " (replacement)"), Version = ++_version };
        }

        public static void RemoveTexture(string textureName) => _textures.Remove(textureName);
        public static void RemoveAudio(string clipName) => _audio.Remove(clipName);

        // ---------------------------------------------------------------- files

        /// <summary>Reads changed replacement files again and applies them.</summary>
        public static string ReloadFiles()
        {
            int loaded = 0, bad = 0;
            var seenT = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenA = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string folder in _folders)
            {
                foreach (string f in Files(Path.Combine(folder, "Textures"), "*.png"))
                {
                    string name = Path.GetFileNameWithoutExtension(f);
                    seenT.Add(name);
                    var stamp = File.GetLastWriteTimeUtc(f);
                    if (_textures.TryGetValue(name, out var have) && have.Source == f && _stamps.TryGetValue(f, out var st) && st == stamp) continue;
                    try
                    {
                        _textures[name] = new TextureRepl { Name = name, Source = f, Image = Png.Decode(File.ReadAllBytes(f)), Version = ++_version };
                        _stamps[f] = stamp;
                        loaded++;
                    }
                    catch (Exception e) { bad++; ApiCore.Log.Warn($"Texture file {Path.GetFileName(f)}: {e.Message}"); }
                }
                foreach (string f in Files(Path.Combine(folder, "Audio"), "*.wav"))
                {
                    string name = Path.GetFileNameWithoutExtension(f);
                    seenA.Add(name);
                    var stamp = File.GetLastWriteTimeUtc(f);
                    if (_audio.TryGetValue(name, out var have) && have.Source == f && _stamps.TryGetValue(f, out var st) && st == stamp) continue;
                    try
                    {
                        _audio[name] = new AudioRepl { Name = name, Source = f, Clip = Wav.Load(f, name + " (replacement)"), Version = ++_version };
                        _stamps[f] = stamp;
                        loaded++;
                    }
                    catch (Exception e) { bad++; ApiCore.Log.Warn($"Sound file {Path.GetFileName(f)}: {e.Message}"); }
                }
            }
            // Files that were deleted stop replacing (what's already changed stays until the next scene load).
            foreach (var k in _textures.Where(kv => kv.Value.Source != "(code)" && !seenT.Contains(kv.Key)).Select(kv => kv.Key).ToList()) _textures.Remove(k);
            foreach (var k in _audio.Where(kv => kv.Value.Source != "(code)" && !seenA.Contains(kv.Key)).Select(kv => kv.Key).ToList()) _audio.Remove(k);
            LastReport = $"{_textures.Count} texture(s) and {_audio.Count} sound(s) ready ({loaded} (re)loaded{(bad > 0 ? $", {bad} unreadable" : "")}).";
            if (loaded + bad > 0)
            {
                ApiCore.Log.Info("Assets: " + LastReport);
                ScanSoon();
            }
            return LastReport;
        }

        private static IEnumerable<string> Files(string dir, string pattern)
        {
            try { return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern, SearchOption.AllDirectories) : Array.Empty<string>(); }
            catch { return Array.Empty<string>(); }
        }

        // ---------------------------------------------------------------- when to look

        private static void ScanSoon() => _scanAt = 0f;

        internal static void OnSceneLoaded(string scene)
        {
            if (_textures.Count == 0) return;
            // Objects keep streaming in after a scene loads (Addressables): look once soon and once a little later.
            float now = Time.unscaledTime;
            _scanAt = now + 0.5f;
            _scanAgainAt = now + 4f;
        }

        internal static void Tick()
        {
            float now = Time.unscaledTime;
            if (_dirty && LiveReload)
            {
                if (_dirtyAt <= 0f) _dirtyAt = now + 0.4f;   // let the editor finish writing
                else if (now >= _dirtyAt)
                {
                    _dirty = false;
                    _dirtyAt = 0f;
                    ReloadFiles();
                }
            }
            if (_textures.Count > 0)
            {
                if (_scanAt >= 0f && now >= _scanAt) { _scanAt = -1f; ApplyTextures(); }
                if (_scanAgainAt >= 0f && now >= _scanAgainAt) { _scanAgainAt = -1f; ApplyTextures(); }
            }
            if (_audio.Count > 0 && now >= _nextAudio)
            {
                _nextAudio = now + 0.5f;
                SwapAudio();
            }
        }

        // ---------------------------------------------------------------- textures

        /// <summary>Applies every texture replacement to the textures loaded now. Returns how many were replaced.</summary>
        public static int ApplyTextures()
        {
            if (_textures.Count == 0) return 0;
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Texture2D> all;
            try { all = Resources.FindObjectsOfTypeAll<Texture2D>(); }
            catch (Exception e) { ApiCore.Log.Warn($"Texture scan failed: {e.Message}"); return 0; }
            foreach (var r in _textures.Values) { r.Done = r.Failed = 0; r.How = r.Why = ""; }
            int replaced = 0;
            for (int i = 0; i < all.Length; i++)
            {
                var tex = all[i];
                if (tex == null) continue;
                int id;
                string name;
                try { id = tex.GetInstanceID(); name = tex.name; } catch { continue; }
                if (string.IsNullOrEmpty(name) || _ours.Contains(id) || !_textures.TryGetValue(name, out var r)) continue;
                if (_tried.TryGetValue(id, out int v) && v == r.Version) continue;
                _tried[id] = r.Version;
                string how = null, why = null;
                try { how = Replace(tex, r, out why); }
                catch (Exception e) { why = e.Message; }
                if (how != null) { r.Done++; r.How = how; replaced++; }
                else { r.Failed++; r.Why = why ?? "unknown"; }
            }
            // One line per texture name and pass, however many copies there were.
            foreach (var r in _textures.Values)
            {
                if (r.Done > 0) ApiCore.Log.Info($"Assets: replaced {r.Name} ({r.Done} cop{(r.Done == 1 ? "y" : "ies")}, {r.How}).");
                if (r.Failed > 0) ApiCore.Log.Warn($"Assets: couldn't replace {r.Failed} cop{(r.Failed == 1 ? "y" : "ies")} of {r.Name}: {r.Why}");
            }
            if (replaced > 0)
            {
                TexturesReplaced += replaced;
                LastReport = $"Replaced {replaced} texture(s) in this scene.";
            }
            return replaced;
        }

        /// <summary>One texture: changed in place, copied over on the graphics card, or (last) swapped where it's used. Null when none worked.</summary>
        private static string Replace(Texture2D tex, TextureRepl r, out string why)
        {
            why = null;
            int w = tex.width, h = tex.height, mips = Math.Max(1, tex.mipmapCount);
            var img = r.Image;
            // The original's size, so every user of it (UI sprite rects, GPU copies) still lines up.
            byte[] rgba = img.Width == w && img.Height == h ? img.RgbaBottomUp() : Resized(img, w, h);

            // 1) A readable texture: rebuilt in place as RGBA32, with every mip level.
            if (tex.isReadable)
            {
                try
                {
                    if (tex.Reinitialize(w, h, TextureFormat.RGBA32, mips > 1))
                    {
                        tex.LoadRawTextureData(MipChain(rgba, w, h, Math.Max(1, tex.mipmapCount), 4));
                        tex.Apply(false, false);
                        return "in place";
                    }
                    why = "the game refused to rebuild it";
                }
                catch (Exception e) { why = e.Message; }
            }

            // 2) The game's own textures (not readable, usually compressed): copied over on the graphics card.
            string copy = GpuCopy(tex, rgba, w, h, mips);
            if (copy == null) return "copied on the graphics card";
            why = copy;

            // 3) A format the API can't produce: what uses it gets a copy of ours.
            int swapped = SwapReferences(tex, rgba, w, h, mips);
            return swapped > 0 ? $"swapped in {swapped} place(s)" : null;
        }

        /// <summary>Copies our pixels over the texture on the graphics card. Null when done, else why not.</summary>
        private static string GpuCopy(Texture2D dst, byte[] rgba, int w, int h, int mips)
        {
            var fmt = dst.format;
            TextureFormat make;
            bool compress;
            switch (fmt)
            {
                case TextureFormat.RGBA32: make = TextureFormat.RGBA32; compress = false; break;
                case TextureFormat.DXT5: make = TextureFormat.RGBA32; compress = true; break;
                case TextureFormat.DXT1: make = TextureFormat.RGB24; compress = true; break;
                default: return $"its format ({fmt}) can't be copied over";
            }
            if (compress && (w % 4 != 0 || h % 4 != 0)) return $"its size ({w}x{h}) isn't a multiple of 4";
            Texture2D tmp = null;
            try
            {
                tmp = new Texture2D(w, h, make, mips > 1) { hideFlags = HideFlags.HideAndDontSave };
                int bpp = make == TextureFormat.RGB24 ? 3 : 4;
                byte[] data = bpp == 4 ? rgba : DropAlpha(rgba);
                tmp.LoadRawTextureData(MipChain(data, w, h, Math.Max(1, tmp.mipmapCount), bpp));
                tmp.Apply(false, false);
                if (compress)
                {
                    tmp.Compress(true);
                    tmp.Apply(false, false);
                }
                if (tmp.format != fmt) return $"its format ({fmt}) didn't match after compressing ({tmp.format})";
                int levels = Math.Min(Math.Max(1, tmp.mipmapCount), mips);
                for (int m = 0; m < levels; m++) Graphics.CopyTexture(tmp, 0, m, dst, 0, m);
                return null;
            }
            catch (Exception e) { return e.Message; }
            finally { if (tmp != null) UnityEngine.Object.Destroy(tmp); }
        }

        /// <summary>Last resort: materials, UI images and sprite renderers that use the texture get a copy of ours.</summary>
        private static int SwapReferences(Texture2D orig, byte[] rgba, int w, int h, int mips)
        {
            int origId = orig.GetInstanceID();
            var ours = new Texture2D(w, h, TextureFormat.RGBA32, mips > 1) { name = orig.name + " (OUAK replacement)", hideFlags = HideFlags.HideAndDontSave };
            _ours.Add(ours.GetInstanceID());
            ours.LoadRawTextureData(MipChain(rgba, w, h, Math.Max(1, ours.mipmapCount), 4));
            ours.Apply(false, false);
            int n = 0;
            try
            {
                var mats = Resources.FindObjectsOfTypeAll<Material>();
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null) continue;
                    var ids = m.GetTexturePropertyNameIDs();
                    for (int j = 0; j < ids.Length; j++)
                    {
                        var cur = m.GetTexture(ids[j]);
                        if (cur != null && cur.GetInstanceID() == origId) { m.SetTexture(ids[j], ours); n++; }
                    }
                }
            }
            catch (Exception e) { ApiCore.Log.Warn($"Material swap: {e.Message}"); }
            try
            {
                var images = Resources.FindObjectsOfTypeAll<UnityEngine.UI.Image>();
                for (int i = 0; i < images.Length; i++)
                {
                    var img = images[i];
                    var sp = img != null ? img.sprite : null;
                    if (sp == null || sp.texture == null || sp.texture.GetInstanceID() != origId) continue;
                    img.sprite = SpriteFor(sp, ours);
                    n++;
                }
                var raws = Resources.FindObjectsOfTypeAll<UnityEngine.UI.RawImage>();
                for (int i = 0; i < raws.Length; i++)
                {
                    var raw = raws[i];
                    if (raw == null || raw.texture == null || raw.texture.GetInstanceID() != origId) continue;
                    raw.texture = ours;
                    n++;
                }
                var renderers = Resources.FindObjectsOfTypeAll<SpriteRenderer>();
                for (int i = 0; i < renderers.Length; i++)
                {
                    var sr = renderers[i];
                    var sp = sr != null ? sr.sprite : null;
                    if (sp == null || sp.texture == null || sp.texture.GetInstanceID() != origId) continue;
                    sr.sprite = SpriteFor(sp, ours);
                    n++;
                }
            }
            catch (Exception e) { ApiCore.Log.Warn($"Sprite swap: {e.Message}"); }
            if (n == 0)
            {
                _ours.Remove(ours.GetInstanceID());
                UnityEngine.Object.Destroy(ours);
            }
            return n;
        }

        private static Sprite SpriteFor(Sprite original, Texture2D tex)
        {
            int id = original.GetInstanceID();
            if (_spriteSwaps.TryGetValue(id, out var have) && have != null) return have;
            var rect = original.rect;
            var pivot = new Vector2(rect.width > 0f ? original.pivot.x / rect.width : 0.5f, rect.height > 0f ? original.pivot.y / rect.height : 0.5f);
            var made = Sprite.Create(tex, rect, pivot, original.pixelsPerUnit, 0, SpriteMeshType.FullRect, original.border);
            made.name = original.name;
            made.hideFlags = HideFlags.HideAndDontSave;
            _spriteSwaps[id] = made;
            return made;
        }

        /// <summary>Every mip level after the first, each half the size (2x2 averaged), appended in order.</summary>
        private static byte[] MipChain(byte[] level0, int w, int h, int levels, int bpp)
        {
            if (levels <= 1) return level0;
            var parts = new List<byte[]> { level0 };
            int total = level0.Length, cw = w, ch = h;
            byte[] cur = level0;
            for (int l = 1; l < levels; l++)
            {
                int nw = Math.Max(1, cw / 2), nh = Math.Max(1, ch / 2);
                var next = new byte[nw * nh * bpp];
                for (int y = 0; y < nh; y++)
                {
                    int y0 = Math.Min(y * 2, ch - 1), y1 = Math.Min(y * 2 + 1, ch - 1);
                    for (int x = 0; x < nw; x++)
                    {
                        int x0 = Math.Min(x * 2, cw - 1), x1 = Math.Min(x * 2 + 1, cw - 1);
                        for (int c = 0; c < bpp; c++)
                        {
                            int sum = cur[(y0 * cw + x0) * bpp + c] + cur[(y0 * cw + x1) * bpp + c] + cur[(y1 * cw + x0) * bpp + c] + cur[(y1 * cw + x1) * bpp + c];
                            next[(y * nw + x) * bpp + c] = (byte)((sum + 2) >> 2);
                        }
                    }
                }
                parts.Add(next);
                total += next.Length;
                cur = next;
                cw = nw;
                ch = nh;
            }
            var all = new byte[total];
            int o = 0;
            foreach (var p in parts) { Buffer.BlockCopy(p, 0, all, o, p.Length); o += p.Length; }
            return all;
        }

        /// <summary>The image scaled to w x h (bilinear), rows from the bottom.</summary>
        private static byte[] Resized(Png.Image img, int w, int h)
        {
            var o = new byte[w * h * 4];
            int sw = img.Width, sh = img.Height;
            for (int y = 0; y < h; y++)
            {
                // Output rows go bottom-up; the image's rows are top-down.
                float fy = Mathf.Clamp((h - 1 - y + 0.5f) * sh / h - 0.5f, 0f, sh - 1);
                int y0 = (int)fy, y1 = Math.Min(y0 + 1, sh - 1);
                float ty = fy - y0;
                for (int x = 0; x < w; x++)
                {
                    float fx = Mathf.Clamp((x + 0.5f) * sw / w - 0.5f, 0f, sw - 1);
                    int x0 = (int)fx, x1 = Math.Min(x0 + 1, sw - 1);
                    float tx = fx - x0;
                    for (int c = 0; c < 4; c++)
                    {
                        float a = img.Rgba[(y0 * sw + x0) * 4 + c], b = img.Rgba[(y0 * sw + x1) * 4 + c];
                        float d = img.Rgba[(y1 * sw + x0) * 4 + c], e = img.Rgba[(y1 * sw + x1) * 4 + c];
                        float top = a + (b - a) * tx, bottom = d + (e - d) * tx;
                        o[(y * w + x) * 4 + c] = (byte)Mathf.Clamp(Mathf.RoundToInt(top + (bottom - top) * ty), 0, 255);
                    }
                }
            }
            return o;
        }

        private static byte[] DropAlpha(byte[] rgba)
        {
            var rgb = new byte[rgba.Length / 4 * 3];
            for (int i = 0, j = 0; i < rgba.Length; i += 4, j += 3)
            {
                rgb[j] = rgba[i];
                rgb[j + 1] = rgba[i + 1];
                rgb[j + 2] = rgba[i + 2];
            }
            return rgb;
        }

        // ---------------------------------------------------------------- sounds

        private static void SwapAudio()
        {
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<AudioSource> sources;
            try { sources = UnityEngine.Object.FindObjectsOfType<AudioSource>(); }
            catch { return; }
            for (int i = 0; i < sources.Length; i++)
            {
                var src = sources[i];
                if (src == null) continue;
                var clip = src.clip;
                if (clip == null) continue;
                string name;
                try { name = clip.name; } catch { continue; }
                if (string.IsNullOrEmpty(name) || !_audio.TryGetValue(name, out var r) || r.Clip == null) continue;
                if (clip.GetInstanceID() == r.Clip.GetInstanceID()) continue;
                bool playing = src.isPlaying;
                float t = src.time;
                src.clip = r.Clip;
                if (playing)
                {
                    src.Play();
                    try { src.time = Mathf.Clamp(t, 0f, Mathf.Max(0f, r.Clip.length - 0.05f)); } catch { }
                }
                SoundsSwapped++;
            }
        }

        // ---------------------------------------------------------------- tools for modders

        /// <summary>Writes Assets/names.txt: every loaded texture and sound clip name (to name replacement files).</summary>
        public static string DumpNames()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Scene: {UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}   ({DateTime.Now:yyyy-MM-dd HH:mm:ss})");
            sb.AppendLine("Name a replacement file exactly like the asset: Textures/<name>.png, Audio/<name>.wav");
            sb.AppendLine();
            var texs = Resources.FindObjectsOfTypeAll<Texture2D>();
            var names = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < texs.Length; i++)
            {
                var t = texs[i];
                if (t == null || _ours.Contains(t.GetInstanceID()) || string.IsNullOrEmpty(t.name) || names.ContainsKey(t.name)) continue;
                names[t.name] = $"{t.width}x{t.height} {t.format}";
            }
            sb.AppendLine($"== Textures ({names.Count})");
            foreach (var kv in names) sb.AppendLine($"{kv.Key}    {kv.Value}");
            var clips = Resources.FindObjectsOfTypeAll<AudioClip>();
            var cn = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < clips.Length; i++)
            {
                var c = clips[i];
                if (c == null || string.IsNullOrEmpty(c.name) || cn.ContainsKey(c.name)) continue;
                cn[c.name] = $"{c.length:0.0}s {c.channels}ch {c.frequency}Hz";
            }
            sb.AppendLine();
            sb.AppendLine($"== Unity sounds ({cn.Count})");
            foreach (var kv in cn) sb.AppendLine($"{kv.Key}    {kv.Value}");

            var meshes = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in Resources.FindObjectsOfTypeAll<Mesh>())
            {
                try
                {
                    if (m == null || string.IsNullOrEmpty(m.name) || meshes.ContainsKey(m.name)) continue;
                    meshes[m.name] = $"{m.vertexCount} vertices{(m.isReadable ? "" : ", not readable")}";
                }
                catch { }
            }
            sb.AppendLine();
            sb.AppendLine($"== Models / meshes ({meshes.Count})");
            foreach (var kv in meshes) sb.AppendLine($"{kv.Key}    {kv.Value}");

            var texts = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in Resources.FindObjectsOfTypeAll<TextAsset>())
            {
                try { if (t != null && !string.IsNullOrEmpty(t.name) && !texts.ContainsKey(t.name)) texts[t.name] = $"{t.bytes?.Length ?? 0} bytes"; }
                catch { }
            }
            sb.AppendLine();
            sb.AppendLine($"== Text files ({texts.Count})");
            foreach (var kv in texts) sb.AppendLine($"{kv.Key}    {kv.Value}");

            var banks = SoundBanks();
            sb.AppendLine();
            sb.AppendLine($"== Game sound banks (CRI ADX2, StreamingAssets/Sound: {banks.Count}) - the game's music and sounds are here, not Unity sounds");
            foreach (var f in banks) sb.AppendLine($"{Path.GetFileName(f)}    {new FileInfo(f).Length / 1024} KB");

            Directory.CreateDirectory(Folder);
            string path = Path.Combine(Folder, "names.txt");
            File.WriteAllText(path, sb.ToString());
            return $"Wrote {names.Count} texture, {cn.Count} Unity sound, {meshes.Count} model, {texts.Count} text and {banks.Count} sound bank names to {path}";
        }

        /// <summary>The game's sound banks (CRI ADX2 .acb / .awb) in StreamingAssets/Sound.</summary>
        public static List<string> SoundBanks()
        {
            try
            {
                string dir = Path.Combine(Application.streamingAssetsPath, "Sound");
                return Directory.Exists(dir)
                    ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Where(f => f.EndsWith(".acb", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".awb", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f).ToList()
                    : new List<string>();
            }
            catch { return new List<string>(); }
        }

        private static string SafeName(string n) => string.Concat(n.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

        /// <summary>
        /// Saves the loaded models (meshes) whose names contain <paramref name="filter"/> as OBJ files in Assets/Export/Models
        /// (positions, normals, UVs, one group per submesh). Meshes the game marked not readable can't be read back and are counted.
        /// </summary>
        public static string ExportModels(string filter = "")
        {
            string dir = Path.Combine(Folder, "Export", "Models");
            Directory.CreateDirectory(dir);
            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int n = 0, unreadable = 0, failed = 0;
            foreach (var m in Resources.FindObjectsOfTypeAll<Mesh>())
            {
                try
                {
                    if (m == null || string.IsNullOrEmpty(m.name) || !done.Add(m.name)) continue;
                    if (!string.IsNullOrEmpty(filter) && m.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (!m.isReadable) { unreadable++; continue; }
                    var v = m.vertices; var nr = m.normals; var uv = m.uv;
                    var sb = new StringBuilder();
                    sb.AppendLine($"# {m.name} from Once Upon a KATAMARI (exported by the OUAK Modding API)");
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    foreach (var p in v) sb.AppendLine(string.Format(ci, "v {0} {1} {2}", -p.x, p.y, p.z));
                    foreach (var p in nr) sb.AppendLine(string.Format(ci, "vn {0} {1} {2}", -p.x, p.y, p.z));
                    foreach (var p in uv) sb.AppendLine(string.Format(ci, "vt {0} {1}", p.x, p.y));
                    bool hasN = nr.Length == v.Length, hasT = uv.Length == v.Length;
                    for (int sub = 0; sub < m.subMeshCount; sub++)
                    {
                        sb.AppendLine($"g {m.name}_{sub}");
                        var tri = m.GetTriangles(sub);
                        for (int i = 0; i + 2 < tri.Length; i += 3)
                        {
                            // Unity is left-handed: x mirrored above, so the winding turns too.
                            int a = tri[i] + 1, b = tri[i + 2] + 1, c = tri[i + 1] + 1;
                            string F(int k) => hasT && hasN ? $"{k}/{k}/{k}" : hasT ? $"{k}/{k}" : hasN ? $"{k}//{k}" : $"{k}";
                            sb.AppendLine($"f {F(a)} {F(b)} {F(c)}");
                        }
                    }
                    File.WriteAllText(Path.Combine(dir, SafeName(m.name) + ".obj"), sb.ToString());
                    n++;
                }
                catch { failed++; }
            }
            return $"Exported {n} model(s) to {dir}" + (unreadable > 0 ? $"; {unreadable} the game marked not readable" : "") + (failed > 0 ? $"; {failed} failed" : "") + ".";
        }

        /// <summary>Saves the loaded text files (TextAssets) as .txt (or .bytes when they aren't text) in Assets/Export/Text.</summary>
        public static string ExportTexts(string filter = "")
        {
            string dir = Path.Combine(Folder, "Export", "Text");
            Directory.CreateDirectory(dir);
            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int n = 0, failed = 0;
            foreach (var t in Resources.FindObjectsOfTypeAll<TextAsset>())
            {
                try
                {
                    if (t == null || string.IsNullOrEmpty(t.name) || !done.Add(t.name)) continue;
                    if (!string.IsNullOrEmpty(filter) && t.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    byte[] data = t.bytes;
                    bool text = data != null && !data.Take(512).Any(b => b == 0);
                    File.WriteAllBytes(Path.Combine(dir, SafeName(t.name) + (text ? ".txt" : ".bytes")), data ?? Array.Empty<byte>());
                    n++;
                }
                catch { failed++; }
            }
            return $"Exported {n} text file(s) to {dir}{(failed > 0 ? $" ({failed} failed)" : "")}.";
        }

        /// <summary>
        /// Sounds: the loaded Unity sounds as WAV files, and copies of the game's CRI sound banks (.acb / .awb, where its
        /// music and sound effects are) for CRI tools such as vgmstream, all in Assets/Export/Sound.
        /// </summary>
        public static string ExportSounds(string filter = "")
        {
            string dir = Path.Combine(Folder, "Export", "Sound");
            Directory.CreateDirectory(dir);
            int clips = 0, banks = 0, failed = 0;
            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in Resources.FindObjectsOfTypeAll<AudioClip>())
            {
                try
                {
                    if (c == null || string.IsNullOrEmpty(c.name) || !done.Add(c.name) || c.samples <= 0) continue;
                    if (!string.IsNullOrEmpty(filter) && c.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var data = new Il2CppStructArray<float>(c.samples * c.channels);
                    if (!c.GetData(data, 0)) { failed++; continue; }
                    File.WriteAllBytes(Path.Combine(dir, SafeName(c.name) + ".wav"), Wav.Encode(data.ToArray(), c.channels, c.frequency));
                    clips++;
                }
                catch { failed++; }
            }
            foreach (var f in SoundBanks())
            {
                try
                {
                    if (!string.IsNullOrEmpty(filter) && Path.GetFileName(f).IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), true);
                    banks++;
                }
                catch { failed++; }
            }
            return $"Exported {clips} Unity sound(s) as WAV and copied {banks} game sound bank(s) to {dir}{(failed > 0 ? $" ({failed} failed)" : "")}.";
        }

        /// <summary>Saves the loaded textures whose names contain <paramref name="filter"/> (all when empty, up to <paramref name="max"/>) as PNGs in Assets/Export.</summary>
        public static string ExportTextures(string filter = "", int max = int.MaxValue)
        {
            string dir = Path.Combine(Folder, "Export");
            Directory.CreateDirectory(dir);
            var texs = Resources.FindObjectsOfTypeAll<Texture2D>();
            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int n = 0, failed = 0;
            for (int i = 0; i < texs.Length && n < max; i++)
            {
                var t = texs[i];
                if (t == null || _ours.Contains(t.GetInstanceID()) || string.IsNullOrEmpty(t.name) || t.width < 4 || t.height < 4 || t.width > 8192 || t.height > 8192) continue;
                if (!string.IsNullOrEmpty(filter) && t.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!done.Add(t.name)) continue;
                string safe = string.Concat(t.name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                string msg = FrameCapture.SaveTexture(t, safe, dir, keepAlpha: true);
                if (msg.EndsWith("saved")) n++; else failed++;
            }
            return $"Exported {n} texture(s) to {dir}{(failed > 0 ? $" ({failed} failed)" : "")}. Copy one to Assets/Textures (same name) and edit it to replace it.";
        }
    }

    /// <summary>WAV files to AudioClips (PCM 8/16/24/32-bit and 32-bit float, any channel count).</summary>
    public static class Wav
    {
        public static AudioClip Load(string path, string clipName = null)
        {
            var samples = Decode(File.ReadAllBytes(path), out int channels, out int rate);
            int frames = samples.Length / channels;
            var clip = AudioClip.Create(clipName ?? Path.GetFileNameWithoutExtension(path), frames, channels, rate, false);
            clip.hideFlags = HideFlags.HideAndDontSave;
            clip.SetData(samples, 0);
            return clip;
        }

        /// <summary>Interleaved samples in -1..1.</summary>
        /// <summary>16-bit PCM WAV bytes from float samples (interleaved, -1..1).</summary>
        public static byte[] Encode(float[] samples, int channels, int rate)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            int bytes = samples.Length * 2;
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + bytes); w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            w.Write(System.Text.Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)channels);
            w.Write(rate); w.Write(rate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16);
            w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(bytes);
            foreach (var f in samples) w.Write((short)Math.Round(Math.Clamp(f, -1f, 1f) * 32767f));
            w.Flush();
            return ms.ToArray();
        }

        public static float[] Decode(byte[] d, out int channels, out int rate)
        {
            if (d.Length < 44 || Encoding.ASCII.GetString(d, 0, 4) != "RIFF" || Encoding.ASCII.GetString(d, 8, 4) != "WAVE")
                throw new InvalidDataException("not a WAV file");
            int pos = 12, format = 0, bits = 0, dataAt = -1, dataLen = 0;
            channels = 0; rate = 0;
            while (pos + 8 <= d.Length)
            {
                string id = Encoding.ASCII.GetString(d, pos, 4);
                int len = BitConverter.ToInt32(d, pos + 4);
                int at = pos + 8;
                if (id == "fmt ")
                {
                    format = BitConverter.ToUInt16(d, at);
                    channels = BitConverter.ToUInt16(d, at + 2);
                    rate = BitConverter.ToInt32(d, at + 4);
                    bits = BitConverter.ToUInt16(d, at + 14);
                    if (format == 0xFFFE && len >= 26) format = BitConverter.ToUInt16(d, at + 24);
                }
                else if (id == "data")
                {
                    dataAt = at;
                    dataLen = Math.Min(len, d.Length - at);
                    break;
                }
                pos = at + len + (len & 1);
            }
            if (dataAt < 0 || channels <= 0 || rate <= 0) throw new InvalidDataException("WAV without format or data");
            int bytes = bits / 8;
            if (bytes <= 0) throw new InvalidDataException($"{bits}-bit WAV isn't supported");
            int n = dataLen / bytes;
            var o = new float[n - n % channels];
            for (int i = 0; i < o.Length; i++)
            {
                int p = dataAt + i * bytes;
                o[i] = (format, bits) switch
                {
                    (3, 32) => BitConverter.ToSingle(d, p),
                    (1, 8) => (d[p] - 128) / 128f,
                    (1, 16) => BitConverter.ToInt16(d, p) / 32768f,
                    (1, 24) => ((d[p] | (d[p + 1] << 8) | ((sbyte)d[p + 2] << 16))) / 8388608f,
                    (1, 32) => BitConverter.ToInt32(d, p) / 2147483648f,
                    _ => throw new InvalidDataException($"WAV format {format} with {bits} bits isn't supported"),
                };
            }
            return o;
        }
    }
}
