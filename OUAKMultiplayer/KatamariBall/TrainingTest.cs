using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Il2Cpp;
using Il2CppApp.KatamariSin;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;
using OUAKModAPI;

namespace OUAKMultiplayer
{
    /// <summary>
    /// Launch test for multiplayer maps, armed by UserData/OUAKMultiplayer/trainingtest.flag holding internal map
    /// names (comma separated, e.g. "Stage_ONTraining,Stage_ONRace1"). Each game launch tests the first map and
    /// writes the rest back: at the title screen it backs the saves up, loads the slot the title screen marks as
    /// last used the way its "continue" does, starts the map through the Levels tab's path, logs PASS once the
    /// round runs (FAIL if it doesn't), lets it play for 15 s, then cuts the round's time limit short and checks
    /// that the round ends and reaches the results before quitting. TrainingTrace logs the steps.
    /// </summary>
    internal static class TrainingTest
    {
        private enum Phase { Title, Ship, Start, Wait, Play, Finish }

        private static string FlagPath => Path.Combine(MelonEnvironment.UserDataDirectory, "OUAKMultiplayer", "trainingtest.flag");

        public static bool Armed { get; private set; }
        private static string _map;
        private static Phase _phase;
        private static float _at, _deadline, _resultsAt = -1f;
        private static string _lastState;
        private static bool _kingChance;
        private static readonly List<string> _kbFreebies = new();
        private static int _kcStep, _tpStep, _kcAllStep;
        private static bool _kcAll, _kcQuarter, _resultsShot;
        private static int _kcqStep, _kcqSeen, _kcqHappened;
        private static float _kcqCaptureAt = -1f, _kcqEndBy;
        private static readonly MultiplaySetting.KingChanceType[] AllChances =
        {
            MultiplaySetting.KingChanceType.monoDrop, MultiplaySetting.KingChanceType.itemDrop, MultiplaySetting.KingChanceType.kingBox,
            MultiplaySetting.KingChanceType.itemConfiscation, MultiplaySetting.KingChanceType.bonusArea, MultiplaySetting.KingChanceType.unexploded,
        };

        public static void CheckFlag()
        {
            if (!File.Exists(FlagPath)) return;
            var maps = File.ReadAllText(FlagPath).Split(new[] { ',', ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            // Option words apply to this launch only; only the map names after the first are written back.
            var rest = maps.Where(m => m.StartsWith("Stage_", StringComparison.OrdinalIgnoreCase)).Skip(1).ToList();
            try
            {
                if (rest.Count == 0) File.Delete(FlagPath);
                else File.WriteAllText(FlagPath, string.Join(",", rest));
            }
            catch (Exception e) { MelonLogger.Warning($"[TrainingTest] flag: {e.Message}"); }
            // "kingchance" in the flag: watch the King Chance chain during the round (KingChanceProbe).
            _kingChance = maps.RemoveAll(m => m.Equals("kingchance", StringComparison.OrdinalIgnoreCase)) > 0;
            // "kcall": fire each King Chance type in turn and log what it does (KingChances / KingChanceProbe).
            _kcAll = maps.RemoveAll(m => m.Equals("kcall", StringComparison.OrdinalIgnoreCase)) > 0;
            // "kcquarter": the timed King Chances (MatchExtras) in a shortened match, each one's time and message measured.
            _kcQuarter = maps.RemoveAll(m => m.Equals("kcquarter", StringComparison.OrdinalIgnoreCase)) > 0;
            if (_kcAll) _kingChance = true;
            // The King Box and kcall tests fire (or count) King Chances themselves: no timed ones in their launch.
            if (_kingChance) MatchExtras.Suspended = true;
            if (_kcAll || _kcQuarter)
            {
                MatchExtras.KingChancesOn = true;
                if (!GamePatches.KingChances.Ready) MelonLogger.Msg("[TrainingTest] King Chances patch: " + GamePatches.KingChances.Error);
            }
            if (_kingChance || _kcQuarter) KingChanceProbe.Install();
            // What King Boxes hand out as freebies with no reward type (Smoke, Hoop, Dice): logged as it happens.
            BattleItemPools.KingBoxGaveFreebie += (core, item) =>
            {
                string who = core != null && GameRefs.Main?.GetCore() != null && core.Pointer == GameRefs.Main.GetCore().Pointer ? "you" : "a CPU";
                _kbFreebies.Add($"{item} to {who}");
                MelonLogger.Msg($"[TrainingTest] a King Box gave {item} to {who}");
            };
            // "rules:off" / "rules:some" / "rules:none" / "rules:kbfree": custom match rules for this launch only (never saved):
            // every freebie off, only Basin, Rocket and Crown allowed, custom rules off, or King Boxes giving only Smoke, Hoop
            // and both Dice.
            string rules = maps.FirstOrDefault(m => m.StartsWith("rules:", StringComparison.OrdinalIgnoreCase));
            if (rules != null)
            {
                maps.Remove(rules);
                CustomMatch.UseTestRules(rules.Substring(6).ToLowerInvariant());
            }
            if (maps.Count == 0) return;
            if (maps[0] == "Stage_ONTarget1") TargetProbe.Install();
            _map = maps[0];
            Armed = true;
            TrainingTrace.AfterStart = 100f;
            MelonLogger.Msg($"[TrainingTest] armed for {_map}");
        }

        public static void Tick()
        {
            if (!Armed || Time.unscaledTime < _at) return;
            try { Advance(); }
            catch (Exception e) { Finish($"FAIL: {e.Message}"); }
        }

        private static void Advance()
        {
            float now = Time.unscaledTime;
            switch (_phase)
            {
                case Phase.Title:
                    var ctrl = GameRefs.SaveCtrl;
                    if (SceneLoader.ActiveScene != "SelectData" || GameRefs.Global == null || GameRefs.Save == null ||
                        ctrl == null || !ctrl.ActionWait() || ctrl.GameData?._SystemData == null)
                    {
                        _at = now + 1f;
                        return;
                    }
                    LoadLastUsedSlot(ctrl);
                    _phase = Phase.Ship;
                    _deadline = now + 60f;
                    _at = now + 1f;
                    return;

                case Phase.Ship:
                    if (SceneLoader.ActiveScene != "OujiShip" || !GameRefs.InGameSession)
                    {
                        if (now > _deadline) throw new InvalidOperationException($"the save never reached the Prince's Ship (scene {SceneLoader.ActiveScene})");
                        _at = now + 0.5f;
                        return;
                    }
                    MelonLogger.Msg($"[TrainingTest] on the ship with slot {GameRefs.SaveCtrl.CurrentUseSlot + 1}");
                    _phase = Phase.Start;
                    _at = now + 5f;
                    return;

                case Phase.Start:
                    var entry = StageCatalog.Entries.FirstOrDefault(e => e.Kind == MapKind.Multiplayer && e.Internal == _map)
                                ?? throw new InvalidOperationException($"there's no multiplayer map called {_map}");
                    MelonLogger.Msg($"[TrainingTest] {MatchExtras.Launch(entry)}");
                    _phase = Phase.Wait;
                    _deadline = now + 100f;
                    _at = now + 1f;
                    return;

                case Phase.Wait:
                    if (TrainingTrace.RoundStarted)
                    {
                        MelonLogger.Msg($"[TrainingTest] PASS: {_map} is running");
                        try
                        {
                            var g = GameRefs.Main; var c = g?.GetCore();
                            MelonLogger.Msg($"[TrainingTest] start size: katamari {g?.KatamariSize}, battle size {c?.multiCoreSize} (base {c?.multiCoreSizeBase}), map {_map}");
                        }
                        catch (Exception e) { MelonLogger.Msg("[TrainingTest] start size: " + e.Message); }
                        _phase = Phase.Play;
                        _at = now + 15f;
                        return;
                    }
                    if (now > _deadline || (!TrainingTrace.Active && now > _deadline - 95f))
                        throw new InvalidOperationException($"{_map} never started. Last state: {TrainingTrace.LastSnapshot} | status: {ModTasks.Status}");
                    _at = now + 0.5f;
                    return;

                case Phase.Play:
                    if (_map == "Stage_ONTarget1" && _tpStep < 6)
                    {
                        // Target round: what the map holds, the counters, a pickup of ours, the HUD, then 20 s of CPU play.
                        switch (_tpStep++)
                        {
                            case 0: TargetProbe.Survey(); KingChanceProbe.StageObjects(); _at = now + 1f; return;
                            case 1: MelonLogger.Msg("[TargetProbe] " + TargetProbe.Players()); MelonLogger.Msg("[TargetProbe] " + TargetProbe.HudPoints()); _at = now + 0.5f; return;
                            case 2:
                                // A crane (one of the round's targets, the game's own check): the points should go up by 10 per crane.
                                MelonLogger.Msg($"[TargetProbe] target points {GamePatches.TargetPoints}, {TargetRound.PointsPerTarget} per target; " +
                                                TargetProbe.RollUpNearest(m => TargetRound.IsTarget(m)));
                                _at = now + 1f; return;
                            case 3: MelonLogger.Msg("[TargetProbe] " + TargetProbe.Players()); MelonLogger.Msg("[TargetProbe] " + TargetProbe.HudPoints()); FrameCapture.Request("target_hud"); _at = now + 20f; return;
                            case 4: MelonLogger.Msg("[TargetProbe] " + TargetProbe.PickupSummary()); MelonLogger.Msg("[TargetProbe] " + TargetProbe.Players()); MelonLogger.Msg("[TargetProbe] " + TargetProbe.HudPoints()); _at = now + 0.5f; return;
                        }
                    }
                    if (_kcAll && _kcAllStep < 2 + AllChances.Length * 2)
                    {
                        // 0: state; 1: everyone holds Shoes + you open a King Box; then per type: fire, and 2.5 s later
                        // a picture + the state, 6 s before the next one (the message has to clear first).
                        int k = _kcAllStep++;
                        if (k == 0) { MelonLogger.Msg("[KingChance] before: " + KingChanceProbe.State()); KingChanceProbe.StageObjects(); MelonLogger.Msg("[KingChance] " + KingChanceProbe.KingBoxList()); KingChanceProbe.TextIds("SYS_263", "SYS_264", "SYS_265", "SYS_266", "SYS_267", "SYS_268", "SYS_269", "SYS_270", "TIPS_048", "TIPS_049", "TIPS_050", "TIPS_051", "TIPS_052", "TIPS_053"); KingChanceProbe.Texts("Multiplayer", "Katamari Ball", "KatamariBall"); _at = now + 1f; return; }
                        if (k == 1)
                        {
                            MelonLogger.Msg("[KingChance] " + KingChanceProbe.GiveEveryone(MultiplaySetting.KingBoxRewardType.shoes));
                            MelonLogger.Msg("[KingChance] " + KingChanceProbe.RollUpKingBox());
                            MelonLogger.Msg("[KingChance] ready: " + KingChanceProbe.State());
                            _at = now + 4f; return;
                        }
                        var type = AllChances[(k - 2) / 2];
                        if ((k - 2) % 2 == 0)
                        {
                            MelonLogger.Msg($"[KingChance] firing {type}: {KingChances.Fire(type)}");
                            _at = now + 2.5f; return;
                        }
                        FrameCapture.Request("kc_" + type);
                        MelonLogger.Msg($"[KingChance] after {type}: " + KingChanceProbe.State());
                        _at = now + 6f; return;
                    }
                    if (_kcQuarter && KcQuarter(now)) return;
                    if (_kingChance && !_kcAll && _kcStep < 4)
                    {
                        // 0: the table; 1: your prince rolls up a King Box; 2: a picture; 3: 40 s of CPU play, then the counts.
                        switch (_kcStep++)
                        {
                            case 0: MelonLogger.Msg("[KingChance] " + KingChanceProbe.Table()); KingChanceProbe.StageObjects(); MelonLogger.Msg("[KingChance] " + KingChanceProbe.KingBoxList()); _at = now + 1f; return;
                            case 1: MelonLogger.Msg("[KingChance] " + KingChanceProbe.RollUpKingBox()); _at = now + 1.5f; return;
                            case 2: FrameCapture.Request("kingchance"); MelonLogger.Msg("[KingChance] after it: " + KingChanceProbe.Table()); _at = now + 40f; return;
                            case 3:
                                MelonLogger.Msg($"[KingChance] after 40 s more: King Boxes rolled up {KingChanceProbe.KingBoxes}, rewards given (GetKingBox) {KingChanceProbe.RewardCalls}, " +
                                                $"reward displays {KingChanceProbe.DisplayCalls}, empty boxes (API pools) {BattleItemPools.EmptyKingBoxes}, King Box swaps {BattleItemPools.KingBoxSwaps}, item box swaps {BattleItemPools.ItemBoxSwaps}, pools [{string.Join(",", BattleItemPools.KingBox ?? new Il2CppApp.KatamariSin.MultiplaySetting.KingBoxRewardType[0])}] [{string.Join(",", BattleItemPools.ItemBox ?? new Il2CppApp.KatamariSin.eInstageItemType[0])}], " +
                                                $"King Chances {KingChanceProbe.Chances}, requests {KingChanceProbe.Requests}; {KingChanceProbe.Table()}; {KingChanceProbe.Buddies()}; " +
                                                $"King Box freebies pool [{string.Join(",", BattleItemPools.KingBoxFreebiePool)}], given {BattleItemPools.KingBoxFreebiesGiven} ({string.Join(", ", _kbFreebies)})");
                                break;
                        }
                    }
                    MelonLogger.Msg($"[TrainingTest] after 15 s of play: {TrainingTrace.Describe()}");
                    // The round ends once MultiplayGameManager.playTime reaches MainGameManager._startTime.
                    var mgr = SingletonMonoBehaviour<MultiplayGameManager>.instance;
                    var gm = GameRefs.Main ?? throw new InvalidOperationException("the stage manager is gone");
                    gm._startTime = mgr.playTime + 10f;
                    MelonLogger.Msg($"[TrainingTest] ending the round in 10 s (time limit {gm._startTime:0.0})");
                    _phase = Phase.Finish;
                    _deadline = now + 60f;
                    _at = now + 0.5f;
                    return;

                case Phase.Finish:
                    string state = TrainingTrace.StateName(SingletonMonoBehaviour<MultiplayGameManager>.instance?.stateMachine);
                    if (state != _lastState) MelonLogger.Msg($"[TrainingTest] state: {state}");
                    _lastState = state;
                    if (state == "ResultState" && _resultsAt < 0f) _resultsAt = now;
                    // The results screen a few seconds in (scores and bonuses counted up), as a picture.
                    if (_resultsAt >= 0f && !_resultsShot && now - _resultsAt > 4.5f)
                    {
                        _resultsShot = true;
                        FrameCapture.Request("results_" + _map);
                    }
                    if (_resultsAt >= 0f && now - _resultsAt > 6f)
                    {
                        Finish($"PASS: the round ended and showed the results. {TrainingTrace.Describe()}");
                        return;
                    }
                    if (now > _deadline) throw new InvalidOperationException($"the round didn't reach the results (state {state}). {TrainingTrace.Describe()}");
                    _at = now + 0.5f;
                    return;
            }
        }

        /// <summary>
        /// "kcquarter": the round is cut to 70 s from now, then the 4 timed King Chances are watched: when each was fired
        /// (round clock vs. due time), when the game showed it (KingChances.Happened) and its message label, with a
        /// picture 2 s after each. True while it still needs the round.
        /// </summary>
        private static bool KcQuarter(float now)
        {
            var mgr = SingletonMonoBehaviour<MultiplayGameManager>.instance;
            var gm = GameRefs.Main;
            if (mgr == null || gm == null) return false;
            if (_kcqStep == 0)
            {
                _kcqStep = 1;
                gm._startTime = mgr.playTime + 70f;
                float len = MatchExtras.RoundLength();
                MelonLogger.Msg($"[KingChance] timed test: round cut to {MatchExtras.Clock(len)} (clock now {MatchExtras.Clock(mgr.playTime)}); due at " +
                                string.Join(", ", Enumerable.Range(0, 4).Select(i => MatchExtras.Clock(MatchExtras.DueAt(i, len)))) +
                                $"; table [{string.Join(", ", KingChances.Table)}]; patch {GamePatches.KingChances}");
                _kcqSeen = KingChances.Next;
                _kcqHappened = KingChances.HappenedCount;
                _kcqEndBy = now + 90f;
                _at = now + 0.25f;
                return true;
            }
            if (KingChances.Next != _kcqSeen)
            {
                _kcqSeen = KingChances.Next;
                MelonLogger.Msg($"[KingChance] timed: {_kcqSeen} fired by clock {mgr.playTime:0.0} s ({string.Join(" | ", MatchExtras.FiredThisMatch)})");
                _kcqCaptureAt = now + 2f;
            }
            if (KingChances.HappenedCount != _kcqHappened)
            {
                _kcqHappened = KingChances.HappenedCount;
                string label = "";
                try { label = mgr.textMeshPlayerInfo?.text; } catch { }
                MelonLogger.Msg($"[KingChance] timed: shown at clock {mgr.playTime:0.0} s; message label \"{OneLine(label)}\"; " + KingChanceProbe.State());
            }
            if (_kcqCaptureAt > 0f && now >= _kcqCaptureAt)
            {
                _kcqCaptureAt = -1f;
                FrameCapture.Request($"kcq_{_kcqSeen}");
                string label = "";
                try { label = mgr.textMeshPlayerInfo?.text; } catch { }
                MelonLogger.Msg($"[KingChance] timed: picture kcq_{_kcqSeen}; info shown {mgr.objPlayerInfo?.activeSelf}, label \"{OneLine(label)}\"");
            }
            bool all = _kcqSeen >= 4 && _kcqHappened - 0 >= 4 && _kcqCaptureAt < 0f;
            if (all || now > _kcqEndBy || mgr.playTime >= gm._startTime - 3f)
            {
                MelonLogger.Msg($"[KingChance] timed result: {(_kcqSeen >= 4 ? "PASS" : "FAIL")}: {_kcqSeen} of 4 fired, shown {KingChances.HappenedCount}, " +
                                $"{string.Join(" | ", MatchExtras.FiredThisMatch)}");
                _kcQuarter = false;
                return false;
            }
            _at = now + 0.25f;
            return true;
        }

        private static string OneLine(string s) => (s ?? "").Replace('\n', '/').Replace('\r', ' ');

        /// <summary>SelectDataUIEnter.OnUpdate for a slot with a game in progress, which goes to the ship.</summary>
        private static void LoadLastUsedSlot(SaveDataController ctrl) => LoadLastUsedSlot(ctrl, "Training test", "[TrainingTest]");

        /// <summary>Backs the saves up, then loads the slot the title screen marks as last used, the way its "continue" does (to the ship).</summary>
        internal static void LoadLastUsedSlot(SaveDataController ctrl, string backupReason, string tag, int forceSlot = -1)
        {
            var data = ctrl.GameData;
            int slot = forceSlot >= 0 ? forceSlot : data._SystemData._LastUser;
            var users = data._UserData?._UserDatas;
            if (users == null || slot < 0 || slot >= users.Length) throw new InvalidOperationException($"no last-used slot ({slot})");
            var progress = users[slot]?._PriorityData;
            if (progress == null || progress._GameProgression <= 0) throw new InvalidOperationException($"slot {slot + 1} has no game in progress");

            BackupManager.Create(backupReason, $"made before the {backupReason.ToLowerInvariant()} loaded slot {slot + 1}");
            MelonLogger.Msg($"{tag} loading slot {slot + 1}");
            ctrl.SelectLoadDataToGameData(slot);
            var save = GameRefs.Save;
            save.SetUseDataLogoMonoGetFlg();
            OujiShipSponePointList.s_SponeIndex = OujiShipSponePointList.ElevatorSpone;
            save.FrontSceneSelectData = true;
            SingletonMonoBehaviour<EncorsSaveDataChecker>.Instance.CheckEncorsContentsData();
            GameRefs.Sound?.StopBGM(0.5f);
            Il2Cpp.SceneManager.LoadSceneAsync("OujiShip", LoadSceneMode.Single, true);
            MelonLogger.Msg($"{tag} loaded: selected={ctrl.SelectedData} slot={ctrl.CurrentUseSlot + 1}");
        }

        private static void Finish(string result)
        {
            Armed = false;
            MelonLogger.Msg($"[TrainingTest] {_map}: {result}");
            MelonLogger.Msg("[TrainingTest] DONE, quitting");
            Application.Quit();
        }
    }
}
