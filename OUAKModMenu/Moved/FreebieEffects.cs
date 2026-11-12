using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Il2CppApp.KatamariSin;
using MelonLoader;
using OUAKModAPI;
using UnityEngine;
using eStage = Il2CppApp.KatamariSin.MainGameManager.eStage;

namespace OUAKModMenu
{
    /// <summary>
    /// Gives three battle freebies (and their Specials) an effect in story stages, where the game otherwise uses them up
    /// with only a cosmetic effect. What each does comes from the game itself (traced 2026-11-06):
    /// <list type="bullet">
    /// <item><b>Shoes</b>: the game's own description (King Box reward text) is "Unlimited dash ({0} sec.)" with
    /// bigBattleShoesTime = 10, but its battle code only resets the dash cooldown. Here it does what it says: 10 s of
    /// unlimited dash, through the unlimited-dash switch both dash timers already check
    /// (GlobalManager._debugInifiniteDash, read by MainGameCore.ExecuteOujiDashTimerSingle / ...Multi), starting with a
    /// full dash (the story dash stamina and tiredness cleared).</item>
    /// <item><b>Crown / Special Crown</b> ("Fast &amp; invincible"): MultiplayGameManager.UseCrown starts crown power for
    /// 15.0 s / 30.0 s; MainGameCore.CoreMultiMove multiplies your target speed by 1.5 meanwhile. Here: the same 1.5x for
    /// the same time, through the speed multiplier the game's own story speed boost (Drink) uses. (The "invincible" part
    /// clears other princes' Washtub, Spiderweb and Bubble from you, which can't happen in story stages.)</item>
    /// <item><b>Mushroom / Special Mushroom</b> ("Enlarge katamari"): MainGameCore.UpdateMultiPlay adds 2.5x / 5.0x a
    /// per-map amount (MultiplaySetting.dicBattleForceWarpCoreSize) at once. That amount is 10% of the map's start size
    /// (StageData) on six of the seven maps, so story stages use the same share of their own start size.</item>
    /// </list>
    /// Also: the game's Random freebie draws again instead of handing out one that does nothing in the stage being played.
    /// </summary>
    internal static class FreebieEffects
    {
        private const float ShoesTime = 10f, CrownTime = 15f, CrownPlusTime = 30f, CrownSpeed = 1.5f;
        private const float MushroomRate = 2.5f, MushroomPlusRate = 5f;

        /// <summary>The battle maps: map id, stage, and the start size / Mushroom amount measured in game (used if the live tables can't be read).</summary>
        private static readonly (GI_MI map, eStage stage, string name, int start, int amount)[] MushroomMaps =
        {
            (GI_MI.GI_MI_ONLINE_EDO, eStage.Stage_OnEdo, "Edo", 500, 50),
            (GI_MI.GI_MI_ONLINE_TRAINING, eStage.Stage_ONTraining, "Training", 1500, 500),
            (GI_MI.GI_MI_ONLINE_CAVE, eStage.Stage_OnCave, "Ice Cave", 2000, 200),
            (GI_MI.GI_MI_ONLINE_GOLD, eStage.Stage_OnGold, "Gold Rush", 2500, 250),
            (GI_MI.GI_MI_ONLINE_PYRAMID, eStage.Stage_OnPyramid, "Pyramid", 6000, 600),
            (GI_MI.GI_MI_ONLINE_BATTLE1, eStage.Stage_ONBattle1, "Battle (Jurassic)", 15000, 1500),
            (GI_MI.GI_MI_ONLINE_SKULL, eStage.Stage_OnSkull, "Skull Island", 30000, 3000),
        };

        private static float _crownUntil = -1f, _dashUntil = -1f;
        private static bool _dashOn;

        public static bool Active => _crownUntil > 0f || _dashUntil > 0f;
        public static int Rerolls, DashRefills, MushroomAdded;
        /// <summary>The last Mushroom: which battle map's amount it used, and how much it added.</summary>
        public static string LastMushroom = "";

        public static void Init(HarmonyLib.Harmony harmony)
        {
            int ok = 0;
            ok += Patch(harmony, AccessTools.Method(typeof(MainGameManager), nameof(MainGameManager.ActivateItemEffect)), nameof(BeforeActivate), nameof(AfterActivate));
            ok += Patch(harmony, AccessTools.Method(typeof(MainGameManager), nameof(MainGameManager.GetItemMultiplier)), null, nameof(AfterMultiplier));
            ok += Patch(harmony, AccessTools.Method(typeof(MainGameManager), nameof(MainGameManager.GetRandomItem)), nameof(BeforeRandom), null);
            if (ok < 3) MelonLogger.Warning($"Freebie effects: only {ok} of 3 hooks are in place.");

            FreebieRules.Register(eInstageItemType.Shoes, $"Unlimited dash for {ShoesTime:0} s");
            FreebieRules.Register(eInstageItemType.Crown, $"Move {CrownSpeed}x faster for {CrownTime:0} s");
            FreebieRules.Register(eInstageItemType.SP_Crown, $"Move {CrownSpeed}x faster for {CrownPlusTime:0} s");
            FreebieRules.Register(eInstageItemType.Mushroom, $"Enlarges your katamari at once by {MushroomRate}x a tenth of the stage's start size");
            FreebieRules.Register(eInstageItemType.SP_Mushroom, $"Enlarges your katamari at once by {MushroomPlusRate}x a tenth of the stage's start size");

            GameEvents.StageRestarted += _ => StopAll();
            GameEvents.StageLeft += StopAll;
            GameEvents.StageEnded += (_, _) => StopAll();
            GameEvents.Quit += StopAll;
        }

        private static int Patch(HarmonyLib.Harmony h, System.Reflection.MethodInfo m, string prefix, string postfix)
        {
            try
            {
                h.Patch(m, prefix: prefix != null ? new HarmonyMethod(AccessTools.Method(typeof(FreebieEffects), prefix)) : null,
                    postfix: postfix != null ? new HarmonyMethod(AccessTools.Method(typeof(FreebieEffects), postfix)) : null);
                return 1;
            }
            catch (Exception e) { MelonLogger.Warning($"Freebie effects: couldn't hook {m?.Name}: {e.Message}"); return 0; }
        }

        // ---------------------------------------------------------------- using one

        private static void BeforeActivate(MainGameManager __instance, out eInstageItemType __state)
        {
            __state = eInstageItemType.NoItem;
            try { __state = __instance._nowInventoryItem; } catch { }
        }

        // The game has just used the freebie up (story stages: a cosmetic effect only); apply the effect.
        private static void AfterActivate(eInstageItemType __state)
        {
            try
            {
                if (GameRefs.IsMultiplay) return;
                if (Start(__state, out string what)) ModTasks.Toast($"{StageControl.Pretty(__state)}: {what}");
            }
            catch (Exception e) { MelonLogger.Warning($"Freebie effect: {e.Message}"); }
        }

        /// <summary>Applies an effect (story stages). False for freebies the game handles itself.</summary>
        public static bool Start(eInstageItemType item, out string what)
        {
            what = FreebieRules.AddedEffect(item);
            switch (item)
            {
                case eInstageItemType.Shoes:
                    RefillDash();
                    _dashUntil = Time.time + ShoesTime;
                    SetUnlimitedDash(true);
                    return true;
                case eInstageItemType.Crown:
                case eInstageItemType.SP_Crown:
                    _crownUntil = Math.Max(_crownUntil, Time.time + (item == eInstageItemType.SP_Crown ? CrownPlusTime : CrownTime));
                    return true;
                case eInstageItemType.Mushroom:
                case eInstageItemType.SP_Mushroom:
                    what = Enlarge(item == eInstageItemType.SP_Mushroom ? MushroomPlusRate : MushroomRate);
                    return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- Shoes

        /// <summary>The story dash stamina back to full and any tiredness over.</summary>
        private static void RefillDash()
        {
            var core = GameRefs.Main?.GetCore();
            if (core == null) return;
            core._oujiDashElapsedTimer = 0f;
            core._oujiDashExhaustTimer = 0f;
            DashRefills++;
        }

        /// <summary>The game's own unlimited-dash switch, which both dash timers read (the dash stamina doesn't fill while it's on).</summary>
        private static void SetUnlimitedDash(bool on)
        {
            var g = GameRefs.Global;
            if (g == null) return;
            // Only switch it off if we switched it on (it's a developer switch someone else may use).
            if (on) { if (!g._debugInifiniteDash) { g._debugInifiniteDash = true; _dashOn = true; } }
            else if (_dashOn) { g._debugInifiniteDash = false; _dashOn = false; }
        }

        /// <summary>True while Shoes' unlimited dash runs.</summary>
        public static bool UnlimitedDash => _dashUntil > 0f && Time.time < _dashUntil;

        // ---------------------------------------------------------------- Mushroom

        /// <summary>
        /// The battle maps' Mushroom amount as a share of their start size: the median of amount / start size over the
        /// maps (read live: MultiplaySetting.dicBattleForceWarpCoreSize and StageData). 10% on six of the seven maps
        /// (Edo 50 / 500, Pyramid 600 / 6000, Skull Island 3000 / 30000...; Training is 500 / 1500).
        /// </summary>
        public static float MushroomShare
        {
            get
            {
                var shares = new List<float>();
                foreach (var m in MushroomMaps)
                {
                    int start = StartSize(m.stage);
                    if (start <= 0) start = m.start;
                    shares.Add(BattleAmount(m.map, m.amount) / (float)start);
                }
                shares.Sort();
                return shares.Count == 0 ? 0.1f : shares[shares.Count / 2];
            }
        }

        /// <summary>Adds rate x (the battle maps' share x this story stage's start size), the way battles add rate x the map's amount.</summary>
        private static string Enlarge(float rate)
        {
            var gm = GameRefs.Main;
            if (gm == null) return "";
            int storyStart = StartSize(gm.TargetStage);
            if (storyStart <= 0) storyStart = Math.Max(1, gm.KatamariSize);
            float share = MushroomShare;
            int amount = Math.Max(1, Mathf.RoundToInt(storyStart * share));
            int add = Mathf.RoundToInt(amount * rate);
            int before = gm.KatamariSize;
            gm.AddKatamariSize(add);
            MushroomAdded += add;
            LastMushroom = $"stage start {Katamari.FormatSize(storyStart)} x battle share {share:P0} = amount {amount}; x{rate} = +{add} " +
                           $"({Katamari.FormatSize(before)} -> {Katamari.FormatSize(gm.KatamariSize)})";
            return $"+{Katamari.FormatSize(add)} at once";
        }

        private static int StartSize(eStage stage)
        {
            try
            {
                var data = GameRefs.Global?.AllStageData;
                if (data?.list == null || (int)stage < 0 || (int)stage >= data.list.Count) return 0;
                return data.list[(int)stage]?.StartSize ?? 0;
            }
            catch { return 0; }
        }

        private static int BattleAmount(GI_MI map, int fallback)
        {
            try
            {
                var d = MultiplaySetting.dicBattleForceWarpCoreSize;
                if (d != null && d.ContainsKey(map)) return d[map];
            }
            catch { }
            return fallback;
        }

        // ---------------------------------------------------------------- timers

        /// <summary>Every frame (OnUpdate): effects run out on game time (they wait while the game is paused).</summary>
        public static void Tick()
        {
            if (!Active) return;
            float now = Time.time;
            if (_crownUntil > 0f && now >= _crownUntil) _crownUntil = -1f;
            if (_dashUntil > 0f && now >= _dashUntil) { _dashUntil = -1f; SetUnlimitedDash(false); }
            if (!GameRefs.InStage) StopAll();
        }

        public static void StopAll()
        {
            _crownUntil = _dashUntil = -1f;
            SetUnlimitedDash(false);
        }

        /// <summary>What's running now, for the menu.</summary>
        public static string Describe()
        {
            float now = Time.time;
            var parts = new List<string>();
            if (_dashUntil > now) parts.Add($"Shoes (unlimited dash) {_dashUntil - now:0} s");
            if (_crownUntil > now) parts.Add($"Crown ({CrownSpeed}x speed) {_crownUntil - now:0} s");
            return string.Join(", ", parts);
        }

        // ---------------------------------------------------------------- Crown

        // The speed multiplier the game reads for Drink (MainGameCore.get_NowMaxSpeed / get_NowDashSpeed).
        private static void AfterMultiplier(eInstageItemType checkItem, ref float __result)
        {
            if (checkItem == eInstageItemType.Drink && _crownUntil > 0f && Time.time < _crownUntil && !GameRefs.IsMultiplay)
                __result = Math.Max(__result, CrownSpeed);
        }

        // ---------------------------------------------------------------- Random

        // The game's Random (MainGameManager.GetRandomItem): half the time a normal freebie, half a Special; story stages
        // draw from Drink..Timer, battles from Tarai..Dice. Draw the same way, leaving out the ones that do nothing here.
        private static bool BeforeRandom(MainGameManager __instance)
        {
            try
            {
                if (__instance._itemEffectTimer >= 0f) return true;   // the game does nothing while an effect runs
                // KatamariBall matches (a MultiplayGameManager is running) are left to the game: this single-player menu
                // doesn't change them. Story stages that run battle-style (Philosopher Hunt) still get the redraw.
                if (GameRefs.IsMultiplay && GameRefs.Multiplay != null) return true;
                bool battle = GameRefs.IsMultiplay;
                var normal = Range(battle ? 6 : 1, battle ? 10 : 5).Where(Works).ToList();
                var special = Range(battle ? 21 : 16, battle ? 6 : 5).Where(Works).ToList();
                var group = UnityEngine.Random.Range(0, 2) == 1 && special.Count > 0 ? special : normal.Count > 0 ? normal : special;
                if (group.Count == 0) return true;
                var pick = group[UnityEngine.Random.Range(0, group.Count)];
                __instance.SetInventoryItem(pick, true, false);
                Rerolls++;
                return false;

                bool Works(eInstageItemType i) => battle ? FreebieRules.WorksInBattle(i) : FreebieRules.WorksInStory(i);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"Random freebie: {e.Message}");
                return true;
            }
        }

        private static IEnumerable<eInstageItemType> Range(int first, int count) =>
            Enumerable.Range(first, count).Select(i => (eInstageItemType)i);
    }
}
