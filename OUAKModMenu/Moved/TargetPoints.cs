using System;
using HarmonyLib;
using Il2Cpp;
using Il2CppApp.KatamariSin;
using MelonLoader;
using OUAKModAPI;

namespace OUAKModMenu
{
    /// <summary>
    /// UNUSED DRAFT (nothing calls Init): Target scoring moved to the Multiplayer mod on top of the API's Target patch,
    /// which reads the game's crane counter. Kept for the Multiplayer session to copy.
    ///
    /// Target (Stage_ONTarget1, the collect rule) offline: 10 points for every target rolled up during the round.
    /// The game's collect rule never counts anything (MainGameCore.collectNum is only zeroed in InitMultiPlay and
    /// copied out in UpdateMultiPlay), so the round only had the results' end bonuses. Points go through the game's
    /// own path: MainGameCore.AddHardPointNum(n) adds to UserStatus.hardPointNum, which the HUD's point labels
    /// (MultiplayGameManager.UpdatePlayerStatus -> listTextMeshPoint, "{0}pt") and the results' score
    /// (MultiplayResult.InitResultManager -> MultiResultPlayerInfo.score) both read, and shows the "+points" effect
    /// for your own katamari. (MultiplayGameManager.AddHardPointNum would also shrink the katamari by 10%: that's
    /// the Big Battle hard point's trade, not used here.) Every roll-up in a match, yours and the CPU princes',
    /// goes through MainGameCore.MultiGetMono.
    /// </summary>
    internal static class TargetPoints
    {
        public const int PointsPerTarget = 10;

        public static int Awarded { get; private set; }

        /// <summary>Which rolled-up objects count as targets. Set once the rule is decided.</summary>
        public static Func<MainGameMonoBase, bool> IsTarget = _ => true;

        public static void Init(HarmonyLib.Harmony harmony)
        {
            try
            {
                harmony.Patch(AccessTools.Method(typeof(MainGameCore), nameof(MainGameCore.MultiGetMono)),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(TargetPoints), nameof(AfterMultiGetMono))));
            }
            catch (Exception e) { MelonLogger.Warning($"Target points patch failed: {e.Message}"); }
        }

        /// <summary>True in an offline Target round.</summary>
        public static bool Applies
        {
            get
            {
                if (!GameRefs.IsMultiplay) return false;
                var m = GameRefs.Multiplay;
                return m != null && m.isSinglePlay && m.nowRule == NetworkBattleRule.collect;
            }
        }

        private static void AfterMultiGetMono(MainGameCore __instance, MainGameMonoBase mono, bool __result)
        {
            try
            {
                if (!__result || __instance == null || mono == null || !Applies || !IsTarget(mono)) return;
                __instance.AddHardPointNum(PointsPerTarget);
                Awarded += PointsPerTarget;
            }
            catch (Exception e) { MelonLogger.Warning($"Target points: {e.Message}"); }
        }
    }
}
