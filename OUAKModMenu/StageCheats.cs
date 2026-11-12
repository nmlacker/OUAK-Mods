using System;
using System.Linq;
using Il2CppApp.KatamariSin;
using MelonLoader;
using UnityEngine;
using OUAKModAPI;

namespace OUAKModMenu
{
    /// <summary>In-stage cheats: freebies (instage items), the stage timer and stage flow.</summary>
    internal static class StageCheats
    {
        /// <summary>Every freebie: the ones that do something where you are first (story stage or battle), then the rest.</summary>
        public static eInstageItemType[] Freebies =>
            FreebieRules.Usable(InBattle).Concat(StageControl.Freebies.Where(IsCosmetic)).ToArray();

        /// <summary>A freebie with no effect where you are: given anyway, it's used up with just its visual effect.</summary>
        public static bool IsCosmetic(eInstageItemType item) => InBattle ? !FreebieRules.WorksInBattle(item) : !FreebieRules.WorksInStory(item);

        /// <summary>
        /// A Katamari Ball match (or another battle-style stage) with the player spawned. GameRefs.InStage is false
        /// there on purpose (story stages only), so the menu's freebie tools check this too (the user, 2026-11-12:
        /// the Freebies tab works in Katamari Ball matches).
        /// </summary>
        public static bool InMatch
        {
            get
            {
                var gm = GameRefs.Main;
                return gm != null && gm.PlayerInitiated && GameRefs.IsMultiplay;
            }
        }

        /// <summary>Battle rules apply where you are (a match, or a stage the game runs battle-style).</summary>
        public static bool InBattle => GameRefs.IsMultiplay;

        /// <summary>The Freebies tab can give and clear: a story stage or a Katamari Ball match.</summary>
        public static bool CanUseFreebies => GameRefs.InStage || InMatch;

        /// <summary>What you hold now (in a match: the battle slot; after use the game leaves the story slot as it was).</summary>
        public static eInstageItemType Held
        {
            get
            {
                var gm = GameRefs.Main;
                if (gm == null) return eInstageItemType.NoItem;
                var item = gm._nowInventoryItem;
                if (!InMatch || item == eInstageItemType.NoItem || gm._itemEffectTimer < 0f) return item;
                return eInstageItemType.NoItem;   // in use: no longer held
            }
        }

        public static bool FreezeFreebie;
        public static bool FreezeTimer;
        public static bool AutoFreebie;
        public static eInstageItemType AutoItem = eInstageItemType.Drink;

        // Last real item the player held while freeze was on; re-given whenever the game consumes it.
        public static eInstageItemType FrozenItem = eInstageItemType.NoItem;

        private static bool _timerFreezeApplied;
        private static readonly object ClockHolder = new();
        private static float _nextRegiveTime;

        /// <summary>The spawn hook is in: the auto freebie is given as the player spawns, like a carried one.</summary>
        public static bool SpawnHookActive { get; private set; }
        /// <summary>Times the auto freebie was given at spawn (self-test).</summary>
        public static int SpawnGiven { get; private set; }
        // The freebie the player really carried into this stage (put back if auto freebie is switched off mid-stage).
        private static IntPtr _carriedFor;
        private static eInstageItemType _carried = eInstageItemType.NoItem;

        // Fallback without the hook: armed when a stage starts or restarts, fired once the player has control.
        private static IntPtr _lastStage;
        private static float _lastPlayTime = -1f;
        private static bool _wasRestarting;
        private static bool _autoPending;
        private static float _runningSince = -1f;

        private static MelonPreferences_Entry<bool> _autoPref;
        private static MelonPreferences_Entry<string> _autoItemPref;

        public static void Init(MelonPreferences_Category cat, HarmonyLib.Harmony harmony)
        {
            // The game gives the freebie you carry in as you spawn: MainGameManager.InitiateAfterOuji stores it in
            // _startInventoryItem and puts it in the slot (silently), and every restart puts it back (ResetItem).
            // Setting that start item right there makes the auto freebie behave exactly like a carried one.
            try
            {
                harmony.Patch(HarmonyLib.AccessTools.Method(typeof(MainGameManager), nameof(MainGameManager.InitiateAfterOuji)),
                    postfix: new HarmonyLib.HarmonyMethod(HarmonyLib.AccessTools.Method(typeof(StageCheats), nameof(AfterSpawn))));
                SpawnHookActive = true;
            }
            catch (Exception e) { MelonLogger.Warning($"Spawn freebie hook failed ({e.Message}); the auto freebie will be given once you have control instead."); }
            _autoPref = cat.CreateEntry("AutoFreebie", false, "Auto-give freebie", "Spawn with the chosen freebie on every stage start and restart.");
            _autoItemPref = cat.CreateEntry("AutoFreebieItem", "Drink", "Auto-give freebie item", "eInstageItemType name, e.g. Drink, Magnet, SP_Timer, RandomItem.");
            AutoFreebie = _autoPref.Value;
            if (Enum.TryParse(_autoItemPref.Value, out eInstageItemType item) && item != eInstageItemType.NoItem && item != eInstageItemType.InstageItemTypeMax)
                AutoItem = item;
            // Freeze freebie re-gives the last freebie given through the API (this menu, hotkeys, other mods).
            StageControl.FreebieGiven += given => { if (given != eInstageItemType.RandomItem) FrozenItem = given; };
            StageControl.FreebieCleared += () => FrozenItem = eInstageItemType.NoItem;
        }

        public static void SavePrefs()
        {
            if (_autoPref == null) return;
            _autoPref.Value = AutoFreebie;
            _autoItemPref.Value = AutoItem.ToString();
            MelonPreferences.Save();
        }

        public static void CycleAutoItem(int step)
        {
            int i = Array.IndexOf(Freebies, AutoItem);
            SetAutoItem(Freebies[((i < 0 ? 0 : i) + step + Freebies.Length) % Freebies.Length]);
        }

        public static void SetAutoItem(eInstageItemType item)
        {
            AutoItem = item;
            SavePrefs();
        }

        // Postfix: the player just spawned, holding the carried freebie. Story stages only (matches have their own items).
        private static void AfterSpawn(MainGameManager __instance)
        {
            try
            {
                if (__instance == null || GameRefs.IsMultiplay) return;
                _carriedFor = __instance.Pointer;
                _carried = __instance._startInventoryItem;
                if (!AutoFreebie || AutoItem == eInstageItemType.NoItem) return;
                __instance._startInventoryItem = AutoItem;
                __instance.SetInventoryItem(AutoItem, false, true);
                if (AutoItem != eInstageItemType.RandomItem) FrozenItem = AutoItem;
                SpawnGiven++;
            }
            catch (Exception e) { MelonLogger.Warning($"Spawn freebie: {e.Message}"); }
        }

        public static string ToggleFreezeTimer()
        {
            FreezeTimer = !FreezeTimer;
            return FreezeTimer ? "Stage timer frozen" : "Stage timer running again";
        }

        public static string ToggleFreezeFreebie()
        {
            FreezeFreebie = !FreezeFreebie;
            return FreezeFreebie ? "Freebie frozen (it comes back after use)" : "Freebie freeze off";
        }

        public static string ToggleAutoFreebie()
        {
            AutoFreebie = !AutoFreebie;
            SavePrefs();
            return AutoFreebie ? $"Spawning with {Pretty(AutoItem)} on every stage start and restart" : "Spawn freebie off";
        }

        public static string GiveAutoItem() => GiveFreebie(AutoItem);

        public static string Pretty(eInstageItemType t) => StageControl.Pretty(t);

        /// <summary>Called every frame from OnUpdate.</summary>
        public static void Tick()
        {
            var gm = GameRefs.Main;
            if (gm != null && FreezeFreebie && InMatch && gm.IsGameRunning) TickFrozenFreebieInMatch(gm);
            if (gm == null || !GameRefs.InStage)
            {
                _runningSince = -1f;
                return;
            }

            if (SpawnHookActive) KeepStartItem(gm);
            else TickAutoFreebie(gm);

            // The game skips CountTimer() while its debug flag is set; the API keeps it set while anyone holds the clock.
            if (FreezeTimer != _timerFreezeApplied)
            {
                StageControl.HoldClock(ClockHolder, FreezeTimer);
                _timerFreezeApplied = FreezeTimer;
            }

            if (FreezeFreebie) TickFrozenFreebie(gm);
        }

        /// <summary>
        /// Switched on, off or changed mid-stage: the next restart (the game's ResetItem gives _startInventoryItem)
        /// uses the new choice, or the freebie you really carried in.
        /// </summary>
        private static void KeepStartItem(MainGameManager gm)
        {
            if (gm.Pointer != _carriedFor) return;   // spawned before the menu loaded, or a match
            var want = AutoFreebie && AutoItem != eInstageItemType.NoItem ? AutoItem : _carried;
            if (gm._startInventoryItem != want) gm._startInventoryItem = want;
        }

        private static void TickAutoFreebie(MainGameManager gm)
        {
            // A new MainGameManager means a freshly loaded stage; a restart resets the clock and toggles the
            // restart-wait flag (MainGameManager.RestartStage / CorRestartStage).
            var ptr = gm.Pointer;
            if (ptr != _lastStage) { _lastStage = ptr; _autoPending = true; _lastPlayTime = -1f; }
            float played = gm._playTime;
            if (_lastPlayTime >= 0f && played + 0.5f < _lastPlayTime) _autoPending = true;
            _lastPlayTime = played;
            bool restarting = gm._isInRestartWait;
            if (_wasRestarting && !restarting) _autoPending = true;
            _wasRestarting = restarting;

            if (!AutoFreebie || AutoItem == eInstageItemType.NoItem) { _autoPending = false; return; }
            if (!_autoPending) return;
            if (!gm.IsGameRunning || gm._isInClearDemo || gm._isInGameOver) { _runningSince = -1f; return; }

            // Wait a moment after control returns so the game's own start-of-stage item logic has finished.
            if (_runningSince < 0f) { _runningSince = Time.unscaledTime; return; }
            if (Time.unscaledTime - _runningSince < 0.4f) return;

            _autoPending = false;
            _runningSince = -1f;
            if (gm._itemEffectTimer >= 0f) gm.EndItemEffect(true);
            gm.SetInventoryItem(AutoItem, false, true);
            if (AutoItem != eInstageItemType.RandomItem) FrozenItem = AutoItem;
            ModTasks.Toast($"Auto freebie: {Pretty(gm._nowInventoryItem)}");
        }

        private static void TickFrozenFreebie(MainGameManager gm)
        {
            var current = gm._nowInventoryItem;
            if (current != eInstageItemType.NoItem && current != eInstageItemType.RandomItem)
            {
                FrozenItem = current;
                return;
            }

            // EndItemEffect() clears the slot and sets the effect timer back to -1 once a freebie is used up.
            if (current != eInstageItemType.NoItem || FrozenItem == eInstageItemType.NoItem) return;
            if (gm._itemEffectTimer >= 0f || gm._isInClearDemo || gm._isInGameOver) return;
            if (Time.unscaledTime < _nextRegiveTime) return;

            _nextRegiveTime = Time.unscaledTime + 0.3f;
            gm.SetInventoryItem(FrozenItem, false, true);
        }

        // Every freebie can be given, the cosmetic-only ones too (the user: "The smoke screen, bubbles, dice, hoop, hand,
        // or spider wasn't in the Mod Menu").
        public static string GiveFreebie(eInstageItemType item)
        {
            if (!InMatch) return StageControl.GiveFreebie(item, evenIfItDoesNothing: true) + (IsCosmetic(item) ? " (cosmetic only here)" : "");
            // A Katamari Ball match: the game's battle way, what an item box does (the roulette, then the battle slot;
            // MainGameManager.SetInventoryItem hands it to the match). Measured 2026-11-12: Crown -> battle item crown.
            var gm = GameRefs.Main;
            if (gm._itemEffectTimer >= 0f) gm.EndItemEffect(true);
            gm.SetInventoryItem(item, true, false);
            if (item != eInstageItemType.RandomItem) FrozenItem = item;
            _regiveAt = Time.unscaledTime + 3f;   // the roulette settles first
            return $"Gave freebie: {Pretty(item)}" + (IsCosmetic(item) ? " (does nothing here)" : "");
        }

        public static string ClearFreebie()
        {
            if (!InMatch) return StageControl.ClearFreebie();
            // A match: the battle slot (MultiplaySetItem syncs it), any running effect, the slot.
            var gm = GameRefs.Main;
            var mgr = GameRefs.Multiplay;
            var core = gm.GetCore();
            if (mgr != null && core != null && core.multiItemType != MultiplaySetting.KingBoxRewardType.none)
                mgr.MultiplaySetItem(core, MultiplaySetting.KingBoxRewardType.none);
            if (gm._itemEffectTimer >= 0f) gm.EndItemEffect(true);
            gm.SetInventoryItem(eInstageItemType.NoItem, false, true);
            FrozenItem = eInstageItemType.NoItem;
            return "Freebie cleared";
        }

        // Freeze in a match: the story slot keeps the freebie after use, so "used up" = its effect ran and ended, or the
        // slot emptied (taken by Hand, confiscated). Re-given the battle way, at most every 3 s (the roulette).
        private static float _regiveAt;
        private static bool _sawEffect;

        private static void TickFrozenFreebieInMatch(MainGameManager gm)
        {
            var slot = gm._nowInventoryItem;
            bool running = gm._itemEffectTimer >= 0f;
            if (running) { _sawEffect = true; return; }
            // A freebie with a battle reward type is held while the battle slot has it (MainGameCore.multiItemType);
            // one used without a countdown (e.g. Mushroom) only shows as that slot emptying.
            var core = gm.GetCore();
            var toReward = MultiplaySetting.dicKingBoxItemTypeToRewardType;
            bool battleSlotEmptied = Time.unscaledTime >= _regiveAt && core != null && toReward != null && toReward.ContainsKey(slot)
                                     && core.multiItemType == MultiplaySetting.KingBoxRewardType.none;
            if (slot != eInstageItemType.NoItem && slot != eInstageItemType.RandomItem && !_sawEffect && !battleSlotEmptied) { FrozenItem = slot; return; }
            if (FrozenItem == eInstageItemType.NoItem || Time.unscaledTime < _regiveAt) return;
            _sawEffect = false;
            _regiveAt = Time.unscaledTime + 3f;
            gm.SetInventoryItem(FrozenItem, true, true);
        }
        public static string CompleteTimer() => StageControl.CompleteTimer();
        public static string FinishStage() => StageControl.FinishStage();
        public static string RestartStage() => StageControl.RestartStage();
        public static string ReturnToPlaza() => StageControl.ReturnToPlaza();
    }
}
