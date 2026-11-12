using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using Il2CppApp.KatamariSin;
using MelonLoader;
using OUAKModAPI;
using UnityEngine;

namespace OUAKMultiplayer
{
    /// <summary>
    /// Test-only (trainingtest.flag with "kingchance"): shows whether King Chances run in an offline battle. Logs every
    /// call of the King Chance chain (MultiplayBigBattle.MultiplayGetKingBox -> network event -> FireEvent ->
    /// GameState -> MultiplayGameManager.SetKingChanceBonus), the map's King Chance table, and makes your prince roll up
    /// a King Box once.
    /// </summary>
    internal static class KingChanceProbe
    {
        private static HarmonyLib.Harmony _h;
        public static int KingBoxes, Chances, Requests, Events;
        private static readonly List<string> _log = new();

        public static void Install()
        {
            if (_h != null) return;
            _h = new HarmonyLib.Harmony("ouak.multiplayer.kingchanceprobe");
            Hook(AccessTools.Method(typeof(MultiplayBigBattle), nameof(MultiplayBigBattle.MultiplayGetKingBox)), nameof(OnKingBox));
            Hook(AccessTools.Method(typeof(MultiplayGameManager), nameof(MultiplayGameManager.KingChanceProcess)), nameof(OnRequest));
            Hook(AccessTools.Method(typeof(MultiplayGameManager), nameof(MultiplayGameManager.SetKingChanceBonus)), nameof(OnChance));
            // Postfixes: they run even when the custom match rules skip the method, so the log shows what really happened.
            Hook(AccessTools.Method(typeof(MainGameCore), nameof(MainGameCore.GetKingBox)), nameof(AfterGetKingBox), post: true);
            Hook(AccessTools.Method(typeof(MultiplayGameManager), nameof(MultiplayGameManager.StartKingBoxProcess)), nameof(AfterKingBoxDisplay), post: true);
            MelonLogger.Msg("[KingChance] probe installed");
        }

        private static void Hook(MethodInfo m, string patch, bool post = false)
        {
            try
            {
                var hm = new HarmonyMethod(AccessTools.Method(typeof(KingChanceProbe), patch));
                if (post) _h.Patch(m, postfix: hm); else _h.Patch(m, prefix: hm);
            }
            catch (Exception e) { MelonLogger.Msg($"[KingChance] couldn't hook {m?.Name}: {e.Message}"); }
        }

        public static int RewardCalls, DisplayCalls;

        private static string Who(MainGameCore core) =>
            core == null ? "?" : core.Pointer == (GameRefs.Main?.GetCore()?.Pointer ?? IntPtr.Zero) ? "you" : "a CPU prince";

        private static void AfterGetKingBox(MainGameCore __instance, MultiplaySetting.KingBoxRewardType type)
        {
            RewardCalls++;
            MelonLogger.Msg($"[KingChance] GetKingBox({type}) ran for {Who(__instance)}; held item now {__instance?.multiItemType}; {Buddies()}");
        }

        private static void AfterKingBoxDisplay(MultiplayGameManager __instance, MultiplaySetting.KingBoxRewardType getType)
        {
            DisplayCalls++;
            MelonLogger.Msg($"[KingChance] StartKingBoxProcess({getType}): display requested {__instance?.isReqStartKingBoxProcess}, shows {__instance?.getKingRewardType}");
        }

        private static void OnKingBox(MainGameCore core)
        {
            KingBoxes++;
            MelonLogger.Msg($"[KingChance] King Box rolled up by core {(core == null ? "?" : core.Pointer == (GameRefs.Main?.GetCore()?.Pointer ?? IntPtr.Zero) ? "you" : "a CPU prince")} (t {Time.time:0.0})");
        }

        private static void OnRequest(MultiplaySetting.KingChanceType type)
        {
            Requests++;
            MelonLogger.Msg($"[KingChance] KingChanceProcess({type})");
        }

        private static void OnChance(MultiplaySetting.KingChanceType type)
        {
            Chances++;
            MelonLogger.Msg($"[KingChance] King Chance happens: {type} (t {Time.time:0.0})");
        }

        /// <summary>The map's King Chance table and where the battle is in it.</summary>
        public static string Table()
        {
            try
            {
                var mgr = SingletonMonoBehaviour<MultiplayGameManager>.instance;
                var bb = mgr?.multiplayBigBattle;
                if (bb == null) return "no big battle";
                var t = bb.kingChanceTable;
                var parts = new List<string>();
                if (t != null) for (int i = 0; i < t.Length; i++) parts.Add(t[i].ToString());
                return $"rule {bb.nowRule}, King Chance table [{string.Join(", ", parts)}], next {bb.nowKingChangeNo}";
            }
            catch (Exception e) { return "table: " + e.Message; }
        }

        /// <summary>
        /// The stage-data objects SetKingChanceBonus switches (MainGameManager.SetActiveStageDataObject finds them under
        /// _inStageData): monoDrop shows "mono/AddMono", itemDrop "mono/AddItems", bonusArea hides
        /// "KingChance/BonusAreaWall". Also lists the stage data's children two levels deep.
        /// </summary>
        public static void StageObjects()
        {
            try
            {
                var root = GameRefs.Main?._inStageData;
                if (root == null) { MelonLogger.Msg("[KingChance] no stage data"); return; }
                var t = root.transform;
                foreach (var path in new[] { "mono/AddMono", "mono/AddItems", "KingChance/BonusAreaWall", "KingChance" })
                {
                    var c = t.Find(path);
                    MelonLogger.Msg($"[KingChance] stage data '{path}': " + (c == null ? "MISSING" :
                        $"active {c.gameObject.activeSelf}, {c.childCount} children, {c.GetComponentsInChildren<MainGameMonoBase>(true).Length} objects"));
                }
                var parts = new List<string>();
                for (int i = 0; i < t.childCount; i++)
                {
                    var c = t.GetChild(i);
                    var sub = new List<string>();
                    for (int j = 0; j < c.childCount && j < 12; j++) sub.Add(c.GetChild(j).name + (c.GetChild(j).gameObject.activeSelf ? "" : "(off)"));
                    parts.Add($"{c.name}{(c.gameObject.activeSelf ? "" : "(off)")}[{string.Join(",", sub)}]");
                }
                MelonLogger.Msg($"[KingChance] stage data {root.gameObject.name}: {string.Join(" ", parts)}");
            }
            catch (Exception e) { MelonLogger.Msg("[KingChance] stage objects: " + e.Message); }
        }

        /// <summary>Game texts by id (every loaded TextCommonTable).</summary>
        public static void TextIds(params string[] ids)
        {
            try
            {
                var want = new HashSet<string>(ids);
                foreach (var table in Resources.FindObjectsOfTypeAll<TextCommonTable>())
                {
                    var list = table?.datas;
                    if (list == null) continue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var e = list[i];
                        if (e != null && want.Remove(e.id)) MelonLogger.Msg($"[Texts] {table.name}/{e.id}: {e.english?.Replace("\n", " / ")} | {e.japanese?.Replace("\n", " / ")}");
                    }
                }
                if (want.Count > 0) MelonLogger.Msg("[Texts] not loaded: " + string.Join(", ", want));
            }
            catch (Exception e) { MelonLogger.Msg("[Texts] " + e.Message); }
        }

        /// <summary>What a King Box can give (MultiplaySetting.kingBoxItemList, the list MultiplayGetKingBox draws from).</summary>
        public static string KingBoxList()
        {
            var list = MultiplaySetting.kingBoxItemList;
            if (list == null) return "no King Box list";
            var parts = new List<string>();
            for (int i = 0; i < list.Length; i++) parts.Add(list[i].ToString());
            var prob = MultiplaySetting.kingBoxItemProbability;
            var rows = new List<string>();
            if (prob != null)
                for (int r = 0; r < prob.Length; r++)
                {
                    var row = prob[r];
                    var cells = new List<string>();
                    if (row != null) for (int c = 0; c < row.Length; c++) cells.Add(row[c].ToString());
                    rows.Add("[" + string.Join(",", cells) + "]");
                }
            return $"King Box list [{string.Join(", ", parts)}]; weights {string.Join(" ", rows)}";
        }

        /// <summary>Game texts (every loaded TextCommonTable) whose English matches one of the words.</summary>
        public static void Texts(params string[] words)
        {
            try
            {
                int n = 0;
                var seen = new HashSet<string>();
                foreach (var table in Resources.FindObjectsOfTypeAll<TextCommonTable>())
                {
                    var list = table?.datas;
                    if (list == null) continue;
                    for (int i = 0; i < list.Count && n < 120; i++)
                    {
                        var e = list[i];
                        string en = e?.english;
                        if (string.IsNullOrEmpty(en) || !seen.Add(e.id)) continue;
                        if (!words.Any(w => en.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                        n++;
                        MelonLogger.Msg($"[Texts] {table.name}/{e.id}: {en.Replace("\n", " / ")}");
                    }
                }
                MelonLogger.Msg($"[Texts] {n} matches for {string.Join(", ", words)}");
            }
            catch (Exception e) { MelonLogger.Msg("[Texts] " + e.Message); }
        }

        /// <summary>What the King Chance effects change: stage objects, King Boxes on the map, held freebies, dash.</summary>
        public static string State()
        {
            try
            {
                var mgr = GameRefs.Multiplay;
                var bb = mgr?.multiplayBigBattle;
                if (bb == null) return "no big battle";
                var root = GameRefs.Main?._inStageData?.transform;
                string Obj(string p)
                {
                    var c = root?.Find(p);
                    return c == null ? "missing" : c.gameObject.activeSelf ? "on" : "off";
                }
                int vis = 0, all = 0;
                var tl = bb.listTreasure;
                if (tl != null) for (int i = 0; i < tl.Count; i++) { all++; if (tl[i] != null && tl[i].isVisible) vis++; }
                var items = new List<string>();
                var it = mgr.dicPlayerCore.GetEnumerator();
                while (it.MoveNext())
                {
                    var core = it.Current.Value;
                    if (core == null) continue;
                    var us = mgr.GetUserStatusFromCoreNo(core.multiCoreNo);
                    items.Add($"{Who(core)} {core.multiItemType} dash {us?.infinityDashTime:0.0}");
                }
                return $"AddMono {Obj("mono/AddMono")}, AddItems {Obj("mono/AddItems")}, BonusAreaWall {Obj("KingChance/BonusAreaWall")}; " +
                       $"King Boxes visible {vis}/{all}; messages queued {mgr.listInfomationText?.Count}, info shown {mgr.objPlayerInfo?.activeSelf}; request {mgr.isRequestKingChanceType}; " +
                       $"chances seen {Chances}; " + string.Join(", ", items);
            }
            catch (Exception e) { return "state: " + e.Message; }
        }

        /// <summary>Every prince's buddy counts: getBrotherNum (the results' "buddies" stat) and brotherNum (buddies now).</summary>
        public static string Buddies()
        {
            try
            {
                var mgr = GameRefs.Multiplay;
                if (mgr?.dicPlayerCore == null) return "no players";
                var parts = new List<string>();
                var it = mgr.dicPlayerCore.GetEnumerator();
                while (it.MoveNext())
                {
                    var core = it.Current.Value;
                    if (core == null) continue;
                    var us = mgr.GetUserStatusFromCoreNo(core.multiCoreNo);
                    parts.Add($"{Who(core)} buddies got {us?.getBrotherNum}, has {us?.brotherNum}, holds {core.multiItemType}");
                }
                return string.Join("; ", parts);
            }
            catch (Exception e) { return "buddies: " + e.Message; }
        }

        /// <summary>Gives every prince a freebie (the game's MultiplaySetItem), so a confiscation has something to take.</summary>
        public static string GiveEveryone(MultiplaySetting.KingBoxRewardType type)
        {
            var mgr = GameRefs.Multiplay;
            if (mgr == null) return "no manager";
            int n = 0;
            var it = mgr.dicPlayerCore.GetEnumerator();
            while (it.MoveNext()) if (it.Current.Value != null) { mgr.MultiplaySetItem(it.Current.Value, type); n++; }
            return $"gave {type} to {n} prince(s)";
        }

        /// <summary>Your prince rolls up a King Box (what touching one does: MultiplayBigBattle.MultiplayGetKingBox).</summary>
        public static string RollUpKingBox()
        {
            try
            {
                var mgr = SingletonMonoBehaviour<MultiplayGameManager>.instance;
                var bb = mgr?.multiplayBigBattle;
                var core = GameRefs.Main?.GetCore();
                if (bb == null || core == null) return "no big battle or no katamari";
                var before = core.multiItemType;
                int calls = RewardCalls;
                bb.MultiplayGetKingBox(core);
                return $"rolled up a King Box: held item {before} -> {core.multiItemType}, GetKingBox calls {RewardCalls - calls}, " +
                       $"empty boxes so far {BattleItemPools.EmptyKingBoxes}";
            }
            catch (Exception e) { return "King Box failed: " + e.Message; }
        }
    }
}
