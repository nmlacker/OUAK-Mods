using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Il2Cpp;
using Il2CppApp.KatamariSin;
using Il2CppApp.KatamariSin.InputStatus;
using MelonLoader.Utils;
using OUAKModAPI;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace OUAKDashBar
{
    /// <summary>
    /// Test only: <c>UserData/OUAKDashBar/dashtest.flag</c> runs the in-game check once, then quits the game.
    /// Title -> the last-used slot -> ship -> As Large As Possible 1 -> skip the opening -> scenarios -> quit.
    /// Every save write is blocked while it runs (SaveDataController.SetSaveAction / SetNewDataAction skipped).
    /// Dashes are real: L2 presses go into the game's own pad buffer after InputController.Tick (the way
    /// OUAKSpeedrunTools' DashBugTest does it), so the game's own dash code runs.
    /// Flag words: <c>probe</c> (only dump the game's gauge and show it at a few values), <c>slot N</c>, and where:
    /// <c>battle</c> (Katamari Ball, Battle (Jurassic) against CPU princes), <c>race</c>, <c>target</c> (the API's modes);
    /// none = the story stage.
    /// Lines start with [DashBarTest]; pictures go to UserData/OUAKDashBar/TestShots; per-frame values to dashtest.csv.
    /// </summary>
    internal static class DashBarTest
    {
        private static readonly ModLog Log = ModLog.For("DashBarTest");
        private static string Dir => System.IO.Path.Combine(MelonEnvironment.UserDataDirectory, "OUAKDashBar");
        private static string Shots => System.IO.Path.Combine(Dir, "TestShots");

        private enum Phase { Off, Title, Ship, Intro, Run, Done }
        private static Phase _phase = Phase.Off;
        private static float _at, _deadline, _phaseStart, _shipSeen;
        private static bool _skippedFly, _probeOnly, _blockSaves;
        private static int _slot = -1;
        private static string _where = "story";
        private static bool Multi => _where != "story";
        private static string _lastIntro;
        private static readonly HookSet Hooks = new("ouak.dashbar.test");
        private static readonly List<string> _fails = new();
        private static StreamWriter _csv;

        /// <summary>True while the test runs (the mod's own drawing waits for it in probe mode).</summary>
        internal static bool Running => _phase != Phase.Off && _phase != Phase.Done;
        internal static bool ProbeOnly => _probeOnly && Running;

        internal static void CheckFlag()
        {
            string flag = System.IO.Path.Combine(Dir, "dashtest.flag");
            if (!File.Exists(flag)) return;
            string text = File.ReadAllText(flag);
            try { File.Delete(flag); } catch { }
            var w = text.Split(new[] { ' ', '\n', '\r', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.ToLowerInvariant()).ToList();
            _probeOnly = w.Remove("probe");
            _where = w.FirstOrDefault(x => x is "battle" or "race" or "target") ?? "story";
            int i = w.IndexOf("slot");
            if (i >= 0 && i + 1 < w.Count && int.TryParse(w[i + 1], out int s)) _slot = s - 1;
            _blockSaves = true;
            bool a = Hooks.Prefix(typeof(SaveDataController), nameof(SaveDataController.SetSaveAction), typeof(DashBarTest), nameof(BlockSave));
            bool b = Hooks.Prefix(typeof(SaveDataController), nameof(SaveDataController.SetNewDataAction), typeof(DashBarTest), nameof(BlockSave), new Type[0]);
            bool c = Hooks.Prefix(typeof(SaveDataController), nameof(SaveDataController.SetNewDataAction), typeof(DashBarTest), nameof(BlockSave), new[] { typeof(int) });
            bool d = Hooks.Postfix(typeof(InputController), "Tick", typeof(DashBarTest), nameof(AfterInputTick));
            Msg($"armed: {(_probeOnly ? "probe" : "full test")} in {_where}, slot {(_slot < 0 ? "last used" : (_slot + 1).ToString())}; save writes blocked {a && b && c}, input hook {d}");
            if (!(a && b && c && d)) { Msg("FAIL: hooks missing; not running"); Hooks.UnpatchAll(); return; }
            Directory.CreateDirectory(Shots);
            Enter(Phase.Title, 120f);
        }

        public static bool BlockSave()
        {
            try { if (_blockSaves) { Msg("blocked a save write"); return false; } } catch { }
            return true;
        }

        private static void Msg(string s) => Log.Info("[DashBarTest] " + s);
        private static string F(float v) => v.ToString("0.000", CultureInfo.InvariantCulture);

        private static void Check(string what, bool ok, string detail = "")
        {
            Msg($"{(ok ? "OK  " : "FAIL")} {what}{(detail != "" ? ": " + detail : "")}");
            if (!ok) _fails.Add(what);
        }

        private static void Enter(Phase p, float timeout)
        {
            _phase = p; _phaseStart = Time.unscaledTime; _deadline = Time.unscaledTime + timeout; _at = Time.unscaledTime;
            Msg($"phase {p} (scene {SceneLoader.ActiveScene})");
        }

        internal static void Tick()
        {
            if (!Running) return;
            float now = Time.unscaledTime;
            if (_failure != null) { Finish("FAIL: " + _failure); return; }
            if (now < _at) return;
            try { Step(now); }
            catch (Exception e) { Finish($"FAIL in {_phase}: {e.GetType().Name}: {e.Message}\n{e.StackTrace}"); }
        }

        private static void Step(float now)
        {
            if (now > _deadline) throw new TimeoutException($"timed out (scene {SceneLoader.ActiveScene})");
            var gm = GameRefs.Main;
            switch (_phase)
            {
                case Phase.Title:
                {
                    var ctrl = GameRefs.SaveCtrl;
                    if (SceneLoader.ActiveScene != "SelectData" || GameRefs.Global == null || GameRefs.Save == null ||
                        ctrl == null || !ctrl.ActionWait() || ctrl.GameData?._SystemData == null) { _at = now + 0.5f; return; }
                    if (now - _phaseStart < 4f) { _at = now + 0.5f; return; }
                    var data = ctrl.GameData;
                    int slot = _slot >= 0 ? _slot : data._SystemData._LastUser;
                    var users = data._UserData?._UserDatas;
                    if (users == null || slot < 0 || slot >= users.Length || users[slot]?._PriorityData == null || users[slot]._PriorityData._GameProgression <= 0)
                        throw new InvalidOperationException($"slot {slot + 1} has no game in progress");
                    Msg($"loading slot {slot + 1} (like the title's continue)");
                    ctrl.SelectLoadDataToGameData(slot);
                    var save = GameRefs.Save;
                    save.SetUseDataLogoMonoGetFlg();
                    OujiShipSponePointList.s_SponeIndex = OujiShipSponePointList.ElevatorSpone;
                    save.FrontSceneSelectData = true;
                    SingletonMonoBehaviour<EncorsSaveDataChecker>.Instance.CheckEncorsContentsData();
                    GameRefs.Sound?.StopBGM(0.5f);
                    Il2Cpp.SceneManager.LoadSceneAsync("OujiShip", LoadSceneMode.Single, true);
                    Enter(Phase.Ship, 90f);
                    return;
                }
                case Phase.Ship:
                    if (SceneLoader.ActiveScene != "OujiShip" || !GameRefs.InGameSession) { _at = now + 0.25f; return; }
                    if (_shipSeen == 0) { _shipSeen = now; Msg("on the ship"); }
                    if (now - _shipSeen < 4f) { _at = now + 0.25f; return; }
                    if (_where is "race" or "target") Msg($"start the {_where} mode: " + GameModes.Start(_where));
                    else if (_where == "battle")
                    {
                        var map = StageCatalog.Entries.FirstOrDefault(e => e.Kind == MapKind.Multiplayer && e.Internal == "Stage_ONBattle1")
                                  ?? throw new InvalidOperationException("no Battle map");
                        Msg("load battle: " + SceneLoader.Load(map));
                    }
                    else
                    {
                        var entry = StageCatalog.Entries.FirstOrDefault(s => s.Kind == MapKind.Stage && s.Internal == "Stage_Big1")
                                    ?? throw new InvalidOperationException("no Stage_Big1");
                        Msg("load stage: " + SceneLoader.LoadStage(entry));
                    }
                    Enter(Phase.Intro, 150f);
                    return;
                case Phase.Intro:
                    if (Multi)
                    {
                        var mgr = GameRefs.Multiplay;
                        string st = TrainingTrace.StateName(mgr?.stateMachine);
                        string intro = $"scene {SceneLoader.ActiveScene}, multiplay {GameRefs.IsMultiplay}, state {st}, player {gm?.PlayerInitiated}, core {Katamari.Core != null}, " +
                                       $"playing {Katamari.Playing}, running {gm?.IsGameRunning}, start message done {gm?.StartMessageDone}, gauge on {gm?.MainUI?.MultiDashUI?.gameObject.activeInHierarchy}";
                        if (intro != _lastIntro) { Msg("  " + intro); _lastIntro = intro; }
                        if (gm != null && gm.MainUI != null && gm.MainUI.IsTipsActive()) { gm.MainUI.DeactivateTipsUI(); _at = now + 1f; return; }
                        if (gm == null || !gm.PlayerInitiated || Katamari.Core == null || st != "GameState") { _at = now + 0.1f; return; }
                        // Test setup: a long round, so the scenarios fit (the round ends when playTime reaches _startTime).
                        Msg($"round running: playTime {mgr.playTime:0.0}, ends at {gm._startTime:0.0}; set to end at {mgr.playTime + 400f:0.0} for the test");
                        gm._startTime = mgr.playTime + 400f;
                        var me = Katamari.Core;
                        Msg($"my core: multiCoreNo {me.multiCoreNo}, CPU {me.isMultiCpu}; " +
                            $"rule {mgr.nowRule}, single play {mgr.isSinglePlay}, InStage {GameRefs.InStage}");
                        HideExplorer();
                        Msg($"constants: MaxOujiDashTime {F(me.MaxOujiDashTime)}, AddTimePerDashActive {F(me.AddTimePerDashActive)}, MultiDashCoolTime {F(me.MultiDashCoolTime)}, " +
                            $"control type {me.CoreInput?.nowControllType}, debug infinite {GameRefs.Global?._debugInifiniteDash}, unlimited flag {BattleDash.Has(me)}, timeScale {Time.timeScale}");
                        _script = (_probeOnly ? Probe() : Scenarios()).GetEnumerator();
                        _wait = null;
                        _running = true;
                        Enter(Phase.Run, 600f);
                        return;
                    }
                    if (gm == null || !gm.PlayerInitiated) { _at = now + 0.1f; return; }
                    if (gm.MainUI != null && gm.MainUI.IsTipsActive()) { gm.MainUI.DeactivateTipsUI(); _at = now + 1f; return; }
                    if (!gm.StartMessageDone)
                    {
                        if (gm._isInDemoCamera && !_skippedFly && now - _phaseStart > 6f) { Msg("skipping the fly-over"); gm.EndDemoCameraWithFade(); _skippedFly = true; }
                        var ui = SingletonMonoBehaviour<GlobalUIController>.Instance;
                        if (!gm._isInDemoCamera && ui != null && ui.CanSkipMessage && now - _phaseStart > 8f) { Msg("skipping the King's talk"); ui.SkipNowMessage(); _at = now + 1f; return; }
                        _at = now + 0.1f; return;
                    }
                    if (!gm.IsGameRunning || Katamari.Core == null) { _at = now + 0.1f; return; }
                    HideExplorer();
                    var core = Katamari.Core;
                    Msg($"playing: {gm.TargetStage}, size {Katamari.SizeText}; InStage {GameRefs.InStage}");
                    Msg($"constants: MaxOujiDashTime {F(core.MaxOujiDashTime)}, AddTimePerDashActive {F(core.AddTimePerDashActive)}, MultiDashCoolTime {F(core.MultiDashCoolTime)}, " +
                        $"control type {core.CoreInput?.nowControllType}, debug infinite {GameRefs.Global?._debugInifiniteDash}, timeScale {Time.timeScale}");
                    _script = (_probeOnly ? Probe() : Scenarios()).GetEnumerator();
                    _wait = null;
                    _running = true;
                    Enter(Phase.Run, 600f);
                    return;
                case Phase.Run:
                    if (_scriptDone) { _running = false; Finish(_fails.Count == 0 ? "PASS" : "FAIL: " + string.Join("; ", _fails)); return; }
                    _at = now + 0.25f;
                    return;
            }
        }

        private static void Finish(string result)
        {
            _running = false;
            _phase = Phase.Done;
            _blockSaves = false;
            try { _csv?.Flush(); _csv?.Dispose(); } catch { }
            _csv = null;
            Msg(result);
            Msg("DONE, quitting");
            Application.Quit();
        }

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

        // ------------------------------------------------------------------ the scenario runner and input

        private static bool _running, _scriptDone;
        private static string _failure;
        private static IEnumerator<object> _script;
        private static object _wait;
        private static bool _l2, _startKey;

        private sealed class Wait
        {
            public int Frames = -1;
            public float Until = -1f;
            public Func<bool> Cond;
            public float Deadline = -1f;
            public string What;
        }

        private static Wait Frames(int n) => new() { Frames = n };
        // Real time (unscaled), so waits also run while the game is paused.
        private static Wait Secs(float s) => new() { Until = Time.unscaledTime + s };
        private static Wait When(Func<bool> c, float timeout, string what) => new() { Cond = c, Deadline = Time.unscaledTime + timeout, What = what };

        private static void AfterInputTick(InputController __instance)
        {
            try
            {
                if (!_running || _script == null || _scriptDone) return;
                Advance();
                Inject(__instance);
            }
            catch (Exception e) { _failure = $"{e.GetType().Name}: {e.Message}"; _running = false; }
        }

        private static void Advance()
        {
            for (int guard = 0; guard < 50; guard++)
            {
                if (_wait is Wait w)
                {
                    if (w.Frames >= 0) { if (w.Frames-- > 0) return; }
                    else if (w.Until >= 0f) { if (Time.unscaledTime < w.Until) return; }
                    else if (w.Cond != null)
                    {
                        bool ok = false;
                        try { ok = w.Cond(); } catch { }
                        if (!ok)
                        {
                            if (Time.unscaledTime < w.Deadline) return;
                            Msg($"  (gave up waiting for {w.What})");
                        }
                    }
                }
                if (!_script.MoveNext()) { _scriptDone = true; Neutral(); Msg("scenarios done"); return; }
                _wait = _script.Current;
            }
        }

        private static Vector2 _lStick, _rStick;
        private static readonly Vector2 Up = new(0f, 1f);
        private static void Fwd() { _lStick = Up; _rStick = Up; }
        private static void Neutral() { _lStick = _rStick = Vector2.zero; _l2 = false; }

        // Into the game's own pad buffer, after InputController.Tick: what IsDown / GetAxis (MainGameCoreInput) read.
        private static void Inject(InputController ic)
        {
            var pad = ic.Pad((InputController.ePadNo)0);
            var idx = pad?.bufferIndexNow;
            if (idx == null || idx.Length == 0) return;
            int cur = idx[0];
            var keys = pad.keylistDown;
            if (_l2 && keys != null && cur >= 0 && cur < keys.Length) keys[cur] |= InputController.KeysFlag[(int)eKeyMap.L2];
            if (_startKey && keys != null && cur >= 0 && cur < keys.Length) keys[cur] |= InputController.KeysFlag[(int)eKeyMap.Start];
            var sl = pad.keylistStickLeft; var sr = pad.keylistStickRight;
            if (sl != null && cur >= 0 && cur < sl.Length) sl[cur] = _lStick;
            if (sr != null && cur >= 0 && cur < sr.Length) sr[cur] = _rStick;
        }

        private static IEnumerable<object> TapL2(int holdFrames = 3, int gapFrames = 5)
        {
            _l2 = true; yield return Frames(holdFrames);
            _l2 = false; yield return Frames(gapFrames);
        }

        // ------------------------------------------------------------------ probe: the game's gauge as it is

        private static IEnumerable<object> Probe()
        {
            var gm = GameRefs.Main;
            var ui = gm?.MainUI;
            Msg($"MainUI {(ui != null ? ui.name : "null")}; MultiDashUI {(ui?.MultiDashUI != null ? ui.MultiDashUI.name : "null")}");
            var g = ui?.MultiDashUI;
            if (g == null) { Check($"the game's dash gauge exists ({_where})", false, "MainGameUIManager.MultiDashUI is null"); yield break; }
            Check($"the game's dash gauge exists ({_where})", true, PathOf(g.transform));
            for (int k = 0; k < 3; k++)
            {
                Msg($"  +{k * 2} s: gauge on {g.gameObject.activeSelf}/{g.gameObject.activeInHierarchy}, base {g.BaseParent?.activeSelf} {g.BaseGauge?.fillAmount:0.00}, max {g.MaxParent?.activeSelf} {g.MaxGauge?.fillAmount:0.00}; " +
                    $"playing {Katamari.Playing}, running {gm.IsGameRunning}, start message done {gm.StartMessageDone}, pause {ui.PauseObjs?.isOpend}, demo {ui.DemoObjs?.isOpend}, " +
                    $"state {TrainingTrace.StateName(GameRefs.Multiplay?.stateMachine)}; dash used {F(C._oujiDashElapsedTimer)}");
                yield return Secs(2f);
            }
            DumpParents(g.transform);
            Dump(g.transform, 0);
            Msg($"parts: BaseParent {Name(g.BaseParent)}, MaxParent {Name(g.MaxParent)}, BaseGauge {Img(g.BaseGauge)}, MaxGauge {Img(g.MaxGauge)}");
            DumpTexts(ui.transform);
            yield return Secs(1f);
            FrameCapture.Request("probe_0_untouched", Shots);
            yield return Secs(0.6f);

            // Show it: the gauge's own object (and any inactive parent, logged).
            var t = g.transform;
            var turnedOn = new List<GameObject>();
            for (var p = t; p != null; p = p.parent)
                if (!p.gameObject.activeSelf) { p.gameObject.SetActive(true); turnedOn.Add(p.gameObject); }
            Msg("turned on: " + (turnedOn.Count == 0 ? "nothing (all active)" : string.Join(", ", turnedOn.Select(o => o.name))));
            yield return Frames(2);
            Msg($"after turning on: activeInHierarchy {g.gameObject.activeInHierarchy}; {ScreenRect(g.transform.TryCast<RectTransform>())}");
            foreach (var (max, v) in new[] { (false, 1f), (false, 0.5f), (false, 0.15f), (true, 1f), (true, 0.5f) })
            {
                g.SetDisplayMax(max);
                g.SetGaugeValue(v);
                yield return Frames(3);
                string name = $"probe_{(max ? "max" : "base")}_{v:0.00}";
                Msg($"{name}: base {g.BaseParent?.activeSelf}/{g.BaseGauge?.fillAmount:0.00}, max {g.MaxParent?.activeSelf}/{g.MaxGauge?.fillAmount:0.00}");
                FrameCapture.Request(name, Shots);
                yield return Secs(0.6f);
            }
            Dump(g.transform, 0);
            foreach (var o in turnedOn) o.SetActive(false);
        }

        private static string Name(GameObject o) => o == null ? "null" : $"{o.name} (active {o.activeSelf})";

        private static string Img(Image i)
        {
            if (i == null) return "null";
            try
            {
                return $"{i.name} sprite '{i.sprite?.name}' tex '{i.sprite?.texture?.name}' type {i.type} fill {i.fillMethod}/{i.fillOrigin} amount {i.fillAmount:0.00} " +
                       $"color {i.color} enabled {i.enabled} active {i.gameObject.activeSelf}";
            }
            catch (Exception e) { return $"{i.name} ({e.Message})"; }
        }

        private static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (var p = t; p != null; p = p.parent) parts.Add(p.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static string Rect(RectTransform r)
        {
            if (r == null) return "no RectTransform";
            return $"anchors {r.anchorMin}-{r.anchorMax} pivot {r.pivot} pos {r.anchoredPosition} size {r.sizeDelta} scale {r.localScale} rot {r.localEulerAngles.z:0}";
        }

        private static string ScreenRect(RectTransform r)
        {
            if (r == null) return "no RectTransform";
            try
            {
                var c = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
                r.GetWorldCorners(c);
                var canvas = r.GetComponentInParent<Canvas>();
                var cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
                Vector2 a = RectTransformUtility.WorldToScreenPoint(cam, c[0]), b = RectTransformUtility.WorldToScreenPoint(cam, c[2]);
                return $"screen {a} - {b} (screen {Screen.width}x{Screen.height}), canvas {(canvas != null ? canvas.name + " " + canvas.renderMode + " order " + canvas.sortingOrder + " enabled " + canvas.enabled : "none")}";
            }
            catch (Exception e) { return "screen rect: " + e.Message; }
        }

        private static string Comps(GameObject o)
        {
            try { return string.Join(",", o.GetComponents<Component>().Select(c => c == null ? "null" : c.GetIl2CppType().Name)); }
            catch (Exception e) { return e.Message; }
        }

        private static void DumpParents(Transform t)
        {
            for (var p = t.parent; p != null; p = p.parent)
                Msg($"  parent {p.name}: activeSelf {p.gameObject.activeSelf}, layer {p.gameObject.layer}, comps [{Comps(p.gameObject)}], {Rect(p.TryCast<RectTransform>())}");
        }

        private static void Dump(Transform t, int depth)
        {
            if (depth > 6) return;
            var img = t.GetComponent<Image>();
            Msg($"  {new string(' ', depth * 2)}{t.name}: activeSelf {t.gameObject.activeSelf} inHierarchy {t.gameObject.activeInHierarchy}, comps [{Comps(t.gameObject)}], {Rect(t.TryCast<RectTransform>())}" +
                (img != null ? " | " + Img(img) : "") + (depth == 0 ? " | " + ScreenRect(t.TryCast<RectTransform>()) : ""));
            for (int i = 0; i < t.childCount; i++) Dump(t.GetChild(i), depth + 1);
        }

        private static void DumpTexts(Transform root)
        {
            try
            {
                foreach (var x in root.GetComponentsInChildren<Il2CppTMPro.TextMeshProUGUI>(true).Take(60))
                    Msg($"  text {PathOf(x.transform)}: '{x.text?.Replace("\n", " / ")}' font '{x.font?.name}' size {x.fontSize:0} color {x.color} active {x.gameObject.activeInHierarchy}");
                foreach (var x in root.GetComponentsInChildren<Image>(true).Where(i => i.type == Image.Type.Filled).Take(40))
                    Msg($"  filled image {PathOf(x.transform)}: {Img(x)}");
            }
            catch (Exception e) { Msg("texts: " + e.Message); }
        }

        // ------------------------------------------------------------------ the full test (written after the probe)

        private static MainGameCore C => Katamari.Core;
        private static DashState S => DashBarMod.Current;
        private static string _scen = "-";
        private static int _calls;

        private static bool Idle() => C != null && !C.InOujiDash && C._nowAction == MainGameCore.eSpecialAction.none
                                      && (C.CoreInput?._dashInputCount ?? 0) == 0 && (C.CoreInput?._dashDisableTimer ?? 0f) <= 0f;

        // Five L2 presses: the first starts the wind-up (count 3), the fifth reaches 7 = a full dash (MainGameCoreInput.CheckDash).
        private static IEnumerable<object> FullDash()
        {
            for (int i = 0; i < 4; i++) foreach (var x in TapL2()) yield return x;
            foreach (var x in TapL2(3, 0)) yield return x;
        }

        private static void AfterTimer(MainGameCore __instance)
        {
            try { if (C != null && __instance.Pointer == C.Pointer) _calls++; } catch { }
        }

        private static string Gauge()
        {
            var g = GaugeView.Gauge;
            if (g == null) return "gauge none";
            try { _ = g.gameObject; } catch { return "gauge destroyed"; }
            return $"gauge on {g.gameObject.activeInHierarchy}, look {(g.MaxParent != null && g.MaxParent.activeSelf ? "Max" : "Base")}, bar {g.BaseGauge?.fillAmount:0.000}/{g.MaxGauge?.fillAmount:0.000}, " +
                   $"icon {GaugeView.Icon?.fillAmount:0.00}, label '{GaugeView.LabelText}'";
        }

        private static string State() => $"{S} | {Gauge()}";

        private static void Shot(string name)
        {
            FrameCapture.Request(name, Shots);
            Msg($"picture {name}: {State()}");
        }

        private static bool Yellow(byte r, byte g, byte b) => r > 170 && g > 170 && b < 120;
        private static bool White(byte r, byte g, byte b) => r > 235 && g > 235 && b > 235;
        // The countdown's red, (1, 0.42, 0.32) = (255, 107, 82), with some room for antialiasing.
        private static bool Red(byte r, byte g, byte b) => r > 225 && g > 70 && g < 150 && b > 45 && b < 125;

        // A frame capture, then how many of the bar's yellow pixels are inside the bar frame's place on screen.
        private static void Picture(string name, Action<int> yellow) => Picture(name, GaugeView.CurrentScreenRect(), Yellow, yellow);

        // A frame capture, then how many pixels inside <r> (screen pixels, bottom-up) pass <pass>.
        private static void Picture(string name, Rect r, Func<byte, byte, byte, bool> pass, Action<int> count)
        {
            Msg($"picture {name}: {State()}; bar frame on screen {r}");
            FrameCapture.Request(name, Shots, path =>
            {
                int n = 0;
                try
                {
                    var img = Png.Decode(File.ReadAllBytes(path));
                    int x0 = Mathf.Max(0, (int)r.xMin), x1 = Mathf.Min(img.Width, (int)r.xMax), y0 = Mathf.Max(0, (int)r.yMin), y1 = Mathf.Min(img.Height, (int)r.yMax);
                    for (int y = y0; y < y1; y++)
                    {
                        int row = (img.Height - 1 - y) * img.Width * 4;   // PNG rows run top-down, screen y bottom-up
                        for (int x = x0; x < x1; x++)
                        {
                            int i = row + x * 4;
                            if (pass(img.Rgba[i], img.Rgba[i + 1], img.Rgba[i + 2])) n++;
                        }
                    }
                }
                catch (Exception e) { Msg($"  {name}: couldn't count pixels: {e.Message}"); n = 0; }
                count(n);
            });
        }

        private static IEnumerable<int> Layers(int mask)
        {
            for (int i = 0; i < 32; i++) if ((mask & (1 << i)) != 0) yield return i;
        }

        private static float LabelValue() =>
            float.TryParse(GaugeView.LabelText, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : float.NaN;

        private static int _frames, _noLabel, _wrongLook;
        private static float _worstBar, _exStart = -1f, _refillAt = -1f;
        private static DashPhase _prevPhase;

        // One CSV row per frame, after the bar was drawn (LateUpdate), plus the per-frame checks.
        private static void Row()
        {
            if (_csv == null || !_running || C == null) return;
            try
            {
                var s = S; var g = GaugeView.Gauge;
                if (g != null && g.gameObject.activeInHierarchy && s.Valid)
                {
                    _frames++;
                    bool rainbow = g.MaxParent != null && g.MaxParent.activeSelf;
                    var bar = rainbow ? g.MaxGauge : g.BaseGauge;
                    _worstBar = Math.Max(_worstBar, Math.Abs(bar.fillAmount - s.Fill));
                    if (s.Phase is DashPhase.Dashing or DashPhase.Exhausted && GaugeView.LabelText == "") _noLabel++;
                    if (rainbow != (s.Phase == DashPhase.Unlimited)) _wrongLook++;
                }
                if (s.Phase == DashPhase.Exhausted && _prevPhase != DashPhase.Exhausted) { _exStart = Time.time - s.ExhaustTimer; _refillAt = -1f; }
                if (s.Phase != DashPhase.Exhausted && _prevPhase == DashPhase.Exhausted) _refillAt = Time.time;
                _prevPhase = s.Phase;
                _csv.WriteLine(string.Join(",", _scen, F(Time.time), F(Time.unscaledTime), F(Time.deltaTime), F(Game.GAME_DELTATIME), _calls,
                    F(C._oujiDashTimer), F(C._oujiDashElapsedTimer), F(C._oujiDashExhaustTimer), s.Phase, F(s.Fill), F(s.RegenIn),
                    GaugeView.LabelText, g != null && g.gameObject.activeInHierarchy ? 1 : 0, g?.MaxParent != null && g.MaxParent.activeSelf ? 1 : 0,
                    F(g?.BaseGauge?.fillAmount ?? -1f), F(GaugeView.Icon?.fillAmount ?? -1f), GaugeView.HudVisible(GameRefs.Main) ? 1 : 0));
            }
            catch { }
            _calls = 0;
        }

        // ------------------------------------------------------------------ where to dash: open space from the spawn point

        private static readonly List<(Vector3 pos, float heading, float room)> _spots = new();
        private static int _spot;
        private static string _lastEnd = "none";

        // The stage's spawn point and its most open heading, where the speedrun session's DashBugTest measured full
        // 1.49 s dashes (2026-11-07); probed again here, not trusted blindly.
        private static readonly Vector3 KnownSpawn = new(0.90f, 1.60f, -3.51f);
        private const float KnownSpawnAngle = 23.33f;

        private static IEnumerable<object> Place(int spot = -1)
        {
            if (spot >= 0) _spot = spot;
            var s = _spots[_spot];
            Neutral();
            yield return Secs(0.3f);
            C.TeleportPlayer(s.pos, s.heading, false);
            yield return Secs(0.5f);
        }

        /// <summary>
        /// Dashes in 8 directions from here and from the stage's spawn point and ranks them by how long the dash ran
        /// before something stopped it (the spawn area is cramped at 5 cm). Test setup: the stamina is reset to full
        /// before each probe dash and after the probe.
        /// </summary>
        private static IEnumerable<object> ProbeHeadings()
        {
            var here = C.transform.position;
            var starts = new List<(Vector3 pos, float a0)> { (here, C._coreAngle) };
            if (!Multi && Vector3.Distance(here, KnownSpawn) < 6f) starts.Add((KnownSpawn, KnownSpawnAngle));
            var found = new List<(Vector3 pos, float heading, float room)>();
            Msg("probing dash directions (test setup: stamina reset to full before each probe dash)");
            foreach (var (pos, a0) in starts)
                for (int k = 0; k < 8; k++)
                {
                    float a = a0 + 45f * k;
                    yield return When(Idle, 5f, "idle");
                    _spots.Clear(); _spots.Add((pos, a, 0f));
                    foreach (var x in Place(0)) yield return x;
                    C._oujiDashElapsedTimer = 0f; C._oujiDashExhaustTimer = 0f;
                    _lastEnd = "the dash timer ran out";
                    Fwd();
                    foreach (var x in FullDash()) yield return x;
                    yield return When(() => C.InOujiDash, 2f, "the probe dash");
                    float t0 = Time.time;
                    yield return When(() => !C.InOujiDash, 2.5f, "the probe dash to end");
                    Neutral();
                    float ran = Time.time - t0;
                    found.Add((pos, a, ran));
                    Msg($"  from {pos} heading {F(a)}: the dash ran {F(ran)} s ({_lastEnd})");
                }
            _spots.Clear();
            _spots.AddRange(found.OrderByDescending(f => f.room));
            _spot = 0;
            yield return When(Idle, 5f, "idle");
            C._oujiDashElapsedTimer = 0f; C._oujiDashExhaustTimer = 0f;
            Msg("dash spots by how long a dash ran: " + string.Join("; ", _spots.Take(4).Select(f => $"{f.pos} {F(f.heading)} ({F(f.room)} s)")));
        }

        // What ended a dash (test-only hooks): QuitOujiDash (brake / climb / out of stamina) or WallStop (a wall).
        private static void BeforeQuit(MainGameCore __instance)
        {
            try { if (C != null && __instance.Pointer == C.Pointer && __instance._oujiDashTimer > 0f) _lastEnd = $"QuitOujiDash (used {F(__instance._oujiDashElapsedTimer)})"; } catch { }
        }

        private static void BeforeWallStop(MainGameCore __instance)
        {
            try { if (C != null && __instance.Pointer == C.Pointer && __instance._oujiDashTimer > 0f) _lastEnd = $"WallStop at timer {F(__instance._oujiDashTimer)}"; } catch { }
        }

        /// <summary>Placed facing open space, sticks forward, five L2 presses: a full dash.</summary>
        private static IEnumerable<object> PlacedDash(int spot = -1)
        {
            foreach (var x in Place(spot)) yield return x;
            _lastEnd = "the dash timer ran out";
            Fwd();
            foreach (var x in FullDash()) yield return x;
            yield return When(() => C.InOujiDash, 3f, "the dash");
        }

        private static IEnumerable<object> Scenarios()
        {
            bool hooked = Hooks.Postfix(typeof(MainGameCore), nameof(MainGameCore.ExecuteOujiDashTimerSingle), typeof(DashBarTest), nameof(AfterTimer));
            bool q = Hooks.Prefix(typeof(MainGameCore), "QuitOujiDash", typeof(DashBarTest), nameof(BeforeQuit));
            bool w = Hooks.Prefix(typeof(MainGameCore), "WallStop", typeof(DashBarTest), nameof(BeforeWallStop));
            _csv = new StreamWriter(System.IO.Path.Combine(Dir, "dashtest.csv"), false, Encoding.UTF8);
            _csv.WriteLine("scenario,t,unscaled,dt,gameDt,timerCalls,dashTimer,used,exhaust,phase,fill,regenIn,label,gaugeOn,maxLook,barFill,iconFill,hud");
            GameEvents.LateUpdate += Row;
            Msg($"hooks: timer {hooked}, quit {q}, wall {w}; settings: timer {DashBarMod.ShowTimer}, while dashing {DashBarMod.TimerWhileDashing}, hide when full {DashBarMod.HideWhenFull}");

            // ---- 1. Full stamina
            _scen = "full";
            yield return When(Idle, 10f, "idle");
            yield return Secs(1f);
            var g = GaugeView.Gauge;
            Check("1 full: the game's gauge is on screen", g != null && g.gameObject.activeInHierarchy, Gauge());
            Check("1 full: full bar, normal look, no countdown", S.Phase == DashPhase.Full && Mathf.Abs((g?.BaseGauge?.fillAmount ?? 0f) - 1f) < 0.001f
                  && g?.BaseParent != null && g.BaseParent.activeSelf && GaugeView.LabelText == "", State());
            int whiteNone = -1;
            Picture("1_full", GaugeView.LabelScreenRect(), White, n => whiteNone = n);
            yield return When(() => whiteNone >= 0, 5f, "the picture");
            Msg($"countdown box on screen {GaugeView.LabelScreenRect()}; white pixels there with no countdown: {whiteNone}");
            Msg($"HUD canvas {GaugeView.HudCanvas?.name}: {GaugeView.HudCanvas?.renderMode}, plane {GaugeView.HudCanvas?.planeDistance}, camera {GaugeView.HudCanvas?.worldCamera?.name}; " +
                $"bar hidden during messages {GaugeView.HidesInMessages}; canvases: {GaugeView.Canvases}");
            var topCanvas = g?.transform.parent?.GetComponent<Canvas>();
            Check("1 on top: the bar is on the mod's own overlay canvas", GaugeView.OnTop && topCanvas != null && topCanvas.renderMode == RenderMode.ScreenSpaceOverlay,
                  $"parent {g?.transform.parent?.name}, {topCanvas?.renderMode}, order {topCanvas?.sortingOrder}");
            Rect home = GaugeView.HomeScreenRect, now = GaugeView.CurrentScreenRect();
            Check("1 on top: in the same place the game's own HUD canvas put it", home.width > 10f && Mathf.Abs(home.x - now.x) < 2f && Mathf.Abs(home.y - now.y) < 2f
                  && Mathf.Abs(home.width - now.width) < 2f && Mathf.Abs(home.height - now.height) < 2f, $"game's canvas {home}, now {now} (screen {Screen.width}x{Screen.height})");
            yield return Secs(0.5f);
            _scen = "probe";
            foreach (var x in ProbeHeadings()) yield return x;
            Msg($"after the heading probe (stamina reset to full): {State()}");

            // Race and Target start in cramped places (measured: every dash ran into a wall within 0.5-0.6 s there), so
            // they sample the dash earlier.
            bool shortList = _where is "race" or "target";
            float s1 = shortList ? 0.12f : 0.35f;

            // ---- 2. A full dash drains it
            _scen = "dash1";
            float used0 = 0f, tDash = 0f, barA = 0f, usedA = 0f;
            bool dashingA = false;
            int nGood = Math.Max(2, _spots.Count(f => f.room >= 1.0f));   // the directions where a probe dash ran (nearly) full
            for (int tries = 0; tries < 8; tries++)
            {
                if (tries > 0)
                {
                    yield return When(Idle, 5f, "idle");
                    C._oujiDashElapsedTimer = 0f;   // test setup only: start the next try rested
                    Msg($"  (test setup: stamina reset to full before try {tries + 1})");
                }
                used0 = C._oujiDashElapsedTimer;
                foreach (var x in PlacedDash(Math.Min(tries % nGood, _spots.Count - 1))) yield return x;
                tDash = Time.time;
                yield return Secs(s1);
                barA = g.BaseGauge.fillAmount; usedA = S.Used; dashingA = C.InOujiDash;
                yield return Secs(s1);
                if (C.InOujiDash) break;
                Msg($"  try {tries + 1}: the dash was cut short ({_lastEnd}) at spot {_spot}");
            }
            Check($"2 dash: a real full dash, still running {2f * s1:0.##} s in", C.InOujiDash, $"timer {F(C._oujiDashTimer)}, used {F(used0)} -> {F(C._oujiDashElapsedTimer)}, spot {_spot}");
            float bar1 = g.BaseGauge.fillAmount, used1 = C._oujiDashElapsedTimer, timer1 = C._oujiDashTimer, drawnUsed1 = S.Used;
            Check("2 draining: the bar is the stamina left (1 - used / max, as drawn)", Mathf.Abs(bar1 - (1f - S.Used / S.Max)) < 0.001f && bar1 < 0.85f,
                  $"bar {bar1:0.000}, used {F(S.Used)} of {F(S.Max)} when drawn ({F(used1)} now)");
            Check("2 draining: the countdown is the dash time left (regeneration starts when it ends)",
                  S.Phase == DashPhase.Dashing && !S.RefillsAtOnce && Mathf.Abs(LabelValue() - timer1) <= 0.07f, $"label '{GaugeView.LabelText}', dash timer {F(timer1)}, {State()}");
            int whiteDash = -1;
            Picture("2_draining", GaugeView.LabelScreenRect(), White, n => whiteDash = n);
            yield return When(() => whiteDash >= 0, 5f, "the picture");
            Check("2 draining: the countdown is drawn on screen (white pixels in its box)", whiteDash > whiteNone + 400,
                  $"{whiteDash} with '{GaugeView.LabelText}', {whiteNone} with no countdown");
            Check($"2 draining: the bar went down with the stamina used between {s1:0.##} s and {2f * s1:0.##} s of dashing",
                  dashingA && barA - bar1 > 0.6f * s1 / C.MaxOujiDashTime && Mathf.Abs((barA - bar1) - (drawnUsed1 - usedA) / C.MaxOujiDashTime) < 0.002f,
                  $"bar {barA:0.000} -> {bar1:0.000}, used {F(usedA)} -> {F(drawnUsed1)}");

            // ---- 3. Regeneration starts the moment the dash ends
            _scen = "regen";
            yield return When(() => !C.InOujiDash, 5f, "the dash to end");
            Neutral();
            float tEnd = Time.time, usedEnd = C._oujiDashElapsedTimer;
            Msg($"dash 1 ended after {F(tEnd - tDash)} s, used {F(usedEnd)}; ended by: {_lastEnd} (a full dash runs 1.5 s)");
            yield return Frames(1);
            float usedNext = C._oujiDashElapsedTimer;
            Check("3 regen: stamina comes back from the first frame after the dash (no delay)", usedNext < usedEnd, $"used {F(usedEnd)} -> {F(usedNext)} one frame later");
            Check("3 regen: no countdown while regenerating", S.Phase == DashPhase.Regenerating && GaugeView.LabelText == "", State());
            yield return Secs(1.5f);
            if (C._oujiDashElapsedTimer < 0.6f)
            {
                C._oujiDashElapsedTimer = 1.5f;   // test setup: a short dash's stamina was nearly back; give the rate room to show
                Msg("  (test setup: stamina used set to 1.5 to measure the regeneration)");
                yield return Frames(2);
            }
            Shot("3_regenerating");
            float tA = Time.time, uA = C._oujiDashElapsedTimer, barR = g.BaseGauge.fillAmount;
            yield return Secs(3f);
            float rate = (uA - C._oujiDashElapsedTimer) / (Time.time - tA);
            Check("3 regen: 0.1 s of dash comes back per second (OUJI_DASH_RECOVERY_RATE)", Mathf.Abs(rate - DashState.RecoveryRate) < 0.005f,
                  $"{rate:0.0000} /s measured over {F(Time.time - tA)} s");
            Check("3 regen: the bar fills back up", g.BaseGauge.fillAmount > barR, $"{barR:0.000} -> {g.BaseGauge.fillAmount:0.000}");

            if (!shortList) {

            // ---- 4. Run out: another full dash empties it; 8 s wait, then full at once
            _scen = "exhaust";
            float predicted = -1f, predictedAt = -1f;
            bool predictedRunOut = false;
            for (int attempt = 1; attempt <= 5 && !C.InOujiDashExhaust; attempt++)
            {
                yield return When(Idle, 5f, "idle");
                predicted = -1f; predictedAt = -1f; predictedRunOut = false;
                foreach (var x in PlacedDash()) yield return x;
                yield return Secs(0.15f);
                if (C.InOujiDash && !C.InOujiDashExhaust)
                {
                    predicted = Time.time + S.RegenIn; predictedAt = S.RegenIn; predictedRunOut = S.RefillsAtOnce;
                    Msg($"dash {attempt + 1}: {State()}");
                }
                yield return When(() => C.InOujiDashExhaust || !C.InOujiDash, 4f, "exhaustion or the dash's end");
                Neutral();
                if (!C.InOujiDashExhaust) Msg($"dash {attempt + 1} ended without running out (used {F(C._oujiDashElapsedTimer)}, ended by: {_lastEnd}); again");
            }
            if (!C.InOujiDashExhaust) Check("4 ran out of dash", false, State());
            else
            {
                // When the wait began: the first frame drawn out of dash, less the game's own counter on it (the dash bug
                // can run you out at the dash's very start, before this script notices).
                yield return Frames(2);   // the per-frame record (LateUpdate) has seen it by then
                float tEx = _exStart;
                Msg($"exhausted (ended by: {_lastEnd}): {State()}");
                if (predicted > 0f)
                    Check("4 the dash that ran out: its countdown included the exhaustion's wait", predictedRunOut && predictedAt > DashState.ExhaustTime,
                          $"{F(predictedAt)} s predicted 0.15 s into the dash");
                else Msg("  (this dash ran out at its start: the game's dash bug, used + 0.5 over the max; no in-dash prediction to check)");
                Check("4 empty: empty bar, red countdown from 8 (two frames in)", S.Phase == DashPhase.Exhausted && g.BaseGauge.fillAmount < 0.001f && Mathf.Abs(LabelValue() - (DashState.ExhaustTime - (Time.time - tEx))) <= 0.07f, State());
                yield return Secs(0.4f);
                Shot("4_empty_start");
                var samples = new List<string>();
                float worst = 0f;
                for (int i = 0; i < 6 && C.InOujiDashExhaust; i++)
                {
                    yield return Secs(1f);
                    if (!C.InOujiDashExhaust) break;
                    float shown = LabelValue(), real = DashState.ExhaustTime - (Time.time - tEx);
                    worst = Math.Max(worst, Math.Abs(shown - real));
                    samples.Add($"{shown:0.0}/{real:0.00}");
                    if (i == 3)
                    {
                        int red = -1;
                        Picture("4_empty_mid", GaugeView.LabelScreenRect(), Red, n => red = n);
                        yield return When(() => red >= 0, 5f, "the picture");
                        Check("4 empty: the red countdown is drawn on screen (red pixels in its box)", red > 400, $"{red} red pixels with '{GaugeView.LabelText}'");
                    }
                }
                Check("4 empty: the countdown matches the time left (shown/real each second)", samples.Count >= 3 && worst <= 0.08f, string.Join(" ", samples) + $", worst {worst:0.000} s");
                float iconMid = GaugeView.Icon?.fillAmount ?? -1f;
                yield return When(() => !C.InOujiDashExhaust, 9f, "the refill");
                yield return Frames(2);
                float tRefill = _refillAt;   // the first frame drawn with the stamina back
                Check("4 refill: the wait is 8 s (OUJI_DASH_EXHAUST_TIME)", Mathf.Abs(tRefill - tEx - DashState.ExhaustTime) < 0.1f, $"{F(tRefill - tEx)} s");
                if (predicted > 0f)
                    Check("4 refill: the countdown shown during the dash reached 0 when the stamina came back", Mathf.Abs(tRefill - predicted) < 0.15f,
                          $"predicted {F(predictedAt)} s ahead, came {F(tRefill - predicted + predictedAt)} s after (off by {F(tRefill - predicted)} s)");
                yield return Frames(2);
                Check("4 refill: full at once, normal look, no countdown", S.Phase == DashPhase.Full && g.BaseGauge.fillAmount > 0.999f && GaugeView.LabelText == "", State());
                Msg($"icon fill during the wait: {iconMid:0.00} at 6 s in (expected 0.75)");
                Shot("4_refilled");
            }
            yield return Secs(0.6f);

            // ---- 5. Shoes: unlimited dash
            _scen = "shoes";
            yield return When(Idle, 5f, "idle");
            foreach (var x in Place()) yield return x;
            var gm = GameRefs.Main;
            gm.EndItemEffect(true);
            gm.SetInventoryItem(eInstageItemType.Shoes, Multi, true);   // battles: through the roulette, like a King Box
            gm.ActivateItemEffect();
            yield return Frames(3);
            Func<bool> unlimitedOn = () => BattleDash.Unlimited(C);
            float ShoesLeft() => Multi ? (GameRefs.Multiplay?.GetUserStatusFromCoreNo(C.multiCoreNo)?.infinityDashTime ?? -1f) : gm._itemEffectTimer;
            if (Multi) Check("5 shoes: unlimited dash on (the API's BattleDash: the prince's flag)", BattleDash.Has(C), $"slot {gm._nowInventoryItem}, time {ShoesLeft():0.00}");
            else Check("5 shoes: unlimited dash on (the API's Shoes)", StoryFreebies.UnlimitedDash && BattleDash.Unlimited(C), $"slot {gm._nowInventoryItem}, countdown {gm._itemEffectTimer:0.00}");
            Check("5 shoes: the game's Max (rainbow) look, the stamina, the unlimited dash's time left as the countdown",
                  S.Phase == DashPhase.Unlimited && g.MaxParent.activeSelf && !g.BaseParent.activeSelf && Mathf.Abs(g.MaxGauge.fillAmount - S.Fill) < 0.001f
                  && Mathf.Abs(LabelValue() - ShoesLeft()) <= 0.07f, $"time left {ShoesLeft():0.000}; {State()}");
            float uS0 = C._oujiDashElapsedTimer, uS1 = -1f, uS2 = -1f;
            bool shoesDash = false;
            for (int tries = 0; tries < 3 && unlimitedOn() && !shoesDash; tries++)
            {
                if (tries > 0) foreach (var x in Place((_spot + 1) % _spots.Count)) yield return x;
                _lastEnd = "the dash timer ran out";
                Fwd();
                foreach (var x in FullDash()) yield return x;
                yield return When(() => C.InOujiDash, 1.5f, "the Shoes dash");
                yield return Secs(0.1f);
                if (!C.InOujiDash) { Msg($"  Shoes dash try {tries + 1} cut short ({_lastEnd}) at spot {_spot}"); continue; }
                uS1 = C._oujiDashElapsedTimer;
                yield return Secs(0.3f);
                shoesDash = C.InOujiDash;
                uS2 = C._oujiDashElapsedTimer;
                if (shoesDash)
                {
                    int whiteShoes = -1;
                    Picture("5_shoes_dashing", GaugeView.LabelScreenRect(), White, n => whiteShoes = n);
                    yield return When(() => whiteShoes >= 0, 5f, "the picture");
                    Check("5 shoes: the Shoes countdown is drawn on screen (white pixels in its box)", whiteShoes > whiteNone + 400,
                          $"{whiteShoes} with '{GaugeView.LabelText}', {whiteNone} with no countdown");
                }
            }
            // The API's unlimited dash is free since 2026-11-09: no start cost, no drain (before: +0.5 at the start).
            Check("5 shoes: a free dash (no start cost, no drain): the bar stays full, in the Max look", shoesDash && uS1 <= uS0 + 0.001f && uS2 <= uS0 + 0.001f
                  && S.Phase == DashPhase.Unlimited && g.MaxParent.activeSelf && g.MaxGauge.fillAmount > 0.999f,
                  $"used {F(uS0)} before, {F(uS1)} 0.1 s into the dash, {F(uS2)} 0.3 s later; still dashing {shoesDash} ({_lastEnd}); {State()}"
                  + $"; API counters: free starts {BattleDash.FreeStarts}, nonstop frames {BattleDash.NonstopFrames}");
            Neutral();
            yield return When(() => !unlimitedOn(), 8f, "the Shoes to end");
            yield return Frames(3);
            Check("5 shoes over: back to the normal look and the real stamina", S.Phase != DashPhase.Unlimited && g.BaseParent.activeSelf && !g.MaxParent.activeSelf
                  && Mathf.Abs(g.BaseGauge.fillAmount - (1f - C._oujiDashElapsedTimer / C.MaxOujiDashTime)) < 0.002f, State());
            Shot("5_shoes_over");
            yield return Secs(0.6f);

            // ---- 6. Hidden with the HUD (the pause menu)
            _scen = "pause";
            if (Multi)
            {
                // Matches: the API's PauseMenu.Open does nothing there (measured); press Start like a player.
                _startKey = true; yield return Frames(3); _startKey = false;
                Msg("pressed Start");
            }
            else Msg($"pause: {PauseMenu.Open()}");
            yield return Secs(1.2f);
            var ui = gm.MainUI;
            Msg($"paused? match pause {GameRefs.Multiplay?.isGamePause}, PauseObjs {ui?.PauseObjs?.isOpend}, LAYER00 on {ui?.Layer00?.gameObject.activeInHierarchy}, timeScale {Time.timeScale}");
            Check("6 pause: the bar hides with the game's HUD", !g.gameObject.activeInHierarchy,
                  $"{Gauge()}; PauseObjs open {ui?.PauseObjs?.isOpend}, DemoObjs open {ui?.DemoObjs?.isOpend}, size UI on {ui?.transform.Find("LAYER00/KatamariSize")?.gameObject.activeInHierarchy}, timeScale {Time.timeScale}");
            Shot("6_paused");
            yield return Secs(0.6f);
            if (Multi)
            {
                // Matches use the story stages' pause menu (measured: PauseObjs open, timeScale 0). Start again closes it
                // like a player would; the API's resume if it doesn't.
                for (int k = 0; k < 2 && ui?.PauseObjs != null && ui.PauseObjs.isOpend; k++)
                {
                    _startKey = true; yield return Frames(3); _startKey = false;
                    yield return Secs(1f);
                    Msg($"pressed Start again: pause menu open {ui.PauseObjs.isOpend}, timeScale {Time.timeScale}");
                }
                if (ui?.PauseObjs != null && ui.PauseObjs.isOpend) { PauseMenu.Resume(); Msg("resumed with the API's PauseMenu.Resume"); }
            }
            else PauseMenu.Resume();
            yield return Secs(1.5f);
            Check("6 resume: the bar is back", g.gameObject.activeInHierarchy && GaugeView.HudVisible(gm), Gauge());

            // ---- 7. Always on top: an object right in front of the camera, over the bar's place on screen
            _scen = "ontop";
            yield return When(Idle, 5f, "idle");
            foreach (var x in Place()) yield return x;
            yield return Secs(1f);
            // A test object with the prince's own material right in front of Main Camera, over the bar's place on screen:
            // on top, the bar must show in full. For comparison, the same in the game's own canvas (measured 2026-11-07:
            // not covered there either, the HUD's UICamera is a separate camera 6 units away).
            var hudCam = GaugeView.HudCanvas != null ? GaugeView.HudCanvas.worldCamera : null;
            var main = Camera.main;
            var cams = Camera.allCameras.Where(k => k != null).ToList();
            Msg("cameras: " + string.Join("; ", cams.Select(k => $"{k.name} depth {k.depth} mask {k.cullingMask} ({string.Join("/", Layers(k.cullingMask))}), target {(k.targetTexture != null ? k.targetTexture.name : "screen")}")) +
                $"; HUD camera at {(hudCam != null && main != null ? Vector3.Distance(hudCam.transform.position, main.transform.position).ToString("0.####") : "?")} from Main Camera");
            var mat = Katamari.Prince?.GetComponentInChildren<Renderer>(true)?.sharedMaterial;
            int yellowBase = -1, yellowTop = -1, yellowGame = -1;
            if (main == null || mat == null) Check("7 on top: a camera and a material for the test object", false, $"camera {main?.name}, material {mat?.name}");
            else
            {
                Picture("7_no_object", n => yellowBase = n);
                yield return When(() => yellowBase >= 0, 5f, "the picture");
                float d = GaugeView.HudCanvas != null ? GaugeView.HudCanvas.planeDistance * 0.5f : 0.05f;
                float halfH = d * Mathf.Tan(main.fieldOfView * 0.5f * Mathf.Deg2Rad), halfW = halfH * main.aspect;
                var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                box.name = "OUAKDashBar_TestOccluder";
                var col = box.GetComponent<Collider>();
                if (col != null) UnityEngine.Object.DestroyImmediate(col);   // nothing may bump into it
                box.GetComponent<Renderer>().sharedMaterial = mat;
                var ct = main.transform;
                box.transform.position = ct.position + ct.forward * d - ct.up * (halfH * 0.6f);
                box.transform.rotation = ct.rotation;
                box.transform.localScale = new Vector3(halfW * 1.2f, halfH * 0.7f, d * 0.05f);
                Msg($"test object with material '{mat.name}' ({mat.shader?.name}) {d:0.###} in front of {main.name} (near {main.nearClipPlane:0.###}, fov {main.fieldOfView:0.#})");
                yield return Frames(3);
                Picture("7_on_top", n => yellowTop = n);
                yield return When(() => yellowTop >= 0, 5f, "the picture");
                GaugeView.SetOnTop(false);   // test only: back in the game's own canvas, for comparison
                yield return Frames(3);
                Picture("7_game_canvas", n => yellowGame = n);
                yield return When(() => yellowGame >= 0, 5f, "the picture");
                GaugeView.SetOnTop(true);
                UnityEngine.Object.Destroy(box);
                yield return Frames(2);
                Msg($"for comparison, in the game's own canvas with the object: {yellowGame} bar-yellow pixels (no object: {yellowBase}): " +
                    $"{(yellowGame < yellowBase / 5 ? "covered" : "not covered")}");
                Check("7 on top: the bar shows in full over it", GaugeView.OnTop && yellowBase > 2000 && Math.Abs(yellowTop - yellowBase) < yellowBase / 10,
                      $"{yellowBase} with no object, {yellowTop} on top with the object; back on top {GaugeView.OnTop}");
            }
            yield return Secs(0.5f);

            }   // the long list (story stage, battle)

            // ---- 8. Matches: the game's battle code writes to the gauge itself; the bar is right again a frame later
            if (Multi && !shortList)
            {
                _scen = "gamewrites";
                yield return When(Idle, 5f, "idle");
                C._oujiDashElapsedTimer = 1.5f;   // test setup: half the stamina
                yield return Frames(2);
                C.ResetOujiDashMultiCoolTime();   // what battle Shoes / the King Chance do: full, in the Max look
                string after = $"bar {g.BaseGauge.fillAmount:0.00}/{g.MaxGauge.fillAmount:0.00}, Max look {g.MaxParent.activeSelf}";
                yield return Frames(1);
                Check("8 game writes: after the game's ResetOujiDashMultiCoolTime (full + Max), the bar is the stamina again a frame later",
                      !g.MaxParent.activeSelf && Mathf.Abs(g.BaseGauge.fillAmount - S.Fill) < 0.001f, $"right after the call: {after}; a frame later: {Gauge()}, {State()}");
                C.ForceOujiDashMultiCoolTime();   // the King Chance's end: empty, normal look
                after = $"bar {g.BaseGauge.fillAmount:0.00}/{g.MaxGauge.fillAmount:0.00}, Max look {g.MaxParent.activeSelf}";
                yield return Frames(1);
                Check("8 game writes: after the game's ForceOujiDashMultiCoolTime (empty), the bar is the stamina again a frame later",
                      !g.MaxParent.activeSelf && Mathf.Abs(g.BaseGauge.fillAmount - S.Fill) < 0.001f && S.Fill > 0.4f, $"right after the call: {after}; a frame later: {Gauge()}, {State()}");
                C._oujiDashElapsedTimer = 0f;
            }

            // ---- 9. Matches: the bar goes away with the round (results screen)
            if (Multi)
            {
                _scen = "results";
                var mgr = GameRefs.Multiplay;
                yield return When(Idle, 5f, "idle");
                Check("9 match: the bar is on screen while the round runs", g.gameObject.activeInHierarchy && GaugeView.MatchState() == "GameState", $"{Gauge()}, state {GaugeView.MatchState()}");
                GameRefs.Main._startTime = mgr.playTime + 2f;   // test setup: end the round now
                Msg("ending the round in 2 s");
                yield return When(() => GaugeView.MatchState() != "GameState", 30f, "the round to end");
                Msg($"round over: state {GaugeView.MatchState()}; {Gauge()}");
                Check("9 match: the bar goes away when the round ends", !g.gameObject.activeInHierarchy, $"state {GaugeView.MatchState()}, {Gauge()}");
                yield return When(() => GaugeView.MatchState() == "ResultState", 30f, "the results");
                yield return Secs(6f);
                Shot("9_results");
                bool barShown = false;
                try { barShown = g != null && g.gameObject.activeInHierarchy; } catch { barShown = false; }   // destroyed with the HUD
                Check("9 match: no bar on the results screen", !barShown && (GaugeView.Gauge == null || !GaugeView.Shown),
                      $"state {GaugeView.MatchState()}, the mod's gauge {(GaugeView.Gauge == null ? "let go" : "held")}");
            }

            GameEvents.LateUpdate -= Row;
            Check("every frame on screen: the bar is the stamina left", _frames > 1000 && _worstBar < 0.001f, $"worst {_worstBar:0.0000} over {_frames} frames");
            Check("every frame on screen: a countdown while dashing or out of dash", _noLabel == 0, $"{_noLabel} frames without one");
            Check("every frame on screen: the rainbow look exactly while the dash is unlimited", _wrongLook == 0, $"{_wrongLook} frames wrong");
            Msg("per-frame values: " + System.IO.Path.Combine(Dir, "dashtest.csv"));
        }
    }
}
