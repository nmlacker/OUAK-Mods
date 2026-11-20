using System;
using System.Collections.Generic;
using LongLostCousins.Data;
using LongLostCousins.Dressing;
using OUAKModAPI;
using UnityEngine;

namespace LongLostCousins.Select
{
    /// <summary>
    /// A cousin's select-screen icon as a Sprite, one per style, built once from the PNG inside the DLL and kept for the
    /// session. It matches the game's own icons: 100 pixels per unit, pivot in the centre.
    /// </summary>
    internal static class CousinIcon
    {
        private static readonly ModLog Log = LongLostCousinsMod.Log.In("Icon");
        private static readonly Dictionary<string, Sprite> Cache = new(StringComparer.Ordinal);
        private static readonly HashSet<string> Failed = new(StringComparer.Ordinal);

        /// <summary>The sprite of <paramref name="c"/>'s icon in <paramref name="style"/>, or null (logged once) when there is none.</summary>
        public static Sprite Get(CousinEntry c, string style)
        {
            if (c == null || style == null) return null;
            string key = c.Id + "/" + style;
            if (Cache.TryGetValue(key, out var s) && s != null) return s;
            if (Failed.Contains(key)) return null;
            try
            {
                string path = c.IconPath(style) ?? throw new InvalidOperationException("cousins.json names no icon for this style");
                var bytes = Catalog.Source("Cousins/" + path) ?? throw new InvalidOperationException("not in the mod: Cousins/" + path);
                var img = Png.Decode(bytes);
                var tex = new Texture2D(img.Width, img.Height, TextureFormat.RGBA32, false, false);
                tex.name = "llc_icon_" + c.Id + "_" + style;
                tex.filterMode = FilterMode.Bilinear;
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.LoadRawTextureData(img.RgbaBottomUp());
                tex.Apply(false, true);
                tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
                Rect r = default;
                r.m_XMin = 0f; r.m_YMin = 0f; r.m_Width = img.Width; r.m_Height = img.Height;
                // FullRect: the texture is not kept readable, and a tight mesh would need to read it.
                s = Sprite.Create(tex, r, U.V2(0.5f, 0.5f), 100f, 0u, SpriteMeshType.FullRect);
                s.name = tex.name;
                s.hideFlags = HideFlags.DontUnloadUnusedAsset;
                Cache[key] = s;
                Log.Info($"{c.Name}'s icon ({style}) built: {img.Width}x{img.Height}");
                return s;
            }
            catch (Exception e)
            {
                Failed.Add(key);
                Log.Warn($"{c.Name}'s icon ({style}) can't be built; her slot keeps the game's placeholder: {e.Message}");
                return null;
            }
        }
    }
}
