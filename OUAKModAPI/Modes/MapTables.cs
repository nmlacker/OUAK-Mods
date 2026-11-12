using System;
using System.Collections.Generic;
using Il2CppApp.KatamariSin;

namespace OUAKModAPI
{
    /// <summary>
    /// The game's per-map battle tables (<c>MultiplaySetting.dicBattle...</c>) miss the maps the game never finished, such as
    /// the Race map (GI_MI_ONLINE_RACE1). <c>MainGameManager.InitMultiPlay</c> looks the map up in
    /// <c>dicBattleWarningRadius</c>, throws KeyNotFoundException for those, and skips the rest of its setup
    /// (<c>StartObjectSync</c>, the battle's network message callback) (measured 2026-11-07, Race mode). Before
    /// InitMultiPlay, a map missing from a table gets Battle1's value (part of <see cref="GamePatches.Race"/> /
    /// <see cref="GamePatches.Target"/>; maps the game has are never touched).
    /// </summary>
    public static class MapTables
    {
        /// <summary>What was filled in, e.g. "dicBattleWarningRadius[GI_MI_ONLINE_RACE1] = 30 (from GI_MI_ONLINE_BATTLE1)".</summary>
        public static readonly List<string> Filled = new();

        private const GI_MI Source = GI_MI.GI_MI_ONLINE_BATTLE1;

        internal static void Fill()
        {
            try
            {
                var g = GameRefs.Global;
                if (g == null) return;
                var map = (GI_MI)g.launcherStage;
                FillF(nameof(MultiplaySetting.dicBattleCpuAddCoreSize), MultiplaySetting.dicBattleCpuAddCoreSize, map);
                FillF(nameof(MultiplaySetting.dicBattleCpuTreasureGetDistance), MultiplaySetting.dicBattleCpuTreasureGetDistance, map);
                FillF(nameof(MultiplaySetting.dicBattleCpuOtherCoreCheckDistance), MultiplaySetting.dicBattleCpuOtherCoreCheckDistance, map);
                FillF(nameof(MultiplaySetting.dicIgnoreWayPointHeightRange), MultiplaySetting.dicIgnoreWayPointHeightRange, map);
                FillF(nameof(MultiplaySetting.dicBattleCpuItemUseWait), MultiplaySetting.dicBattleCpuItemUseWait, map);
                FillF(nameof(MultiplaySetting.dicWayPointGUIBallSize), MultiplaySetting.dicWayPointGUIBallSize, map);
                FillF(nameof(MultiplaySetting.dicBattleWarningRadius), MultiplaySetting.dicBattleWarningRadius, map);
                FillF(nameof(MultiplaySetting.dicBattleIgnoreHardPointCheckYRange), MultiplaySetting.dicBattleIgnoreHardPointCheckYRange, map);
                FillF(nameof(MultiplaySetting.dicBattleCpuWayPointReachDistance), MultiplaySetting.dicBattleCpuWayPointReachDistance, map);
                FillF(nameof(MultiplaySetting.dicBattleMoveMinSpeed), MultiplaySetting.dicBattleMoveMinSpeed, map);
                FillI(nameof(MultiplaySetting.dicBattleAreaFixedMonoSizeMin), MultiplaySetting.dicBattleAreaFixedMonoSizeMin, map);
                FillI(nameof(MultiplaySetting.dicBattleAreaFixedMonoSizeMax), MultiplaySetting.dicBattleAreaFixedMonoSizeMax, map);
                FillI(nameof(MultiplaySetting.dicBattleForceWarpCoreSize), MultiplaySetting.dicBattleForceWarpCoreSize, map);
            }
            catch (Exception e) { ApiCore.Log.Warn($"Map tables: {e.Message}"); }
        }

        private static void FillF(string name, Il2CppSystem.Collections.Generic.Dictionary<GI_MI, float> d, GI_MI map)
        {
            if (d == null || d.ContainsKey(map) || !d.ContainsKey(Source)) return;
            float v = d[Source];
            d[map] = v;
            Note($"{name}[{map}] = {v} (from {Source})");
        }

        private static void FillI(string name, Il2CppSystem.Collections.Generic.Dictionary<GI_MI, int> d, GI_MI map)
        {
            if (d == null || d.ContainsKey(map) || !d.ContainsKey(Source)) return;
            int v = d[Source];
            d[map] = v;
            Note($"{name}[{map}] = {v} (from {Source})");
        }

        private static void Note(string s)
        {
            Filled.Add(s);
            ApiCore.Log.Info("Map tables: " + s);
        }
    }
}
