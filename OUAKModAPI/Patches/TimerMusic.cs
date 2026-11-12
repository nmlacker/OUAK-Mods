using System;
using HarmonyLib;
using Il2CppApp.KatamariSin;

namespace OUAKModAPI
{
    /// <summary>
    /// The Timer freebie keeps the stage music paused (<see cref="GamePatches.TimerMusic"/>; the user, 2026-11-12: "When
    /// unpausing with the Timer active, it should not resume any music"). The game's bug (measured by the Song Selector
    /// session): the Timer pauses the stage music (<c>ActivateItemEffect</c> → <c>SoundManager.PauseBGMInStage</c>) and
    /// freezes the clock, but closing the pause menu (<c>MainGameUIManager.QuitPauseMenu</c> →
    /// <c>SoundManager.ResumeBGMInStage</c>) starts the music again while the clock stays frozen, and nothing pauses it
    /// again until the Timer ends. The same call comes from a King's message (<c>GlobalUIController.SetInMessage</c>) and
    /// from opening the pause menu (<c>SuspendItemEffect</c>).
    /// <para>The fix: in a story stage, while the Timer (or Special Timer) runs, <c>ResumeBGMInStage</c> does nothing,
    /// except from <c>EndItemEffect</c> (the Timer ending, which resumes the music the game's way).</para>
    /// </summary>
    public static class TimerMusic
    {
        /// <summary>Music resumes held back while the Timer ran.</summary>
        public static int Held { get; private set; }
        /// <summary>Music resumes the game made (counted, held or not).</summary>
        public static int Resumes { get; private set; }

        private static int _inEnd;

        /// <summary>True while the Timer or Special Timer runs in a story stage.</summary>
        public static bool TimerRunning
        {
            get
            {
                try
                {
                    var gm = GameRefs.Main;
                    return gm != null && !GameRefs.IsMultiplay && gm._itemEffectTimer > 0f
                        && (gm._nowInventoryItem == eInstageItemType.Timer || gm._nowInventoryItem == eInstageItemType.SP_Timer);
                }
                catch { return false; }
            }
        }

        internal static bool Install()
        {
            var h = ApiCore.Instance?.HarmonyInstance ?? throw new InvalidOperationException("the API isn't initialised");
            h.Patch(AccessTools.Method(typeof(SoundManager), nameof(SoundManager.ResumeBGMInStage)),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(TimerMusic), nameof(BeforeResume))));
            h.Patch(AccessTools.Method(typeof(MainGameManager), nameof(MainGameManager.EndItemEffect)),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(TimerMusic), nameof(BeforeEnd))),
                finalizer: new HarmonyMethod(AccessTools.Method(typeof(TimerMusic), nameof(AfterEnd))));
            return true;
        }

        private static void BeforeEnd() => _inEnd++;
        private static Exception AfterEnd(Exception __exception) { if (_inEnd > 0) _inEnd--; return __exception; }

        private static bool BeforeResume()
        {
            Resumes++;
            if (_inEnd > 0 || !TimerRunning) return true;
            Held++;
            return false;   // the Timer still runs: the music stays paused
        }
    }
}
