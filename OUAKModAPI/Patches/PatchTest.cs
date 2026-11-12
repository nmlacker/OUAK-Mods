using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Il2Cpp;
using Il2CppApp.KatamariSin;
using MelonLoader.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;
using Chance = Il2CppApp.KatamariSin.MultiplaySetting.KingChanceType;
using Reward = Il2CppApp.KatamariSin.MultiplaySetting.KingBoxRewardType;

namespace OUAKModAPI
{
    /// <summary>
    /// Test-only: <c>UserData/OUAKModAPI/patchtest.flag</c> runs one in-game check of the patches per launch, then quits.
    /// Tokens: <c>target</c> (Target round + target points), <c>kingchance</c> (every King Chance type in a battle),
    /// <c>race</c> (Race round start to results), <c>freebies</c> (the story-stage freebie patches, with pictures),
    /// <c>battlemushroom</c> (both Mushrooms used in a battle),
    /// <c>texts</c> (game texts about freebies, at the title screen),
    /// <c>slot N</c> (save slot to load; default the title's last used). Makes a save backup first, like the menu's tests.
    /// Lines start with [PatchTest]; ends with "[PatchTest] DONE".
    /// </summary>
    internal static class PatchTest
    {
        private const string Me = "OUAK API patch test";
        private static readonly ModLog Log = ModLog.For("PatchTest");
        private static string FlagPath => Path.Combine(MelonEnvironment.UserDataDirectory, "OUAKModAPI", "patchtest.flag");

        private enum Phase { Title, Ship, Start, Wait, Run, Results, Pyramid, PyramidCheck, Done }
        private static int _pyrStep, _pyrScored, _scoredCount;

        private static bool _armed;
        private static string _what;
        private static bool _texts;
        private static int _slot = -1;
        private static Phase _phase;
        private static float _at, _deadline, _resultsAt = -1f;
        private static int _step;
        private static readonly List<string> _fails = new();

        internal static void CheckFlag()
        {
            if (!File.Exists(FlagPath)) return;
            string text = File.ReadAllText(FlagPath);
            try { File.Delete(FlagPath); } catch { }
            var t = text.Split(new[] { ',', ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.ToLowerInvariant()).ToList();
            _texts = t.Remove("texts");
            _ringsOnly = t.Remove("rings");
            int si = t.IndexOf("slot");
            if (si >= 0 && si + 1 < t.Count && int.TryParse(t[si + 1], out int n)) { _slot = n - 1; t.RemoveRange(si, 2); }
            _what = t.FirstOrDefault(x => x is "target" or "kingchance" or "race" or "freebies" or "battlemushroom" or "unused" or "unusedbattle" or "hud" or "battlebugs" or "racebox" or "dashstory" or "dashbattle");
            _armed = true;
            KingChances.TestOnlyBlockOthers = true;   // the tests fire their own King Chances (filter: false); other mods' would upset their timing
            TrainingTrace.AfterStart = 100f;
            Msg($"armed: {_what ?? "texts only"}{(_texts ? " + texts" : "")}");
        }

        private static void Msg(string s) => Log.Info("[PatchTest] " + s);

        private static void Check(string what, bool ok, string detail = "")
        {
            Msg($"{(ok ? "OK  " : "FAIL")} {what}{(detail != "" ? ": " + detail : "")}");
            if (!ok) _fails.Add(what);
        }

        internal static void Tick()
        {
            if (!_armed || Time.unscaledTime < _at) return;
            try { Advance(); }
            catch (Exception e) { Finish($"FAIL: {e.GetType().Name}: {e.Message}\n{e.StackTrace}"); }
        }

        private static void Advance()
        {
            float now = Time.unscaledTime;
            switch (_phase)
            {
                case Phase.Title:
                {
                    var ctrl = GameRefs.SaveCtrl;
                    if (SceneLoader.ActiveScene != "SelectData" || GameRefs.Global == null || GameRefs.Save == null ||
                        ctrl == null || !ctrl.ActionWait() || ctrl.GameData?._SystemData == null) { _at = now + 1f; return; }
                    Msg("patches at the title screen: " + string.Join("; ", GamePatches.All.Select(p => p.ToString())));
                    Check("every patch is in place", GamePatches.All.All(p => p.Ready), string.Join("; ", GamePatches.All.Select(p => p.ToString())));
                    if (_texts) DumpTexts();
                    if (_what == null) { Finish("texts dumped"); return; }
                    LoadSlot(ctrl);
                    _phase = Phase.Ship; _deadline = now + 60f; _at = now + 1f;
                    return;
                }
                case Phase.Ship:
                    if (SceneLoader.ActiveScene != "OujiShip" || !GameRefs.InGameSession)
                    {
                        if (now > _deadline) throw new InvalidOperationException($"never reached the ship ({SceneLoader.ActiveScene})");
                        _at = now + 0.5f; return;
                    }
                    _phase = Phase.Start; _at = now + 5f;
                    return;
                case Phase.Start:
                    StartRound();
                    _phase = Phase.Wait; _deadline = now + 100f; _at = now + 1f;
                    return;
                case Phase.Wait:
                    if (_what is "freebies" or "unused" or "hud" or "dashstory")
                    {
                        var g = GameRefs.Main;
                        if (g != null && g.MainUI != null && g.MainUI.IsTipsActive()) { g.MainUI.DeactivateTipsUI(); _at = now + 3f; return; }
                        // Skip the opening the game's own ways (fly-over, then the King's talk): the clock and item
                        // countdowns only run once the talk is over.
                        if (g != null && g.PlayerInitiated && !g.StartMessageDone)
                        {
                            try
                            {
                                if (g._isInDemoCamera) g.EndDemoCameraWithFade();
                                var ui = SingletonMonoBehaviour<GlobalUIController>.Instance;
                                if (ui != null && ui.CanSkipMessage) ui.SkipNowMessage();
                            }
                            catch (Exception e) { Msg("skip: " + e.Message); }
                            if (now > _deadline) throw new InvalidOperationException("the opening never ended");
                            _at = now + 1f;
                            return;
                        }
                        if (g != null && g.PlayerInitiated && g.GetCore() != null && Katamari.Playing)
                        {
                            Msg($"in {SceneLoader.ActiveScene}, size {g.KatamariSize}, playing (opening over)");
                            _phase = Phase.Run; _step = 0; _at = now + 4f;
                            return;
                        }
                        if (now > _deadline) throw new InvalidOperationException("the player never spawned");
                        _at = now + 0.5f;
                        return;
                    }
                    if (TrainingTrace.RoundStarted)
                    {
                        Msg($"round running: {TrainingTrace.Describe()}");
                        _phase = Phase.Run; _step = 0; _at = now + 3f;
                        return;
                    }
                    if (now > _deadline) throw new InvalidOperationException($"the round never started: {TrainingTrace.LastSnapshot}");
                    _at = now + 0.5f;
                    return;
                case Phase.Run:
                    float wait = _what switch { "target" => TargetStep(), "kingchance" => KingChanceStep(), "freebies" => FreebieStep(), "battlemushroom" => BattleMushroomStep(),
                        "unused" => UnusedStoryStep(), "unusedbattle" => UnusedBattleStep(), "hud" => HudStep(), "battlebugs" => BattleBugsStep(), "racebox" => RaceBoxStep(),
                        "dashstory" => DashStep(false), "dashbattle" => DashStep(true), _ => RaceStep() };
                    if (wait >= 0f) { _at = now + wait; return; }
                    if (_what is "freebies" or "battlemushroom" or "unused" or "unusedbattle" or "hud" or "battlebugs" or "racebox" or "dashstory" or "dashbattle") { _phase = Phase.Done; _at = now + 0.5f; return; }
                    // End the round now; the round ends when playTime reaches MainGameManager._startTime.
                    var gm = GameRefs.Main ?? throw new InvalidOperationException("the stage manager is gone");
                    gm._startTime = GameRefs.Multiplay.playTime + 3f;
                    Msg("ending the round in 3 s");
                    _phase = Phase.Results; _deadline = now + 60f; _at = now + 0.5f;
                    return;
                case Phase.Results:
                {
                    string state = TrainingTrace.StateName(GameRefs.Multiplay?.stateMachine);
                    if (state == "ResultState" && _resultsAt < 0f) { _resultsAt = now; Msg("results screen"); }
                    if (_resultsAt >= 0f && now - _resultsAt > 9f)
                    {
                        FrameCapture.Request($"patch_{_what}_results");
                        if (_what == "target")
                        {
                            // The user's report: crane points in a Pyramid match after a Target round. Play one now.
                            var pyr = StageCatalog.Entries.FirstOrDefault(e => e.Kind == MapKind.Multiplayer && e.Internal == "Stage_OnPyramid");
                            if (pyr != null)
                            {
                                Msg("now a Pyramid match: " + SceneLoader.Load(pyr));
                                _phase = Phase.Pyramid; _deadline = now + 120f; _at = now + 3f; _pyrStep = 0;
                                return;
                            }
                            Msg("no Pyramid map entry");
                        }
                        _phase = Phase.Done; _at = now + 1.5f;
                        return;
                    }
                    if (now > _deadline) throw new InvalidOperationException($"no results (state {state})");
                    _at = now + 0.5f;
                    return;
                }
                case Phase.Pyramid:
                {
                    var mgr = GameRefs.Multiplay;
                    if (_pyrStep == 0)
                    {
                        bool running = mgr != null && mgr.gimiSingleStage == GI_MI.GI_MI_ONLINE_PYRAMID && TrainingTrace.StateName(mgr.stateMachine) == "GameState"
                                       && GameRefs.Main != null && GameRefs.Main.GetCore() != null;
                        if (!running)
                        {
                            if (now > _deadline) { Check("Pyramid match started", false, TrainingTrace.Describe()); _phase = Phase.Done; }
                            _at = now + 1f; return;
                        }
                        _pyrStep = 1; _at = now + 3f; return;
                    }
                    int scoredBefore = _scoredCount;
                    Check("Pyramid after Target: Big Battle rule, not the Target mode", mgr.nowRule == NetworkBattleRule.bigBattle && !TargetRound.InRound,
                        $"rule {mgr.nowRule}, map {mgr.gimiSingleStage}, Target mode {TargetRound.InRound}");
                    Msg("you roll something up on Pyramid: " + Drop(_ => true, out var got));
                    _phase = Phase.PyramidCheck; _at = now + 2f; _pyrScored = scoredBefore;
                    return;
                }
                case Phase.PyramidCheck:
                    Check("Pyramid: no crane points", _scoredCount == _pyrScored, $"scored events {_pyrScored} -> {_scoredCount}; your points {Status(GameRefs.Main?.GetCore())?.hardPointNum}");
                    FrameCapture.Request("patch_pyramid");
                    _phase = Phase.Done; _at = now + 0.5f;
                    return;
                case Phase.Done:
                    Finish(_fails.Count == 0 ? "PASS" : "FAIL: " + string.Join(", ", _fails));
                    return;
            }
        }

        private static void StartRound()
        {
            switch (_what)
            {
                case "freebies":
                case "unused":
                case "hud":
                case "dashstory":
                {
                    var stage = StageCatalog.Entries.FirstOrDefault(e => e.Kind == MapKind.Stage && e.Internal == "Stage_Big1")
                                ?? throw new InvalidOperationException("no Stage_Big1");
                    Msg(SceneLoader.LoadStage(stage));
                    return;
                }
                case "target":
                    Check("Target mode listed", GameModes.Find("target") != null);
                    TargetRound.Scored += (core, targets, points) =>
                    {
                        _cpuTargets[core.multiCoreNo] = _cpuTargets.GetValueOrDefault(core.multiCoreNo) + targets;
                        _scoredCount++;
                        Msg($"scored: {Who(core)} +{points} for {targets} target(s); points now {Status(core)?.hardPointNum}");
                    };
                    Msg(GameModes.Start("target"));
                    return;
                case "race":
                case "racebox":
                    Check("Race mode listed", GameModes.Find("race") != null);
                    Msg(GameModes.Start("race"));
                    return;
                default:
                    KingChances.Happened += t => Msg($"King Chance happened: {t}");
                    var entry = StageCatalog.Entries.FirstOrDefault(e => e.Kind == MapKind.Multiplayer && e.Internal == "Stage_ONBattle1")
                                ?? throw new InvalidOperationException("no Battle map");
                    Msg(SceneLoader.Load(entry));
                    return;
            }
        }

        // ------------------------------------------------------------------ Target

        private static MainGameMonoBase _picked;

        private static float TargetStep()
        {
            var gm = GameRefs.Main;
            var me = gm?.GetCore();
            switch (_step++)
            {
                case 0:
                    Check("collect rule", TargetRound.InRound, $"rule {GameRefs.Multiplay?.nowRule}");
                    TargetSurvey();
                    SyncSurvey();
                    CpuWatch();
                    CraneProbe();
                    Msg("before: " + TargetState());
                    _pointsBefore = Status(me)?.hardPointNum ?? 0;
                    _countBefore = TargetRound.Count(me);
                    return 1f;
                case 1:
                case 3:
                    // Real roll-ups happen inside the prince's own update (the game counts them there), so the test
                    // drops an object into your katamari and lets the game pick it up.
                    Msg("a crane dropped into your katamari: " + Drop(m => TargetRound.IsTarget(m), out _picked));
                    return 2f;
                case 2:
                case 4:
                {
                    bool taken = _picked != null && _picked.transform.parent != _pickedParent;
                    Msg($"after the crane (taken {taken}): " + TargetState());
                    Msg(CraneChecks() + " | " + Touches());
                    int worth = TargetRound.TargetsIn(_picked);
                    int points = Status(me)?.hardPointNum ?? 0, count = TargetRound.Count(me);
                    if (_step == 5)
                    {
                        // The second drop is only informational (a CPU may be in the way).
                        Msg($"second crane: taken {taken}, count {_countBefore} -> {count}, points {_pointsBefore} -> {points}");
                    }
                    else if (taken)
                    {
                        Check("the game's counter went up by the crane's worth", count - _countBefore == worth, $"{_countBefore} -> {count} (worth {worth})");
                        Check("points = counter x 10", points - _pointsBefore == worth * TargetRound.PointsPerTarget, $"{_pointsBefore} -> {points}");
                        Check("the HUD counter shows it", HudSizeTexts().Contains($">{count}<"), HudSizeTexts());
                    }
                    else Check("the game rolled the crane up", false, "it stayed where it was dropped");
                    _pointsBefore = points; _countBefore = count;
                    FrameCapture.Request(_step == 3 ? "patch_target_hud" : "patch_target_hud_b");
                    return 1f;
                }
                case 5:
                    Msg("something that isn't a target dropped in: " + Drop(m => !TargetRound.IsTarget(m), out _picked));
                    return 2f;
                case 6:
                {
                    bool taken = _picked != null && _picked.transform.parent != _pickedParent;
                    Msg($"after the non-target (taken {taken}): " + TargetState());
                    if (taken)
                    {
                        Check("no counter change for a non-target", TargetRound.Count(me) == _countBefore, $"{TargetRound.Count(me)}");
                        Check("no points for a non-target", (Status(me)?.hardPointNum ?? 0) == _pointsBefore, $"{Status(me)?.hardPointNum}");
                    }
                    _mySize = Status(me)?.hardPointNum ?? 0; _myCount = TargetRound.Count(me);
                    Msg("30 s of CPU play...");
                    return 30f;
                }
                case 7:
                    Msg("after 30 s: " + TargetState());
                    Msg(CraneChecks() + " | " + Touches());
                    Msg($"points given by the patch: {TargetRound.PointsGiven}; CPU pickups by the patch: {TargetRound.CpuPickups}");
                    {
                        var mgr = GameRefs.Multiplay;
                        var cpus = KingChances.Cores(mgr).Where(c => c.isMultiCpu).ToList();
                        int cpuCount = cpus.Sum(TargetRound.Count);
                        bool pointsMatch = cpus.All(c => (Status(c)?.hardPointNum ?? 0) == TargetRound.Count(c) * TargetRound.PointsPerTarget);
                        Check("CPU princes rolled things up", TargetRound.CpuPickups > 0, $"{TargetRound.CpuPickups} pickups; sizes {string.Join(", ", cpus.Select(c => c.multiCoreSize))}");
                        Check("CPU princes collected cranes", cpuCount > 0, $"counters {string.Join(", ", cpus.Select(c => TargetRound.Count(c)))}");
                        Msg("CPU counter vs points (points stay when a crane falls off again): " + string.Join(", ", cpus.Select(c => $"{Who(c)} {TargetRound.Count(c)} -> {Status(c)?.hardPointNum}pt")));
                        Check("CPU points never exceed what they rolled up", cpus.All(c => (Status(c)?.hardPointNum ?? 0) <= _cpuTargets.GetValueOrDefault(c.multiCoreNo) * TargetRound.PointsPerTarget),
                            string.Join(", ", cpus.Select(c => $"{Who(c)} {Status(c)?.hardPointNum}pt for {_cpuTargets.GetValueOrDefault(c.multiCoreNo)} target(s) taken")));
                        Check("your count and points untouched by their pickups", (Status(me)?.hardPointNum ?? 0) == _mySize && TargetRound.Count(me) == _myCount,
                            $"points {_mySize} -> {Status(me)?.hardPointNum}, count {_myCount} -> {TargetRound.Count(me)}");
                    }
                    FrameCapture.Request("patch_target_hud2");
                    return 1f;
                default:
                    return -1f;
            }
        }

        private static int _pointsBefore, _countBefore, _drops, _mySize, _myCount;
        private static readonly Dictionary<uint, int> _cpuTargets = new();
        private static Transform _pickedParent;

        /// <summary>Moves the nearest matching object your katamari can take into it; the game's own collision then rolls it up.</summary>
        private static string Drop(Func<MainGameMonoBase, bool> filter, out MainGameMonoBase picked)
        {
            picked = null;
            _pickedParent = null;
            var gm = GameRefs.Main;
            var core = gm?.GetCore();
            if (core == null) return "no katamari";
            var c = core.transform.position;
            int size = gm.KatamariSize;
            float bestSq = float.MaxValue;
            foreach (var m in Resources.FindObjectsOfTypeAll<MainGameMonoBase>())
            {
                try
                {
                    if (m == null || !m.gameObject.activeInHierarchy || !m.gameObject.scene.isLoaded) continue;
                    if (m.transform.IsChildOf(core.transform) || !filter(m)) continue;
                    int need = gm.GetMonoRequireSize(m.MonoID);
                    if (need <= 0 || need > size) continue;
                    if (m.GetComponentsInChildren<MainGameMonoBase>(true).Length > 1) continue;
                    float sq = (m.transform.position - c).sqrMagnitude;
                    if (sq < bestSq) { bestSq = sq; picked = m; }
                }
                catch { }
            }
            if (picked == null) return "nothing to drop";
            _pickedParent = picked.transform.parent;
            _drops++;
            var fwd = core.transform.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            float r = core.KatamariRadius;
            // On the front surface: dropped at the very centre the game sees no contact (measured).
            picked.transform.position = c + fwd.normalized * r * 0.9f;
            return $"{picked.gameObject.name} (MonoID {picked.MonoID}, target {TargetRound.IsTarget(picked)} worth {TargetRound.TargetsIn(picked)}, was {Mathf.Sqrt(bestSq):0.0} away)";
        }

        /// <summary>
        /// MainGameCore.MultiGetMono only lets a CPU prince take objects with MainGameMonoBase.isMultiplaySync set
        /// (no code sets it to true: it comes from the map's data). How many have it here?
        /// </summary>
        private static void SyncSurvey()
        {
            int all = 0, sync = 0, targets = 0, targetSync = 0;
            foreach (var m in Resources.FindObjectsOfTypeAll<MainGameMonoBase>())
            {
                try
                {
                    if (m == null || !m.gameObject.scene.isLoaded) continue;
                    all++;
                    bool t = TargetRound.IsTarget(m);
                    if (t) targets++;
                    if (m.isMultiplaySync) { sync++; if (t) targetSync++; }
                }
                catch { }
            }
            Msg($"objects with isMultiplaySync (CPU princes can take only those): {sync} of {all}; targets {targetSync} of {targets}");
        }

        private static readonly Dictionary<string, int> _touches = new();
        private static bool _cpuWatching;

        /// <summary>Test-only hook: every prince's MultiGetMono calls (touches), split by the object's isMultiplaySync.</summary>
        private static void CpuWatch()
        {
            if (_cpuWatching) return;
            _cpuWatching = true;
            try
            {
                ApiCore.Instance.HarmonyInstance.Patch(HarmonyLib.AccessTools.Method(typeof(MainGameCore), nameof(MainGameCore.MultiGetMono)),
                    prefix: new HarmonyLib.HarmonyMethod(HarmonyLib.AccessTools.Method(typeof(PatchTest), nameof(BeforeMultiGetMono))));
            }
            catch (Exception e) { Msg("touch hook: " + e.Message); }
        }

        private static void BeforeMultiGetMono(MainGameCore __instance, MainGameMonoBase mono)
        {
            try
            {
                string key = $"{Who(__instance)} sync={mono?.isMultiplaySync}";
                _touches.TryGetValue(key, out int n);
                _touches[key] = n + 1;
            }
            catch { }
        }

        private static readonly Dictionary<string, int> _craneChecks = new();

        /// <summary>Test-only: what the game decides when a katamari touches a target (cranes).</summary>
        private static void CraneProbe()
        {
            var h = ApiCore.Instance.HarmonyInstance;
            void Post(string method, string patch)
            {
                try
                {
                    h.Patch(HarmonyLib.AccessTools.Method(typeof(MainGameCore), method),
                        postfix: new HarmonyLib.HarmonyMethod(HarmonyLib.AccessTools.Method(typeof(PatchTest), patch)));
                }
                catch (Exception e) { Msg($"crane probe {method}: {e.Message}"); }
            }
            Post(nameof(MainGameCore.CheckCanGetMono), nameof(AfterCanGet));
            Post(nameof(MainGameCore.CheckCrashMono), nameof(AfterCrash));
        }

        private static void Note(string what, MainGameCore core, MainGameMonoBase mono, bool result)
        {
            try
            {
                if (mono == null || !TargetRound.IsTarget(mono)) return;
                string key = $"{Who(core)} {what}={result}";
                _craneChecks.TryGetValue(key, out int n);
                _craneChecks[key] = n + 1;
            }
            catch { }
        }

        private static void AfterCanGet(MainGameCore __instance, MainGameMonoBase TargetMono, bool __result) => Note("CanGet", __instance, TargetMono, __result);
        private static void AfterCrash(MainGameCore __instance, MainGameMonoBase TargetMono, bool __result) => Note("Crash", __instance, TargetMono, __result);

        private static string CraneChecks() => _craneChecks.Count == 0 ? "no collision checks with targets" :
            "collision checks with targets: " + string.Join(", ", _craneChecks.OrderBy(k => k.Key).Select(k => $"{k.Key} x{k.Value}"));

        private static string Touches() => _touches.Count == 0 ? "no MultiGetMono calls" :
            "MultiGetMono calls: " + string.Join(", ", _touches.OrderBy(k => k.Key).Select(k => $"{k.Key} x{k.Value}"));

        private static void TargetSurvey()
        {
            var groups = new Dictionary<string, (int n, int worth)>();
            foreach (var m in Resources.FindObjectsOfTypeAll<MainGameMonoBase>())
            {
                try
                {
                    if (m == null || !m.gameObject.scene.isLoaded || !TargetRound.IsTarget(m)) continue;
                    string name = m.gameObject.name;
                    int cut = name.IndexOf(" (", StringComparison.Ordinal);
                    if (cut > 0) name = name.Substring(0, cut);
                    groups.TryGetValue(name, out var g);
                    groups[name] = (g.n + 1, TargetRound.TargetsIn(m));
                }
                catch { }
            }
            var gm = GameRefs.Main;
            Msg($"targets on the map (the game's CheckMonoTargetType): {groups.Sum(g => g.Value.n)} objects, worth {groups.Sum(g => g.Value.n * g.Value.worth)}: " +
                string.Join(", ", groups.OrderByDescending(g => g.Value.n).Select(g => $"{g.Key} x{g.Value.n} (worth {g.Value.worth})")));
        }

        private static string TargetState()
        {
            var mgr = GameRefs.Multiplay;
            var mm = GameRefs.Main?.MonoManager;
            var parts = new List<string>();
            if (mgr?.dicPlayerCore != null)
                foreach (var core in KingChances.Cores(mgr))
                    parts.Add($"{Who(core)} count {TargetRound.Count(core)} points {Status(core)?.hardPointNum} objects {Status(core)?.totalMonoGetNum}");
            string lists = "";
            try
            {
                var a = mm?.listMultiCorrectNum; var b = mm?.listMultiCurrentMonoStored;
                lists = $" | typeCurrent {mm?.currentTypeMono} monoStored {mm?.GetCurrentMonoIndex(false)} correct[{(a == null ? "" : string.Join(",", Enumerable.Range(0, a.Count).Select(i => a[i])))}] stored[{(b == null ? "" : string.Join(",", Enumerable.Range(0, b.Count).Select(i => b[i])))}]";
            }
            catch (Exception e) { lists = " | lists: " + e.Message; }
            return string.Join("; ", parts) + lists + " | HUD: " + HudSizeTexts() + " | pt labels: " + PointLabels();
        }

        /// <summary>The text of every active size/count display (UISizeBase and subclasses).</summary>
        private static string HudSizeTexts()
        {
            var parts = new List<string>();
            foreach (var ui in Resources.FindObjectsOfTypeAll<UISizeBase>())
            {
                try
                {
                    if (ui == null || !ui.gameObject.activeInHierarchy) continue;
                    var texts = ui.GetComponentsInChildren<Il2CppTMPro.TextMeshProUGUI>(false).Where(t => t != null && !string.IsNullOrWhiteSpace(t.text)).Select(t => $"{t.gameObject.name}='{t.text.Replace("\n", " ")}'");
                    parts.Add($"{ui.GetIl2CppType().Name}[{string.Join(" ", texts)}]");
                }
                catch { }
            }
            return parts.Count == 0 ? "none" : string.Join(" ", parts);
        }

        private static string PointLabels()
        {
            var list = GameRefs.Multiplay?.listTextMeshPoint;
            if (list == null) return "none";
            var parts = new List<string>();
            for (int i = 0; i < list.Count; i++) parts.Add(list[i] == null ? "null" : $"'{list[i].text}'");
            return string.Join(",", parts);
        }

        // ------------------------------------------------------------------ King Chances

        private static readonly Chance[] Order =
            { Chance.max, Chance.monoDrop, Chance.itemDrop, Chance.kingBox, Chance.itemConfiscation, Chance.bonusArea, Chance.unexploded };
        private static int _kc;
        private static float _firedAt, _shownAt;
        private static string _shownText;

        private static float KingChanceStep()
        {
            float now = Time.unscaledTime;
            var mgr = GameRefs.Multiplay;
            if (_step == 0)
            {
                _step = 1;
                Msg($"table {string.Join(", ", KingChances.Table)}; in offline match {KingChances.InOfflineMatch}");
                SyncSurvey();
                CpuWatch();
                var ids = MultiplaySetting.bigBattleKingChanceMessage;
                Msg($"message ids: {(ids == null ? "none" : string.Join(", ", Enumerable.Range(0, ids.Length).Select(i => $"[{i}] {ids[i]}")))}");
                for (int i = 0; i <= (int)Chance.max; i++) Msg($"message for {(Chance)i}: {KingChances.MessageFor((Chance)i)?.Replace("\n", " / ")}");
                Msg("before: " + KcState());
                return 1f;
            }
            if (_step == 1)
            {
                if (_kc >= Order.Length) { _step = 3; _firedAt = now; Msg("waiting 62 s for the unlimited dash to end..."); return 62f; }
                var type = Order[_kc];
                // SetKingChanceBonus reads bigBattleKingChanceMessage[type]: past its end it would throw inside the game.
                var msgIds = MultiplaySetting.bigBattleKingChanceMessage;
                if (msgIds == null || (int)type >= msgIds.Length)
                {
                    Msg($"{type}: no message for it (table has {msgIds?.Length} entries), not fired");
                    _kc++;
                    return 0.1f;
                }
                if (type == Chance.itemConfiscation) Msg(GiveEveryone(Reward.basin));
                KingChances.Fire(type, filter: false);   // another mod's Filter (e.g. freebies off) mustn't change the test
                _firedAt = now; _shownAt = -1f; _shownText = null;
                _step = 2;
                return 0.1f;
            }
            if (_step == 2)
            {
                // Watch the King Chance message: MultiplayGameManager.objPlayerInfo / textMeshPlayerInfo.
                var type = Order[_kc];
                bool shown = mgr?.objPlayerInfo != null && mgr.objPlayerInfo.activeSelf;
                string text = mgr?.textMeshPlayerInfo?.text;
                if (shown && _shownAt < 0f && text != null && text.Contains(KingChances.MessageFor(type)?.Split('\n').Last() ?? "\u0001"))
                {
                    _shownAt = now; _shownText = text;
                    FrameCapture.Request("patch_kc_" + type);
                }
                if (now - _firedAt < 4f) return 0.1f;
                Check($"{type}: message shown", _shownAt > 0f, _shownText == null ? $"label '{text?.Replace("\n", " / ")}' active {shown}" : $"'{_shownText.Replace("\n", " / ")}' after {_shownAt - _firedAt:0.0} s");
                Msg($"after {type}: " + KcState());
                _kc++;
                _step = 1;
                return 2f;
            }
            if (_step == 3)
            {
                Msg("62 s after the last King Chance: " + KcState());
                Msg(Touches());
                bool ended = mgr != null && KingChances.Cores(mgr).All(c => (Status(c)?.infinityDashTime ?? 0f) <= 0f);
                Check("unlimited dash ended after 60 s", ended);
                Check("no crane points outside the Target mode", !TargetRound.InRound, $"rule {mgr?.nowRule}, map {mgr?.gimiSingleStage}");
                PoolTest();
                _step = 4;
                return 0.5f;
            }
            return -1f;
        }

        /// <summary>BattleItemPools on a CPU prince (no reward display) and an item box of the map.</summary>
        private static void PoolTest()
        {
            var mgr = GameRefs.Multiplay;
            var bb = mgr?.multiplayBigBattle;
            var cpu = mgr == null ? null : KingChances.Cores(mgr).FirstOrDefault(c => c.isMultiCpu);
            if (bb == null || cpu == null) { Check("pools: a battle with a CPU prince", false); return; }
            var allowed = new[] { Reward.hand, Reward.shoes };
            BattleItemPools.SetKingBox(allowed);
            var got = new List<Reward>();
            for (int k = 0; k < 6; k++)
            {
                mgr.MultiplaySetItem(cpu, Reward.none);
                bb.MultiplayGetKingBox(cpu);
                got.Add(cpu.multiItemType);
            }
            Check("King Box pool: only Hand / Shoes given", got.All(r => allowed.Contains(r)), string.Join(", ", got) + $"; swaps {BattleItemPools.KingBoxSwaps}");
            BattleItemPools.SetKingBox(Array.Empty<Reward>());
            mgr.MultiplaySetItem(cpu, Reward.none);
            int empty = BattleItemPools.EmptyKingBoxes;
            bb.MultiplayGetKingBox(cpu);
            Check("King Box pool empty: nothing given", cpu.multiItemType == Reward.none && BattleItemPools.EmptyKingBoxes == empty + 1, $"{cpu.multiItemType}");
            var box = Resources.FindObjectsOfTypeAll<MainGameItemBox>().FirstOrDefault(b => b != null && b.gameObject.scene.isLoaded);
            if (box == null) Msg("no item box on the map to test");
            else
            {
                var natural = new List<string>();
                BattleItemPools.SetItemBox(null);
                for (int k = 0; k < 8; k++) natural.Add(((eInstageItemType)(box.GetRandomItemMulti() + 1)).ToString());
                BattleItemPools.SetItemBox(new[] { eInstageItemType.Shoes, eInstageItemType.Spider });
                var draws = new List<eInstageItemType>();
                for (int k = 0; k < 10; k++) draws.Add((eInstageItemType)(box.GetRandomItemMulti() + 1));
                Msg($"item box, the game's own draw: {string.Join(", ", natural)}");
                Check("item box pool: only Shoes / Spider", draws.All(i => i is eInstageItemType.Shoes or eInstageItemType.Spider), string.Join(", ", draws));
            }
            BattleItemPools.Reset();
        }

        private static string KcState()
        {
            var mgr = GameRefs.Multiplay;
            var root = GameRefs.Main?._inStageData?.transform;
            string Obj(string p) { var c = root?.Find(p); return c == null ? "missing" : c.gameObject.activeSelf ? "on" : "off"; }
            var items = mgr == null ? "" : string.Join(", ", KingChances.Cores(mgr).Select(c => $"{Who(c)} {c.multiItemType} dash {Status(c)?.infinityDashTime:0.0}"));
            return $"AddMono {Obj("mono/AddMono")}, AddItems {Obj("mono/AddItems")}, BonusAreaWall {Obj("KingChance/BonusAreaWall")}; next {KingChances.Next}, fired {KingChances.FiredCount}, happened {KingChances.HappenedCount}; {items}";
        }

        private static string GiveEveryone(Reward type)
        {
            var mgr = GameRefs.Multiplay;
            int n = 0;
            foreach (var c in KingChances.Cores(mgr)) { mgr.MultiplaySetItem(c, type); n++; }
            return $"gave {type} to {n} prince(s)";
        }

        // ------------------------------------------------------------------ The HUD for the game's freebies and the API's (story stage)

        private static readonly eInstageItemType[] HudItems = { eInstageItemType.Drink, eInstageItemType.Magnet, eInstageItemType.Shoes, eInstageItemType.Crown };

        private static string ItemUiState(MainGameManager gm)
        {
            try
            {
                var ui = gm.MainUI?.ItemUI;
                if (ui == null) return "no item UI";
                string clip = "";
                try { var infos = ui.ControllAnimator.GetCurrentAnimatorClipInfo(0); if (infos.Length > 0) clip = infos[0].clip.name; } catch { }
                return $"slot {gm._nowInventoryItem}, countdown {gm._itemEffectTimer:0.0}, end called {gm._hasCalledEffectEnd}; UI: get {ui._getDemoState}, activate {ui._activateDemoState} ({ui._activateDemoTimer:0.00}), " +
                       $"icon '{ui.ItemIcon?.sprite?.name}' on {ui.ItemIcon?.enabled} a {ui.ItemIcon?.color.a:0.##} at {ui.ItemIcon?.rectTransform.anchoredPosition}, " +
                       $"gauge {ui.ItemTimerGauge?.fillAmount:0.00} on {ui.ItemTimerGauge?.enabled}, clip '{clip}', canvas a {ui.CanvasGroup?.alpha:0.##}";
            }
            catch (Exception e) { return "item UI unreadable: " + e.Message; }
        }

        private static float HudStep()
        {
            var gm = GameRefs.Main;
            int s = _step++;
            int item = s / 4, phase = s % 4;
            if (s == 0) HideExplorer();
            if (item >= HudItems.Length) { gm.EndItemEffect(true); return -1f; }
            var it = HudItems[item];
            switch (phase)
            {
                case 0:
                    gm.EndItemEffect(true);
                    gm.SetInventoryItem(eInstageItemType.NoItem, false, true);
                    gm.SetInventoryItem(it, false, true);
                    gm.ActivateItemEffect();   // what the use button does (UpdateItem -> ActivateItemEffect)
                    Msg($"{it} used: {ItemUiState(gm)}");
                    return 0.2f;
                case 1:
                    Msg($"{it} after 0.2 s: {ItemUiState(gm)}");
                    return 0.5f;
                case 2:
                    FrameCapture.Request($"hud_{it}_07");
                    Msg($"{it} after 0.7 s: {ItemUiState(gm)}");
                    return 1.8f;
                default:
                    FrameCapture.Request($"hud_{it}_25");
                    Msg($"{it} after 2.5 s: {ItemUiState(gm)}");
                    return 0.5f;
            }
        }

        // ------------------------------------------------------------------ Smoke, Hoop, Dice and item boxes (story stage)

        private static float Alpha(MainGameCore core) { try { return core._prevAlpha; } catch { return -1f; } }

        // Next to the katamari, at its depth from the camera, spread sideways (right = -1, 0, 1).
        private static void PlaceInView(MainGameItemBox box, float right)
        {
            var cam = Camera.main;
            var core = GameRefs.Main?.GetCore();
            if (cam == null || box == null || core == null) return;
            var c = core.transform.position;
            float d = Vector3.Distance(cam.transform.position, c);
            box.transform.position = c + cam.transform.right * right * d * 0.55f - cam.transform.up * d * 0.25f;
        }

        /// <summary>Where a box's model is on screen (viewport 0..1, and in front of the camera or not).</summary>
        private static string OnScreen(MainGameItemBox box)
        {
            var cam = Camera.main;
            if (cam == null || box == null || box.ItemParent == null) return "?";
            var v = cam.WorldToViewportPoint(box.ItemParent.transform.position);
            bool seen = v.z > 0f && v.x is > 0f and < 1f && v.y is > 0f and < 1f;
            return $"{box.name} at ({v.x:0.00}, {v.y:0.00}) {(seen ? "in view" : "out of view")}";
        }

        private static bool _settled;

        private static List<eInstageItemType> Rolls(MainGameManager gm, eInstageItemType dice, int n)
        {
            var got = new List<eInstageItemType>();
            for (int k = 0; k < n; k++)
            {
                Use(gm, dice, out _, out _);
                got.Add(gm._nowInventoryItem);
            }
            return got;
        }

        private static float UnusedStoryStep()
        {
            var gm = GameRefs.Main;
            var core = gm.GetCore();
            switch (_step++)
            {
                case 0:
                    HideExplorer();
                    if (!_settled) { _settled = true; _step = 0; return 2f; }   // let the camera settle after the opening
                    Check("Smoke/Hoop/Dice and item box patches in place", GamePatches.UnusedFreebies.Ready && GamePatches.ItemBoxes.Ready,
                        $"{GamePatches.UnusedFreebies}; {GamePatches.ItemBoxes}");
                    Msg($"player alpha before Smoke: {Alpha(core):0.##}");
                    Use(gm, eInstageItemType.Smoke, out _, out _);
                    Check("Smoke (story): ghosted, on the HUD for 10 s", gm._nowInventoryItem == eInstageItemType.Smoke && gm._itemEffectTimer > 9f && UnusedFreebies.SmokeActive,
                        $"slot {gm._nowInventoryItem}, countdown {gm._itemEffectTimer:0.0}, alpha {Alpha(core):0.##}, ring {gm.GetItemEffectTimeRatio():0.00}");
                    return 0.5f;
                case 1:
                    FrameCapture.Request("uf_smoke");
                    Msg($"Smoke after 0.5 s: alpha {Alpha(core):0.##}");
                    gm._itemEffectTimer = 0.05f;   // the game's countdown runs out
                    return 0.6f;
                case 2:
                    Check("Smoke (story): back to normal when it ends", !UnusedFreebies.SmokeActive && Alpha(core) > 0.95f, $"alpha {Alpha(core):0.##}, slot {gm._nowInventoryItem}");
                    _objsBefore = EffectObjects();
                    Use(gm, eInstageItemType.Hoop, out _, out _);
                    Check($"Hoop (story): the ring, on the HUD for {UnusedFreebies.HoopShowTime:0} s", gm._nowInventoryItem == eInstageItemType.Hoop
                        && Mathf.Abs(gm._itemEffectTimer - UnusedFreebies.HoopShowTime) < 0.3f && UnusedFreebies.HoopActive,
                        $"slot {gm._nowInventoryItem}, countdown {gm._itemEffectTimer:0.0}");
                    return 0.4f;
                case 3:
                    FrameCapture.Request("uf_hoop");
                    Msg($"Hoop after 0.4 s: new effect objects: {NewEffectObjects()}");
                    gm._itemEffectTimer = 0.05f;
                    return 0.6f;
                case 4:
                {
                    var normal = UnusedFreebies.DiceChoices(false);
                    var rolled = Rolls(gm, eInstageItemType.Dice, 10);
                    Check("Dice (story): rolls a working normal freebie, never a Dice", rolled.All(i => normal.Contains(i)),
                        $"{string.Join(", ", rolled)}; pool {string.Join(", ", normal)}");
                    var special = UnusedFreebies.DiceChoices(true);
                    rolled = Rolls(gm, eInstageItemType.SP_Dice, 10);
                    Check("Special Dice (story): rolls a working Special, never a Dice", rolled.All(i => special.Contains(i)),
                        $"{string.Join(", ", rolled)}; pool {string.Join(", ", special)}");
                    gm.EndItemEffect(true);
                    gm.SetInventoryItem(eInstageItemType.NoItem, false, true);
                    Use(gm, eInstageItemType.Dice, out _, out _);   // one more, for the pictures of the roulette
                    Msg($"Dice for the pictures rolled {UnusedFreebies.LastRoll}");
                    return 0.25f;
                }
                case 5:
                    FrameCapture.Request("uf_dice_roll");
                    return 1.5f;
                case 6:
                    FrameCapture.Request("uf_dice_after");
                    gm.EndItemEffect(true);
                    gm.SetInventoryItem(eInstageItemType.NoItem, false, true);
                    _ib = ItemBoxes.All().OrderBy(b => b.name).ToList();
                    Msg($"story boxes: {string.Join("; ", _ib.Select(ItemBoxes.Describe))}");
                    if (_ib.Count < 3) { Check("item boxes: three boxes to test with", false, $"{_ib.Count}"); return -1f; }
                    // Swap to a freebie the game has no model of: an icon card.
                    // Two boxes beside the katamari for the pictures (the Magnet box left, the Sonar box right).
                    PlaceInView(_ib.First(b => ItemBoxes.GameContents(b).Contains(eInstageItemType.Magnet)), -1f);
                    PlaceInView(_ib.First(b => ItemBoxes.GameContents(b).Contains(eInstageItemType.Sonar)), 1f);
                    ItemBoxes.SetSwap(eInstageItemType.Magnet, eInstageItemType.Spider);
                    Msg($"swap Magnet -> Spider: {string.Join("; ", _ib.Select(ItemBoxes.Describe))}; Spider model from: {FreebieModels.Source(eInstageItemType.Spider)}");
                    return 0.5f;
                case 7:
                    Msg("boxes on screen: " + string.Join("; ", _ib.Select(OnScreen)));
                    FrameCapture.Request("ib_card");
                    ItemBoxes.SetSwap(eInstageItemType.Magnet, eInstageItemType.Shoes);
                    ItemBoxes.SetSwap(eInstageItemType.Sonar, eInstageItemType.Crown);
                    Msg($"swap Magnet -> Shoes, Sonar -> Crown: {string.Join("; ", _ib.Select(ItemBoxes.Describe))}; models from: Shoes {FreebieModels.Source(eInstageItemType.Shoes)}, Crown {FreebieModels.Source(eInstageItemType.Crown)}");
                    return 0.5f;
                case 8:
                {
                    FrameCapture.Request("ib_swapped");
                    var magnetBox = _ib.First(b => ItemBoxes.GameContents(b).Contains(eInstageItemType.Magnet));
                    Check("item boxes: a swapped box shows the new model", magnetBox.ItemParent != null && magnetBox.ItemParent.GetComponentsInChildren<Transform>(false).Any(t => t.name.StartsWith("FreebieModel_")),
                        ItemBoxes.Describe(magnetBox));
                    gm.EndItemEffect(true);
                    gm.SetInventoryItem(eInstageItemType.NoItem, false, true);
                    magnetBox.Collected();   // the game's call when you roll it up
                    Check("item boxes: a swapped box gives the new freebie", gm._nowInventoryItem == eInstageItemType.Shoes, $"slot {gm._nowInventoryItem}");
                    // One box set on its own, to two freebies.
                    var other = _ib.First(b => b != magnetBox && ItemBoxes.GameContents(b).Contains(eInstageItemType.Magnet));
                    ItemBoxes.SetBox(other, new[] { eInstageItemType.Mushroom, eInstageItemType.Dice });
                    Msg($"set one box to Mushroom / Dice: {ItemBoxes.Describe(other)}; active {other.gameObject.activeSelf}, taken {other.enabled}");
                    gm.EndItemEffect(true);
                    gm.SetInventoryItem(eInstageItemType.NoItem, false, true);
                    other.Collected();
                    Check("item boxes: a box set to Mushroom / Dice gives one of them", gm._nowInventoryItem is eInstageItemType.Mushroom or eInstageItemType.Dice, $"slot {gm._nowInventoryItem}");
                    // Off: the Sonar box (Sonar was swapped to Crown: turn Crown off) disappears, and comes back.
                    var sonarBox = _ib.FirstOrDefault(b => ItemBoxes.GameContents(b).Contains(eInstageItemType.Sonar));
                    ItemBoxes.SetEnabled(eInstageItemType.Crown, false);
                    bool hidden = sonarBox != null && !sonarBox.gameObject.activeSelf;
                    ItemBoxes.SetEnabled(eInstageItemType.Crown, true);
                    bool back = sonarBox != null && sonarBox.gameObject.activeSelf;
                    Check("item boxes: a box with nothing left disappears, and comes back", hidden && back, $"hidden {hidden}, back {back}");
                    ItemBoxes.Reset();
                    return 0.5f;
                }
                case 9:
                {
                    var sonarBox = _ib.FirstOrDefault(b => ItemBoxes.GameContents(b).Contains(eInstageItemType.Sonar));
                    bool restored = sonarBox != null && sonarBox.ItemParent.GetComponentsInChildren<Transform>(false).All(t => !t.name.StartsWith("FreebieModel_"))
                                    && sonarBox.ItemParent.GetComponentsInChildren<Transform>(false).Any(t => t.name == "SONAR");
                    Check("item boxes: Reset puts the game's own model back", restored && !ItemBoxes.Active, sonarBox == null ? "no Sonar box" : ItemBoxes.Describe(sonarBox));
                    FrameCapture.Request("ib_reset");
                }
                    // The game's own freebie models from its asset catalog (the black-card fix), and the asset exports.
                    {
                        var models = FreebieModels.GameAddresses.Keys.Select(it => $"{it}: {FreebieModels.CatalogModel(it) ?? "none"}").ToList();
                        Check("freebie models: the game's own models load from its catalog", FreebieModels.GameAddresses.Keys.All(it => FreebieModels.CatalogModel(it) != null), string.Join(", ", models));
                        Msg("names: " + AssetReplacer.DumpNames());
                        string em = AssetReplacer.ExportModels("SHOES"), et = AssetReplacer.ExportTexts(), es = AssetReplacer.ExportSounds("SE_Kinetic");
                        Msg("exports: " + em + " | " + et + " | " + es);
                        string ex = System.IO.Path.Combine(AssetReplacer.Folder, "Export");
                        Check("asset export: models, text and sounds are written", System.IO.Directory.Exists(System.IO.Path.Combine(ex, "Sound")) && System.IO.File.Exists(System.IO.Path.Combine(ex, "Sound", "SE_Kinetic.acb")),
                            $"{em} | {et} | {es}");
                    }
                    // The Timer and the stage music (TimerMusic): use the Timer, then open and close the pause menu.
                    gm.EndItemEffect(true);
                    gm.SetInventoryItem(eInstageItemType.Timer, false, true);
                    gm.ActivateItemEffect();
                    _tmHeld0 = TimerMusic.Held;
                    return 1.5f;
                case 10:
                    Msg($"Timer running {TimerMusic.TimerRunning}; pause menu: {PauseMenu.Open()}");
                    return 1.5f;
                case 11:
                    PauseMenu.Resume();
                    return 1.5f;
                case 12:
                    Check("Timer: closing the pause menu doesn't restart the music while the Timer runs (the game's resume held back)",
                        TimerMusic.TimerRunning && TimerMusic.Held > _tmHeld0, $"held {TimerMusic.Held - _tmHeld0}, Timer running {TimerMusic.TimerRunning}, countdown {gm._itemEffectTimer:0.0}");
                    _tmRes0 = TimerMusic.Resumes; _tmHeld1 = TimerMusic.Held;
                    gm.EndItemEffect(true);   // the Timer ends: the game's own resume must go through
                    return 0.5f;
                case 13:
                    Check("Timer: when it ends the music resumes (not held)", TimerMusic.Resumes > _tmRes0 && TimerMusic.Held == _tmHeld1,
                        $"resumes {TimerMusic.Resumes - _tmRes0}, held {TimerMusic.Held - _tmHeld1}");
                    gm.SetInventoryItem(eInstageItemType.NoItem, false, true);
                    return -1f;
                default:
                    return -1f;
            }
        }

        private static List<MainGameItemBox> _ib;

        // ------------------------------------------------------------------ Smoke, Hoop, Dice in a battle

        private static readonly List<string> _involves = new(), _blowns = new();
        private static MainGameCore _overlapCpu, _overlapMe;
        private static float _overlapUntil = -1f;
        private static bool _overlapHooked;

        private static void BeforeSetInvolve(MainGameCore __0, MainGameCore __1) { try { _involves.Add($"{Who(__0)} rolls up {Who(__1)}"); } catch { } }
        // MultiplayGameManager.SetBlown(core, target): core is knocked away by target (its event runs core.ForceBlown(target)).
        private static void BeforeSetBlown(MainGameCore __0, MainGameCore __1, bool __2) { try { _blowns.Add($"{Who(__0)} knocked away by {Who(__1)}{(__2 ? " (each other)" : "")}"); } catch { } }

        private static void Overlap()
        {
            if (_overlapUntil < 0f || _overlapCpu == null || _overlapMe == null) return;
            if (Time.unscaledTime > _overlapUntil) { _overlapUntil = -1f; return; }
            try { _overlapCpu.transform.position = _overlapMe.transform.position + _overlapMe.transform.forward * 0.05f; } catch { }
        }

        private static List<MainGameCore> _cpus;
        private static int _warps0;
        private static string _controlStartRolled;

        private static string StateOf(MainGameCore c)
        {
            var st = Status(c);
            return $"{Who(c)} size {c.multiCoreSize}, rolled-up-by {(c.involvedCore == null ? "none" : Who(c.involvedCore))}, involved {st?.isInvolved}, fall {st?.isFall}, " +
                   $"invincible {st?.timeInvincible:0.0}, crown {st?.timeCrownPower:0.0}, transparent {st?.timeTransparent:0.0}, no-bump {st?.disableBlownTime:0.0}";
        }

        // One protection phase: use the freebie (NoItem = none), then hold a bigger CPU on you for 2.5 s.
        private static void StartPhase(MainGameManager gm, MainGameCore me, MainGameCore cpu, eInstageItemType item)
        {
            Msg($"before {(item == eInstageItemType.NoItem ? "control" : item.ToString())}: {StateOf(me)} | {StateOf(cpu)}");
            if (item != eInstageItemType.NoItem)
            {
                gm.EndItemEffect(true);
                gm.SetInventoryItem(item, false, true);
                gm.ActivateItemEffect();
            }
            _overlapCpu = cpu;
            _involves.Clear(); _blowns.Clear();
            _overlapUntil = Time.unscaledTime + 2.5f;
        }

        private static float UnusedBattleStep()
        {
            var gm = GameRefs.Main;
            var mgr = GameRefs.Multiplay;
            var me = gm?.GetCore();
            if (gm == null || mgr == null || me == null) throw new InvalidOperationException("no battle");
            var st = Status(me);
            string meName = Who(me);
            int s = _step++;
            switch (s)
            {
                case 0:
                {
                    HideExplorer();
                    var h = ApiCore.Instance.HarmonyInstance;
                    h.Patch(HarmonyLib.AccessTools.Method(typeof(MultiplayGameManager), nameof(MultiplayGameManager.SetInvolve)),
                        prefix: new HarmonyLib.HarmonyMethod(typeof(PatchTest).GetMethod(nameof(BeforeSetInvolve), BindingFlags.NonPublic | BindingFlags.Static)));
                    h.Patch(HarmonyLib.AccessTools.Method(typeof(MultiplayGameManager), nameof(MultiplayGameManager.SetBlown)),
                        prefix: new HarmonyLib.HarmonyMethod(typeof(PatchTest).GetMethod(nameof(BeforeSetBlown), BindingFlags.NonPublic | BindingFlags.Static)));
                    if (!_overlapHooked) { _overlapHooked = true; GameEvents.Update += Overlap; }
                    _overlapMe = me;
                    _cpus = KingChances.Cores(mgr).Where(c => c.isMultiCpu).Take(3).ToList();
                    if (_cpus.Count < 3) { Check("three CPU princes to test against", false, $"{_cpus.Count}"); return -1f; }
                    // Make them much bigger the game's way (Special Mushroom bonus), so they could roll you up.
                    foreach (var c in _cpus) for (int k = 0; k < 4; k++) { mgr.MultiplaySetItem(c, Reward.mushroomPlus); c.MultiItemUse(); }
                    return 1.5f;
                }
                case 1:
                    _hoopPos = me.transform.position;
                    _warps0 = UnusedFreebies.Warps;
                    gm.EndItemEffect(true);
                    gm.SetInventoryItem(eInstageItemType.Hoop, false, true);
                    gm.ActivateItemEffect();
                    Check("Hoop (battle): used, on the HUD", gm._nowInventoryItem == eInstageItemType.Hoop && gm._itemEffectTimer > 0f && UnusedFreebies.Warps == _warps0 + 1,
                        $"slot {gm._nowInventoryItem}, countdown {gm._itemEffectTimer:0.0}, warps {UnusedFreebies.Warps - _warps0}");
                    return 0.5f;
                case 2:
                    FrameCapture.Request("uf_hoop_battle");
                    return 2.3f;
                case 3:
                {
                    float moved = Vector3.Distance(_hoopPos, me.transform.position);
                    Check("Hoop (battle): drops you through the ring to a random spot on the map", moved > UnusedFreebies.HoopMinDistance,
                        $"moved {moved:0.0} (patch {UnusedFreebies.LastWarpDistance:0.0})");
                    UnusedFreebies.StopAll();
                    gm.EndItemEffect(true);
                    return 1f;
                }
                case 4:
                    StartPhase(gm, me, _cpus[1], eInstageItemType.Smoke);
                    Check("Smoke (battle): the Smoke bomb runs (UserStatus.timeTransparent), on the HUD for 10 s",
                        st != null && st.timeTransparent > 9f && gm._nowInventoryItem == eInstageItemType.Smoke && gm._itemEffectTimer > 9f,
                        $"timeTransparent {st?.timeTransparent:0.0}, slot {gm._nowInventoryItem}, countdown {gm._itemEffectTimer:0.0}, ring {gm.GetItemEffectTimeRatio():0.00}");
                    return 0.5f;
                case 5:
                    FrameCapture.Request("uf_smoke_battle");
                    return 2.3f;
                case 6:
                    Check("Smoke (battle): the bigger CPU can't roll you up", !_involves.Any(x => x.EndsWith("rolls up " + meName)),
                        $"roll-ups: {string.Join("; ", _involves)}; bumps: {string.Join("; ", _blowns.Take(6))} ({_blowns.Count})");
                    UnusedFreebies.StopAll();
                    gm.EndItemEffect(true);
                    return 1f;
                case 7:
                    _controlStartRolled = me.involvedCore != null ? Who(me.involvedCore) : null;
                    StartPhase(gm, me, _cpus[2], eInstageItemType.NoItem);
                    return 2.8f;
                case 8:
                    Msg($"control (no Smoke or Hoop): roll-ups: {string.Join("; ", _involves)}; bumps: {string.Join("; ", _blowns.Take(6))} ({_blowns.Count}); now {StateOf(me)}");
                    Check("control: without Smoke or Hoop a bigger CPU rolls you up",
                        _controlStartRolled != null || _involves.Any(x => x.EndsWith("rolls up " + meName)) || me.involvedCore != null,
                        _controlStartRolled != null ? $"rolled up by {_controlStartRolled} right after the Smoke ended" : $"roll-ups: {string.Join("; ", _involves)}");
                    return 6f;   // the respawn after being rolled up
                case 9:
                {
                    var normal = UnusedFreebies.DiceChoices(false);
                    var rolled = Rolls(gm, eInstageItemType.Dice, 8);
                    Check("Dice (battle): rolls a working normal freebie, never a Dice", rolled.All(i => normal.Contains(i)),
                        $"{string.Join(", ", rolled)}; pool {string.Join(", ", normal)}");
                    var special = UnusedFreebies.DiceChoices(true);
                    rolled = Rolls(gm, eInstageItemType.SP_Dice, 8);
                    Check("Special Dice (battle): rolls a working Special, never a Dice", rolled.All(i => special.Contains(i)),
                        $"{string.Join(", ", rolled)}; pool {string.Join(", ", special)}");
                    gm.EndItemEffect(true);
                    Use(gm, eInstageItemType.Dice, out _, out _);
                    return 0.25f;
                }
                case 10:
                    FrameCapture.Request("uf_dice_battle");
                    Msg($"battle Dice for the picture rolled {UnusedFreebies.LastRoll}");
                    var box = Resources.FindObjectsOfTypeAll<MainGameItemBox>().FirstOrDefault(b => b != null && b.gameObject.scene.isLoaded && b.IsMultiplayBox);
                    if (box != null)
                    {
                        BattleItemPools.SetItemBox(new[] { eInstageItemType.Smoke, eInstageItemType.Hoop, eInstageItemType.Dice, eInstageItemType.SP_Dice });
                        var draws = Enumerable.Range(0, 8).Select(_ => (eInstageItemType)(box.GetRandomItemMulti() + 1)).ToList();
                        BattleItemPools.Reset();
                        Check("item box pool can hold Smoke / Hoop / Dice now", draws.All(i => UnusedFreebies.Items.Contains(i)), string.Join(", ", draws));
                    }
                    return 1f;
                default:
                    _overlapUntil = -1f;
                    return -1f;
            }
        }

        // ------------------------------------------------------------------ Battle freebie bugs (dash, timers, Spiderweb HUD, Hand, confiscation)

        private static float _bbT0, _bbE0;
        private static eInstageItemType _bbItem;
        private static IDisposable _bbFilter;
        private static readonly List<string> _steals = new();
        private static bool _stealHooked;

        private static void BeforeStealItem(MainGameCore __instance, Reward __0, MainGameCore __1) { try { _steals.Add($"{Who(__instance)} steals {__0} from {Who(__1)}"); } catch { } }
        private static void BeforeStolenItem(MainGameCore __instance, Reward __0, MainGameCore __1) { try { _steals.Add($"{Who(__instance)} loses {__0} to {Who(__1)}"); } catch { } }

        private static float DashUsed(MainGameCore c) { try { return c._oujiDashElapsedTimer; } catch { return -1f; } }

        private static string Timers(MainGameManager gm, MainGameCore me)
        {
            var st = Status(me);
            string ui = "";
            try
            {
                var it = gm.MainUI?.ItemUI;
                if (it != null) ui = $"; UI activate {it._activateDemoState}, gauge {it.ItemTimerGauge?.fillAmount:0.00} on {it.ItemTimerGauge?.enabled}, multi ratio {it._multiTimerRatio:0.00}";
            }
            catch { }
            return $"slot {gm._nowInventoryItem} countdown {gm._itemEffectTimer:0.00}, ring {gm.GetItemEffectTimeRatio():0.00}; holds {me.multiItemType} (status {st?.itemType}); status: crown {st?.timeCrownPower:0.00}, dash {st?.infinityDashTime:0.00}, slowly {st?.slowlyTime:0.00}, itemTimer {st?.itemEffectTimer:0.00}; " +
                   $"core: crownPower {me.isMultiCrownPower}, rocket {me.isMultiRocket}, slowly {me.isMultiSlowly}, gauge {me.isDispMultiItemGauge} {me.multiItemGaugeTime:0.00}/{me.multiItemGaugeTimeMax:0.00}, unlimited dash {BattleDash.Has(me)}; GAME_DELTATIME {Game.GAME_DELTATIME:0.0000} dt {Time.deltaTime:0.0000}{ui}";
        }

        // As a battle item box gives it (SetInventoryItem with the roulette also hands over the King Box reward), then used.
        // ------------------------------------------------------------------ unlimited dash with real dashes (DashTest)

        private static IDisposable _dashFilter;

        private static float DashStep(bool battle)
        {
            if (_step++ == 0)
            {
                HideExplorer();
                if (battle) _dashFilter = KingChances.AddFilter("PatchTest", _ => KingChances.Nothing);   // no other King Chances meanwhile
                Check("the unlimited dash patch is in place", GamePatches.BattleDash.Ready, GamePatches.BattleDash.ToString());
                DashTest.Begin(battle, Check, Msg);
                return 1f;
            }
            if (!DashTest.Done)
            {
                if (_step > 360) { Check("the dash test finished", false, "still running after 3 minutes"); _dashFilter?.Dispose(); return -1f; }
                return 0.5f;
            }
            _dashFilter?.Dispose();
            FrameCapture.Request($"patch_dash_{(battle ? "battle" : "story")}");
            return -1f;
        }

        private static void UseInBattle(MainGameManager gm, eInstageItemType item)
        {
            gm.EndItemEffect(true);
            gm.SetInventoryItem(item, true, true);
            gm.ActivateItemEffect();
            _bbItem = item;
            _bbT0 = Time.unscaledTime;
        }

        private static void DashStart(MainGameCore me)
        {
            me._oujiDashElapsedTimer = 0f; me._oujiDashExhaustTimer = 0f; me._oujiDashTimer = 5f;
            _bbE0 = DashUsed(me);
        }

        private static int _hc0;
        private static int _handWaits, _nearest0, _hoopWaits, _chest0, _chest1, _tmHeld0, _tmHeld1, _tmRes0;
        private static float _handGivenAt;
        private static bool _ringsOnly;
        private static Vector3 _hoopPos;
        private static int _iconsBefore;
        private static string _kbText1 = "";
        private static bool _hcSeen;

        private static void ClearDash(MultiplayGameManager mgr)
        {
            foreach (var c in KingChances.Cores(mgr)) { var s = Status(c); if (s != null) s.infinityDashTime = 0f; mgr.SetInfinityDashOff(c); }
        }

        private static string Holds(MultiplayGameManager mgr) =>
            string.Join("; ", KingChances.Cores(mgr).Select(c => $"{Who(c)} holds {c.multiItemType}, crown {Status(c)?.timeCrownPower:0.0}, dash {Status(c)?.infinityDashTime:0.0}/{BattleDash.Has(c)}"));

        private static float BattleBugsStep()
        {
            var gm = GameRefs.Main;
            var mgr = GameRefs.Multiplay;
            var me = gm?.GetCore();
            if (gm == null || mgr == null || me == null) throw new InvalidOperationException("no battle");
            var cpus = KingChances.Cores(mgr).Where(c => c.isMultiCpu).ToList();
            var st = Status(me);
            int s = _step++;
            switch (s)
            {
                case 0:
                {
                    HideExplorer();
                    _bbFilter = KingChances.AddFilter("PatchTest", _ => KingChances.Nothing);
                    if (_ringsOnly) { _step = 17; return 1f; }
                    if (!_stealHooked)
                    {
                        _stealHooked = true;
                        var h = ApiCore.Instance.HarmonyInstance;
                        h.Patch(HarmonyLib.AccessTools.Method(typeof(MainGameCore), nameof(MainGameCore.StealItem)),
                            prefix: new HarmonyLib.HarmonyMethod(typeof(PatchTest).GetMethod(nameof(BeforeStealItem), BindingFlags.NonPublic | BindingFlags.Static)));
                        h.Patch(HarmonyLib.AccessTools.Method(typeof(MainGameCore), nameof(MainGameCore.StolenItem)),
                            prefix: new HarmonyLib.HarmonyMethod(typeof(PatchTest).GetMethod(nameof(BeforeStolenItem), BindingFlags.NonPublic | BindingFlags.Static)));
                    }
                    Check("battle patches in place", GamePatches.BattleDash.Ready && GamePatches.BattleFreebies.Ready && GamePatches.KingChances.Ready,
                        $"{GamePatches.BattleDash}; {GamePatches.BattleFreebies}");
                    KingChances.Fire(Chance.unexploded, filter: false);
                    return 2f;
                }
                case 1:
                    BattleDash.Off = true;
                    DashStart(me);
                    return 1f;
                case 2:
                    Msg($"King Chance dash, the game as it is: stamina used {DashUsed(me) - _bbE0:0.00} in 1 s");
                    BattleDash.Off = false;
                    DashStart(me);
                    return 1f;
                case 3:
                {
                    float used = DashUsed(me) - _bbE0;
                    Check("Unlimited dash King Chance: dashing uses no stamina", BattleDash.Has(me) && used <= 0.01f, $"used {used:0.00} in 1 s, frames {BattleDash.Frames}");
                    me._oujiDashTimer = 0f;
                    ClearDash(mgr);
                    return 1f;
                }
                case 4:
                    UseInBattle(gm, eInstageItemType.Shoes);
                    Check($"Shoes (battle): unlimited dash and HUD countdown for {BattleDash.ShoesTime:0} s", BattleDash.Has(me) && st != null
                        && Mathf.Abs(st.infinityDashTime - BattleDash.ShoesTime) < 0.2f && Mathf.Abs(gm._itemEffectTimer - BattleDash.ShoesTime) < 0.2f, Timers(gm, me));
                    DashStart(me);
                    return 1f;
                case 5:
                {
                    float used = DashUsed(me) - _bbE0;
                    Check("Shoes (battle): dashing uses no stamina", BattleDash.Has(me) && used <= 0.01f, $"used {used:0.00} in 1 s");
                    me._oujiDashTimer = 0f;
                    return BattleDash.ShoesTime - 1f + 0.6f;
                }
                case 6:
                    Check($"Shoes (battle): the unlimited dash ends after its {BattleDash.ShoesTime:0} s", !BattleDash.Has(me) && gm._nowInventoryItem == eInstageItemType.NoItem,
                        $"+{Time.unscaledTime - _bbT0:0.0} s: {Timers(gm, me)}");
                    gm.EndItemEffect(true);
                    UseInBattle(gm, eInstageItemType.Spider);
                    return 0.5f;
                case 7:
                    FrameCapture.Request("bb_spider_use");
                    Check("Spiderweb: its countdown ring shows", gm.GetItemEffectTimeRatio() > 0.5f, Timers(gm, me));
                    gm.EndItemEffect(true);
                    UseInBattle(gm, eInstageItemType.Crown);
                    return 2f;
                case 8:
                    Check("Crown: its countdown ring shows (and the game's Crown gauge)", gm.GetItemEffectTimeRatio() > 0.5f, Timers(gm, me));
                    gm.EndItemEffect(true);
                    mgr.FinishCrown(me);
                    return 1f;
                case 9:
                    // Hand: you steal.
                    _steals.Clear();
                    mgr.MultiplaySetItem(cpus[0], Reward.crown);
                    UseInBattle(gm, eInstageItemType.Hand);
                    return 1.5f;
                case 10:
                    FrameCapture.Request("bb_hand");
                    Check("Hand: the stolen freebie is in your slot, ready to use",
                        me.multiItemType != Reward.none && gm._nowInventoryItem == BattleFreebies.FreebieOf(me.multiItemType) && gm._itemEffectTimer < 0f,
                        $"steals: {string.Join("; ", _steals)}; you: {Timers(gm, me)}; {Who(cpus[0])} holds {cpus[0].multiItemType}");
                    // A CPU steals it back (the game's CPU Hand picks a random holder: only you hold something).
                    _steals.Clear();
                    foreach (var c in cpus) if (c.Pointer != cpus[1].Pointer) mgr.MultiplaySetItem(c, Reward.none);
                    mgr.MultiplaySetItem(cpus[1], Reward.hand);
                    cpus[1].MultiItemUse();
                    return 1.5f;
                case 11:
                    Check("Hand: a freebie stolen from you leaves your slot", gm._nowInventoryItem == eInstageItemType.NoItem && me.multiItemType == Reward.none,
                        $"steals: {string.Join("; ", _steals)}; you: {Timers(gm, me)}");
                    // Confiscation 1: your Crown in use, a CPU holding a reward, a CPU's Crown in use, a CPU's Shoes dash.
                    UseInBattle(gm, eInstageItemType.Crown);
                    mgr.MultiplaySetItem(cpus[0], Reward.bubble);
                    mgr.MultiplaySetItem(cpus[1], Reward.crown); cpus[1].MultiItemUse();
                    mgr.MultiplaySetItem(cpus[2], Reward.shoes); cpus[2].MultiItemUse();
                    return 1.5f;
                case 12:
                    if (KingChances.Pending) { _step = 12; return 0.5f; }   // another King Chance is being shown
                    Msg($"before confiscation: you {Timers(gm, me)} | {Holds(mgr)}");
                    _hc0 = KingChances.HappenedCount;
                    KingChances.Fire(Chance.itemConfiscation, filter: false);
                    return 1f;
                case 13:
                {
                    // The King's message comes first: wait for the effect, then a moment for the game to end the Crowns.
                    if (KingChances.HappenedCount == _hc0 && Time.unscaledTime - _bbT0 < 60f) { _step = 13; return 0.25f; }
                    if (!_hcSeen) { _hcSeen = true; _step = 13; return 0.4f; }
                    _hcSeen = false;
                    FrameCapture.Request("bb_confiscation");
                    bool youOk = gm._nowInventoryItem == eInstageItemType.NoItem && gm._itemEffectTimer < 0f && (st?.timeCrownPower ?? 0f) <= 0f && !me.isMultiCrownPower;
                    bool cpusOk = cpus.All(c => c.multiItemType == Reward.none && (Status(c)?.timeCrownPower ?? 0f) <= 0f && !c.isMultiCrownPower && !BattleDash.Has(c));
                    Check("Confiscation: freebies in use and held ones are taken from everyone", youOk && cpusOk, $"you {Timers(gm, me)} | {Holds(mgr)}");
                    // Confiscation 2: a freebie in reserve in your slot.
                    gm.SetInventoryItem(eInstageItemType.Bubble, true, true);
                    return 1f;
                }
                case 14:
                    if (KingChances.Pending) { _step = 14; return 0.5f; }
                    Msg($"before confiscation 2: you {Timers(gm, me)}");
                    KingChances.Fire(Chance.itemConfiscation, filter: false);
                    return 2.5f;
                case 15:
                    Check("Confiscation: your freebie in reserve is taken", gm._nowInventoryItem == eInstageItemType.NoItem && me.multiItemType == Reward.none, Timers(gm, me));
                    // King Box freebies: you get Smoke from a King Box; CPUs never get one.
                    BattleItemPools.SetKingBox(Array.Empty<Reward>(), new[] { eInstageItemType.Smoke });
                    mgr.multiplayBigBattle.MultiplayGetKingBox(me);
                    return 0.5f;
                case 16:
                {
                    _bbT0 = Time.unscaledTime;
                    Check("King Box: can give you Smoke (no reward type), with the roulette", gm._nowInventoryItem == eInstageItemType.Smoke, $"{Timers(gm, me)}; given {BattleItemPools.KingBoxFreebiesGiven}");
                    BattleItemPools.SetKingBox(new[] { Reward.crown }, new[] { eInstageItemType.Smoke });
                    var got = new List<Reward>();
                    for (int k = 0; k < 6; k++) { mgr.MultiplaySetItem(cpus[0], Reward.none); mgr.multiplayBigBattle.MultiplayGetKingBox(cpus[0]); got.Add(cpus[0].multiItemType); }
                    Check("King Box: a CPU gets a reward from the pool, never a freebie it can't use", got.All(r => r == Reward.crown), string.Join(", ", got));
                    BattleItemPools.Reset();
                    // King Chests: with Smoke (no reward type) in your slot the game's host loop would let you take one;
                    // the API refuses it there every frame, like a regular freebie.
                    _chest0 = UnusedFreebies.ChestsRefused;
                    _step = 160;
                    return 1f;
                }
                case 160:
                {
                    int refused = UnusedFreebies.ChestsRefused - _chest0;
                    Check("King Chest: no chest for you while Smoke is in your slot (the game's host check is refused every frame)",
                        gm._nowInventoryItem == eInstageItemType.Smoke && refused > 20, $"refused {refused}x in 1 s; {Timers(gm, me)}");
                    gm.EndItemEffect(true);
                    gm.SetInventoryItem(eInstageItemType.NoItem, false, true);
                    ClearDash(mgr);
                    _chest1 = UnusedFreebies.ChestsRefused;
                    _step = 161;
                    return 1f;
                }
                case 161:
                    Check("King Chest: with no unused freebie in your slot nothing is refused", UnusedFreebies.ChestsRefused == _chest1,
                        $"refused {UnusedFreebies.ChestsRefused - _chest1}x in 1 s; slot {gm._nowInventoryItem}");
                    _step = 17;
                    return 0.5f;
                // The on-screen timers: pictures of the ring over Shoes' 10 s and Crown's 15 s.
                case 17:
                    // Handed over like a battle box does (the roulette), then used only after the roulette has landed.
                    gm.EndItemEffect(true);
                    gm.SetInventoryItem(eInstageItemType.Shoes, true, false);
                    _step = 117;
                    return 2.5f;
                case 117:
                    Msg($"Shoes after the roulette: slot {gm._nowInventoryItem}, HUD icon '{gm.MainUI?.ItemUI?.ItemIcon?.sprite?.name}'");
                    gm.ActivateItemEffect();
                    _bbItem = eInstageItemType.Shoes; _bbT0 = Time.unscaledTime;
                    _step = 18;
                    return 1f;
                case 18: case 19: case 20:
                {
                    float t = Time.unscaledTime - _bbT0;
                    FrameCapture.Request($"bb_shoes_ring_{t:0}s");
                    Msg($"Shoes on screen +{t:0.0} s: ring {gm.GetItemEffectTimeRatio():0.00}, countdown {gm._itemEffectTimer:0.0}, dash {Status(me)?.infinityDashTime:0.0}, UI gauge {gm.MainUI?.ItemUI?.ItemTimerGauge?.fillAmount:0.00}, icon '{gm.MainUI?.ItemUI?.ItemIcon?.sprite?.name}'");
                    return s == 20 ? 1.6f : 1.5f;
                }
                case 21:
                    Msg($"Shoes on screen +{Time.unscaledTime - _bbT0:0.0} s: slot {gm._nowInventoryItem}, dash flag {BattleDash.Has(me)}");
                    gm.EndItemEffect(true);
                    gm.SetInventoryItem(eInstageItemType.Crown, true, false);
                    _step = 121;
                    return 2.5f;
                case 121:
                    Msg($"Crown after the roulette: slot {gm._nowInventoryItem}, HUD icon '{gm.MainUI?.ItemUI?.ItemIcon?.sprite?.name}'");
                    gm.ActivateItemEffect();
                    _bbItem = eInstageItemType.Crown; _bbT0 = Time.unscaledTime;
                    _step = 22;
                    return 1f;
                case 22: case 23: case 24:
                {
                    float t = Time.unscaledTime - _bbT0;
                    FrameCapture.Request($"bb_crown_ring_{t:0}s");
                    Msg($"Crown on screen +{t:0.0} s: ring {gm.GetItemEffectTimeRatio():0.00}, countdown {gm._itemEffectTimer:0.0}, crown {Status(me)?.timeCrownPower:0.0}, UI gauge {gm.MainUI?.ItemUI?.ItemTimerGauge?.fillAmount:0.00}, icon '{gm.MainUI?.ItemUI?.ItemIcon?.sprite?.name}'");
                    return s == 24 ? 1.6f : 1.5f;
                }
                case 25:
                    Check($"Crown (battle): HUD, Crown power and the prince's Crown end after {BattleFreebies.CrownTime:0} s",
                        gm._nowInventoryItem == eInstageItemType.NoItem && !me.isMultiCrownPower && (Status(me)?.timeCrownPower ?? 0f) <= 0f,
                        $"+{Time.unscaledTime - _bbT0:0.0} s: slot {gm._nowInventoryItem}, crown power {me.isMultiCrownPower}, status crown {Status(me)?.timeCrownPower:0.0}, gauge max {me.multiItemGaugeTimeMax:0.0}");
                    gm.EndItemEffect(true);
                    gm.SetInventoryItem(eInstageItemType.SP_Crown, true, false);
                    return 2.5f;
                case 26:
                    gm.ActivateItemEffect();
                    _bbT0 = Time.unscaledTime;
                    return 1.5f;
                case 27:
                    Check($"Special Crown (battle): {BattleFreebies.CrownPlusTime:0} s", Mathf.Abs(gm._itemEffectTimer - (BattleFreebies.CrownPlusTime - 1.5f)) < 0.3f
                        && Mathf.Abs((Status(me)?.timeCrownPower ?? 0f) - (BattleFreebies.CrownPlusTime - 1.5f)) < 0.5f && Mathf.Abs(me.multiItemGaugeTimeMax - BattleFreebies.CrownPlusTime) < 0.1f,
                        $"+1.5 s: countdown {gm._itemEffectTimer:0.0}, crown {Status(me)?.timeCrownPower:0.0}, gauge {me.multiItemGaugeTime:0.0}/{me.multiItemGaugeTimeMax:0.0}, ring {gm.GetItemEffectTimeRatio():0.00}");
                    return BattleFreebies.CrownPlusTime - 1.5f + 0.8f;
                case 28:
                    Check("Special Crown (battle): ends with its time", gm._nowInventoryItem == eInstageItemType.NoItem && !me.isMultiCrownPower,
                        $"+{Time.unscaledTime - _bbT0:0.0} s: slot {gm._nowInventoryItem}, crown power {me.isMultiCrownPower}");
                    // The King Box reward window's text for Shoes and Crown.
                    BattleItemPools.SetKingBox(new[] { Reward.shoes });
                    mgr.MultiplaySetItem(me, Reward.none);
                    mgr.multiplayBigBattle.MultiplayGetKingBox(me);
                    return 1.2f;
                case 29:
                    FrameCapture.Request("bb_kingbox_shoes_text");
                    Msg($"King Box Shoes text: '{mgr.textMeshKingBoxExplain?.text?.Replace("\n", " ")}' (reward shown {mgr.getKingRewardType})");
                    _kbText1 = mgr.textMeshKingBoxExplain?.text ?? "";
                    return 4f;
                case 30:
                    BattleItemPools.SetKingBox(new[] { Reward.crown });
                    mgr.MultiplaySetItem(me, Reward.none);
                    gm.EndItemEffect(true);
                    gm.SetInventoryItem(eInstageItemType.NoItem, false, true);
                    mgr.multiplayBigBattle.MultiplayGetKingBox(me);
                    return 1.2f;
                case 31:
                {
                    FrameCapture.Request("bb_kingbox_crown_text");
                    string t2 = mgr.textMeshKingBoxExplain?.text ?? "";
                    Msg($"King Box Crown text: '{t2.Replace("\n", " ")}' (reward shown {mgr.getKingRewardType})");
                    Check("King Box texts show the new times", _kbText1.Contains($"{BattleDash.ShoesTime:0}") && !_kbText1.Contains("10") && t2.Contains($"{BattleFreebies.CrownTime:0}") && !t2.Contains("15"),
                        $"Shoes '{_kbText1}', Crown '{t2}'");
                    BattleItemPools.Reset();
                    return 4f;
                }
                // Hoop: the random warp.
                case 32:
                    gm.EndItemEffect(true);
                    gm.SetInventoryItem(eInstageItemType.NoItem, false, true);
                    mgr.MultiplaySetItem(me, Reward.none);
                    gm.SetInventoryItem(eInstageItemType.Hoop, true, false);
                    return 2.5f;
                case 33:
                    _hoopPos = me.transform.position;
                    gm.ActivateItemEffect();
                    FrameCapture.Request("bb_hoop_in");
                    return 0.15f;
                case 34:
                    // A warp may be redone if it landed too close: wait for the arrival (the patch measures it), up to 6 s.
                    if (UnusedFreebies.LastWarpDistance < 0f && _hoopWaits++ < 24) { _step = 34; return 0.25f; }
                    return 0.3f;
                case 35:
                {
                    FrameCapture.Request("bb_hoop_out");
                    float moved = Vector3.Distance(_hoopPos, me.transform.position);
                    Check("Hoop (battle): drops you to a random spot on the map", UnusedFreebies.Warps > 0 && moved > 3f,
                        $"warps {UnusedFreebies.Warps}, moved {moved:0.0} (measured by the patch {UnusedFreebies.LastWarpDistance:0.0}), slot {gm._nowInventoryItem}, countdown {gm._itemEffectTimer:0.0}");
                    return 1f;
                }
                // Hand: the others' freebies shown while Hand is in your slot, taken from the nearest.
                case 36:
                {
                    gm.EndItemEffect(true);
                    gm.SetInventoryItem(eInstageItemType.NoItem, false, true);
                    mgr.MultiplaySetItem(me, Reward.none);
                    foreach (var c in cpus) mgr.MultiplaySetItem(c, Reward.none);
                    mgr.MultiplaySetItem(cpus[0], Reward.crown);
                    mgr.MultiplaySetItem(cpus[1], Reward.bubble);
                    // cpu2 right next to you, cpu1 farther away.
                    var fwd = me.transform.forward; fwd.y = 0f; if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward; fwd.Normalize();
                    cpus[1].transform.position = me.transform.position + fwd * 3f;
                    cpus[0].transform.position = me.transform.position + fwd * 12f + Vector3.right * 4f;
                    gm.SetInventoryItem(eInstageItemType.Hand, true, false);
                    _iconsBefore = BattleFreebies.HandIconsDrawn;
                    _handGivenAt = Time.unscaledTime;
                    _handWaits = 0;
                    return 2.5f;
                }
                case 37:
                {
                    // The roulette / pickup animation (~3-4 s) refuses the freebie: wait until the game lets it be used.
                    if ((!gm.CanActivateItem() || me.involvedCore != null || Time.unscaledTime - _handGivenAt < 4.5f) && _handWaits++ < 40) { _step = 37; return 0.25f; }
                    Msg($"before using Hand ({Time.unscaledTime - _handGivenAt:0.0} s after it was given): {UseState(gm, me)}; nearest steals so far {BattleFreebies.NearestSteals}");
                    _nearest0 = BattleFreebies.NearestSteals;
                    mgr.MultiplaySetItem(cpus[0], Reward.crown);
                    mgr.MultiplaySetItem(cpus[1], Reward.bubble);
                    {
                        // The CPUs move while the test waits: put cpu2 right next to you and cpu1 farther away again.
                        var fwd2 = me.transform.forward; fwd2.y = 0f; if (fwd2.sqrMagnitude < 0.01f) fwd2 = Vector3.forward; fwd2.Normalize();
                        cpus[1].transform.position = me.transform.position + fwd2 * 6f;
                        cpus[0].transform.position = me.transform.position + fwd2 * 16f + Vector3.right * 5f;
                    }
                    FrameCapture.Request("bb_hand_reveal");
                    Check("Hand: the others' freebies show above them while Hand is in your slot", BattleFreebies.HandIconsDrawn > _iconsBefore,
                        $"icons drawn {BattleFreebies.HandIconsDrawn - _iconsBefore}; cpu1 holds {BattleFreebies.HeldBy(cpus[0])}, cpu2 holds {BattleFreebies.HeldBy(cpus[1])}, nearest {Who(BattleFreebies.NearestHolder(me))}");
                    _steals.Clear();
                    gm.ActivateItemEffect();
                    return 1.5f;
                }
                case 38:
                    Check("Hand: takes from the nearest prince who holds a freebie", gm._nowInventoryItem == eInstageItemType.Bubble && me.multiItemType == Reward.bubble && BattleFreebies.NearestSteals > 0,
                        $"steals: {string.Join("; ", _steals)}; you: slot {gm._nowInventoryItem}, holds {me.multiItemType}; cpu1 {cpus[0].multiItemType}, cpu2 {cpus[1].multiItemType}; " +
                        $"nearest steals {BattleFreebies.NearestSteals - _nearest0}; now {UseState(gm, me)}");
                    return 0.5f;
                default:
                    _bbFilter?.Dispose();
                    return -1f;
            }
        }

        // ------------------------------------------------------------------ Race: item boxes and the Rocket (Drink) softlock

        private static List<MainGameItemBox> _raceBoxes;
        private static int _rbBox;

        private static string UseState(MainGameManager gm, MainGameCore me)
        {
            var st = Status(me);
            string can = "?", forbid = "?";
            try { can = gm.CanActivateItem().ToString(); } catch { }
            try { forbid = gm.MainUI?.ItemUI?.IsItemForbidden().ToString(); } catch { }
            string tips = "?", demo = "?", freeze = "?";
            try { tips = gm.MainUI?.IsTipsActive().ToString(); } catch { }
            try { demo = $"get {gm.MainUI?.ItemUI?._getDemoState} disabled {gm.MainUI?.ItemUI?._disabled}"; } catch { }
            try { freeze = gm.InFreeze.ToString(); } catch { }
            return $"tips open {tips}, HUD {demo}, freeze {freeze}; slot {gm._nowInventoryItem}, countdown {gm._itemEffectTimer:0.0}, holds {me.multiItemType} (status {st?.itemType}), CanActivateItem {can}, forbidden {forbid}, " +
                   $"rolled-up {me.involvedCore != null}, race goal {me.isRaceGoal}, watching {GameRefs.Multiplay?.isWatching}, rocket {me.isMultiRocket}, itemTimer {st?.itemEffectTimer:0.0}, rule {GameRefs.Multiplay?.nowRule}";
        }

        private static float RaceBoxStep()
        {
            var gm = GameRefs.Main;
            var mgr = GameRefs.Multiplay;
            var me = gm?.GetCore();
            if (gm == null || mgr == null || me == null) throw new InvalidOperationException("no race");
            int s = _step++;
            switch (s)
            {
                case 0:
                {
                    HideExplorer();
                    _raceBoxes = Resources.FindObjectsOfTypeAll<MainGameItemBox>().Where(b => b != null && b.gameObject.scene.isLoaded && b.IsMultiplayBox).ToList();
                    Msg($"race: {_raceBoxes.Count} item boxes; {UseState(gm, me)}");
                    if (_raceBoxes.Count > 0)
                    {
                        var draws = Enumerable.Range(0, 30).Select(_ => (eInstageItemType)(_raceBoxes[0].GetRandomItemMulti() + 1)).GroupBy(i => i).Select(g => $"{g.Key} x{g.Count()}");
                        Msg($"race box draws (30): {string.Join(", ", draws)}");
                    }
                    // Drink (the Rocket icon), handed over like a box does.
                    gm.EndItemEffect(true);
                    gm.SetInventoryItem(eInstageItemType.Drink, true, false);
                    return 2.5f;
                }
                case 1:
                    FrameCapture.Request("race_drink_roulette");
                    Msg($"map tables filled: {MapTables.Filled.Count}; HUD parts missing on this map: {(HudSafety.Missing.Count == 0 ? "none" : string.Join(", ", HudSafety.Missing))} (API HUD frames {HudSafety.Frames})");
                    Msg("Drink, 2.5 s after the pickup: " + UseState(gm, me));
                    _step = 101;
                    return 1.5f;
                case 101:
                    Msg("Drink, 4 s after the pickup: " + UseState(gm, me));
                    return 2f;
                case 102:
                    Msg("Drink, 6 s after the pickup: " + UseState(gm, me));
                    return 2f;
                case 103:
                    Check("Race: the use button works for a Drink (Rocket) from the roulette (8 s after the pickup)", gm.CanActivateItem() && gm._nowInventoryItem == eInstageItemType.Drink && me.multiItemType == Reward.rocket,
                        UseState(gm, me));
                    gm.ActivateItemEffect();
                    _step = 2;
                    return 1f;
                case 2:
                    FrameCapture.Request("race_drink_used");
                    Msg("Drink +1 s after use: " + UseState(gm, me));
                    gm.EndItemEffect(true);
                    gm.SetInventoryItem(eInstageItemType.SP_Drink, true, false);
                    return 2.5f;
                case 3:
                    Msg("Special Drink after the roulette: " + UseState(gm, me));
                    gm.ActivateItemEffect();
                    return 1f;
                case 4:
                    Msg("Special Drink +1 s after use: " + UseState(gm, me));
                    gm.EndItemEffect(true);
                    gm.SetInventoryItem(eInstageItemType.NoItem, false, true);
                    mgr.MultiplaySetItem(me, Reward.none);
                    _rbBox = 0;
                    return 1f;
                // Real box pickups (the game's Collected), then use, five times.
                case 5: case 7: case 9: case 11: case 13:
                {
                    var box = _raceBoxes?.Skip(_rbBox++).FirstOrDefault(b => b.gameObject.activeInHierarchy && !b.enabled);
                    if (box == null) { Msg("no untaken box left"); _step = 15; return 0.5f; }
                    gm.EndItemEffect(true);
                    gm.SetInventoryItem(eInstageItemType.NoItem, false, true);
                    mgr.MultiplaySetItem(me, Reward.none);
                    box.Collected();
                    return 2.5f;
                }
                case 6: case 8: case 10: case 12: case 14:
                    Msg($"box pickup {(s - 4) / 2}: after the roulette: {UseState(gm, me)}");
                    gm.ActivateItemEffect();
                    Msg($"box pickup {(s - 4) / 2}: right after use: {UseState(gm, me)}");
                    return 1.5f;
                case 15:
                    Msg("end: " + UseState(gm, me));
                    return -1f;
                default:
                    return -1f;
            }
        }

        // ------------------------------------------------------------------ Mushrooms in a battle

        private static int _bonusCalls, _effectCalls;
        private static string _bonusTypes = "";

        private static void BeforeMushroomBonus(Reward __0) { _bonusCalls++; _bonusTypes += __0 + " "; }
        private static void BeforeMushroomEffect() { _effectCalls++; }

        /// <summary>Mushroom and Special Mushroom used the game's way in a battle: which of the game's mushroom calls run, and pictures.</summary>
        private static float BattleMushroomStep()
        {
            var gm = GameRefs.Main;
            var mgr = GameRefs.Multiplay;
            var me = gm?.GetCore();
            if (gm == null || mgr == null || me == null) throw new InvalidOperationException("no battle");
            int s = _step++;
            if (s == 0)
            {
                HideExplorer();
                var h = ApiCore.Instance.HarmonyInstance;
                h.Patch(HarmonyLib.AccessTools.Method(typeof(MainGameCore), nameof(MainGameCore.SetMushroomBonus)),
                    prefix: new HarmonyLib.HarmonyMethod(typeof(PatchTest).GetMethod(nameof(BeforeMushroomBonus), BindingFlags.NonPublic | BindingFlags.Static)));
                h.Patch(HarmonyLib.AccessTools.Method(typeof(MainGameManager), nameof(MainGameManager.RequestMushroomEffect)),
                    prefix: new HarmonyLib.HarmonyMethod(typeof(PatchTest).GetMethod(nameof(BeforeMushroomEffect), BindingFlags.NonPublic | BindingFlags.Static)));
                Msg("the game's Mushroom effect prefab: " + PrefabName());
                SurveyBoxes();
                return 1f;
            }
            if (s > 4) return -1f;
            bool plus = s >= 3;
            var item = plus ? eInstageItemType.SP_Mushroom : eInstageItemType.Mushroom;
            var reward = plus ? Reward.mushroomPlus : Reward.mushroom;
            if (s % 2 == 1)
            {
                // Give it like a King Box / item box does, then use it like the player (ActivateItemEffect).
                _bonusCalls = 0; _effectCalls = 0; _bonusTypes = "";
                _fxBefore = PlayingEffects();
                _objsBefore = EffectObjects();
                _sizeBefore = me.multiCoreSize;
                gm.EndItemEffect(true);
                mgr.MultiplaySetItem(me, reward);
                Msg($"{item}: after MultiplaySetItem the slot shows {gm._nowInventoryItem}, the prince holds {me.multiItemType}");
                if (gm._nowInventoryItem != item) gm.SetInventoryItem(item, false, true);
                gm.ActivateItemEffect();
                Msg($"{item}: used; slot now {gm._nowInventoryItem}, prince holds {me.multiItemType}, SetMushroomBonus calls {_bonusCalls} ({_bonusTypes.Trim()})");
                return 0.4f;
            }
            string shot = plus ? "bm_spmushroom" : "bm_mushroom";
            FrameCapture.Request(shot);
            var now = PlayingEffects();
            var started = now.Where(kv => kv.Value > _fxBefore.GetValueOrDefault(kv.Key)).Select(kv => $"{kv.Key} x{kv.Value - _fxBefore.GetValueOrDefault(kv.Key)}").ToList();
            Check($"{item} in a battle: the game's mushroom bonus and effect ran", _bonusCalls >= 1 && _effectCalls >= 1,
                $"SetMushroomBonus {_bonusCalls} ({_bonusTypes.Trim()}), RequestMushroomEffect {_effectCalls}, size {_sizeBefore:0.##} -> {me.multiCoreSize}");
            Msg($"{shot} after 0.4 s: particle systems that started: {(started.Count == 0 ? "none" : string.Join(", ", started))}; new effect objects: {NewEffectObjects()}");
            return 2.5f;
        }

        private static float _sizeBefore;

        /// <summary>Survey: every item box in the scene (what it holds, what it shows) and loaded objects named after freebies.</summary>
        private static void SurveyBoxes()
        {
            try
            {
                var names = MainGameItemBox.itemName;
                Msg($"item box names table: {(names == null ? "null" : string.Join(", ", names))}");
                var boxes = Resources.FindObjectsOfTypeAll<MainGameItemBox>().Where(b => b != null && b.gameObject.scene.isLoaded).ToList();
                Msg($"{boxes.Count} item box(es) in the scene");
                foreach (var b in boxes.Take(12))
                {
                    var bits = new List<string>();
                    for (int i = 0; i < 32; i++) if ((b.ContainItem & (1 << i)) != 0) bits.Add(i < (names?.Length ?? 0) ? names[i] : "#" + i);
                    string model = "no ItemParent";
                    if (b.ItemParent != null)
                    {
                        var parts = new List<string>();
                        foreach (var t in b.ItemParent.GetComponentsInChildren<Transform>(true))
                        {
                            string mesh = "";
                            try { var mf = t.GetComponent<MeshFilter>(); if (mf != null && mf.sharedMesh != null) mesh = " mesh " + mf.sharedMesh.name; } catch { }
                            try { var sm = t.GetComponent<SkinnedMeshRenderer>(); if (sm != null && sm.sharedMesh != null) mesh += " skinned " + sm.sharedMesh.name; } catch { }
                            try { var r = t.GetComponent<Renderer>(); if (r != null && r.sharedMaterial != null) mesh += " mat " + r.sharedMaterial.name; } catch { }
                            parts.Add($"{t.name}{(t.gameObject.activeSelf ? "" : " (off)")}{mesh}");
                        }
                        model = string.Join(" | ", parts.Take(8));
                    }
                    Msg($"box {b.name} active {b.gameObject.activeInHierarchy} enabled {b.enabled} multi {b.IsMultiplayBox} contains 0x{b.ContainItem:X} [{string.Join(", ", bits)}] at {b.transform.position}; shows: {model}");
                }
                var words = new[] { "Drink", "Magnet", "Pickel", "Sonar", "Timer", "Tarai", "Spider", "Bubble", "Mushroom", "Hand", "Hoop", "Smoke", "Shoes", "Crown", "Dice", "Sunglass", "Item" };
                var found = new SortedDictionary<string, int>();
                foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
                {
                    try
                    {
                        if (go == null || go.transform.parent != null) continue;   // roots only (scene roots and loaded prefabs)
                        string n = go.name;
                        if (words.Any(w => n.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            string key = $"{n}{(go.scene.isLoaded ? "" : " [asset]")}";
                            found[key] = found.GetValueOrDefault(key) + 1;
                        }
                    }
                    catch { }
                }
                Msg($"root objects named after freebies ({found.Count}): {string.Join(", ", found.Take(80).Select(kv => kv.Value > 1 ? $"{kv.Key} x{kv.Value}" : kv.Key))}");
                var meshes = new SortedSet<string>();
                foreach (var m in Resources.FindObjectsOfTypeAll<Mesh>())
                    try { if (m != null && words.Take(16).Any(w => m.name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)) meshes.Add(m.name); } catch { }
                Msg($"meshes named after freebies ({meshes.Count}): {string.Join(", ", meshes.Take(80))}");
            }
            catch (Exception e) { Msg("survey: " + e.Message); }
        }

        // ------------------------------------------------------------------ Freebies (story stage)

        private static float _el0;
        private static Dictionary<string, int> _fx;

        private static float FreebieStep()
        {
            var gm = GameRefs.Main;
            var core = gm.GetCore();
            switch (_step++)
            {
                case 0:
                    HideExplorer();
                    SurveyBoxes();
                    // The HUD for a story freebie of the game's own (Drink), to compare with Shoes / Crown.
                    Use(gm, eInstageItemType.Drink, out _, out _);
                    Msg($"Drink: slot {gm._nowInventoryItem}, countdown {gm._itemEffectTimer:0.0} s");
                    _step = 100;
                    return 0.7f;
                case 100:
                    FrameCapture.Request("fb_drink");
                    gm.EndItemEffect(true);
                    _step = 101;
                    return 0.5f;
                case 101:
                    _step = 1;
                    Check("FreebieRules lists Shoes, Crown and Mushroom in story stages",
                        FreebieRules.WorksInStory(eInstageItemType.Shoes) && FreebieRules.WorksInStory(eInstageItemType.Crown) && FreebieRules.WorksInStory(eInstageItemType.Mushroom));
                    // Shoes: tired out first, then used the game's way.
                    core._oujiDashElapsedTimer = core.MaxOujiDashTime + 0.5f;
                    core._oujiDashExhaustTimer = 3f;
                    Use(gm, eInstageItemType.Shoes, out _, out _);
                    Check("Shoes: unlimited dash on, stamina full", GameRefs.Global._debugInifiniteDash && core._oujiDashElapsedTimer == 0f && StoryFreebies.UnlimitedDash,
                        $"switch {GameRefs.Global._debugInifiniteDash}, stamina used {core._oujiDashElapsedTimer:0.##}, running '{StoryFreebies.Describe()}'");
                    Check($"Shoes: stays on the HUD with its {StoryFreebies.ShoesTime:0} s countdown", gm._nowInventoryItem == eInstageItemType.Shoes && Mathf.Abs(gm._itemEffectTimer - StoryFreebies.ShoesTime) < 0.2f,
                        $"slot {gm._nowInventoryItem}, countdown {gm._itemEffectTimer:0.0} s");
                    return 0.7f;
                case 1:
                    FrameCapture.Request("fb_shoes");
                    Msg($"Shoes after 0.7 s: slot {gm._nowInventoryItem}, countdown {gm._itemEffectTimer:0.0} s");
                    core._oujiDashTimer = 5f;   // dashing: the story dash timer counts stamina while this is above 0
                    _el0 = core._oujiDashElapsedTimer;
                    return 1f;
                case 2:
                    Check("Shoes: 1 s of dashing uses no stamina", core._oujiDashElapsedTimer <= _el0 + 0.01f, $"{_el0:0.##} -> {core._oujiDashElapsedTimer:0.##}");
                    core._oujiDashTimer = 0f;
                    gm._itemEffectTimer = 0.05f;   // let the game's own countdown run out now
                    return 0.6f;
                case 3:
                    Check("Shoes: ends with the game's countdown", !GameRefs.Global._debugInifiniteDash && !StoryFreebies.UnlimitedDash && gm._itemEffectTimer < 0f,
                        $"switch {GameRefs.Global._debugInifiniteDash}, countdown {gm._itemEffectTimer:0.0}, slot {gm._nowInventoryItem}");
                    {
                        float speed = core.NowMaxSpeed;
                        Use(gm, eInstageItemType.Crown, out _, out _);
                        Check("Crown: 1.5x speed with its countdown", Mathf.Abs(gm.GetItemMultiplier(eInstageItemType.Drink) - 1.5f) < 0.01f && StoryFreebies.CrownActive
                            && gm._nowInventoryItem == eInstageItemType.Crown && Mathf.Abs(gm._itemEffectTimer - StoryFreebies.CrownTime) < 0.2f,
                            $"multiplier {gm.GetItemMultiplier(eInstageItemType.Drink):0.##}, max speed {speed:0.###} -> {core.NowMaxSpeed:0.###}, slot {gm._nowInventoryItem}, countdown {gm._itemEffectTimer:0.0}");
                    }
                    return 1f;
                case 4:
                    FrameCapture.Request("fb_crown");
                    gm._itemEffectTimer = 0.05f;   // the game's countdown runs out
                    return 1.5f;
                case 5:
                    Check("Crown: speed and glow end with the countdown", !StoryFreebies.CrownActive && gm.GetItemMultiplier(eInstageItemType.Drink) < 1.01f,
                        $"multiplier {gm.GetItemMultiplier(eInstageItemType.Drink):0.##}, slot {gm._nowInventoryItem}");
                    FrameCapture.Request("fb_crown_after");
                    _fx = ActiveEffects();
                    return 0.5f;
                case 6:
                case 8:
                case 10:
                case 12:
                {
                    // Mushroom / Special Mushroom: with the API's effect, then the game's own (item switched off) for comparison.
                    var (m, patched) = MushroomRun(_step - 1);
                    _fxBefore = PlayingEffects();
                    _onKatamariBefore = OnKatamari(core);
                    _objsBefore = EffectObjects();
                    if (_step - 1 == 6) Msg("the game's Mushroom effect prefab (EffectManager._efItem_MushroomPrefab): " + PrefabName());
                    if (!patched) StoryFreebies.SetItem(m, false);
                    float fxScale = core._childEffectScale;
                    Use(gm, m, out int b, out int a);
                    if (!patched) StoryFreebies.SetItem(m, true);
                    if (patched) Check($"{m}: grows at once", a > b, $"{b} -> {a} (+{a - b}); {StoryFreebies.LastMushroom}");
                    if (patched) Check($"{m}: stays on the HUD for the battle's time", gm._nowInventoryItem == m && gm._itemEffectTimer > StoryFreebies.MushroomShowTime - 0.2f,
                        $"slot {gm._nowInventoryItem}, countdown {gm._itemEffectTimer:0.0} s (Game.ONLINE_ITEM_TIME {StoryFreebies.MushroomShowTime:0.##})");
                    else Msg($"{m} (game only): size {b} -> {a}");
                    Msg($"{m}{(patched ? "" : " (game only)")}: effect scale (MainGameCore._childEffectScale) {fxScale:0.###}");
                    return 0.3f;
                }
                case 7:
                case 9:
                case 11:
                case 13:
                {
                    var (m, patched) = MushroomRun(_step - 2);
                    string shot = (m == eInstageItemType.Mushroom ? "fb_mushroom" : "fb_spmushroom") + (patched ? "" : "_game");
                    FrameCapture.Request(shot);
                    var now = PlayingEffects();
                    var started = now.Where(kv => kv.Value > _fxBefore.GetValueOrDefault(kv.Key)).Select(kv => $"{kv.Key} x{kv.Value - _fxBefore.GetValueOrDefault(kv.Key)}").ToList();
                    var added = OnKatamari(core).Except(_onKatamariBefore).ToList();
                    Msg($"{shot}: new effect objects anywhere: {NewEffectObjects()}");
                    int live = MushroomParticles();
                    if (patched) Check($"{m}: the game's Mushroom effect plays", live > 0, $"Ef_Mushroom_Mass particles after 0.3 s: {live}");
                    else Msg($"{m} without the API's fix: Ef_Mushroom_Mass particles after 0.3 s: {live} (the game removes its own effect)");
                    Msg($"{shot} after 0.3 s: particle systems that started playing: {(started.Count == 0 ? "none" : string.Join(", ", started))}; " +
                        $"new objects on the katamari: {(added.Count == 0 ? "none" : string.Join(", ", added.Take(12)))}");
                    return 2f;
                }
                case 14:
                {
                    _objsBefore = EffectObjects();
                    GameObject go = null;
                    try { go = gm.effect.PlayEffectParticle(EffectManager.eParticleType.EfItem_Mushroom, core.transform.position); }
                    catch (Exception e) { Msg("direct PlayEffectParticle threw: " + e.Message); }
                    Msg("direct PlayEffectParticle(EfItem_Mushroom) returned: " + Describe(go));
                    return 0.3f;
                }
                case 15:
                    FrameCapture.Request("fb_mushroom_direct");
                    Msg($"fb_mushroom_direct: new effect objects anywhere: {NewEffectObjects()}");
                    return 2f;
                case 16:
                {
                    // The game's own Random in a story stage is never touched: its draws (Drink..Timer, Specials) all work.
                    int before = RandomFreebie.Draws;
                    var rolled = new List<eInstageItemType>();
                    for (int k = 0; k < 12; k++)
                    {
                        gm.EndItemEffect(true);
                        gm.SetInventoryItem(eInstageItemType.NoItem, false, true);
                        gm.GetRandomItem();
                        rolled.Add(gm._nowInventoryItem);
                    }
                    gm.EndItemEffect(true);
                    gm.SetInventoryItem(eInstageItemType.NoItem, false, true);
                    bool vanilla = rolled.All(i => (int)i is >= 1 and <= 5 or >= 16 and <= 20);
                    Check("Random in story: the game's own draws, untouched", vanilla && RandomFreebie.Draws == before,
                        $"{string.Join(", ", rolled)} (replaced by the patch: {RandomFreebie.Draws - before})");
                    // Per-item switch (a host's choice): Shoes off -> not listed, no effect.
                    StoryFreebies.SetItem(eInstageItemType.Shoes, false);
                    bool listed = FreebieRules.WorksInStory(eInstageItemType.Shoes);
                    Use(gm, eInstageItemType.Shoes, out _, out _);
                    bool dash = GameRefs.Global._debugInifiniteDash;
                    StoryFreebies.SetItem(eInstageItemType.Shoes, true);
                    StoryFreebies.StopAll();
                    Check("per-item switch: Shoes off -> not listed, no effect", !listed && !dash, $"listed {listed}, dash switch {dash}; back on: listed {FreebieRules.WorksInStory(eInstageItemType.Shoes)}");
                    return -1f;
                }
                default:
                    return -1f;
            }
        }

        /// <summary>Active particle effects, counted by their root object's name.</summary>
        private static Dictionary<string, int> ActiveEffects()
        {
            var d = new Dictionary<string, int>();
            foreach (var ps in Resources.FindObjectsOfTypeAll<ParticleSystem>())
            {
                try
                {
                    if (ps == null || !ps.gameObject.activeInHierarchy || !ps.gameObject.scene.isLoaded) continue;
                    string name = ps.transform.root.name;
                    d[name] = d.GetValueOrDefault(name) + 1;
                }
                catch { }
            }
            return d;
        }

        private static (eInstageItemType item, bool patched) MushroomRun(int step) => ((step - 6) / 2) switch
        {
            0 => (eInstageItemType.Mushroom, true),
            1 => (eInstageItemType.SP_Mushroom, true),
            2 => (eInstageItemType.Mushroom, false),
            _ => (eInstageItemType.SP_Mushroom, false),
        };

        // Read-only probes for the Mushroom checks. (Hooking EffectManager.PlayEffectParticle breaks it: Il2CppInterop's
        // trampoline throws a NullReferenceException on its Nullable<Vector3> argument and the game's call never runs.)
        private static Dictionary<string, int> _fxBefore = new();
        private static HashSet<string> _onKatamariBefore = new();

        private static HashSet<int> _objsBefore = new();

        /// <summary>Active objects in loaded scenes whose name starts with "Ef" or mentions a mushroom (the effect prefabs' naming).</summary>
        private static HashSet<int> EffectObjects()
        {
            var set = new HashSet<int>();
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                try
                {
                    if (go == null || !go.activeInHierarchy || !go.scene.isLoaded) continue;
                    string n = go.name;
                    if (n.StartsWith("Ef", StringComparison.OrdinalIgnoreCase) || n.IndexOf("mushroom", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("kinoko", StringComparison.OrdinalIgnoreCase) >= 0)
                        set.Add(go.GetInstanceID());
                }
                catch { }
            }
            return set;
        }

        private static string NewEffectObjects()
        {
            var now = EffectObjects();
            var added = now.Except(_objsBefore).ToList();
            if (added.Count == 0) return "none";
            var names = new List<string>();
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
                try { if (go != null && added.Contains(go.GetInstanceID()) && (go.transform.parent == null || !added.Contains(go.transform.parent.gameObject.GetInstanceID()))) names.Add(Describe(go)); } catch { }
            return string.Join("; ", names.Take(8));
        }

        private static string Describe(GameObject go)
        {
            try
            {
                if (go == null) return "nothing (null)";
                int ps = go.GetComponentsInChildren<ParticleSystem>(true).Length, live = 0, r = go.GetComponentsInChildren<Renderer>(true).Length;
                foreach (var p in go.GetComponentsInChildren<ParticleSystem>(true)) live += p.particleCount;
                return $"{go.name} (active {go.activeInHierarchy}, under {go.transform.parent?.name ?? "the scene root"}, {ps} particle system(s) with {live} particle(s), {r} renderer(s), scale {go.transform.lossyScale.x:0.##})";
            }
            catch (Exception e) { return "unreadable: " + e.Message; }
        }

        /// <summary>Live particles in active Mushroom effects (Ef_Mushroom_Mass clones).</summary>
        private static int MushroomParticles()
        {
            int n = 0;
            foreach (var ps in Resources.FindObjectsOfTypeAll<ParticleSystem>())
            {
                try
                {
                    if (ps == null || !ps.gameObject.activeInHierarchy || !ps.gameObject.scene.isLoaded) continue;
                    var t = ps.transform;
                    while (t != null && !t.name.StartsWith("Ef_Mushroom_Mass")) t = t.parent;
                    if (t != null) n += ps.particleCount;
                }
                catch { }
            }
            return n;
        }

        private static string PrefabName()
        {
            try { var p = GameRefs.Main?.effect?._efItem_MushroomPrefab; return p == null ? "not loaded (null)" : p.name; }
            catch (Exception e) { return "unreadable: " + e.Message; }
        }

        /// <summary>Particle systems with live particles, counted by "root/name".</summary>
        private static Dictionary<string, int> PlayingEffects()
        {
            var d = new Dictionary<string, int>();
            foreach (var ps in Resources.FindObjectsOfTypeAll<ParticleSystem>())
            {
                try
                {
                    if (ps == null || !ps.gameObject.activeInHierarchy || !ps.gameObject.scene.isLoaded || !ps.isPlaying || ps.particleCount == 0) continue;
                    string key = ps.transform.root.name + "/" + ps.name;
                    d[key] = d.GetValueOrDefault(key) + 1;
                }
                catch { }
            }
            return d;
        }

        /// <summary>Active objects under the katamari's transform (where the game parents the Mushroom effect), by name and id.</summary>
        private static HashSet<string> OnKatamari(MainGameCore core)
        {
            var set = new HashSet<string>();
            try
            {
                foreach (var t in core.transform.GetComponentsInChildren<Transform>(false))
                    if (t != null && !t.name.StartsWith("MonoAttach")) set.Add($"{t.name}#{t.GetInstanceID()}");
            }
            catch { }
            return set;
        }

        /// <summary>Hides UnityExplorer's windows (if installed) so the test pictures show the stage.</summary>
        private static void HideExplorer()
        {
            try
            {
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var p = a.GetType("UnityExplorer.UI.UIManager")?.GetProperty("ShowMenu", BindingFlags.Public | BindingFlags.Static);
                    if (p == null) continue;
                    p.SetValue(null, false);
                    Msg("UnityExplorer's windows hidden for the pictures");
                    return;
                }
            }
            catch (Exception e) { Msg("couldn't hide UnityExplorer: " + e.Message); }
        }

        /// <summary>Puts a freebie in the slot and uses it the game's way (ActivateItemEffect).</summary>
        private static void Use(MainGameManager gm, eInstageItemType item, out int sizeBefore, out int sizeAfter)
        {
            gm.EndItemEffect(true);
            gm.SetInventoryItem(item, false, true);
            sizeBefore = gm.KatamariSize;
            gm.ActivateItemEffect();
            sizeAfter = gm.KatamariSize;
        }


        // ------------------------------------------------------------------ Race

        private static float RaceStep()
        {
            switch (_step++)
            {
                case 0:
                    Check("race rule", GameRefs.Multiplay?.nowRule == NetworkBattleRule.race, $"rule {GameRefs.Multiplay?.nowRule}");
                    Msg($"race course points {GameRefs.Multiplay?.listRacePointData?.Count}");
                    return 10f;
                case 1:
                    FrameCapture.Request("patch_race");
                    Msg("after 10 s: " + TrainingTrace.Describe());
                    return 1f;
                default:
                    return -1f;
            }
        }

        // ------------------------------------------------------------------ helpers

        private static string Who(MainGameCore core) =>
            core == null ? "?" : core.Pointer == (GameRefs.Main?.GetCore()?.Pointer ?? IntPtr.Zero) ? "you" : $"cpu{core.multiCoreNo}";

        private static MultiplayController.UserStatus Status(MainGameCore core) =>
            core == null ? null : GameRefs.Multiplay?.GetUserStatusFromCoreNo(core.multiCoreNo);

        /// <summary>Your katamari rolls up the nearest object it can take that matches (the match pickup, MultiGetMono).</summary>
        private static string RollUp(Func<MainGameMonoBase, bool> filter, out MainGameMonoBase picked)
        {
            picked = null;
            var gm = GameRefs.Main;
            var core = gm?.GetCore();
            if (core == null) return "no katamari";
            var c = core.transform.position;
            int size = gm.KatamariSize;
            float bestSq = float.MaxValue;
            foreach (var m in Resources.FindObjectsOfTypeAll<MainGameMonoBase>())
            {
                try
                {
                    if (m == null || !m.gameObject.activeInHierarchy || !m.gameObject.scene.isLoaded) continue;
                    if (m.transform.IsChildOf(core.transform) || !filter(m)) continue;
                    int need = gm.GetMonoRequireSize(m.MonoID);
                    if (need <= 0 || need > size) continue;
                    if (m.GetComponentsInChildren<MainGameMonoBase>(true).Length > 1) continue;
                    float sq = (m.transform.position - c).sqrMagnitude;
                    if (sq < bestSq) { bestSq = sq; picked = m; }
                }
                catch { }
            }
            if (picked == null) return "nothing to roll up";
            string name = picked.gameObject.name;
            picked.transform.position = c + Vector3.up * core.KatamariRadius;
            bool ok = core.MultiGetMono(picked);
            return $"{name} (MonoID {picked.MonoID}, target {TargetRound.IsTarget(picked)} worth {TargetRound.TargetsIn(picked)}, {Mathf.Sqrt(bestSq):0.0} away): {ok}";
        }

        private static void LoadSlot(SaveDataController ctrl)
        {
            var data = ctrl.GameData;
            int slot = _slot >= 0 ? _slot : data._SystemData._LastUser;
            var users = data._UserData?._UserDatas;
            if (users == null || slot < 0 || slot >= users.Length) throw new InvalidOperationException($"no slot {slot + 1}");
            var progress = users[slot]?._PriorityData;
            if (progress == null || progress._GameProgression <= 0) throw new InvalidOperationException($"slot {slot + 1} has no game in progress");
            BackupManager.Create("Patch test", $"made before the API patch test loaded slot {slot + 1}");
            Msg($"loading slot {slot + 1}");
            ctrl.SelectLoadDataToGameData(slot);
            var save = GameRefs.Save;
            save.SetUseDataLogoMonoGetFlg();
            OujiShipSponePointList.s_SponeIndex = OujiShipSponePointList.ElevatorSpone;
            save.FrontSceneSelectData = true;
            SingletonMonoBehaviour<EncorsSaveDataChecker>.Instance.CheckEncorsContentsData();
            GameRefs.Sound?.StopBGM(0.5f);
            Il2Cpp.SceneManager.LoadSceneAsync("OujiShip", LoadSceneMode.Single, true);
        }

        /// <summary>Game texts about freebies (tips, system and string tables), for what the unfinished ones were meant to do.</summary>
        private static void DumpTexts()
        {
            string[] words =
            {
                "freebie", "Hoop", "Smoke", "Dice", "Sunglass", "Washtub", "Basin", "Spider", "Bubble", "Mushroom", "Hand", "Shoes", "Crown",
                "Drink", "Magnet", "Pick", "Sonar", "Timer", "Rocket", "Special", "Super",
            };
            int n = 0;
            var seen = new HashSet<string>();
            foreach (var table in Resources.FindObjectsOfTypeAll<TextCommonTable>())
            {
                if (table == null || table.name == "name") continue;
                var list = table.datas;
                if (list == null) continue;
                for (int i = 0; i < list.Count; i++)
                {
                    var e = list[i];
                    string en = e?.english;
                    if (string.IsNullOrEmpty(en) || !seen.Add(table.name + "/" + e.id)) continue;
                    if (!words.Any(w => en.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                    n++;
                    Msg($"text {table.name}/{e.id}: {en.Replace("\n", " / ")} | {e.japanese?.Replace("\n", " / ")}");
                }
            }
            Msg($"{n} texts");
        }

        private static void Finish(string result)
        {
            _armed = false;
            Msg($"{_what ?? "texts"}: {result}");
            Msg("DONE, quitting");
            Application.Quit();
        }
    }
}
