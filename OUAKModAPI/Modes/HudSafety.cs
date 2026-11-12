using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppApp.KatamariSin;

namespace OUAKModAPI
{
    /// <summary>
    /// <c>MainGameUIManager.LateUpdate</c> updates the HUD parts one after another and expects every one to exist: a part
    /// that is missing makes it throw there every frame, and the parts after it (the freebie HUD among them) never update.
    /// The freebie's "get" animation would then never end, so the freebie would stay "forbidden" (can't be used or
    /// replaced). This is a safety net for maps the game never finished (Race, Target): in the 2026-11-08 Race tests every
    /// part was present and it never acted, so it is NOT the proven cause of the user's Race Rocket softlock. When a part is
    /// missing, the API runs the same updates for the parts that exist, in the game's order; with every part there the game's
    /// own LateUpdate runs untouched. Part of <see cref="GamePatches.Race"/> / <see cref="GamePatches.Target"/>.
    /// </summary>
    public static class HudSafety
    {
        /// <summary>The HUD parts found missing (by field name).</summary>
        public static readonly HashSet<string> Missing = new();
        /// <summary>Frames the API updated the HUD in the game's place.</summary>
        public static int Frames { get; private set; }

        internal static void Install(HarmonyLib.Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(MainGameUIManager), nameof(MainGameUIManager.LateUpdate)),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(HudSafety), nameof(Before))));
        }

        private static bool Before(MainGameUIManager __instance)
        {
            try
            {
                var u = __instance;
                if (u == null) return true;
                var sel = u.TimerSelector;
                bool clockOk = sel != null && (sel._noClock || (sel._useTimer ? u.TimerClockUI != null : u.CountupClockUI != null));
                bool cutinOk = u.CutinUI != null && (u.CutinUI._isInCutin ? u.CutinDisplayUI != null : u.LastCatchUI != null);
                if (u.SizeUI != null && clockOk && u.CollectiveUI != null && u.ItemUI != null && u.OujiUI != null && u.MonoNameUI != null
                    && u.PauseMenuUI != null && cutinOk && u.MonoCatchUI != null && u.AreaChangeUI != null && u.ConfirmUI_Standalone != null
                    && u.ClearTargetUI != null && u.RankTargetUI != null && u.SubChallengeUI != null && u.BGMNameUI != null)
                    return true;   // nothing missing: the game's own LateUpdate

                Frames++;
                if (u.SizeUI != null) Try(u.SizeUI.UpdateSizeUI); else Miss("SizeUI");
                if (sel == null) Miss("TimerSelector");
                else if (!sel._noClock)
                {
                    if (sel._useTimer) { if (u.TimerClockUI != null) Try(u.TimerClockUI.UpdateTimer); else Miss("TimerClockUI"); }
                    else { if (u.CountupClockUI != null) Try(u.CountupClockUI.UpdateTimer); else Miss("CountupClockUI"); }
                }
                if (u.CollectiveUI != null) Try(u.CollectiveUI.UpdateCollectiveUI); else Miss("CollectiveUI");
                if (u.ItemUI != null) Try(u.ItemUI.UpdateItemUI); else Miss("ItemUI");
                if (u.OujiUI != null) Try(u.OujiUI.UpdateOujiUI); else Miss("OujiUI");
                if (u.MonoNameUI != null) Try(u.MonoNameUI.UpdateMonoNameUI); else Miss("MonoNameUI");
                if (u.PauseMenuUI != null) Try(u.PauseMenuUI.UpdatePauseUI); else Miss("PauseMenuUI");
                if (u.CutinUI != null)
                {
                    if (u.CutinUI._isInCutin)
                    {
                        Try(u.CutinUI.UpdateCutinUI);
                        if (u.CutinDisplayUI != null) Try(u.CutinDisplayUI.UpdateLastCatchUI); else Miss("CutinDisplayUI");
                    }
                    else if (u.LastCatchUI != null) Try(u.LastCatchUI.UpdateLastCatchUI); else Miss("LastCatchUI");
                }
                else Miss("CutinUI");
                if (u.MonoCatchUI != null) Try(u.MonoCatchUI.UpdatePauseCatchUI); else Miss("MonoCatchUI");
                if (u.AreaChangeUI != null) Try(u.AreaChangeUI.UpdateAreaChangeUI); else Miss("AreaChangeUI");
                if (u.ConfirmUI_Standalone != null) Try(u.ConfirmUI_Standalone.UpdateConfirmWindow); else Miss("ConfirmUI_Standalone");
                if (u.ClearTargetUI != null) Try(u.ClearTargetUI.UpdateClearTargetUI); else Miss("ClearTargetUI");
                if (u.RankTargetUI != null) Try(u.RankTargetUI.UpdateRankTargetUI); else Miss("RankTargetUI");
                if (u.SubChallengeUI != null) Try(u.SubChallengeUI.UpdateSubChallengeUI); else Miss("SubChallengeUI");
                if (u.BGMNameUI != null) Try(u.BGMNameUI.UpdateBGMDisplay); else Miss("BGMNameUI");
                Try(u.UpdateStageUniqueUI);
                return false;
            }
            catch (Exception e) { ApiCore.Log.Warn($"HUD: {e.Message}"); return true; }
        }

        private static void Try(Action a) { try { a(); } catch { } }

        private static void Miss(string part)
        {
            if (Missing.Add(part)) ApiCore.Log.Info($"HUD: this map has no {part}; the API updates the rest of the HUD (the game's update would stop there).");
        }
    }
}
