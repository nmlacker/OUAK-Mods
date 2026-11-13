using MelonLoader;
using OUAKModAPI;

namespace OUAKMultiplayer
{
    /// <summary>
    /// How other players' katamaris (ghosts) appear on your screen. Only what you see: the player bar, the race and
    /// shared pickups are unaffected. Distances are measured from your katamari's surface to theirs, in your
    /// katamari's diameters, so the same setting works at 10 cm and at 100 m.
    /// </summary>
    internal static class GhostSettings
    {
        public static bool Shown = true, Cousins = true, Stuck = true, Tags = true, HideBlocking = true, Bubbles = true;
        /// <summary>Other players drawn see-through (the game's "ghost" look); off: solid, as in their own game (the user, 2026-11-12).</summary>
        public static bool SeeThrough = true;
        public static int Near = 1, Far, Max;

        /// <summary>Vanish when the gap to you is under this many of your diameters (-1 = never, 0 = overlapping).</summary>
        public static readonly float[] NearSteps = { -1f, 0f, 1f, 3f, 10f };
        public static readonly string[] NearNames = { "never", "when overlapping you", "within 1x your size", "within 3x your size", "within 10x your size" };
        /// <summary>Vanish when the gap is over this many of your diameters (0 = never).</summary>
        public static readonly float[] FarSteps = { 0f, 10f, 25f, 50f, 100f };
        public static readonly string[] FarNames = { "never", "beyond 10x your size", "beyond 25x your size", "beyond 50x your size", "beyond 100x your size" };
        /// <summary>At most this many ghosts, the nearest first (0 = all).</summary>
        public static readonly int[] MaxSteps = { 0, 3, 1 };
        public static readonly string[] MaxNames = { "all of them", "the 3 nearest", "the nearest one" };

        private static MelonPreferences_Entry<bool> _shown, _cousins, _stuck, _tags, _blocking, _bubbles, _seeThrough;
        private static MelonPreferences_Entry<int> _near, _far, _max;

        public static void Init(MelonPreferences_Category cat)
        {
            _shown = cat.CreateEntry("GhostsShown", true, "Show other players' katamaris", "Ghost katamaris, cousins and name tags on your screen.");
            _near = cat.CreateEntry("GhostsNear", 1, "Ghosts vanish when near", "0 never, 1 when overlapping you, 2/3/4 within 1x/3x/10x your katamari's size.");
            _far = cat.CreateEntry("GhostsFar", 0, "Ghosts vanish when far", "0 never, 1/2/3/4 beyond 10x/25x/50x/100x your katamari's size.");
            _max = cat.CreateEntry("GhostsMax", 0, "Ghosts shown at most", "0 all, 1 the 3 nearest, 2 the nearest one.");
            _blocking = cat.CreateEntry("GhostsHideBlocking", true, "Ghosts vanish while blocking your view", "A ghost between the camera and your katamari isn't drawn.");
            _cousins = cat.CreateEntry("GhostsCousins", true, "Show their cousins", "");
            _stuck = cat.CreateEntry("GhostsStuck", true, "Show what they've rolled up", "");
            _tags = cat.CreateEntry("GhostsTags", true, "Show name tags", "");
            _bubbles = cat.CreateEntry("GhostsBubbles", true, "Show chat bubbles", "What a player says in the chat shows over their cousin for a few seconds.");
            _seeThrough = cat.CreateEntry("GhostsSeeThrough", true, "Other players see-through", "Draw other players' katamaris, cousins and rolled-up objects see-through (off: solid).");
            Shown = _shown.Value;
            SeeThrough = _seeThrough.Value;
            Near = Clamp(_near.Value, NearSteps.Length);
            Far = Clamp(_far.Value, FarSteps.Length);
            Max = Clamp(_max.Value, MaxSteps.Length);
            HideBlocking = _blocking.Value;
            Cousins = _cousins.Value;
            Stuck = _stuck.Value;
            Tags = _tags.Value;
            Bubbles = _bubbles.Value;
        }

        public static void Save()
        {
            if (_shown == null) return;
            _shown.Value = Shown;
            _near.Value = Near;
            _far.Value = Far;
            _max.Value = Max;
            _blocking.Value = HideBlocking;
            _cousins.Value = Cousins;
            _stuck.Value = Stuck;
            _tags.Value = Tags;
            _bubbles.Value = Bubbles;
            _seeThrough.Value = SeeThrough;
            MelonPreferences.Save();
        }

        /// <summary>A value of the settings, to tell when one changed (the menu saves then).</summary>
        public static int Hash() =>
            (Shown ? 1 : 0) | (Cousins ? 2 : 0) | (Stuck ? 4 : 0) | (Tags ? 8 : 0) | (HideBlocking ? 16 : 0) | (Near << 5) | (Far << 9) | (Max << 13)
            | (Bubbles ? 1 << 16 : 0) | (SeeThrough ? 1 << 17 : 0);

        public static int Cycle(int value, int dir, int count) => ((value + dir) % count + count) % count;

        private static int Clamp(int v, int count) => v < 0 || v >= count ? 0 : v;
    }
}
