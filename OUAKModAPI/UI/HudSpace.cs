using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace OUAKModAPI
{
    /// <summary>
    /// Screen space that mods' HUD elements take up, so other mods can keep clear of them without depending on each other
    /// (e.g. the OUAK Dash Bar moves up above the OUAK Multiplayer Player Bar, however many rows it stacks).
    /// <code>HudSpace.Claim("OUAK Multiplayer/PlayerBar", rect);        // every frame it's drawn (OnGUI Repaint is fine)
    /// float up = HudSpace.BottomUsed(gaugeLeft, gaugeRight, "OUAK Dash Bar");   // px from the screen bottom to keep clear</code>
    /// Rects are screen pixels in IMGUI coordinates (top-left origin, y down). A claim lasts until <see cref="Lifetime"/>
    /// seconds of unscaled time, and at least two frames, after its last <see cref="Claim"/>, which covers a frame's lag
    /// between OnGUI and LateUpdate, paused frames and single long frames. Main thread only.
    /// </summary>
    public static class HudSpace
    {
        /// <summary>How long a claim stays after its last <see cref="Claim"/> (unscaled seconds).</summary>
        public const float Lifetime = 0.25f;

        private static readonly Dictionary<string, (Rect rect, float until, int frame)> _claims = new(StringComparer.Ordinal);

        /// <summary>Takes (or keeps) <paramref name="screenRect"/> for <paramref name="owner"/>; call it every frame the element is drawn.</summary>
        public static void Claim(string owner, Rect screenRect)
        {
            if (string.IsNullOrEmpty(owner)) return;
            _claims[owner] = (screenRect, Time.unscaledTime + Lifetime, Time.frameCount);
        }

        /// <summary>Gives <paramref name="owner"/>'s space back at once (otherwise it lapses by itself).</summary>
        public static void Release(string owner)
        {
            if (owner != null) _claims.Remove(owner);
        }

        /// <summary>The live claims.</summary>
        public static IReadOnlyList<(string owner, Rect rect)> Claims
        {
            get
            {
                Prune();
                return _claims.Select(kv => (kv.Key, kv.Value.rect)).ToList();
            }
        }

        /// <summary>
        /// The highest top edge, in pixels from the screen bottom, of the live claims that overlap the horizontal span
        /// <paramref name="xMin"/>..<paramref name="xMax"/> (screen pixels), leaving out <paramref name="exceptOwner"/>; 0 if none.
        /// </summary>
        public static float BottomUsed(float xMin, float xMax, string exceptOwner = null)
        {
            Prune();
            float used = 0f;
            foreach (var kv in _claims)
            {
                if (kv.Key == exceptOwner) continue;
                var r = kv.Value.rect;
                if (r.xMax <= xMin || r.xMin >= xMax) continue;
                used = Mathf.Max(used, Screen.height - r.yMin);
            }
            return used;
        }

        private static void Prune()
        {
            if (_claims.Count == 0) return;
            float now = Time.unscaledTime;
            int frame = Time.frameCount;
            // A claim also lives two frames past its last Claim, so one long frame (a frame capture, a load hitch) doesn't
            // make every claim lapse (found by the Dash Bar session, 2026-11-12).
            foreach (var k in _claims.Where(kv => kv.Value.until < now && frame - kv.Value.frame > 2).Select(kv => kv.Key).ToList()) _claims.Remove(k);
        }
    }
}
