using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Il2CppApp.KatamariSin;
using UnityEngine;
using eStage = Il2CppApp.KatamariSin.MainGameManager.eStage;

namespace OUAKModAPI
{
    /// <summary>
    /// Battle freebies in story stages (<see cref="GamePatches.StoryFreebies"/>, part of the API). Story stages never hand
    /// these out, so this only acts when a mod gives you one (the Mod Menu, an online host's lobby...).
    /// <para>In story stages the game uses Shoes, Crown and Mushroom (and the Special Crown / Mushroom) up with only a
    /// cosmetic effect: <c>MainGameManager.ActivateItemEffect</c>'s story branch ends the freebie at once (Crown also
    /// starts the crown glow and never stops it); their real code only runs in battles. The patch makes them do what the
    /// game's own texts and battle code say (traced and measured 2026-11-06), and Shoes and Crown keep their icon and
    /// countdown on the HUD the way the battle branch does (<c>_itemEffectTimer</c>, <c>UIIngameItem.StartItemActivationDemo</c>),
    /// with the glow stopped when the Crown runs out:</para>
    /// <list type="bullet">
    /// <item><b>Shoes</b>: the King Box text says "Unlimited dash ({0} sec.)" with <c>bigBattleShoesTime</c> 10:
    /// <see cref="ShoesTime"/> s of unlimited dash (the game's own switch both dash timers read,
    /// <c>GlobalManager._debugInifiniteDash</c>), starting from a full dash.</item>
    /// <item><b>Crown / Special Crown</b> ("Fast &amp; invincible"): battles give 1.5x speed for 15 / 30 s
    /// (<c>UseCrown</c>, <c>CoreMultiMove</c>); here the same through the speed multiplier Drink uses
    /// (<c>MainGameManager.GetItemMultiplier</c>). "Invincible" protects from other princes' freebies, which story
    /// stages don't have.</item>
    /// <item><b>Mushroom / Special Mushroom</b> ("Enlarge katamari"): battles add 2.5x / 5x a per-map amount at once
    /// (<c>dicBattleForceWarpCoreSize</c>), 10% of the map's start size on six of the seven maps; here 2.5x / 5x that
    /// share of the story stage's start size. Both stay on the HUD for <see cref="MushroomShowTime"/> like in battles, so
    /// the game's Mushroom effect (which the story branch removes at once) plays.</item>
    /// </list>
    /// <see cref="FreebieRules"/> lists them as working in story stages (unless switched off with <see cref="SetItem"/>).
    /// </summary>
    public static class StoryFreebies
    {
        /// <summary>
        /// How long Shoes and Crown last: 5 s, Special Crown 10 s (the user's choice, 2026-11-07; the game's battle values
        /// are 10 / 15 / 30 s). Battles use the same times (<see cref="BattleDash.ShoesTime"/>, <see cref="BattleFreebies.CrownTime"/>).
        /// </summary>
        public static float ShoesTime = 5f, CrownTime = 5f, CrownPlusTime = 10f, CrownSpeed = 1.5f;
        public static float MushroomRate = 2.5f, MushroomPlusRate = 5f;

        /// <summary>
        /// How long a Mushroom stays on the HUD after use (its effect plays meanwhile): the battle's own time,
        /// <c>Game.ONLINE_ITEM_TIME</c> (3 s if it can't be read).
        /// </summary>
        public static float MushroomShowTime
        {
            get
            {
                try { float t = Game.ONLINE_ITEM_TIME; if (t > 0f && t < 60f) return t; } catch { }
                return 3f;
            }
        }

        /// <summary>A freebie got its effect: the freebie, and what it did.</summary>
        public static event Action<eInstageItemType, string> Applied;

        /// <summary>The freebies this patch gives an effect: Shoes, Crown, Special Crown, Mushroom, Special Mushroom.</summary>
        public static readonly eInstageItemType[] Items =
        {
            eInstageItemType.Shoes, eInstageItemType.Crown, eInstageItemType.SP_Crown, eInstageItemType.Mushroom, eInstageItemType.SP_Mushroom,
        };

        private static readonly HashSet<eInstageItemType> _off = new();

        /// <summary>True when <paramref name="item"/> gets its effect while the patch is on (all of them unless switched off).</summary>
        public static bool IsOn(eInstageItemType item) => Array.IndexOf(Items, item) >= 0 && !_off.Contains(item);

        /// <summary>
        /// Switches one of the <see cref="Items"/> on or off (all on by default), e.g. a host's choice. The setting is
        /// shared by every mod that enables the patch. <see cref="FreebieRules"/> follows it.
        /// </summary>
        public static void SetItem(eInstageItemType item, bool on)
        {
            if (Array.IndexOf(Items, item) < 0) return;
            if (on ? !_off.Remove(item) : !_off.Add(item)) return;
            if (GamePatches.StoryFreebies.Ready) OnChanged(true);
        }

        /// <summary>True while Shoes' unlimited dash runs.</summary>
        public static bool UnlimitedDash => _dashUntil > 0f && Time.time < _dashUntil;
        /// <summary>True while a Crown's speed runs.</summary>
        public static bool CrownActive => _crownUntil > 0f && Time.time < _crownUntil;
        /// <summary>The last Mushroom: the amount it used and what it added.</summary>
        public static string LastMushroom { get; private set; } = "";

        // The battle maps' start size and Mushroom amount, measured in game (used when the live tables can't be read).
        private static readonly (GI_MI map, eStage stage, int start, int amount)[] MushroomMaps =
        {
            (GI_MI.GI_MI_ONLINE_EDO, eStage.Stage_OnEdo, 500, 50),
            (GI_MI.GI_MI_ONLINE_TRAINING, eStage.Stage_ONTraining, 1500, 500),
            (GI_MI.GI_MI_ONLINE_CAVE, eStage.Stage_OnCave, 2000, 200),
            (GI_MI.GI_MI_ONLINE_GOLD, eStage.Stage_OnGold, 2500, 250),
            (GI_MI.GI_MI_ONLINE_PYRAMID, eStage.Stage_OnPyramid, 6000, 600),
            (GI_MI.GI_MI_ONLINE_BATTLE1, eStage.Stage_ONBattle1, 15000, 1500),
            (GI_MI.GI_MI_ONLINE_SKULL, eStage.Stage_OnSkull, 30000, 3000),
        };

        private static float _crownUntil = -1f, _dashUntil = -1f;
        private static bool _dashOn;
        private static readonly List<IDisposable> _registered = new();

        internal static bool Install()
        {
            var h = ApiCore.Instance?.HarmonyInstance ?? throw new InvalidOperationException("the API isn't initialised");
            h.Patch(AccessTools.Method(typeof(MainGameManager), nameof(MainGameManager.ActivateItemEffect)),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(StoryFreebies), nameof(BeforeActivate))),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(StoryFreebies), nameof(AfterActivate))));
            h.Patch(AccessTools.Method(typeof(MainGameManager), nameof(MainGameManager.GetItemMultiplier)),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(StoryFreebies), nameof(AfterMultiplier))));
            // The HUD's countdown ring (UIIngameItem.UpdateTimerGauge) reads GetItemEffectTimeRatio, whose table only has
            // the story freebies' times: for the others it says 0 and the ring stays empty.
            h.Patch(AccessTools.Method(typeof(MainGameManager), nameof(MainGameManager.GetItemEffectTimeRatio)),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(StoryFreebies), nameof(AfterTimeRatio))));
            // The game ends a freebie (its countdown ran out, or something cleared it): end ours with it.
            h.Patch(AccessTools.Method(typeof(MainGameManager), nameof(MainGameManager.EndItemEffect)),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(StoryFreebies), nameof(AfterEndItemEffect))));
            GameEvents.StageRestarted += _ => StopAll();
            GameEvents.StageLeft += StopAll;
            GameEvents.StageEnded += (_, _) => StopAll();
            GameEvents.Quit += StopAll;
            GameEvents.Update += Tick;
            OnChanged(true);
            return true;
        }

        internal static void OnChanged(bool on)
        {
            foreach (var r in _registered) r.Dispose();
            _registered.Clear();
            if (!on) { StopAll(); return; }
            void Reg(eInstageItemType item, string what) { if (IsOn(item)) _registered.Add(FreebieRules.Register(item, what)); }
            Reg(eInstageItemType.Shoes, $"Unlimited dash for {ShoesTime:0} s");
            Reg(eInstageItemType.Crown, $"Move {CrownSpeed}x faster for {CrownTime:0} s");
            Reg(eInstageItemType.SP_Crown, $"Move {CrownSpeed}x faster for {CrownPlusTime:0} s");
            Reg(eInstageItemType.Mushroom, $"Enlarges your katamari at once by {MushroomRate}x a tenth of the stage's start size");
            Reg(eInstageItemType.SP_Mushroom, $"Enlarges your katamari at once by {MushroomPlusRate}x a tenth of the stage's start size");
        }

        private static void BeforeActivate(MainGameManager __instance, out eInstageItemType __state)
        {
            __state = eInstageItemType.NoItem;
            try { __state = __instance._nowInventoryItem; } catch { }
        }

        // The game has just used the freebie up (story stages: cosmetic only): apply the effect.
        private static void AfterActivate(eInstageItemType __state)
        {
            if (GameRefs.IsMultiplay) return;
            try
            {
                if (!Start(__state, out string what)) return;
                try { Applied?.Invoke(__state, what); } catch (Exception e) { GameEvents.Report(Applied, e); }
            }
            catch (Exception e) { ApiCore.Log.Warn($"Story freebie {__state}: {e.Message}"); }
        }

        private static bool Start(eInstageItemType item, out string what)
        {
            what = FreebieRules.AddedEffect(item);
            if (!IsOn(item)) return false;
            switch (item)
            {
                case eInstageItemType.Shoes:
                    var core = GameRefs.Main?.GetCore();
                    if (core != null) { core._oujiDashElapsedTimer = 0f; core._oujiDashExhaustTimer = 0f; }
                    _dashUntil = Time.time + ShoesTime;
                    SetUnlimitedDash(true);
                    ShowCountdown(item, ShoesTime);
                    return true;
                case eInstageItemType.Crown:
                case eInstageItemType.SP_Crown:
                    float t = item == eInstageItemType.SP_Crown ? CrownPlusTime : CrownTime;
                    _crownUntil = Time.time + t;
                    _crownGlow = true;   // the game's story branch started the crown glow (EffectManager.PlayCrownEffect(core, 0))
                    ShowCountdown(item, t);
                    return true;
                case eInstageItemType.Mushroom:
                case eInstageItemType.SP_Mushroom:
                    what = Enlarge(item == eInstageItemType.SP_Mushroom ? MushroomPlusRate : MushroomRate);
                    // The game's story branch started the Mushroom effect (EffectManager.PlayEffectParticle(EfItem_Mushroom),
                    // "Ef_Mushroom_Mass") and ended the freebie at once, so EffectManager.UpdateItemEffect removed the effect
                    // right away (StopItemEffect while no item is in effect). Battles keep the Mushroom on the HUD for
                    // Game.ONLINE_ITEM_TIME, which lets the effect play: do the same.
                    ShowCountdown(item, MushroomShowTime);
                    return true;
            }
            return false;
        }

        // Only switch the game's developer flag off if this switched it on.
        private static void SetUnlimitedDash(bool on)
        {
            var g = GameRefs.Global;
            if (g == null) return;
            if (on) { if (!g._debugInifiniteDash) { g._debugInifiniteDash = true; _dashOn = true; } }
            else if (_dashOn) { g._debugInifiniteDash = false; _dashOn = false; }
        }

        /// <summary>The battle maps' Mushroom amount as a share of their start size (median over the maps; 10%).</summary>
        public static float MushroomShare
        {
            get
            {
                var shares = new List<float>();
                foreach (var m in MushroomMaps)
                {
                    int start = StartSize(m.stage);
                    if (start <= 0) start = m.start;
                    int amount = m.amount;
                    try
                    {
                        var d = MultiplaySetting.dicBattleForceWarpCoreSize;
                        if (d != null && d.ContainsKey(m.map)) amount = d[m.map];
                    }
                    catch { }
                    shares.Add(amount / (float)start);
                }
                shares.Sort();
                return shares.Count == 0 ? 0.1f : shares[shares.Count / 2];
            }
        }

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
            LastMushroom = $"stage start {Katamari.FormatSize(storyStart)} x share {share:P0} = {amount}; x{rate} = +{add} " +
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

        // The speed multiplier the game reads for Drink (MainGameCore.get_NowMaxSpeed / get_NowDashSpeed).
        private static void AfterMultiplier(eInstageItemType checkItem, ref float __result)
        {
            if (checkItem == eInstageItemType.Drink && CrownActive && !GameRefs.IsMultiplay)
                __result = Math.Max(__result, CrownSpeed);
        }

        private static void Tick()
        {
            if (_crownUntil < 0f && _dashUntil < 0f) return;
            float now = Time.time;
            if (_crownUntil > 0f && now >= _crownUntil) { _crownUntil = -1f; StopCrownGlow(); }
            if (_dashUntil > 0f && now >= _dashUntil) { _dashUntil = -1f; SetUnlimitedDash(false); }
            if (!GameRefs.InStage) StopAll();
        }

        /// <summary>Ends the running effects now (Shoes' unlimited dash, Crown's speed and glow).</summary>
        public static void StopAll()
        {
            _crownUntil = _dashUntil = -1f;
            SetUnlimitedDash(false);
            StopCrownGlow();
        }

        private static bool _crownGlow, _ending;

        private static void StopCrownGlow()
        {
            if (!_crownGlow) return;
            _crownGlow = false;
            try { GameRefs.Main?.effect?.StopCrownEffect(0); }
            catch (Exception e) { ApiCore.Log.Warn($"Crown glow: {e.Message}"); }
        }

        /// <summary>
        /// Keeps the freebie on the HUD with its countdown, the way ActivateItemEffect's battle branch does for Shoes and
        /// Crown: the slot keeps the item, <c>_itemEffectTimer</c> counts down (the game ends it with EndItemEffect),
        /// plus the activation animation, the use effect and the sound.
        /// </summary>
        internal static void ShowCountdown(eInstageItemType item, float seconds)
        {
            var gm = GameRefs.Main;
            if (gm == null) return;
            _countdownItem = item;
            _countdownTotal = seconds;
            try
            {
                gm._nowInventoryItem = item;
                gm._itemEffectTimer = seconds;
                gm._hasCalledEffectEnd = false;
                gm.MainUI?.ItemUI?.StartItemActivationDemo();
                gm.EffectController?.StartItemUseEffect();
                gm.sound?.PlaySE(0x2FC, false, false);
            }
            catch (Exception e) { ApiCore.Log.Warn($"Freebie countdown: {e.Message}"); }
        }

        private static eInstageItemType _countdownItem = eInstageItemType.NoItem;
        private static float _countdownTotal;

        /// <summary>The countdown a freebie started with, for the HUD ring (battles: what the game set; see <see cref="BattleFreebies"/>).</summary>
        internal static void RememberCountdown(eInstageItemType item, float seconds)
        {
            if (seconds <= 0f) return;
            _countdownItem = item;
            _countdownTotal = seconds;
        }

        // The ring's share for a countdown whose freebie the game's ratio table has no case for (it says 0 for those).
        private static void AfterTimeRatio(MainGameManager __instance, ref float __result)
        {
            if (__result > 0f || _countdownItem == eInstageItemType.NoItem || _countdownTotal <= 0f) return;
            try
            {
                if (__instance._nowInventoryItem != _countdownItem) { _countdownItem = eInstageItemType.NoItem; return; }
                __result = Mathf.Clamp01(__instance._itemEffectTimer / _countdownTotal);
            }
            catch { }
        }

        // The game's countdown ran out (or the freebie was cleared): stop ours too.
        private static void AfterEndItemEffect()
        {
            if (_ending || (_dashUntil < 0f && _crownUntil < 0f && !_crownGlow)) return;
            _ending = true;
            try { StopAll(); } finally { _ending = false; }
        }

        /// <summary>True while an effect runs (Shoes' dash or Crown's speed).</summary>
        public static bool Active => UnlimitedDash || CrownActive;

        /// <summary>What's running now, e.g. "Shoes (unlimited dash) 7 s, Crown (1.5x speed) 12 s", or "".</summary>
        public static string Describe()
        {
            float now = Time.time;
            var parts = new List<string>();
            if (_dashUntil > now) parts.Add($"Shoes (unlimited dash) {_dashUntil - now:0} s");
            if (_crownUntil > now) parts.Add($"Crown ({CrownSpeed}x speed) {_crownUntil - now:0} s");
            return string.Join(", ", parts);
        }
    }

    /// <summary>
    /// The Random freebie (<see cref="GamePatches.RandomFreebie"/>, part of the API).
    /// <para>The game's <c>MainGameManager.GetRandomItem</c> draws half the time a normal freebie, half a Special:
    /// story stages from Drink..Timer (all of which work), battles from Tarai..Dice, so in battles it can hand out Hoop,
    /// Smoke, Dice and Special Dice, which do nothing there (and Philosopher Hunt, a story stage that runs as a battle,
    /// battle freebies that do nothing). The patch only steps in when the game's own draw is one of those: it draws
    /// again from the same group (normal or Special) among freebies that work where you are (<see cref="FreebieRules"/>).
    /// A draw that works is never touched, so story stages play exactly as before.</para>
    /// </summary>
    public static class RandomFreebie
    {
        /// <summary>Draws the patch replaced.</summary>
        public static int Draws { get; private set; }

        internal static bool Install()
        {
            var h = ApiCore.Instance?.HarmonyInstance ?? throw new InvalidOperationException("the API isn't initialised");
            h.Patch(AccessTools.Method(typeof(MainGameManager), nameof(MainGameManager.GetRandomItem)),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(RandomFreebie), nameof(AfterRandom))));
            return true;
        }

        private static void AfterRandom(MainGameManager __instance)
        {
            try
            {
                var item = __instance._nowInventoryItem;
                if (item == eInstageItemType.NoItem || FreebieRules.WorksHere(item)) return;
                bool battle = GameRefs.IsMultiplay;
                bool special = (int)item >= (int)eInstageItemType.SP_Drink && (int)item <= (int)eInstageItemType.SP_Dice;
                var normal = Range(battle ? 6 : 1, battle ? 10 : 5).Where(Works).ToList();
                var specials = Range(battle ? 21 : 16, battle ? 6 : 5).Where(Works).ToList();
                var group = special && specials.Count > 0 ? specials : normal.Count > 0 ? normal : specials;
                if (group.Count == 0) return;
                var pick = group[UnityEngine.Random.Range(0, group.Count)];
                __instance.SetInventoryItem(pick, true, false);
                Draws++;
                ApiCore.Log.Debug($"Random freebie: {item} does nothing here -> {pick}");

                bool Works(eInstageItemType i) => battle ? FreebieRules.WorksInBattle(i) : FreebieRules.WorksInStory(i);
            }
            catch (Exception e) { ApiCore.Log.Warn($"Random freebie: {e.Message}"); }
        }

        private static IEnumerable<eInstageItemType> Range(int first, int count) =>
            Enumerable.Range(first, count).Select(i => (eInstageItemType)i);
    }
}
