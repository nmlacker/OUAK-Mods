using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Il2CppApp.KatamariSin;
using MelonLoader;
using MelonLoader.Utils;
using OUAKModAPI;
using UnityEngine;
using eStage = Il2CppApp.KatamariSin.MainGameManager.eStage;

namespace OUAKModMenu
{
    /// <summary>
    /// Measures what each freebie really does in a story stage (UserData/OUAKModMenu/itemtest.flag): the game's
    /// battle item tables, each freebie's effect time and strength right after the game's own ActivateItemEffect,
    /// and whether the core's battle-item switches (Mushroom growth bonus, Shoes' infinite dash) work outside battles.
    /// Loads the last-used slot (after a backup) and As Large As Possible 1, logs [ItemTest] lines and quits.
    /// </summary>
    internal static class ItemTest
    {
        private static string FlagPath => Path.Combine(MelonEnvironment.UserDataDirectory, "OUAKModMenu", "itemtest.flag");
        private static bool _armed, _prevAuto;
        private static (string off, string swaps) _boxRulesSaved;
        private static List<MainGameItemBox> _boxes = new();
        private static string _stage = "Stage_Big1";
        private static int _step, _index, _picks;
        private static float _at, _deadline;
        private static readonly List<float> _gainBase = new(), _gainBonus = new();

        private static readonly eInstageItemType[] Items = Enum.GetValues(typeof(eInstageItemType)).Cast<eInstageItemType>()
            .Where(t => t != eInstageItemType.NoItem && t != eInstageItemType.InstageItemTypeMax).ToArray();

        public static void CheckFlag()
        {
            if (!File.Exists(FlagPath)) return;
            // The flag may name a story stage ("Stage_Greece"); As Large As Possible 1 otherwise.
            try { string t = File.ReadAllText(FlagPath).Trim(); if (t.StartsWith("Stage_")) _stage = t; } catch { }
            try { File.Delete(FlagPath); } catch { }
            _armed = true;
            MelonLogger.Msg("[ItemTest] armed");
        }

        public static void Tick()
        {
            if (!_armed || Time.unscaledTime < _at) return;
            try { Step(); }
            catch (Exception e) { Finish($"FAIL at step {_step}: {e}"); }
        }

        private static void Log(string s) => MelonLogger.Msg("[ItemTest] " + s);

        private static void Step()
        {
            float now = Time.unscaledTime;
            var gm = GameRefs.Main;
            switch (_step)
            {
                case 0:
                {
                    var ctrl = GameRefs.SaveCtrl;
                    if (SceneLoader.ActiveScene != "SelectData" || GameRefs.Global == null || GameRefs.Save == null ||
                        ctrl == null || !ctrl.ActionWait() || ctrl.GameData?._SystemData == null) { _at = now + 1f; return; }
                    LogTables();
                    TestSlot.LoadLastUsedSlot(ctrl, "Item test", "[ItemTest]");
                    _step = 1; _deadline = now + 60f; _at = now + 1f;
                    return;
                }
                case 1:
                {
                    if (SceneLoader.ActiveScene != "OujiShip" || !GameRefs.InGameSession)
                    {
                        if (now > _deadline) throw new InvalidOperationException("the save never reached the ship");
                        _at = now + 1f; return;
                    }
                    _prevAuto = StageCheats.AutoFreebie;
                    StageCheats.AutoFreebie = false;
                    var entry = StageCatalog.Entries.First(e => e.Kind == MapKind.Stage && e.Internal == _stage);
                    Log(SceneLoader.LoadStage(entry));
                    _step = 2; _deadline = now + 90f; _at = now + 1f;
                    return;
                }
                case 2:
                    // Wait for control (closing a one-time tip the way the game does, once every few seconds).
                    if (gm != null && gm.MainUI != null && gm.MainUI.IsTipsActive()) { gm.MainUI.DeactivateTipsUI(); _at = now + 3f; return; }
                    // The King's opening talk waits for a button, so don't wait for control: the player existing is enough.
                    if (gm == null || !gm.PlayerInitiated || gm.GetCore() == null)
                    {
                        if (now > _deadline) throw new InvalidOperationException("the player never spawned");
                        _at = now + 0.5f; return;
                    }
                    if (_spawnedAt <= 0f) { _spawnedAt = now; _at = now + 4f; return; }
                    Log($"story start size {gm.KatamariSize} ({KatamariState_Text(gm.KatamariSize)}), dash: max {gm.GetCore().MaxOujiDashTime} s, battle cool time {gm.GetCore().MultiDashCoolTime} s");
                    LogIcons(gm);
                    Log($"in the stage; multiplayer {GameRefs.Global?.isMultiPlay}; base values: drink time {gm.DrinkItem_EffectTime} x{gm.DrinkItem_SpeedMultiplier}, " +
                        $"magnet time {gm.MagnetItem_EffectTime} range x{gm.MagnetItem_RangeMultiplier} (plus x{gm.MagnetPlusItem_RangeMultiplier}), pickel time {gm.PickelItem_EffectTime} " +
                        $"x{gm.PickelItem_SpeedMultiplier}, sonar time {gm.SonarItem_EffectTime}, timer time {gm.TimerItem_EffectTime}");
                    _step = 3; _index = 0; _at = now + 1f;
                    return;
                case 3:
                    // Each freebie: put it in the slot, run the game's own activation, then read what it set.
                    if (_index >= Items.Length) { _step = 5; _at = now + 0.5f; return; }
                    if (gm.MainUI != null && gm.MainUI.IsTipsActive()) { gm.MainUI.DeactivateTipsUI(); _at = now + 2f; return; }
                    {
                        var item = Items[_index];
                        gm.EndItemEffect(true);
                        gm.SetInventoryItem(item, false, true);
                        float speedBefore = gm.GetCore().NowMaxSpeed;
                        gm.ActivateItemEffect();
                        _at = now + 0.3f;
                        _step = 4;
                        _speedBefore = speedBefore;
                    }
                    return;
                case 4:
                {
                    var item = Items[_index];
                    var core = gm.GetCore();
                    var b = BaseOf(item);
                    string mult = "";
                    try { mult = $", multiplier({b}) {gm.GetItemMultiplier(b):0.##}"; } catch { }
                    Log($"{item}: effect time {gm._itemEffectTimer:0.##} s, slot now {gm._nowInventoryItem}, in effect as itself {gm.IsTargetItemInEffect(item)}, " +
                        $"as {b} {gm.IsTargetItemInEffect(b)}{mult}, max speed {_speedBefore:0.##} -> {core.NowMaxSpeed:0.##}");
                    gm.EndItemEffect(true);
                    _index++;
                    _step = 3; _at = now + 1.2f;
                    return;
                }
                case 5:
                {
                    // The rebuilt freebies, used the game's way (its ActivateItemEffect; FreebieEffects starts the effect after).
                    StoryFreebies.StopAll();
                    var core = gm.GetCore();
                    float speed = core.NowMaxSpeed;
                    gm.EndItemEffect(true);
                    gm.SetInventoryItem(eInstageItemType.Crown, false, true);
                    gm.ActivateItemEffect();
                    Log($"Crown used: max speed {speed:0.###} -> {core.NowMaxSpeed:0.###} (rebuilt: x1.5), running: {StoryFreebies.Describe()}");
                    StoryFreebies.StopAll();
                    Log($"Crown stopped: max speed {core.NowMaxSpeed:0.###}");
                    // Freebies that do nothing here are refused.
                    string refusal;
                    try { refusal = StageControl.GiveFreebie(eInstageItemType.Hoop); } catch (InvalidOperationException e) { refusal = "refused: " + e.Message; }
                    Log($"give Hoop: {refusal}; give Spider: {Try(() => StageControl.GiveFreebie(eInstageItemType.Spider))}; give Mushroom: {Try(() => StageControl.GiveFreebie(eInstageItemType.Mushroom))}");
                    // The menu gives the cosmetic-only ones anyway (StageControl.GiveFreebie(item, evenIfItDoesNothing: true)):
                    // each one given, then used the game's way.
                    var cosmetic = new List<string>();
                    foreach (var c in StageCheats.Freebies.Where(StageCheats.IsCosmetic))
                    {
                        gm.EndItemEffect(true);
                        string gave = Try(() => StageCheats.GiveFreebie(c));
                        var held = gm._nowInventoryItem;
                        gm.ActivateItemEffect();
                        cosmetic.Add($"{c}: {gave} -> held {held}, after use slot {gm._nowInventoryItem} timer {gm._itemEffectTimer:0.#}");
                    }
                    gm.EndItemEffect(true);
                    Log($"menu gives the cosmetic-only freebies ({cosmetic.Count}): {string.Join("; ", cosmetic)}");
                    Log($"usable in story stages: {string.Join(", ", FreebieRules.Usable().Select(StageControl.Pretty))}");
                    Log($"usable in battles: {string.Join(", ", FreebieRules.Usable(true).Select(StageControl.Pretty))}");
                    // The game's Random, 12 times (story stage: Drink..Timer and their Specials).
                    var rolled = new List<string>();
                    for (int k = 0; k < 12; k++)
                    {
                        gm.EndItemEffect(true);
                        gm.SetInventoryItem(eInstageItemType.NoItem, false, true);
                        gm.GetRandomItem();
                        rolled.Add(gm._nowInventoryItem.ToString());
                    }
                    Log($"Random rolled: {string.Join(", ", rolled)} (drawn by the mod {RandomFreebie.Draws}x)");
                    gm.EndItemEffect(true);
                    gm.SetInventoryItem(eInstageItemType.NoItem, false, true);
                    // Shoes: tired out first, then used the game's way; dash for a second and see the stamina stay put.
                    core._oujiDashElapsedTimer = core.MaxOujiDashTime + 0.5f;
                    core._oujiDashExhaustTimer = 3f;
                    float el = core._oujiDashElapsedTimer, ex = core._oujiDashExhaustTimer;
                    gm.SetInventoryItem(eInstageItemType.Shoes, false, true);
                    gm.ActivateItemEffect();
                    Log($"Shoes used: stamina used {el:0.##} -> {core._oujiDashElapsedTimer:0.##} s, tired {ex:0.##} -> {core._oujiDashExhaustTimer:0.##} s, " +
                        $"unlimited-dash switch {GameRefs.Global._debugInifiniteDash}, running: {StoryFreebies.Describe()}");
                    core._oujiDashTimer = 5f;   // dashing (the story dash timer counts the stamina while this is above 0)
                    _el0 = core._oujiDashElapsedTimer;
                    _step = 6; _at = now + 1f;
                    return;
                }
                case 6:
                {
                    var core = gm.GetCore();
                    Log($"after 1 s of dashing with Shoes: stamina used {_el0:0.##} -> {core._oujiDashElapsedTimer:0.##} s (should stay), dash timer {core._oujiDashTimer:0.##}");
                    StoryFreebies.StopAll();
                    core._oujiDashElapsedTimer = 0f;
                    core._oujiDashTimer = 5f;
                    Log($"Shoes stopped: unlimited-dash switch {GameRefs.Global._debugInifiniteDash}");
                    _step = 7; _at = now + 1f;
                    return;
                }
                case 7:
                {
                    var core = gm.GetCore();
                    Log($"after 1 s of dashing without Shoes: stamina used 0 -> {core._oujiDashElapsedTimer:0.##} s (should fill)");
                    core._oujiDashTimer = 0f;
                    core._oujiDashElapsedTimer = 0f;
                    // Mushroom and Special Mushroom: one-off growth.
                    foreach (var m in new[] { eInstageItemType.Mushroom, eInstageItemType.SP_Mushroom })
                    {
                        int before = gm.KatamariSize;
                        gm.EndItemEffect(true);
                        gm.SetInventoryItem(m, false, true);
                        gm.ActivateItemEffect();
                        Log($"{m} used: size {before} -> {gm.KatamariSize} (+{gm.KatamariSize - before}); {StoryFreebies.LastMushroom}");
                    }
                    // Ask for every Special icon; they're looked up in Update.
                    foreach (var i in FreebieRules.Usable()) GameSprites.Freebie(i);
                    _step = 8; _at = now + 3.5f;
                    return;
                }
                case 8:
                {
                    var found = new List<string>();
                    var missing = new List<string>();
                    foreach (var i in StageControl.Freebies)
                        (GameSprites.Freebie(i) != null ? found : missing).Add(i.ToString());
                    Log($"icons found: {string.Join(", ", found)}; none: {string.Join(", ", missing)}");
                    _step = 20; _at = now + 0.5f;
                    return;
                }
                case 20:
                {
                    // Item boxes tab rules (BoxRules -> the API's ItemBoxes): the user's saved rules put aside first.
                    _boxRulesSaved = BoxRules.Snapshot();
                    _boxes = ItemBoxes.All().OrderBy(b => b.name).ToList();
                    Log($"boxes: {_boxes.Count}, saved rules: off '{_boxRulesSaved.off}', swaps '{_boxRulesSaved.swaps}'; {string.Join("; ", _boxes.Select(ItemBoxes.Describe))}");
                    BoxRules.Reset();
                    BoxRules.SetSwap(eInstageItemType.Magnet, eInstageItemType.Shoes);
                    BoxRules.SetEnabled(eInstageItemType.Pickel, false);
                    Log($"menu rules now: {BoxRules.Summary()}; {string.Join("; ", _boxes.Select(ItemBoxes.Describe))}");
                    var magnet = _boxes.FirstOrDefault(b => ItemBoxes.GameContents(b).Contains(eInstageItemType.Magnet));
                    var cam = Camera.main;
                    if (magnet != null && cam != null)
                    {
                        var c = gm.GetCore().transform.position;
                        float d = Vector3.Distance(cam.transform.position, c);
                        magnet.transform.position = c + cam.transform.right * d * 0.45f - cam.transform.up * d * 0.2f;
                    }
                    _step = 21; _at = now + 1.5f;
                    return;
                }
                case 21:
                    FrameCapture.Request("boxes_swapped_view");
                    _step = 22; _at = now + 1.2f;
                    return;
                case 22:
                {
                    var magnet = _boxes.FirstOrDefault(b => ItemBoxes.GameContents(b).Contains(eInstageItemType.Magnet));
                    var pickel = _boxes.FirstOrDefault(b => ItemBoxes.GameContents(b).SequenceEqual(new[] { eInstageItemType.Pickel }));
                    gm.EndItemEffect(true);
                    gm.SetInventoryItem(eInstageItemType.NoItem, false, true);
                    if (magnet != null) magnet.Collected();   // the game's call when you roll it up
                    Log($"rolled up the Magnet box ({magnet?.name ?? "none"}): slot now {gm._nowInventoryItem}; Pickel-only box {(pickel == null ? "none in this stage" : pickel.name + " shown " + pickel.gameObject.activeSelf)}");
                    gm.EndItemEffect(true);
                    gm.SetInventoryItem(eInstageItemType.NoItem, false, true);
                    ModMenu.CurrentTab = "boxes";
                    ModMenu.Open();
                    _step = 23; _at = now + 1.5f;
                    return;
                }
                case 23:
                    FrameCapture.Request("boxes_tab_in_stage");
                    _step = 24; _at = now + 1.2f;
                    return;
                case 24:
                    BoxRules.Restore(_boxRulesSaved);
                    Log($"rules put back: {BoxRules.Summary()}; {string.Join("; ", _boxes.Select(ItemBoxes.Describe))}");
                    // A picture of the Freebies tab inside the stage (both grids: working, cosmetic only).
                    ModMenu.CurrentTab = "freebies";
                    _step = 9; _at = now + 1.5f;
                    return;
                case 9:
                    FrameCapture.Request("freebies_all_in_stage");
                    _step = 10; _at = now + 1.5f;
                    return;
                case 10:
                    ModMenu.Close();
                    Finish("DONE");
                    return;
            }
        }

        private static string Try(Func<string> f)
        {
            try { return f(); } catch (InvalidOperationException e) { return "refused: " + e.Message; }
        }

        private static float _speedBefore, _spawnedAt, _el0;

        private static string KatamariState_Text(int size) => $"{size / 10f:0.#} mm";

        private static float Avg(List<float> l) => l.Count == 0 ? -1f : l.Average();

        private static eInstageItemType BaseOf(eInstageItemType t)
        {
            int v = (int)t;
            if (v >= 16 && v <= 20) return (eInstageItemType)(v - 15);
            if (v >= 21 && v <= 26) return v == 25 ? eInstageItemType.Crown : v == 26 ? eInstageItemType.Dice : (eInstageItemType)(v - 15);
            return t;
        }

        /// <summary>Rolls the nearest object the katamari can take onto it (the game's own GetTargetMono). Returns size gained / object size.</summary>
        private static float Pickup(MainGameManager gm)
        {
            var core = gm.GetCore();
            var c = core.transform.position;
            int size = gm.KatamariSize;
            MainGameMonoBase best = null;
            float bestSq = float.MaxValue;
            foreach (var m in Resources.FindObjectsOfTypeAll<MainGameMonoBase>())
            {
                try
                {
                    if (m == null || !m.gameObject.activeInHierarchy) continue;
                    if (m.transform.IsChildOf(core.transform)) continue;
                    int need = gm.GetMonoRequireSize(m.MonoID);
                    if (need <= 0 || need > size) continue;
                    if (m.GetComponentsInChildren<MainGameMonoBase>(true).Length > 1) continue;
                    float sq = (m.transform.position - c).sqrMagnitude;
                    if (sq < bestSq) { bestSq = sq; best = m; }
                }
                catch { }
            }
            if (best == null) { Log("no object to pick up"); return -1f; }
            int needSize = gm.GetMonoRequireSize(best.MonoID);
            best.transform.position = c + Vector3.up * core.KatamariRadius;
            int before = gm.KatamariSize;
            core.GetTargetMono(best);
            int after = gm.KatamariSize;
            return needSize > 0 ? (after - before) / (float)needSize : -1f;
        }

        private static void LogTables()
        {
            try
            {
                var toReward = MultiplaySetting.dicKingBoxItemTypeToRewardType;
                var parts = new List<string>();
                if (toReward != null) foreach (var kv in toReward) parts.Add($"{kv.Key}->{kv.Value}");
                Log("battle items (freebie -> King Box reward): " + string.Join(", ", parts));
                var toItem = MultiplaySetting.dicKingBoxRewardConvert;
                parts.Clear();
                if (toItem != null) foreach (var kv in toItem) parts.Add($"{kv.Key}->{kv.Value}");
                Log("King Box rewards (reward -> freebie): " + string.Join(", ", parts));
                foreach (var (name, dict) in new[] { ("mushroom base size (dicBattleForceWarpCoreSize)", MultiplaySetting.dicBattleForceWarpCoreSize),
                             ("area fixed object size min", MultiplaySetting.dicBattleAreaFixedMonoSizeMin), ("area fixed object size max", MultiplaySetting.dicBattleAreaFixedMonoSizeMax) })
                {
                    parts.Clear();
                    if (dict != null) foreach (var kv in dict) parts.Add($"{kv.Key}={kv.Value}");
                    Log($"{name}: {string.Join(", ", parts)}");
                }
            }
            catch (Exception e) { Log("tables: " + e.Message); }
            LogResearch();
        }

        /// <summary>King Chance / King Box texts as the game shows them, and the stages' start sizes (StageData).</summary>
        private static void LogResearch()
        {
            var g = GameRefs.Global;
            string Text(string id)
            {
                if (string.IsNullOrEmpty(id) || g == null) return id;
                foreach (var t in new[] { g.SystemTable, g.StringTable })
                {
                    try { string r = t?.GetText(id); if (!string.IsNullOrEmpty(r) && r != id) return r.Replace('\r', ' ').Replace('\n', ' '); } catch { }
                }
                return id;
            }
            try
            {
                Log("bigBattleKingChanceDashTime = " + MultiplaySetting.bigBattleKingChanceDashTime);
                var msg = MultiplaySetting.bigBattleKingChanceMessage;
                if (msg != null) for (int i = 0; i < msg.Length; i++) Log($"King Chance message {i} ({(MultiplaySetting.KingChanceType)i}): {msg[i]} = {Text(msg[i])}");
                var rt = MultiplaySetting.KingBoxRewardText; var re = MultiplaySetting.KingBoxRewardExplain;
                if (rt != null) for (int i = 0; i < rt.Length; i++)
                    Log($"King Box reward {i} ({(MultiplaySetting.KingBoxRewardType)i}): {Text(rt[i])} | {(re != null && i < re.Length ? Text(re[i]) : "")}");
            }
            catch (Exception e) { Log("king chance texts: " + e.Message); }
            try
            {
                var data = g?.AllStageData ?? AssetUtil.Load<StageData>("StageDataAsset");
                var parts = new List<string>();
                foreach (eStage st in Enum.GetValues(typeof(eStage)))
                {
                    if ((int)st >= (data?.list?.Count ?? 0)) continue;
                    var prm = data.list[(int)st];
                    if (prm != null) parts.Add($"{st}={prm.StartSize}");
                }
                Log("start sizes (StageData): " + string.Join(", ", parts));
            }
            catch (Exception e) { Log("start sizes: " + e.Message); }
        }

        /// <summary>The story HUD's freebie icons (UIIngameItem.IconSprites / OverrideIcons).</summary>
        private static void LogIcons(MainGameManager gm)
        {
            try
            {
                var ui = gm.MainUI?.ItemUI;
                var icons = ui?.IconSprites;
                var parts = new List<string>();
                if (icons != null) for (int i = 0; i < icons.Length; i++) parts.Add($"{i}:{(icons[i] != null ? icons[i].name : "-")}");
                Log($"HUD icons ({icons?.Length ?? 0}): {string.Join(", ", parts)}");
                var ov = ui?.OverrideIcons;
                parts.Clear();
                if (ov != null) for (int i = 0; i < ov.Length; i++) parts.Add($"{i}:{(ov[i] != null ? ov[i].name : "-")}");
                Log($"HUD override icons: {string.Join(", ", parts)}");
            }
            catch (Exception e) { Log("icons: " + e.Message); }
        }

        private static void Finish(string result)
        {
            _armed = false;
            StageCheats.AutoFreebie = _prevAuto;
            Log(result);
            ModTasks.Later(Application.Quit, 1.5f);
        }
    }
}
