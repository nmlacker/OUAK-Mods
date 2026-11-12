using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppApp.KatamariSin;

namespace OUAKModAPI
{
    /// <summary>The two pause menu choices that end the attempt.</summary>
    public enum PauseAction
    {
        /// <summary>"Retry": restart the stage.</summary>
        Retry,
        /// <summary>Back to the plaza (stage select).</summary>
        Plaza,
    }

    /// <summary>
    /// The stage's pause menu (UIPauseMenu). Mods can take over its Retry and back-to-plaza choices: an
    /// interceptor runs when the player picks one, before the game's own Yes/No confirmation opens, and
    /// returning true means "handled" (the confirmation doesn't open and the pause menu stays as it is). Later the
    /// mod carries the action out the game's own way with <see cref="Confirm"/>, closes the menu with
    /// <see cref="Resume"/>, or does nothing (the player is still in the pause menu).
    /// <code>PauseMenu.Interceptors.Add((menu, action) =&gt; { Prompt.Show(...); return true; });</code>
    /// </summary>
    public static class PauseMenu
    {
        /// <summary>Asked in order; the first that returns true handles the choice.</summary>
        public static readonly List<Func<UIPauseMenu, PauseAction, bool>> Interceptors = new();

        /// <summary>The pause menu last opened (null outside stages).</summary>
        public static UIPauseMenu Current
        {
            get { try { return _current != null ? _current : null; } catch { return null; } }
        }

        public static bool IsOpen => GameEvents.Paused;
        public static bool HooksActive { get; private set; }

        private static UIPauseMenu _current;
        private static bool _bypass;

        internal static void Init(HarmonyLib.Harmony harmony)
        {
            int ok = 0;
            ok += Patch(harmony, nameof(UIPauseMenu.OpenRetryConfirm), prefix: nameof(BeforeRetryConfirm));
            ok += Patch(harmony, nameof(UIPauseMenu.OpenSelectConfirm), prefix: nameof(BeforeSelectConfirm));
            ok += Patch(harmony, nameof(UIPauseMenu.OpenPauseMenu), postfix: nameof(AfterOpen));
            ok += Patch(harmony, nameof(UIPauseMenu.QuitPauseMenu), postfix: nameof(AfterQuit));
            // MainGameManager.Update resumes the game when this reads true (the pause menu's Back clicked).
            try
            {
                harmony.Patch(AccessTools.PropertyGetter(typeof(UIPauseMenu), nameof(UIPauseMenu.OnBackUIMouseDown)),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(PauseMenu), nameof(AfterBackDown))));
                ok++;
            }
            catch (Exception e) { ApiCore.Log.Warn($"Couldn't hook the pause menu's Back: {e.Message}"); }
            HooksActive = ok == 5;
            GameEvents.StageLeft += () => _current = null;
        }

        private static int Patch(HarmonyLib.Harmony harmony, string method, string prefix = null, string postfix = null)
        {
            try
            {
                harmony.Patch(AccessTools.Method(typeof(UIPauseMenu), method),
                    prefix: prefix != null ? new HarmonyMethod(AccessTools.Method(typeof(PauseMenu), prefix)) : null,
                    postfix: postfix != null ? new HarmonyMethod(AccessTools.Method(typeof(PauseMenu), postfix)) : null);
                return 1;
            }
            catch (Exception e)
            {
                ApiCore.Log.Warn($"Couldn't hook the pause menu's {method}: {e.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Does <paramref name="action"/> the way the pause menu does after "Yes": restart (MainGameManager.RestartStage)
        /// or back to the plaza (ToSelectFromPause), with the menu's own bookkeeping and sound. Interceptors are skipped.
        /// </summary>
        public static string Confirm(UIPauseMenu menu, PauseAction action)
        {
            menu ??= Current;
            if (menu == null)
            {
                // No pause menu (a mod's own command): the same calls without it.
                return action == PauseAction.Retry ? StageControl.RestartStage() : StageControl.ReturnToPlaza();
            }
            _bypass = true;
            try
            {
                if (action == PauseAction.Retry) menu.OpenRetryConfirm();
                else menu.OpenSelectConfirm();
                menu.StartConfirmedAction();
            }
            finally { _bypass = false; }
            return action == PauseAction.Retry ? "Restarting the stage" : "Returning to the Plaza";
        }

        /// <summary>
        /// Opens the pause menu the way the game does when the Steam overlay opens (MainGameManager.steamOverlayPause:
        /// the next MainGameManager.Update pauses and opens the menu). Only while playing a stage.
        /// </summary>
        public static bool Open()
        {
            var gm = GameRefs.Main;
            if (gm == null || IsOpen || !GameRefs.InStage) return false;
            gm.steamOverlayPause = true;
            return true;
        }

        /// <summary>
        /// Closes the pause menu and goes back to the game, through the game's own resume (as if Back had been
        /// clicked: MainGameManager.Update then unpauses, closes the menu and resumes the sounds).
        /// </summary>
        public static void Resume(UIPauseMenu menu = null)
        {
            if (!IsOpen) return;
            _resumeFrom = UnityEngine.Time.frameCount;
        }

        private static int _resumeFrom = -1;

        private static void AfterBackDown(ref bool __result)
        {
            // True for a few frames after Resume was asked (the next MainGameManager.Update reads it once).
            if (_resumeFrom < 0) return;
            int age = UnityEngine.Time.frameCount - _resumeFrom;
            if (age > 30 || !IsOpen) { _resumeFrom = -1; return; }
            __result = true;
        }

        private static bool Intercept(UIPauseMenu menu, PauseAction action)
        {
            if (_bypass) return true;
            _current = menu;
            foreach (var f in Interceptors.ToArray())
            {
                try
                {
                    if (f(menu, action)) return false;
                }
                catch (Exception e)
                {
                    ApiCore.Log.Warn($"Pause menu interceptor {f.Method.DeclaringType?.Name}.{f.Method.Name} failed: {e.Message}");
                }
            }
            return true;
        }

        // false = the game's confirmation doesn't open.
        private static bool BeforeRetryConfirm(UIPauseMenu __instance) => Intercept(__instance, PauseAction.Retry);
        private static bool BeforeSelectConfirm(UIPauseMenu __instance) => Intercept(__instance, PauseAction.Plaza);

        private static void AfterOpen(UIPauseMenu __instance)
        {
            _current = __instance;
            GameEvents.RaisePause(true);
        }

        private static void AfterQuit()
        {
            _resumeFrom = -1;
            GameEvents.RaisePause(false);
        }
    }
}
