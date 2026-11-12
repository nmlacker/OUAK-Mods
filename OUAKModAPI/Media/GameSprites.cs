using System;
using System.Collections.Generic;
using UnityEngine;

namespace OUAKModAPI
{
    /// <summary>
    /// The game's own pictures (sprites and textures) by name, as textures GUI.DrawTexture can draw: freebie icons
    /// ("game_item01".."game_item28", numbered like eInstageItemType), rank badges, UI art. A sprite cut from an
    /// atlas is copied out once (the IMGUI call that draws part of a texture is stripped from this game).
    /// <para>Cheap: <see cref="Get"/> only returns what's cached. A name not found yet is looked for in Update at most
    /// every 3 s while someone keeps asking, and again after scene loads (the game loads its art as it needs it).</para>
    /// </summary>
    public static class GameSprites
    {
        private static readonly Dictionary<string, Texture> _found = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> _wanted = new(StringComparer.Ordinal);
        private static float _nextLook;

        /// <summary>The picture called <paramref name="name"/>, or null while the game hasn't loaded it (ask again next frame).</summary>
        public static Texture Get(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_found.TryGetValue(name, out var t))
            {
                if (t != null) return t;
                _found.Remove(name);   // destroyed with its scene: look again
            }
            _wanted[name] = Time.unscaledTime;
            return null;
        }

        /// <summary>
        /// The icon of a freebie, or null while it isn't loaded. The story HUD's icons (UIIngameItem.IconSprites) are
        /// game_item01..15 for the freebies and game_itemNN_sp for the Specials, NN being the normal version's number
        /// (Special Crown is game_item14_sp, Special Dice game_item15_sp). Sunglasses and Random have none.
        /// </summary>
        public static Texture Freebie(Il2CppApp.KatamariSin.eInstageItemType item)
        {
            int n = (int)item;
            if (n >= 1 && n <= 15) return Get($"game_item{n:00}");
            int baseItem = n switch { >= 16 and <= 24 => n - 15, 25 => 14, 26 => 15, _ => 0 };
            return baseItem > 0 ? Get($"game_item{baseItem:00}_sp") : null;
        }

        /// <summary>A sprite's own rectangle as a standalone texture (copied once with Graphics.Blit; call from Update, not OnGUI).</summary>
        public static Texture Of(Sprite s, int maxSize = 256)
        {
            if (s == null) return null;
            var tex = s.texture;
            if (tex == null || tex.width <= 0 || tex.height <= 0) return null;
            var rect = s.textureRect;
            if (rect.width < 1f || rect.height < 1f) return null;
            if (rect.width >= tex.width - 0.5f && rect.height >= tex.height - 0.5f) return tex;
            float k = Mathf.Min(1f, maxSize / Mathf.Max(rect.width, rect.height));
            var rt = new RenderTexture(Mathf.Max(1, Mathf.RoundToInt(rect.width * k)), Mathf.Max(1, Mathf.RoundToInt(rect.height * k)), 0)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            var prev = RenderTexture.active;
            try
            {
                Graphics.Blit(tex, rt, new Vector2(rect.width / tex.width, rect.height / tex.height), new Vector2(rect.x / tex.width, rect.y / tex.height));
            }
            finally { RenderTexture.active = prev; }
            return rt;
        }

        internal static void OnSceneLoaded() => _nextLook = 0f;

        /// <summary>Update: looks for the names asked for in the last 2 s (one pass over the loaded sprites serves them all).</summary>
        internal static void Tick()
        {
            if (_wanted.Count == 0) return;
            float now = Time.unscaledTime;
            if (now < _nextLook) return;
            _nextLook = now + 3f;
            var want = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kv in _wanted) if (now - kv.Value < 2f && !_found.ContainsKey(kv.Key)) want.Add(kv.Key);
            _wanted.Clear();
            if (want.Count == 0) return;
            var sprites = Resources.FindObjectsOfTypeAll<Sprite>();
            for (int i = 0; i < sprites.Length && want.Count > 0; i++)
            {
                var s = sprites[i];
                if (s == null) continue;
                string n;
                try { n = s.name; } catch { continue; }
                if (n == null || !want.Contains(n)) continue;
                var t = Of(s, 128);
                if (t == null) continue;
                _found[n] = t;
                want.Remove(n);
            }
            if (want.Count == 0) return;
            var textures = Resources.FindObjectsOfTypeAll<Texture2D>();
            for (int i = 0; i < textures.Length && want.Count > 0; i++)
            {
                var t = textures[i];
                if (t == null) continue;
                string n;
                try { n = t.name; } catch { continue; }
                if (n == null || !want.Remove(n)) continue;
                _found[n] = t;
            }
        }
    }
}
