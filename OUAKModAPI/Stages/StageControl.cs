using System;
using System.Linq;
using Il2CppApp.KatamariSin;
using UnityEngine;

namespace OUAKModAPI
{
    /// <summary>
    /// Actions on the stage you're in: freebies (instage items), the clock, finishing, restarting and leaving.
    /// Each returns a message for the status line and throws <see cref="InvalidOperationException"/> (with a
    /// readable reason) when it can't be done right now; run them through <see cref="ModTasks.Run(Func{string}, Action, float, bool)"/>.
    /// </summary>
    public static class StageControl
    {
        /// <summary>Every freebie the game has (without NoItem and the enum's end marker), working or not: see <see cref="FreebieRules"/>.</summary>
        public static readonly eInstageItemType[] Freebies = Enum.GetValues(typeof(eInstageItemType))
            .Cast<eInstageItemType>()
            .Where(t => t != eInstageItemType.NoItem && t != eInstageItemType.InstageItemTypeMax)
            .ToArray();

        /// <summary>A freebie was given through the API (the item asked for).</summary>
        public static event Action<eInstageItemType> FreebieGiven;
        /// <summary>The freebie slot was cleared through the API.</summary>
        public static event Action FreebieCleared;

        /// <summary>A readable freebie name ("Special Timer", "Sunglasses", "Random"...).</summary>
        public static string Pretty(eInstageItemType t)
        {
            switch (t)
            {
                case eInstageItemType.RandomItem: return "Random";
                case eInstageItemType.Sunglass: return "Sunglasses";
                default: return t.ToString().Replace("SP_", "Special ").Replace('_', ' ');
            }
        }

        /// <summary>Puts a freebie in the slot. Refuses one that does nothing in this stage (see the overload).</summary>
        public static string GiveFreebie(eInstageItemType item) => GiveFreebie(item, false);

        /// <summary>
        /// Puts a freebie in the slot. With <paramref name="evenIfItDoesNothing"/> it also gives one that has no effect in
        /// this stage (<see cref="FreebieRules.WorksHere"/>): the game then uses it up with only its cosmetic effect
        /// (Hoop's ring, Smoke's puff, the battle freebies' hit effects on yourself in story stages).
        /// </summary>
        public static string GiveFreebie(eInstageItemType item, bool evenIfItDoesNothing)
        {
            var gm = RequireStage();
            if (item == eInstageItemType.NoItem || item >= eInstageItemType.InstageItemTypeMax) throw new InvalidOperationException($"There's no freebie {item}.");
            // A freebie that does nothing here would just be used up: refuse it unless asked (FreebieRules).
            if (!evenIfItDoesNothing && !FreebieRules.WorksHere(item)) throw new InvalidOperationException(FreebieRules.WhyNot(item));
            // SetInventoryItem() refuses while another freebie's effect is running, so end that first.
            if (gm._itemEffectTimer >= 0f) gm.EndItemEffect(true);
            gm.SetInventoryItem(item, false, false);
            try { FreebieGiven?.Invoke(item); } catch (Exception e) { GameEvents.Report(FreebieGiven, e); }
            return $"Gave freebie: {Pretty(gm._nowInventoryItem)}";
        }

        public static string ClearFreebie()
        {
            var gm = RequireStage();
            if (gm._itemEffectTimer >= 0f) gm.EndItemEffect(true);
            gm.SetInventoryItem(eInstageItemType.NoItem, false, true);
            try { FreebieCleared?.Invoke(); } catch (Exception e) { GameEvents.Report(FreebieCleared, e); }
            return "Freebie cleared";
        }

        /// <summary>Runs the clock out now: the game then decides clear or game over, as when time is up.</summary>
        public static string CompleteTimer()
        {
            var gm = RequirePlayable();
            gm._playTime = gm._startTime;
            gm._nowTime = 0f;
            // Same call CountTimer() makes when the clock runs out.
            gm.EndGame(false);
            return "Stage timer completed";
        }

        /// <summary>Clears the stage now (the goal counts as reached).</summary>
        public static string FinishStage()
        {
            var gm = RequirePlayable();
            if (!gm._hasCleared) gm.MakeStageCleared();
            gm.EndGame(true);
            return "Stage finished";
        }

        /// <summary>The pause menu's Retry, without the pause menu.</summary>
        public static string RestartStage()
        {
            var gm = RequireStage();
            if (gm._isInClearDemo || gm._isInGameOver)
                throw new InvalidOperationException("The stage is already ending; wait for the result screen.");
            if (gm._isInRestartWait)
                throw new InvalidOperationException("Already restarting.");
            gm.RestartStage(false);
            return "Restarting stage";
        }

        /// <summary>The pause menu's "back to the plaza", without the pause menu.</summary>
        public static string ReturnToPlaza()
        {
            var gm = RequireStage();
            gm.ToSelectFromPause();
            return "Returning to the Plaza";
        }

        /// <summary>
        /// Stops or restarts the stage clock (MainGameManager._debugTimerStop: the game skips CountTimer while it's
        /// set). Several mods may hold the clock: it runs again only when every holder let go.
        /// </summary>
        public static void HoldClock(object holder, bool hold)
        {
            if (holder == null) return;
            if (hold) ClockHolders.Add(holder); else ClockHolders.Remove(holder);
            ApplyClock();
        }

        /// <summary>True while any mod holds the clock through <see cref="HoldClock"/>.</summary>
        public static bool ClockHeld => ClockHolders.Count > 0;

        private static readonly System.Collections.Generic.HashSet<object> ClockHolders = new();
        private static bool _clockApplied;

        internal static void ApplyClock()
        {
            var gm = GameRefs.Main;
            if (gm == null) { _clockApplied = false; return; }
            if (ClockHolders.Count > 0)
            {
                if (!gm._debugTimerStop) gm._debugTimerStop = true;
                _clockApplied = true;
            }
            else if (_clockApplied)
            {
                gm._debugTimerStop = false;
                _clockApplied = false;
            }
        }

        public static MainGameManager RequireStage()
        {
            if (!GameRefs.InStage) throw new InvalidOperationException("You need to be in a stage.");
            return GameRefs.Main;
        }

        public static MainGameManager RequirePlayable()
        {
            var gm = RequireStage();
            if (gm._isInClearDemo || gm._isInGameOver)
                throw new InvalidOperationException("The stage is already ending.");
            if (!gm.IsGameRunning)
                throw new InvalidOperationException("Unpause and wait for any cutscene to finish first.");
            return gm;
        }
    }
}
