using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using Il2CppApp.KatamariSin;
using MelonLoader;
using OUAKModAPI;
using Chance = Il2CppApp.KatamariSin.MultiplaySetting.KingChanceType;
using Reward = Il2CppApp.KatamariSin.MultiplaySetting.KingBoxRewardType;

namespace OUAKModMenu
{
    /// <summary>
    /// TEST-ONLY working copy (2026-11-06): King Chances moved to the Multiplayer mod (the user: "This is a feature for
    /// the Multiplayer mod"), on top of the API's King Chance patch. Only TrainingTest's "kcall" token hooks this.
    ///
    /// King Chances in offline matches. The game has them (each map's MultiplayBigBattle.kingChanceTable, the texts
    /// MultiplaySetting.bigBattleKingChanceMessage, the handler) but never starts one: KingChanceProcess has no
    /// callers and nowKingChangeNo is only ever set to 0. Firing one goes through the game's own path:
    /// KingChanceProcess(type) sets the request (0x218/0x21C), GameState.Update hands it to SetKingChanceBonus once
    /// no King Box reward or message is on screen, and SetKingChanceBonus queues the message (SYS_263 + the type's
    /// text) and does the stage part: monoDrop shows the stage data's "mono/AddMono", itemDrop "mono/AddItems",
    /// bonusArea hides "KingChance/BonusAreaWall" (MainGameManager.SetActiveStageDataObject). The other three types
    /// only get their message there, so a postfix adds what their texts promise:
    /// - unexploded ("unlimited dash for a set time"): MultiplayGameManager.MultiplayInfinityDash for every prince
    ///   (UserStatus.infinityDashTime = 60 s, MainGameCore.SetMultiInfinityDash; HostGameProcess ends it with
    ///   SetInfinityDashOff), the game's own unused helper for it;
    /// - itemConfiscation ("Confiscated a freebie?!"): every prince's held freebie goes (MultiplaySetItem(core, none),
    ///   which also syncs it);
    /// - kingBox ("Received a King Chest!"): every prince gets a King Chest reward (MultiplayBigBattle.MultiplayGetKingBox,
    ///   the reward step of rolling one up).
    /// </summary>
    internal static class KingChances
    {
        public static int Fired { get; private set; }
        public static readonly List<string> History = new();

        public static void Init(HarmonyLib.Harmony harmony)
        {
            try
            {
                harmony.Patch(AccessTools.Method(typeof(MultiplayGameManager), nameof(MultiplayGameManager.SetKingChanceBonus)),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(KingChances), nameof(AfterSetKingChanceBonus))));
            }
            catch (Exception e) { MelonLogger.Warning($"King Chance patch failed: {e.Message}"); }
        }

        /// <summary>True in an offline Big Battle match (Sports Katamari / training), the only place they run.</summary>
        public static bool InOfflineMatch
        {
            get
            {
                if (!GameRefs.IsMultiplay) return false;
                var m = GameRefs.Multiplay;
                return m != null && m.isSinglePlay && m.nowRule == NetworkBattleRule.bigBattle && m.multiplayBigBattle != null;
            }
        }

        /// <summary>The map's table, e.g. Battle (Jurassic) [kingBox, unexploded, monoDrop, itemConfiscation].</summary>
        public static Chance[] Table()
        {
            var t = GameRefs.Multiplay?.multiplayBigBattle?.kingChanceTable;
            if (t == null) return Array.Empty<Chance>();
            var r = new Chance[t.Length];
            for (int i = 0; i < t.Length; i++) r[i] = t[i];
            return r;
        }

        /// <summary>
        /// With custom match rules taking freebies away, freebie chances become object drops: kingBox when no King
        /// Box freebie is allowed, itemDrop when no item box freebie is.
        /// </summary>
        public static Chance Allowed(Chance type)
        {
            if (!CustomMatch.Applies) return type;
            if (type == Chance.kingBox && CustomMatch.NoKingBoxRewards) return Chance.monoDrop;
            if (type == Chance.itemDrop && CustomMatch.NoBoxFreebies) return Chance.monoDrop;
            return type;
        }

        /// <summary>Fires the map's next King Chance (wrapping round the table). Returns what it fired, or null.</summary>
        public static string FireNext()
        {
            var mgr = GameRefs.Multiplay;
            var bb = mgr?.multiplayBigBattle;
            var table = bb?.kingChanceTable;
            if (bb == null || table == null || table.Length == 0) return null;
            int n = bb.nowKingChangeNo;
            var type = table[((n % table.Length) + table.Length) % table.Length];
            bb.nowKingChangeNo = n + 1;
            return Fire(type, $"#{n + 1}");
        }

        /// <summary>Requests one King Chance through the game's own path (shown and applied by GameState.Update).</summary>
        public static string Fire(Chance type, string why = "")
        {
            var mgr = GameRefs.Multiplay;
            if (mgr == null) return null;
            var real = Allowed(type);
            mgr.KingChanceProcess(real);
            Fired++;
            string what = real == type ? real.ToString() : $"{type} -> {real} (freebies off)";
            History.Add(what);
            MelonLogger.Msg($"King Chance {why}: {what}");
            return what;
        }

        private static void AfterSetKingChanceBonus(MultiplayGameManager __instance, Chance type)
        {
            try
            {
                var mgr = __instance;
                var cores = mgr?.dicPlayerCore;
                if (cores == null) return;
                int n = 0;
                switch (type)
                {
                    case Chance.unexploded:
                        foreach (var core in Cores(mgr)) { mgr.MultiplayInfinityDash(core); n++; }
                        MelonLogger.Msg($"King Chance: unlimited dash for {n} prince(s), 60 s.");
                        break;
                    case Chance.itemConfiscation:
                        foreach (var core in Cores(mgr))
                        {
                            if (core.multiItemType == Reward.none) continue;
                            mgr.MultiplaySetItem(core, Reward.none);
                            n++;
                        }
                        MelonLogger.Msg($"King Chance: {n} freebie(s) confiscated.");
                        break;
                    case Chance.kingBox:
                        // "Received a King Chest!": every prince gets what rolling one up gives (the game's own reward
                        // step, so the draw, the display for you and the custom match rules all apply).
                        var bb = mgr.multiplayBigBattle;
                        if (bb == null) break;
                        foreach (var core in Cores(mgr)) { bb.MultiplayGetKingBox(core); n++; }
                        MelonLogger.Msg($"King Chance: a King Chest for {n} prince(s).");
                        break;
                }
            }
            catch (Exception e) { MelonLogger.Warning($"King Chance effect ({type}): {e.Message}"); }
        }

        private static List<MainGameCore> Cores(MultiplayGameManager mgr)
        {
            var list = new List<MainGameCore>();
            var it = mgr.dicPlayerCore.GetEnumerator();
            while (it.MoveNext()) if (it.Current.Value != null) list.Add(it.Current.Value);
            return list;
        }
    }
}
