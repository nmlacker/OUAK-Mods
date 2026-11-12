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
using MelonLoader;
using MelonLoader.Utils;
using OUAKModAPI;
using UnityEngine;
using UnityEngine.SceneManagement;
using eStage = Il2CppApp.KatamariSin.MainGameManager.eStage;

namespace DashBugTest
{
    /// <summary>
    /// TEST HARNESS ONLY. Reproduces the dash bug in a story stage: title -> last-used slot -> ship -> stage -> scenarios
    /// -> quit. Only runs when UserData/OUAKSpeedrunTools/dashtest.flag exists (deleted at start).
    /// Inputs go through the game's own pad buffers: after InputController.Tick, the L2 bit is OR-ed into
    /// keylistDown[bufferIndexNow[0]] (InputController.KeysFlag[L2]) and the sticks are written to keylistStickLeft/Right
    /// at that index, so IsDown / GetAxis (what MainGameCoreInput reads) see a real press.
    /// Every save write is blocked (SaveDataController.SetSaveAction / SetNewDataAction skipped) while it runs.
    /// Writes a per-frame CSV (UserData/OUAKSpeedrunTools/dashtest.csv) and "[DashTest]" lines.
    /// Flag text: scenario names (default: all), "stage Stage_Big1", "slot N".
    /// </summary>
    public sealed class DashBugTest : HotMod
    {
        private enum Phase { Off, Title, Ship, Intro, Run, Done }

        private static DashBugTest I;
        private static bool _blockSaves;
        private Phase _phase = Phase.Off;
        private float _at, _deadline, _phaseStart, _shipSeen;
        private bool _skippedFly;
        private eStage _stage = eStage.Stage_Big1;
        private int _slot = -1;
        private List<string> _which = new();

        private static string Dir => Path.Combine(MelonEnvironment.UserDataDirectory, "OUAKSpeedrunTools");

        public override void OnLoad()
        {
            string flag = Path.Combine(Dir, "dashtest.flag");
            if (!System.IO.File.Exists(flag)) return;
            string text = System.IO.File.ReadAllText(flag).Trim();
            System.IO.File.Delete(flag);
            var w = text.Split(new[] { ' ', '\n', '\r', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            int i = w.IndexOf("stage"); if (i >= 0 && i + 1 < w.Count && Enum.TryParse(w[i + 1], out eStage st)) { _stage = st; w.RemoveRange(i, 2); }
            i = w.IndexOf("slot"); if (i >= 0 && i + 1 < w.Count && int.TryParse(w[i + 1], out int s)) { _slot = s - 1; w.RemoveRange(i, 2); }
            _which = w;
            I = this;
            _blockSaves = true;
            bool a = Hooks.Prefix(typeof(SaveDataController), nameof(SaveDataController.SetSaveAction), typeof(DashBugTest), nameof(BlockSave));
            bool b = Hooks.Prefix(typeof(SaveDataController), nameof(SaveDataController.SetNewDataAction), typeof(DashBugTest), nameof(BlockSave), new Type[0]);
            bool c = Hooks.Prefix(typeof(SaveDataController), nameof(SaveDataController.SetNewDataAction), typeof(DashBugTest), nameof(BlockSave), new[] { typeof(int) });
            bool d = Hooks.Postfix(typeof(InputController), "Tick", typeof(DashBugTest), nameof(AfterInputTick));
            bool e = Hooks.Postfix(typeof(MainGameCore), nameof(MainGameCore.ExecuteOujiDashTimerSingle), typeof(DashBugTest), nameof(AfterTimer));
            bool f = Hooks.Prefix(typeof(MainGameCore), nameof(MainGameCore.ExecuteOujiDash), typeof(DashBugTest), nameof(BeforeExecute));
            bool g = Hooks.Postfix(typeof(MainGameCore), nameof(MainGameCore.ExecuteOujiDash), typeof(DashBugTest), nameof(AfterExecute));
            bool h = Hooks.Postfix(typeof(MainGameCore), nameof(MainGameCore.ExtendOujiDash), typeof(DashBugTest), nameof(AfterExtend));
            bool k = Hooks.Prefix(typeof(MainGameCore), "QuitOujiDash", typeof(DashBugTest), nameof(BeforeQuit));
            bool l = Hooks.Postfix(typeof(MainGameCore), nameof(MainGameCore.StartAction), typeof(DashBugTest), nameof(AfterStartAction));
            bool m = Hooks.Prefix(typeof(MainGameCore), "WallStop", typeof(DashBugTest), nameof(BeforeWallStop))
                     && Hooks.Postfix(typeof(MainGameCore), "WallStop", typeof(DashBugTest), nameof(AfterWallStop));
            bool n = Hooks.Prefix(typeof(MainGameCore), nameof(MainGameCore.ExecuteOujiDashTimerSingle), typeof(DashBugTest), nameof(BeforeTimer));
            Msg($"hooks wallstop {m}, timer prefix {n}");
            Msg($"armed: stage {_stage}, slot {(_slot < 0 ? "last used" : (_slot + 1).ToString())}, scenarios {(_which.Count == 0 ? "all" : string.Join(" ", _which))}; " +
                $"save writes blocked {a && b && c}; hooks input {d} timer {e} exec {f && g} extend {h} quit {k} action {l}");
            if (!(a && b && c && d && e)) { Msg("FAIL: hooks missing; not running"); I = null; return; }
            Directory.CreateDirectory(Dir);
            _csv = new StreamWriter(Path.Combine(Dir, "dashtest.csv"), false, Encoding.UTF8) { AutoFlush = false };
            _csv.WriteLine("scenario,t,frame,calls,gdt,timer,elapsed,exhaust,action,count,inWait,lastUp,waitT,disableT,canDash,L_y,R_y,L2,speed,moveSpeed,climb,x,z");
            Enter(Phase.Title, 120f);
        }

        public override void OnUnload() { _blockSaves = false; _running = false; try { _csv?.Flush(); _csv?.Dispose(); } catch { } _csv = null; }

        public static bool BlockSave()
        {
            try { if (_blockSaves) { Msg("blocked a save write"); return false; } } catch { }
            return true;
        }

        private static void Msg(string s) => MelonLogger.Msg($"[DashTest] {DateTime.Now:HH:mm:ss.fff} {s}");
        private static string F(float v) => v.ToString("0.000", CultureInfo.InvariantCulture);

        private void Enter(Phase p, float timeout)
        {
            _phase = p; _phaseStart = Time.unscaledTime; _deadline = Time.unscaledTime + timeout; _at = Time.unscaledTime;
            Msg($"phase {p} (scene {SceneLoader.ActiveScene})");
        }

        public override void OnUpdate()
        {
            if (_phase == Phase.Off || _phase == Phase.Done) return;
            float now = Time.unscaledTime;
            if (_failure != null) { Finish("FAIL: " + _failure); return; }
            if (now < _at) return;
            try { Step(now); }
            catch (Exception e) { Finish($"FAIL in {_phase}: {e.GetType().Name}: {e.Message}"); }
        }

        private void Step(float now)
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
                    var entry = StageCatalog.Entries.FirstOrDefault(s => s.Kind == MapKind.Stage && s.Stage == _stage)
                                ?? new MapEntry { Kind = MapKind.Stage, Stage = _stage, Name = _stage.ToString() };
                    Msg("load stage: " + SceneLoader.LoadStage(entry));
                    Enter(Phase.Intro, 120f);
                    return;
                case Phase.Intro:
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
                    _core = core;
                    Msg($"playing: {gm.TargetStage}, size {Katamari.SizeText}, clock {gm._playTime:0.00}");
                    Msg($"constants: MaxOujiDashTime {F(core.MaxOujiDashTime)}, AddTimePerDashActive {F(core.AddTimePerDashActive)}, DashSpeed {F(core.DashSpeed)}, " +
                        $"NowDashSpeed {F(core.NowDashSpeed)}, MultiDashCoolTime {F(core.MultiDashCoolTime)}, control type {core.CoreInput?.nowControllType} " +
                        $"(GlobalManager.GetControllType {GameRefs.Global?.GetControllType()}), debug dash {GameRefs.Global?._debugOujiDash}, debug infinite {GameRefs.Global?._debugInifiniteDash}, " +
                        $"timeScale {Time.timeScale}, GAME_DELTATIME {F(Game.GAME_DELTATIME)}");
                    Msg($"state at start: timer {F(core._oujiDashTimer)}, elapsed {F(core._oujiDashElapsedTimer)}, exhaust {F(core._oujiDashExhaustTimer)}, action {core._nowAction}");
                    _script = Scenarios().GetEnumerator();
                    _wait = null;
                    _running = true;
                    Enter(Phase.Run, 400f);
                    return;
                case Phase.Run:
                    if (_scriptDone) { _running = false; Finish(_fails.Count == 0 ? "PASS (scenarios ran)" : "FAIL: " + string.Join("; ", _fails)); return; }
                    _at = now + 0.25f;
                    return;
            }
        }

        // ------------------------------------------------------------------ input injection and the scenario runner

        private static bool _running, _scriptDone;
        private static string _failure;
        private static IEnumerator<object> _script;
        private static object _wait;
        private static Vector2 _l, _r;
        private static bool _l2, _l3;
        private static int _frame, _callsThisFrame, _lastCallFrame;
        private static MainGameCore _core;
        private static StreamWriter _csv;
        private static string _scen = "-";
        private static float _scenStart;
        private static readonly List<string> _fails = new();

        private sealed class Wait
        {
            public int Frames = -1;
            public float Until = -1f;
            public Func<bool> Cond;
            public float Deadline = -1f;
            public string What;
        }

        private static Wait Frames(int n) => new() { Frames = n };
        private static Wait Secs(float s) => new() { Until = Time.time + s };
        private static Wait When(Func<bool> c, float timeout, string what) => new() { Cond = c, Deadline = Time.time + timeout, What = what };

        private static void AfterInputTick(InputController __instance)
        {
            try
            {
                _frame++;
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
                    else if (w.Until >= 0f) { if (Time.time < w.Until) return; }
                    else if (w.Cond != null)
                    {
                        bool ok = false;
                        try { ok = w.Cond(); } catch { }
                        if (!ok)
                        {
                            if (Time.time < w.Deadline) return;
                            Msg($"  (gave up waiting for {w.What} after {F(w.Deadline - Time.time)} s)");
                        }
                    }
                }
                if (!_script.MoveNext()) { _scriptDone = true; _l = _r = Vector2.zero; _l2 = false; Msg("scenarios done"); return; }
                _wait = _script.Current;
            }
        }

        private static void Inject(InputController ic)
        {
            var pad = ic.Pad((InputController.ePadNo)0);
            if (pad == null) return;
            var idx = pad.bufferIndexNow;
            if (idx == null || idx.Length == 0) return;
            int cur = idx[0];
            var keys = pad.keylistDown;
            if (_l2 && keys != null && cur >= 0 && cur < keys.Length) keys[cur] |= InputController.KeysFlag[(int)eKeyMap.L2];
            if (_l3 && keys != null && cur >= 0 && cur < keys.Length) keys[cur] |= InputController.KeysFlag[(int)eKeyMap.L3];
            var sl = pad.keylistStickLeft; var sr = pad.keylistStickRight;
            if (sl != null && cur < sl.Length) sl[cur] = _l;
            if (sr != null && cur < sr.Length) sr[cur] = _r;
        }

        // ------------------------------------------------------------------ logging

        private static Vector3 _lastPos;
        private static bool _havePos;

        private static float _timerBefore, _exhaustBefore;

        private static void BeforeTimer(MainGameCore __instance)
        {
            try { if (_core != null && __instance.Pointer == _core.Pointer) { _timerBefore = __instance._oujiDashTimer; _exhaustBefore = __instance._oujiDashExhaustTimer; } } catch { }
        }

        /// <summary>TEST SWITCH: while on, a wall (WallStop) can't end a dash: the dash timer WallStop clears is put back.
        /// Only used to stand in for open ground in this cluttered stage; every use is logged.</summary>
        private static bool _wallGuard;
        private static float _guardTimer = -1f;
        private static int _guarded;

        private static void BeforeWallStop(MainGameCore __instance)
        {
            try
            {
                _guardTimer = -1f;
                if (_core == null || __instance.Pointer != _core.Pointer || __instance._oujiDashTimer <= 0f) return;
                if (_wallGuard)
                {
                    _guardTimer = __instance._oujiDashTimer;
                    if (_guarded++ < 2) Ev($"WallStop while dashing (test switch on: the dash is kept): timer {F(__instance._oujiDashTimer)}, elapsed {F(__instance._oujiDashElapsedTimer)}");
                    return;
                }
                _lastQuit = $"WallStop at +{F(Time.time - _scenStart)}";
                Ev($"WallStop (a wall) while dashing: timer {F(__instance._oujiDashTimer)}, elapsed {F(__instance._oujiDashElapsedTimer)}");
            }
            catch { }
        }

        private static void AfterWallStop(MainGameCore __instance)
        {
            try { if (_guardTimer > 0f && _core != null && __instance.Pointer == _core.Pointer) __instance._oujiDashTimer = _guardTimer; _guardTimer = -1f; } catch { }
        }

        private static void AfterTimer(MainGameCore __instance)
        {
            try
            {
                if (_core != null && __instance.Pointer == _core.Pointer && _timerBefore > 0f && __instance._oujiDashTimer <= 0f)
                {
                    _lastQuit = $"timer function at +{F(Time.time - _scenStart)}";
                    Ev($"dash timer stopped by ExecuteOujiDashTimerSingle: timer {F(_timerBefore)} -> {F(__instance._oujiDashTimer)}, exhaust {F(_exhaustBefore)} -> {F(__instance._oujiDashExhaustTimer)}, elapsed now {F(__instance._oujiDashElapsedTimer)}");
                }
                if (_csv == null || _core == null || __instance.Pointer != _core.Pointer) return;
                if (_lastCallFrame == _frame) _callsThisFrame++; else { _callsThisFrame = 1; _lastCallFrame = _frame; }
                var c = __instance;
                var inp = c.CoreInput;
                var sp = c._nowSpeed;
                var pos = c.transform.position;
                float dt = Time.deltaTime;
                float move = _havePos && dt > 0f ? new Vector2(pos.x - _lastPos.x, pos.z - _lastPos.z).magnitude / dt : 0f;
                _lastPos = pos; _havePos = true;
                _csv.WriteLine(string.Join(",", _scen, F(Time.time - _scenStart), _frame, _callsThisFrame, F(Game.GAME_DELTATIME), F(c._oujiDashTimer), F(c._oujiDashElapsedTimer),
                    F(c._oujiDashExhaustTimer), (int)c._nowAction, inp?._dashInputCount ?? -1, inp != null && inp._inDashWait ? 1 : 0, inp != null && inp._lastDashInputLUp ? 1 : 0,
                    F(inp?._dashWaitTimer ?? 0f), F(inp?._dashDisableTimer ?? 0f), c.CanOujiDash() ? 1 : 0, F(_l.y), F(_r.y), _l2 ? 1 : 0,
                    F(new Vector2(sp.x, sp.z).magnitude), F(move), c._climbing ? 1 : 0, F(pos.x), F(pos.z)));
            }
            catch { }
        }

        private static float _execElapsedBefore, _execTimerBefore;
        private static int _execs;
        private static readonly List<string> _events = new();

        private static void Ev(string s)
        {
            string line = $"  {_scen} +{F(Time.time - _scenStart)} s: {s}";
            _events.Add(line);
            Msg(line);
        }

        private static void BeforeExecute(MainGameCore __instance)
        {
            try { if (_core == null || __instance.Pointer != _core.Pointer) return; _execElapsedBefore = __instance._oujiDashElapsedTimer; _execTimerBefore = __instance._oujiDashTimer; } catch { }
        }

        private static void AfterExecute(MainGameCore __instance, bool __0)
        {
            try
            {
                if (_core == null || __instance.Pointer != _core.Pointer) return;
                _execs++;
                var c = __instance;
                _lastExecWeak = __0;
                _lastExecElapsed = _execElapsedBefore;
                _lastExecAt = Time.time;
                Ev($"ExecuteOujiDash(weak={__0}): elapsed {F(_execElapsedBefore)} -> {F(c._oujiDashElapsedTimer)} (max {F(c.MaxOujiDashTime)}), timer {F(_execTimerBefore)} -> {F(c._oujiDashTimer)}, " +
                   $"exhausted now {c.InOujiDashExhaust}, speed {F(new Vector2(c._nowSpeed.x, c._nowSpeed.z).magnitude)}");
            }
            catch { }
        }

        private static bool _lastExecWeak;
        private static string _lastQuit = "none";
        private static float _lastExecElapsed, _lastExecAt;

        private static void AfterExtend(MainGameCore __instance)
        {
            try { if (_core != null && __instance.Pointer == _core.Pointer) _lastExtendAt = Time.time; if (_core != null && __instance.Pointer == _core.Pointer) Ev($"ExtendOujiDash: timer -> {F(__instance._oujiDashTimer)}, elapsed {F(__instance._oujiDashElapsedTimer)}"); } catch { }
        }

        private static void BeforeQuit(MainGameCore __instance)
        {
            try { if (_core != null && __instance.Pointer == _core.Pointer) _lastQuit = $"QuitOujiDash at +{F(Time.time - _scenStart)}"; if (_core != null && __instance.Pointer == _core.Pointer) Ev($"QuitOujiDash: timer {F(__instance._oujiDashTimer)}, elapsed {F(__instance._oujiDashElapsedTimer)}, climbing {__instance._climbing}"); } catch { }
        }

        private static void AfterStartAction(MainGameCore __instance, MainGameCore.eSpecialAction __0)
        {
            try { if (_core != null && __instance.Pointer == _core.Pointer && _running) Ev($"StartAction({__0})"); } catch { }
        }

        // ------------------------------------------------------------------ scenarios

        private static MainGameCore C => _core;
        private static float M => C.MaxOujiDashTime;
        private static float A => C.AddTimePerDashActive;
        private static readonly Vector2 Up = new(0f, 1f), Down = new(0f, -1f);
        private static Vector3 _spawn;
        private static float _spawnAngle;
        private static readonly List<float> _headings = new();

        /// <summary>Puts the katamari back on the spawn point facing <paramref name="angle"/> (the game's own TeleportPlayer).</summary>
        private static IEnumerable<object> Place(float angle)
        {
            Neutral();
            yield return Secs(0.3f);
            C.TeleportPlayer(_spawn, angle, false);
            yield return Secs(0.5f);
        }

        /// <summary>Rolls forward from the spawn point in 8 directions and ranks them by how far the katamari got.</summary>
        private static IEnumerable<object> ProbeHeadings()
        {
            _spawn = C.transform.position;
            _spawnAngle = C._coreAngle;
            Begin("probe", $"8 headings from the spawn point {_spawn} (angle {F(_spawnAngle)})");
            var dist = new List<(float a, float d)>();
            for (int k = 0; k < 8; k++)
            {
                float a = _spawnAngle + 45f * k;
                foreach (var x in Place(a)) yield return x;
                var p0 = C.transform.position;
                Fwd(); yield return Secs(2f);
                var p1 = C.transform.position;
                float d = new Vector2(p1.x - p0.x, p1.z - p0.z).magnitude;
                dist.Add((a, d));
                Msg($"  heading {F(a)}: {F(d)} in 2 s (angle now {F(C._coreAngle)})");
            }
            _headings.Clear();
            _headings.AddRange(dist.OrderByDescending(t => t.d).Select(t => t.a));
            Msg($"  headings by open space: {string.Join(", ", _headings.Select(F))}");
            foreach (var x in Place(_headings[0])) yield return x;
        }

        private static void Fwd() { _l = Up; _r = Up; _l2 = false; }
        private static void Neutral() { _l = _r = Vector2.zero; _l2 = false; }

        /// <summary>One counted stick step of the dash wiggle (tank controls): L up + R down, then L down + R up.</summary>
        private static IEnumerable<object> Wiggle(int from, int steps, int framesPerStep = 6)
        {
            for (int i = from; i < from + steps; i++)
            {
                bool lUp = i % 2 == 0;
                _l = lUp ? Up : Down; _r = lUp ? Down : Up; _l2 = false;
                yield return Frames(framesPerStep);
            }
        }

        private static IEnumerable<object> TapL2(int holdFrames = 3, int gapFrames = 5)
        {
            _l2 = true; yield return Frames(holdFrames);
            _l2 = false; yield return Frames(gapFrames);
        }

        private static bool Idle() => !C.InOujiDash && C._nowAction == MainGameCore.eSpecialAction.none && (C.CoreInput?._dashInputCount ?? 0) == 0
                                      && (C.CoreInput?._dashDisableTimer ?? 0f) <= 0f;

        /// <summary>Waits for the dash to end, puts the katamari on the spawn point facing open space, then sets the
        /// stamina to <paramref name="elapsed"/> (0 = rested).</summary>
        private static IEnumerable<object> Rest(float elapsed = 0f, int heading = 0)
        {
            Neutral();
            yield return When(Idle, 15f, "the dash to end");
            yield return When(() => !C.InOujiDashExhaust, 10f, "the exhaustion to end");
            if (_headings.Count > 0) foreach (var x in Place(_headings[Math.Min(heading, _headings.Count - 1)])) yield return x;
            yield return Secs(0.3f);
            C._oujiDashExhaustTimer = 0f;
            C._oujiDashElapsedTimer = elapsed;
        }

        private static void Begin(string name, string what)
        {
            _scen = name; _scenStart = Time.time; _execs = 0; _lastQuit = "none";
            Msg($"=== scenario {name}: {what} (elapsed {F(C._oujiDashElapsedTimer)}, timer {F(C._oujiDashTimer)})");
        }

        private static bool Want(string name) => I._which.Count == 0 || I._which.Contains(name);

        private static void Check(string what, bool ok, string detail)
        {
            Msg($"{(ok ? "OK  " : "FAIL")} {what}: {detail}");
            if (!ok) _fails.Add(what);
        }

        /// <summary>Waits until the dash timer stops (or <paramref name="max"/> s), and measures how long it ran.</summary>
        private static IEnumerable<object> WatchDash(string name, float max, Action<float> result, float[] shotsAt = null)
        {
            float t0 = Time.time;
            int shot = 0;
            while (C.InOujiDash && Time.time - t0 < max)
            {
                if (shotsAt != null && shot < shotsAt.Length && Time.time - t0 >= shotsAt[shot])
                {
                    string label = $"{name}_{shotsAt[shot]:0.0}s";
                    FrameCapture.Request(label, Path.Combine(Dir, "TestShots"));
                    Ev($"frame capture {label}: timer {F(C._oujiDashTimer)}, elapsed {F(C._oujiDashElapsedTimer)}, exhaust {F(C._oujiDashExhaustTimer)}, " +
                       $"speed {F(new Vector2(C._nowSpeed.x, C._nowSpeed.z).magnitude)}");
                    shot++;
                }
                yield return Frames(1);
            }
            result(Time.time - t0);
        }

        private static IEnumerable<object> EdgeDash(string name, float startElapsed, bool l2, bool neutralAfter, int heading, Action<float> result, float[] shots)
        {
            foreach (var x in Rest(0f, heading)) yield return x;
            Begin(name, $"full {(l2 ? "L2 (5 taps)" : "stick (7 steps)")} dash starting at elapsed {F(startElapsed)} (max {F(M)}, add {F(A)}), heading {F(_headings.Count > 0 ? _headings[Math.Min(heading, _headings.Count - 1)] : 0f)}, sticks {(neutralAfter ? "neutral" : "forward")} after");
            Fwd(); yield return Secs(0.5f);
            if (l2) { for (int i = 0; i < 4; i++) foreach (var x in TapL2()) yield return x; }
            else foreach (var x in Wiggle(0, 6)) yield return x;
            C._oujiDashElapsedTimer = startElapsed;
            if (l2) { foreach (var x in TapL2(3, 0)) yield return x; }
            else foreach (var x in Wiggle(6, 1)) yield return x;
            if (neutralAfter) Neutral(); else Fwd();
            var p0 = C.transform.position;
            float len = 0f;
            foreach (var x in WatchDash(name, 14f, v => len = v, shots)) yield return x;
            var p1 = C.transform.position;
            Msg($"  {name}: dash ran {F(len)} s, {F(new Vector2(p1.x - p0.x, p1.z - p0.z).magnitude)} travelled; elapsed at the dash {F(_lastExecElapsed)}; " +
                $"now timer {F(C._oujiDashTimer)}, elapsed {F(C._oujiDashElapsedTimer)}, exhaust {F(C._oujiDashExhaustTimer)}, last quit: {_lastQuit}");
            result(len);
        }

        private static IEnumerable<object> Scenarios()
        {
            Neutral();
            yield return Secs(1f);
            foreach (var x in ProbeHeadings()) yield return x;

            // 1. A full stick dash from rested stamina (the normal case).
            if (Want("stick"))
            {
                foreach (var x in Rest(0f)) yield return x;
                Begin("stick", "full dash with the sticks (7 wiggle steps), rested");
                Fwd(); yield return Secs(0.5f);
                foreach (var x in Wiggle(0, 7)) yield return x;
                Fwd();
                float len = 0f;
                foreach (var x in WatchDash("stick", 12f, v => len = v, new[] { 0.7f })) yield return x;
                Check("stick: a normal full dash lasts the dash time (1.5 s)", len > 1.4f && len < 1.6f,
                    $"dash ran {F(len)} s; elapsed {F(_lastExecElapsed)} at the dash, {F(C._oujiDashElapsedTimer)} now (add {F(A)} + 1.5 of dashing expected); last quit: {_lastQuit}");
                yield return Secs(1f);
            }

            // 2. One L2 tap: the short (weak) dash.
            if (Want("l2tap"))
            {
                foreach (var x in Rest(0f)) yield return x;
                Begin("l2tap", "one L2 tap (wind-up, then the short dash 0.5 s later), rested");
                Fwd(); yield return Secs(0.5f);
                int ex = _execs;
                foreach (var x in TapL2()) yield return x;
                yield return When(() => _execs > ex, 2f, "the short dash");
                Check("l2tap: one tap = one short dash with no dash timer", _execs == ex + 1 && _lastExecWeak && !C.InOujiDash,
                    $"execs {_execs - ex}, weak {_lastExecWeak}, timer {F(C._oujiDashTimer)}, elapsed {F(_lastExecElapsed)} -> {F(C._oujiDashElapsedTimer)}");
                yield return Secs(1f);
            }

            // 3. The bug, set up directly: a full stick dash that starts within one dash cost of the limit. Tried on the
            //    most open headings until one isn't cut short by a wall.
            if (Want("edge"))
            {
                float best = 0f;
                for (int h = 0; h < 3 && best < 7f; h++)
                {
                    float len = 0f;
                    foreach (var x in EdgeDash("edge" + h, M - A * 0.5f, false, false, h, v => len = v, new[] { 1f, 4f, 7f })) yield return x;
                    best = Math.Max(best, len);
                }
                Check("edge: the dash keeps going while exhausted (until the 8 s exhaustion ends)", best > 7f, $"longest {F(best)} s (normal 1.5)");
                yield return Secs(1f);
            }

            // 4. Same, sticks neutral after the dash starts.
            if (Want("edgeneutral"))
            {
                float len = 0f;
                foreach (var x in EdgeDash("edgeneutral", M - A * 0.5f, false, true, 0, v => len = v, null)) yield return x;
                yield return Secs(1f);
            }

            // 5. Control: the same dash starting more than one dash cost below the limit ends normally (at the limit).
            if (Want("control"))
            {
                float len = 0f;
                float start = M - A * 1.5f;
                foreach (var x in EdgeDash("control", start, false, false, 0, v => len = v, null)) yield return x;
                float expect = M - (start + A);
                Check("control: outside the window the dash ends when the stamina runs out", Math.Abs(len - expect) < 0.1f,
                    $"dash ran {F(len)} s, expected {F(expect)} (max - elapsed after the cost); exhausted {C.InOujiDashExhaust}");
                yield return Secs(1f);
            }

            // 6. The L2 route to a full dash (5 taps) at the edge.
            if (Want("edgel2"))
            {
                float len = 0f;
                foreach (var x in EdgeDash("edgel2", M - A * 0.5f, true, false, 0, v => len = v, null)) yield return x;
                Check("edgel2: a full dash from L2 taps at the edge keeps going too", len > 7f, $"dash ran {F(len)} s; weak {_lastExecWeak}");
                yield return Secs(1f);
            }

            // 7. A short (weak) dash at the edge: it exhausts you but never sets the dash timer.
            if (Want("edgeweak"))
            {
                foreach (var x in Rest(0f)) yield return x;
                Begin("edgeweak", "one L2 tap (short dash) at elapsed = max - add/2");
                Fwd(); yield return Secs(0.5f);
                C._oujiDashElapsedTimer = M - A * 0.5f;
                int ex = _execs;
                foreach (var x in TapL2()) yield return x;
                yield return When(() => _execs > ex, 2f, "the short dash");
                yield return Secs(1f);
                Check("edgeweak: a short dash at the edge exhausts without dashing", !C.InOujiDash && C.InOujiDashExhaust,
                    $"weak {_lastExecWeak}, timer {F(C._oujiDashTimer)}, elapsed {F(C._oujiDashElapsedTimer)}, exhaust {F(C._oujiDashExhaustTimer)}");
            }

            // 8. The user's recipe, natural stamina from rested: 2 short dashes (L2 taps), then 2 long stick dashes.
            foreach (var x in Validation()) yield return x;

            if (Want("recipe"))
            {
                foreach (var x in Recipe("recipe", 0)) yield return x;
            }
            // 9. The same with the first long dash made longer (4 more wiggle steps = one ExtendOujiDash).
            if (Want("recipeext"))
            {
                foreach (var x in Recipe("recipeext", 4)) yield return x;
            }

            Neutral();
            Msg("event list:\n" + string.Join("\n", _events));
        }

        // ------------------------------------------------------------------ setup validation

        private static float _lastExtendAt = -1f;
        private static bool _lastQturnClean;

        private static IEnumerable<object> TapL3()
        {
            _l3 = true; yield return Frames(3);
            _l3 = false; yield return Frames(5);
        }

        /// <summary>One short dash: tap L2, wait until it goes off, then pause <paramref name="after"/> s.</summary>
        private static IEnumerable<object> Short(float after)
        {
            int ex = _execs;
            foreach (var x in TapL2()) yield return x;
            yield return When(() => _execs > ex, 2f, "the short dash");
            Fwd(); yield return Secs(after);
        }

        /// <summary>A long dash from 5 quick L2 taps.</summary>
        private static IEnumerable<object> LongL2()
        {
            int ex = _execs;
            for (int i = 0; i < 5; i++) foreach (var x in TapL2()) yield return x;
            Fwd();
            yield return When(() => _execs > ex, 1f, "the long dash");
        }

        private static IEnumerable<object> CheckBug(string name, string setup)
        {
            yield return Frames(2);
            bool bug = !_lastExecWeak && _lastExecElapsed > M - A && _lastExecElapsed <= M && C.InOujiDashExhaust && C._oujiDashTimer >= 1.49f;
            Check($"{name}: {setup} gives the bug dash", bug, $"last long dash fired at used {F(_lastExecElapsed)} (window {F(M - A)}..{F(M)}); now used {F(C._oujiDashElapsedTimer)}, " +
                  $"timer {F(C._oujiDashTimer)}, exhausted {C.InOujiDashExhaust}");
        }

        /// <summary>
        /// A setup written as tokens, run from full stamina at a human pace: "S1.0" = short dash then 1.0 s of rolling,
        /// "L1.0" = full long dash (5 L2 taps; the test switch keeps walls from ending it), then 1.0 s of rolling,
        /// "B" = the final long dash, checked for the bug (or for no bug when <paramref name="expectBug"/> is false).
        /// </summary>
        private static IEnumerable<object> Sequence(string name, string seq, bool expectBug)
        {
            foreach (var x in Rest(0f)) yield return x;
            Begin(name, $"{seq} from full stamina (expect {(expectBug ? "the bug" : "no bug")})");
            Fwd(); yield return Secs(0.5f);
            float firstCost = -1f;
            foreach (var tok in seq.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                char k = tok[0];
                float pause = tok.Length > 1 ? float.Parse(tok.Substring(1), CultureInfo.InvariantCulture) : 0f;
                if (k == 'S')
                {
                    int ex = _execs;
                    foreach (var x in TapL2()) yield return x;
                    yield return When(() => _execs > ex, 2f, "the short dash");
                    if (firstCost < 0f) firstCost = _lastExecAt;
                    if (C.InOujiDashExhaust)
                    {
                        Check($"{name}: {seq} {(expectBug ? "gives" : "does not give")} the bug dash", !expectBug,
                            $"normally exhausted by a short dash before the final long dash (used {F(C._oujiDashElapsedTimer)}, {F(_lastExecAt - firstCost)} s after the first)");
                        break;
                    }
                    Fwd(); yield return Secs(pause);
                }
                else if (k == 'L')
                {
                    _wallGuard = true; _guarded = 0;
                    foreach (var x in LongL2()) yield return x;
                    if (firstCost < 0f) firstCost = _lastExecAt;
                    float t0 = _lastExecAt;
                    yield return When(() => !C.InOujiDash, 3f, "the long dash to end");
                    _wallGuard = false;
                    Msg($"  {name}: long dash ran {F(Time.time - t0)} s (walls kept: {_guarded}), used {F(C._oujiDashElapsedTimer)}, exhausted {C.InOujiDashExhaust}");
                    if (C.InOujiDashExhaust) break;
                    yield return Secs(Math.Max(0.15f, pause));
                }
                else if (k == 'B')
                {
                    foreach (var x in LongL2()) yield return x;
                    float rest = _lastExecAt - firstCost;
                    yield return Frames(2);
                    bool bug = !_lastExecWeak && _lastExecElapsed > M - A && _lastExecElapsed <= M && C.InOujiDashExhaust && C._oujiDashTimer >= 1.49f;
                    Check($"{name}: {seq} {(expectBug ? "gives" : "does not give")} the bug dash", bug == expectBug,
                        $"final long dash fired at used {F(_lastExecElapsed)} (window {F(M - A)}..{F(M)}), {F(rest)} s after the first dash went off; " +
                        $"bug {bug}, timer {F(C._oujiDashTimer)}, exhausted {C.InOujiDashExhaust}");
                }
            }
            Neutral();
            yield return Secs(1f);
        }

        private static IEnumerable<object> Validation()
        {
            // Human-paced setups.
            if (Want("human"))
            {
                foreach (var x in Sequence("slow1", "S1.0 S1.0 L1.0 B", true)) yield return x;
                foreach (var x in Sequence("tooslow1", "S1.7 S1.7 L1.7 B", false)) yield return x;
                foreach (var x in Sequence("slsl", "S0.5 L0.5 S0.5 B", true)) yield return x;
                foreach (var x in Sequence("seven", "S0.8 S0.8 S0.8 S0.8 S0.8 S0.8 S0.3 B", true)) yield return x;
                foreach (var x in Sequence("sevenfast", "S0.15 S0.15 S0.15 S0.15 S0.15 S0.15 S0.15 B", false)) yield return x;
            }

            // Setup 3: L2 only, 6 short dashes then 5 quick taps. Natural all the way; then the bug dash is measured
            // to its end with the test switch keeping walls from ending it.
            if (Want("s3"))
            {
                foreach (var x in Rest(0f)) yield return x;
                Begin("s3", "setup 3: 6 short dashes, then 5 quick L2 taps, from full stamina");
                Fwd(); yield return Secs(0.5f);
                float first = -1f;
                for (int i = 0; i < 6; i++)
                {
                    foreach (var x in Short(0.15f)) yield return x;
                    if (i == 0) first = _lastExecAt;
                }
                Msg($"  after 6 short dashes: used {F(C._oujiDashElapsedTimer)}, {F(Time.time - first)} s since the first went off");
                _wallGuard = true; _guarded = 0;
                foreach (var x in LongL2()) yield return x;
                foreach (var x in CheckBug("s3", "6 shorts + L2 long")) yield return x;
                _l = Up; _r = new Vector2(0f, 0.25f);   // a gentle curve, to stay off the walls
                float len = 0f, fired = _lastExecAt;
                foreach (var x in WatchDash("s3", 14f, v => len = v, new[] { 4f })) yield return x;
                _wallGuard = false;
                float since = Time.time - fired;
                Msg($"  s3: bug dash ran {F(since)} s from firing (walls kept from ending it: {_guarded} wall hits); ended by: {_lastQuit}; " +
                    $"now used {F(C._oujiDashElapsedTimer)}, exhaust {F(C._oujiDashExhaustTimer)}, can dash {C.CanOujiDash()}");
                Check("s3: with no wall in the way the bug dash lasts until the 8 s exhaustion ends, then stamina is full",
                    since > 7.8f && since < 8.3f && C._oujiDashElapsedTimer == 0f && C.CanOujiDash(), $"{F(since)} s, used {F(C._oujiDashElapsedTimer)}");
                yield return Secs(1f);
            }

            // Setup 1: short, short, full long, long.
            if (Want("s1"))
            {
                foreach (var x in Rest(0f)) yield return x;
                Begin("s1", "setup 1: short, short, full long dash, long dash, from full stamina");
                Fwd(); yield return Secs(0.5f);
                foreach (var x in Short(0.3f)) yield return x;
                foreach (var x in Short(0.3f)) yield return x;
                _wallGuard = true; _guarded = 0;
                foreach (var x in LongL2()) yield return x;
                float t0 = Time.time;
                yield return When(() => !C.InOujiDash, 3f, "the setup long dash to end");
                _wallGuard = false;
                Msg($"  setup long dash ran {F(Time.time - t0)} s (wall hits kept: {_guarded}); used now {F(C._oujiDashElapsedTimer)}; ended by: {_lastQuit}");
                yield return Secs(0.15f);
                foreach (var x in LongL2()) yield return x;
                foreach (var x in CheckBug("s1", "short, short, full long, long")) yield return x;
                yield return Secs(1f);
            }

            // Setup 4: long + 1 extend (0.6..1.0 s in) + long.
            if (Want("s4"))
            {
                foreach (var x in Rest(0f)) yield return x;
                Begin("s4", "setup 4: long dash, one extend about 0.7 s in (4 L2 taps), long dash, from full stamina");
                Fwd(); yield return Secs(0.5f);
                _wallGuard = true; _guarded = 0;
                foreach (var x in LongL2()) yield return x;
                float fired = _lastExecAt;
                yield return Secs(0.1f);
                for (int i = 0; i < 4; i++) { foreach (var x in TapL2(3, 0)) yield return x; yield return Secs(0.17f); }
                Msg($"  extend landed {F(_lastExtendAt - fired)} s after the dash started");
                yield return When(() => !C.InOujiDash, 3f, "the setup long dash to end");
                _wallGuard = false;
                Msg($"  setup long dash ran {F(Time.time - fired)} s (wall hits kept: {_guarded}); used now {F(C._oujiDashElapsedTimer)}; ended by: {_lastQuit}");
                yield return Secs(0.15f);
                foreach (var x in LongL2()) yield return x;
                foreach (var x in CheckBug("s4", "long + 1 extend + long")) yield return x;
                yield return Secs(1f);
            }

            // A quick turn (L3) during a normal long dash, then during a bug dash. The test switch keeps walls out of it.
            foreach (var (bug, heading) in new[] { (false, 0), (true, 1), (true, 2), (true, 3) })
            {
                string name = bug ? "qturnbug" : "qturn";
                if (!Want(name)) continue;
                if (bug && heading > 1 && _lastQturnClean) continue;
                foreach (var x in Rest(0f, heading)) yield return x;
                Begin(name + heading, $"quick turn (L3) 0.2 s into a {(bug ? "bug" : "normal")} long dash, heading {heading}");
                Fwd(); yield return Secs(0.5f);
                _wallGuard = false; _guarded = 0;
                for (int i = 0; i < 4; i++) foreach (var x in TapL2()) yield return x;
                if (bug) C._oujiDashElapsedTimer = M - A * 0.5f;
                foreach (var x in TapL2()) yield return x;
                Fwd();
                yield return Secs(0.2f);
                if (!C.InOujiDash) { Msg($"  {name}{heading}: a wall ended the dash before the turn ({_lastQuit}); trying another heading"); _lastQturnClean = false; continue; }
                _lastQturnClean = true;
                Msg($"  before the turn: timer {F(C._oujiDashTimer)}, used {F(C._oujiDashElapsedTimer)}, exhaust {F(C._oujiDashExhaustTimer)}, speed {F(new Vector2(C._nowSpeed.x, C._nowSpeed.z).magnitude)}, angle {F(C._coreAngle)}");
                foreach (var x in TapL3()) yield return x;
                yield return When(() => C._nowAction == MainGameCore.eSpecialAction.OujiTurn, 0.3f, "the turn to start");
                bool turned = C._nowAction == MainGameCore.eSpecialAction.OujiTurn;
                float tt = C._oujiDashTimer, tu = C._oujiDashElapsedTimer, te = C._oujiDashExhaustTimer;
                yield return When(() => C._nowAction != MainGameCore.eSpecialAction.OujiTurn, 1f, "the turn to end");
                Msg($"  turn {(turned ? "happened" : "did NOT start")}; during it: timer {F(tt)} -> {F(C._oujiDashTimer)}, used {F(tu)} -> {F(C._oujiDashElapsedTimer)}, exhaust {F(te)} -> {F(C._oujiDashExhaustTimer)}; " +
                    $"speed {F(new Vector2(C._nowSpeed.x, C._nowSpeed.z).magnitude)}, angle {F(C._coreAngle)}");
                yield return Frames(3);
                Msg($"  {name}: 3 frames after the turn: dashing {C.InOujiDash}, timer {F(C._oujiDashTimer)}, speed {F(new Vector2(C._nowSpeed.x, C._nowSpeed.z).magnitude)}; ended by: {_lastQuit}");
                yield return Secs(0.5f);
                Msg($"  {name}: 0.5 s later: dashing {C.InOujiDash}, timer {F(C._oujiDashTimer)}, speed {F(new Vector2(C._nowSpeed.x, C._nowSpeed.z).magnitude)}; ended by: {_lastQuit}");
                _wallGuard = false;
                yield return Secs(0.5f);
            }
        }

        private static IEnumerable<object> Recipe(string name, int extraSteps)
        {
            foreach (var x in Rest(0f)) yield return x;
            Begin(name, $"2 L2 taps, then 2 long stick dashes ({(extraSteps > 0 ? extraSteps + " extra steps on the first" : "plain")}); natural stamina from rested");
            Fwd(); yield return Secs(0.5f);
            for (int k = 0; k < 2; k++)
            {
                int ex = _execs;
                foreach (var x in TapL2()) yield return x;
                yield return When(() => _execs > ex, 2f, "short dash " + (k + 1));
                Fwd(); yield return Secs(0.8f);
            }
            Msg($"  after 2 short dashes: elapsed {F(C._oujiDashElapsedTimer)}");
            foreach (var x in Wiggle(0, 7 + extraSteps)) yield return x;
            Fwd();
            yield return When(() => !C.InOujiDash, 6f, "long dash 1 to end");
            Msg($"  long dash 1 over: elapsed {F(C._oujiDashElapsedTimer)}, exhausted {C.InOujiDashExhaust}, last quit: {_lastQuit}");
            yield return When(Idle, 3f, "the input to reset");
            // Back to the open heading for the dash that matters (TeleportPlayer does not touch the stamina).
            float before = C._oujiDashElapsedTimer;
            foreach (var x in Place(_headings.Count > 0 ? _headings[0] : C._coreAngle)) yield return x;
            Msg($"  placed on the open heading: elapsed {F(before)} -> {F(C._oujiDashElapsedTimer)}");
            if (C.InOujiDashExhaust)
            {
                Check(name + ": long dash 1 left stamina for long dash 2", false, $"exhausted after long dash 1 (elapsed {F(C._oujiDashElapsedTimer)})");
                yield break;
            }
            Fwd(); yield return Secs(0.3f);
            foreach (var x in Wiggle(0, 7)) yield return x;
            Fwd();
            float len = 0f;
            foreach (var x in WatchDash(name, 14f, v => len = v, extraSteps == 0 ? new[] { 4f } : null)) yield return x;
            bool inWindow = _lastExecElapsed > M - A && _lastExecElapsed <= M;
            Msg($"  {name}: long dash 2 ran {F(len)} s; elapsed at it {F(_lastExecElapsed)} (window {F(M - A)}..{F(M)}: {inWindow}); last quit: {_lastQuit}");
            if (extraSteps == 0)
                Check(name + ": the second long dash keeps going until the stamina comes back", len > 7f, $"dash ran {F(len)} s; elapsed at the dash {F(_lastExecElapsed)}");
            yield return Secs(1f);
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

        private void Finish(string result)
        {
            _running = false;
            try { _csv?.Flush(); } catch { }
            Msg(result);
            Msg("DONE, quitting");
            _phase = Phase.Done;
            Application.Quit();
        }
    }
}
