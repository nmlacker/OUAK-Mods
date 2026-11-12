using System;
using HarmonyLib;
using Il2CppApp.KatamariSin;
using UnityEngine;
using Reward = Il2CppApp.KatamariSin.MultiplaySetting.KingBoxRewardType;

namespace OUAKModAPI
{
    /// <summary>
    /// Fixes for the game's own battle freebies (<see cref="GamePatches.BattleFreebies"/>, part of the API). Traced and
    /// measured 2026-11-07:
    /// <list type="bullet">
    /// <item><b>Hand</b>: the host's steal works (<c>MainGameCore.StealItem</c> / <c>StolenItem</c> move the other prince's
    /// King Box reward to you), but the stolen freebie never reaches your freebie slot: the slot keeps showing Hand, then
    /// empties, so you can't use what you stole. Now the stolen freebie goes to your slot (with the roulette, as battle
    /// boxes give freebies), and a freebie stolen from you leaves your slot instead of staying there unusable.</item>
    /// <item><b>Hand, your way</b> (the user's design, 2026-11-07): while Hand is in your slot, an icon above every other
    /// prince shows the freebie they hold; using it takes from the nearest one who holds something (the game: a random one).</item>
    /// <item><b>The HUD's countdown ring</b>: <c>MainGameManager.GetItemEffectTimeRatio</c> has no case for Spiderweb,
    /// Bubble, Hand, Shoes, Crown (nor Hoop, Smoke, Dice) and none for the Specials after SP_Timer, so their ring stays
    /// empty while their countdown runs. Now the ring shows the countdown's share of the time the game set when the
    /// freebie was used.</item>
    /// <item><b>Durations</b> (the user's choice, 2026-11-07): Crown 5 s, Special Crown 10 s (the game: 15 / 30), Shoes 5 s
    /// (<see cref="BattleDash.ShoesTime"/>; the game: 10): the HUD countdown, the host's Crown power, the prince's own Crown
    /// and the King Box reward's text all follow.</item>
    /// </list>
    /// Shoes' missing unlimited dash is in <see cref="BattleDash"/>.
    /// </summary>
    public static class BattleFreebies
    {
        /// <summary>How long Crown / Special Crown last in battles: 5 / 10 s (the user's choice; the game's are 15 / 30).</summary>
        public static float CrownTime = 5f, CrownPlusTime = 10f;

        /// <summary>Hand steals that took from the nearest holder (the game's own picks a random one).</summary>
        public static int NearestSteals { get; private set; }

        /// <summary>Icons drawn above other princes so far (counts up while the reveal shows).</summary>
        public static int HandIconsDrawn { get; private set; }

        /// <summary>Show the other princes' freebies above them while Hand is in your slot (true by default).</summary>
        public static bool HandReveal = true;

        /// <summary>The freebie a prince holds (its King Box reward, as a freebie; NoItem when none).</summary>
        public static eInstageItemType HeldBy(MainGameCore core)
        {
            try
            {
                if (core == null) return eInstageItemType.NoItem;
                return FreebieOf(core.multiItemType);
            }
            catch { return eInstageItemType.NoItem; }
        }

        /// <summary>The nearest other prince who holds a freebie, or null.</summary>
        public static MainGameCore NearestHolder(MainGameCore from)
        {
            MainGameCore best = null;
            float bestD = float.MaxValue;
            try
            {
                var mgr = GameRefs.Multiplay;
                if (mgr == null || from == null) return null;
                foreach (var c in KingChances.Cores(mgr))
                {
                    if (c == null || c == from || HeldBy(c) == eInstageItemType.NoItem) continue;
                    float d = (c.transform.position - from.transform.position).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = c; }
                }
            }
            catch { }
            return best;
        }

        // Your Hand: take from the nearest holder (the game's own steal, SetItemSteal(victim, thief): StealItem, StolenItem
        // and its network event) instead of the host's random pick. CPU princes keep the game's random one.
        private static bool BeforeLocalUseItem(MultiplayGameManager __instance, MainGameCore __0, Reward __1)
        {
            if (__1 != Reward.hand || !IsYou(__0)) return true;
            try
            {
                var target = NearestHolder(__0);
                if (target == null) return true;   // nobody holds anything: the game's own use (it finds nothing either)
                // Done once ActivateItemEffect has finished (it sets Hand's own countdown after this call), else the
                // stolen freebie would take over that countdown and be gone when it ends.
                _pendingThief = __0; _pendingVictim = target; _pendingFrame = Time.frameCount;
                return false;
            }
            catch (Exception e) { ApiCore.Log.Warn($"Hand: {e.Message}"); return true; }
        }

        private static MainGameCore _pendingThief, _pendingVictim;
        private static int _pendingFrame;

        private static void DoPendingSteal()
        {
            if (_pendingThief == null) return;
            var thief = _pendingThief; var victim = _pendingVictim;
            _pendingThief = null; _pendingVictim = null;
            try
            {
                var mgr = GameRefs.Multiplay;
                if (mgr == null || victim == null || HeldBy(victim) == eInstageItemType.NoItem) return;
                // SetItemSteal takes what the host's record says the victim holds: make it agree with the prince.
                var st = mgr.GetUserStatusFromCoreNo(victim.multiCoreNo);
                if (st != null && st.itemType != victim.multiItemType) st.itemType = victim.multiItemType;
                mgr.SetItemSteal(victim, thief);
                NearestSteals++;
            }
            catch (Exception e) { ApiCore.Log.Warn($"Hand: {e.Message}"); }
        }

        // Icons above the other princes while Hand is in your slot.
        private static void DrawHandReveal()
        {
            if (!HandReveal || Event.current == null || Event.current.type != EventType.Repaint) return;
            try
            {
                var gm = GameRefs.Main;
                var mgr = GameRefs.Multiplay;
                if (gm == null || mgr == null || !GameRefs.IsMultiplay || gm._nowInventoryItem != eInstageItemType.Hand) return;
                var cam = Camera.main;
                var me = gm.GetCore();
                if (cam == null) return;
                float size = Mathf.Round(Screen.height * 0.05f);
                foreach (var c in KingChances.Cores(mgr))
                {
                    if (c == null || c == me) continue;
                    var item = HeldBy(c);
                    var tex = item == eInstageItemType.NoItem ? null : GameSprites.Freebie(item);
                    if (tex == null) continue;
                    var p = c.transform.position + Vector3.up * Mathf.Max(0.5f, c.KatamariRadius * 2.4f);
                    var sp = cam.WorldToScreenPoint(p);
                    if (sp.z <= 0f) continue;
                    GUI.DrawTexture(new Rect(sp.x - size / 2f, Screen.height - sp.y - size, size, size), tex);
                    HandIconsDrawn++;
                }
            }
            catch { }
        }

        /// <summary>Freebies you stole with Hand that the API put in your slot.</summary>
        public static int StolenToSlot { get; private set; }
        /// <summary>A freebie stolen from you that the API took out of your slot.</summary>
        public static int StolenFromSlot { get; private set; }

        internal static bool Install()
        {
            var h = ApiCore.Instance?.HarmonyInstance ?? throw new InvalidOperationException("the API isn't initialised");
            h.Patch(AccessTools.Method(typeof(MainGameCore), nameof(MainGameCore.StealItem)),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(BattleFreebies), nameof(AfterSteal))));
            // Hand (the user's design): you see what the others hold while Hand is in your slot, and it takes from the nearest.
            h.Patch(AccessTools.Method(typeof(MultiplayGameManager), nameof(MultiplayGameManager.LocalUseItem)),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(BattleFreebies), nameof(BeforeLocalUseItem))));
            ModGui.AddOverlay(DrawHandReveal, 50, "OUAK API");
            GameEvents.Update += () => { if (_pendingThief != null && Time.frameCount > _pendingFrame) DoPendingSteal(); };   // a use that didn't come through ActivateItemEffect
            h.Patch(AccessTools.Method(typeof(MainGameCore), nameof(MainGameCore.StolenItem)),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(BattleFreebies), nameof(AfterStolen))));
            h.Patch(AccessTools.Method(typeof(MainGameManager), nameof(MainGameManager.ActivateItemEffect)),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(BattleFreebies), nameof(AfterActivate))));
            // Crown's time: the host's Crown power and the prince's own Crown (speed, glow, gauge) both start in UseCrown.
            h.Patch(AccessTools.Method(typeof(MultiplayGameManager), nameof(MultiplayGameManager.UseCrown)),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(BattleFreebies), nameof(AfterUseCrown))));
            // The King Box reward's text ("Unlimited dash (10 sec.)"...): the game writes its own numbers into it.
            h.Patch(AccessTools.Method(typeof(MultiplayGameManager.GameState), nameof(MultiplayGameManager.GameState.Update)),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(BattleFreebies), nameof(AfterGameStateUpdate))));
            return true;
        }

        private static bool IsYou(MainGameCore core)
        {
            try { var gm = GameRefs.Main; return core != null && gm != null && GameRefs.IsMultiplay && core == gm.GetCore(); }
            catch { return false; }
        }

        /// <summary>The freebie a King Box reward is in your slot (NoItem when it has none).</summary>
        public static eInstageItemType FreebieOf(Reward r)
        {
            try
            {
                var table = MultiplaySetting.dicKingBoxRewardConvert;
                if (table != null && table.ContainsKey(r)) return table[r];
            }
            catch { }
            return eInstageItemType.NoItem;
        }

        // StealItem(type, target): this prince took `type` from target.
        private static void AfterSteal(MainGameCore __instance, Reward __0)
        {
            if (!IsYou(__instance)) return;
            try
            {
                var item = FreebieOf(__0);
                if (item == eInstageItemType.NoItem) return;
                var gm = GameRefs.Main;
                if (gm._itemEffectTimer >= 0f) gm.EndItemEffect(true);   // Hand's own countdown
                gm.SetInventoryItem(item, true, false);
                StolenToSlot++;
                ApiCore.Log.Info($"Hand: you stole {StageControl.Pretty(item)}.");
            }
            catch (Exception e) { ApiCore.Log.Warn($"Hand: {e.Message}"); }
        }

        // StolenItem(type, target): this prince lost `type` to target.
        private static void AfterStolen(MainGameCore __instance, Reward __0)
        {
            if (!IsYou(__instance)) return;
            try
            {
                var gm = GameRefs.Main;
                if (gm._itemEffectTimer >= 0f) return;   // a freebie in use can't be stolen; leave its countdown alone
                if (gm._nowInventoryItem == eInstageItemType.NoItem) return;
                gm.SetInventoryItem(eInstageItemType.NoItem, false, true);
                StolenFromSlot++;
                ApiCore.Log.Info($"Hand: {StageControl.Pretty(FreebieOf(__0))} was stolen from you.");
            }
            catch (Exception e) { ApiCore.Log.Warn($"Hand: {e.Message}"); }
        }

        /// <summary>The time a freebie the API retimes lasts in battles (0 = the game's own).</summary>
        public static float TimeOf(eInstageItemType item) => item switch
        {
            eInstageItemType.Shoes => BattleDash.ShoesTime,
            eInstageItemType.Crown => CrownTime,
            eInstageItemType.SP_Crown => CrownPlusTime,
            _ => 0f,
        };

        // Battles: the HUD countdown the game set (Shoes 10, Crown 15, Special Crown 30) to the API's times, and remember it
        // for the ring (the game's ratio table has no case for many freebies).
        private static void AfterActivate(MainGameManager __instance)
        {
            if (!GameRefs.IsMultiplay) return;
            DoPendingSteal();
            try
            {
                var item = __instance._nowInventoryItem;
                if (item == eInstageItemType.NoItem || __instance._itemEffectTimer <= 0f) return;
                float t = TimeOf(item);
                if (t > 0f && __instance._itemEffectTimer > t) __instance._itemEffectTimer = t;
                StoryFreebies.RememberCountdown(item, __instance._itemEffectTimer);
            }
            catch { }
        }

        private static void AfterUseCrown(MultiplayGameManager __instance, MainGameCore __0, Reward __1)
        {
            try
            {
                if (__0 == null) return;
                float t = __1 == Reward.crownPlus ? CrownPlusTime : CrownTime;
                var st = __instance.GetUserStatusFromCoreNo(__0.multiCoreNo);
                if (st != null && st.timeCrownPower > t) st.timeCrownPower = t;
                if (__0.multiItemGaugeTimeMax > t) __0.multiItemGaugeTimeMax = t;   // RequestStartCrownPower's time: the prince's own Crown
            }
            catch (Exception e) { ApiCore.Log.Warn($"Crown time: {e.Message}"); }
        }

        // The King Box reward window: the game formats its explanation with its own times (Shoes 10, Crown 15, Crown+ 30).
        private static void AfterGameStateUpdate()
        {
            try
            {
                var mgr = GameRefs.Multiplay;
                if (mgr == null) return;
                var r = mgr.getKingRewardType;
                string old, now;
                switch (r)
                {
                    case Reward.shoes: old = "10"; now = $"{BattleDash.ShoesTime:0.#}"; break;
                    case Reward.crown: old = "15"; now = $"{CrownTime:0.#}"; break;
                    case Reward.crownPlus: old = "30"; now = $"{CrownPlusTime:0.#}"; break;
                    default: return;
                }
                if (old == now) return;
                var text = mgr.textMeshKingBoxExplain;
                if (text == null) return;
                string s = text.text;
                if (string.IsNullOrEmpty(s) || !s.Contains(old)) return;
                int i = s.IndexOf(old, StringComparison.Ordinal);
                text.text = s.Substring(0, i) + now + s.Substring(i + old.Length);
            }
            catch { }
        }
    }
}
