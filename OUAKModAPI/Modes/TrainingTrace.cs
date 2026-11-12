using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using Il2Cpp;
using Il2CppApp.KatamariSin;
using Il2CppIceMilkTea.Core;
using MelonLoader;
using UnityEngine;
using UnitySceneManager = UnityEngine.SceneManagement.SceneManager;

namespace OUAKModAPI
{
    /// <summary>
    /// Logs how a multiplayer-map launch progresses, since this build writes no Player.log and a stalled launch
    /// otherwise leaves nothing to go on: the scenes, the multiplayer manager's state machine, loading flags and
    /// rule, plus any Unity errors raised meanwhile. A line is written whenever that picture changes. SceneLoader
    /// starts a trace for every multiplayer map; it ends a few seconds after the round starts, or after 90 s.
    /// </summary>
    public static class TrainingTrace
    {
        private const float MaxDuration = 90f;

        private static readonly ConcurrentQueue<string> UnityErrors = new();
        private static readonly Dictionary<string, int> SeenErrors = new();
        private static Application.LogCallback _logCallback; // kept referenced so the IL2CPP side can keep calling it
        private static volatile bool _capturing;
        private static bool _stageLogged;
        private static float _start, _until, _lastLine;
        private static string _label, _last;

        public static bool Active { get; private set; }
        public static bool RoundStarted { get; private set; }
        public static string LastSnapshot => _last;

        /// <summary>How long the trace keeps going once the round runs (the self-test watches the round end).</summary>
        public static float AfterStart = 5f;

        public static void Begin(string label)
        {
            HookUnityLog();
            float now = Time.unscaledTime;
            _start = now;
            _until = now + MaxDuration;
            _lastLine = 0f;
            _label = label;
            _last = null;
            RoundStarted = false;
            _stageLogged = false;
            SeenErrors.Clear();
            Active = true;
            _capturing = true;
            MelonLogger.Msg($"[Trace] {label}: started");
        }

        public static void Note(string text)
        {
            if (Active) MelonLogger.Msg($"[Trace +{Time.unscaledTime - _start:0.0}s] {text}");
        }

        public static void Tick()
        {
            if (!Active) return;
            DrainUnityErrors();
            float now = Time.unscaledTime;
            string snap = Describe(out bool playing);
            if (snap != _last || now - _lastLine >= 10f)
            {
                MelonLogger.Msg($"[Trace +{now - _start:0.0}s] {snap}");
                _last = snap;
                _lastLine = now;
            }
            if (!_stageLogged) LogSpawnPoints();
            if (playing && !RoundStarted)
            {
                RoundStarted = true;
                _until = now + AfterStart;
                MelonLogger.Msg($"[Trace] {_label}: the round is running");
            }
            if (now >= _until)
            {
                DrainUnityErrors();
                foreach (var e in SeenErrors)
                    if (e.Value > 1) MelonLogger.Warning($"[Unity] {e.Value}x: {e.Key}");
                Active = false;
                _capturing = false;
                MelonLogger.Msg($"[Trace] {_label}: {(RoundStarted ? "done" : "gave up waiting for the round to start")}");
            }
        }

        public static string Describe() => Describe(out _);

        private static string Describe(out bool playing)
        {
            playing = false;
            var sb = new StringBuilder();
            try
            {
                sb.Append("scene=").Append(UnitySceneManager.GetActiveScene().name);
                int n = UnitySceneManager.sceneCount;
                if (n > 1)
                {
                    sb.Append(" [");
                    for (int i = 0; i < n; i++)
                    {
                        var scene = UnitySceneManager.GetSceneAt(i);
                        sb.Append(i > 0 ? "," : "").Append(scene.name).Append(scene.isLoaded ? "" : "(loading)");
                    }
                    sb.Append(']');
                }
            }
            catch (Exception e) { sb.Append("scene=? (").Append(e.Message).Append(')'); }

            try
            {
                var m = SingletonMonoBehaviour<MultiplayGameManager>.instance;
                if (m == null) sb.Append(" | mgr=none");
                else
                {
                    var sm = m.stateMachine;
                    string state = StateName(sm);
                    sb.Append(" | mgr state=").Append(state);
                    if (sm?.nextState != null) sb.Append("->").Append(sm.nextState.GetIl2CppType().Name);
                    var training = sm?.currentState?.TryCast<MultiplayGameManager.TrainingState>();
                    if (training != null) sb.Append(" step=").Append(training.step);
                    sb.Append(" takesTraining=").Append(SceneLoader.AcceptsTraining(sm))
                      .Append(" loading=").Append(m.isLoading)
                      .Append(" trainingCo=").Append(m.isStartTrainingCoroutine)
                      .Append(" single=").Append(m.isSinglePlay)
                      .Append(" rule=").Append(m.nowRule)
                      .Append(" load=").Append(m.isStartLoad).Append('/').Append(m.isStartLoadFinish);
                    var rules = m.roomInfo?.rule;
                    if (rules != null && rules.Length > 0) sb.Append(" room=").Append(rules[0]);
                    if (m.dicPlayerCore != null) sb.Append(" cores=").Append(m.dicPlayerCore.Count);
                    var points = m.GetRacePointData();
                    if (points != null) sb.Append(" racePoints=").Append(points.Count);
                    playing = state == "GameState";
                }
            }
            catch (Exception e) { sb.Append(" | mgr=? (").Append(e.Message).Append(')'); }

            try
            {
                var g = GameRefs.Global;
                if (g != null) sb.Append(" | global multi=").Append(g.isMultiPlay).Append(" stage=").Append(g.launcherStage);
                var gm = GameRefs.Main;
                if (gm != null) sb.Append(" | main spawned=").Append(gm.PlayerInitiated).Append(" running=").Append(gm.IsGameRunning);
            }
            catch (Exception e) { sb.Append(" | global=? (").Append(e.Message).Append(')'); }
            return sb.ToString();
        }

        // MainGameManager.SpawnMultiPlayer places the katamaris on the stage's StartPoint{n} children of
        // SpawnPointParent and ReStartPoint{n} under its parent's "ReStartPoint", so log what the map has.
        private static void LogSpawnPoints()
        {
            try
            {
                var stage = GameRefs.Main?._inStageData;
                if (stage == null) return;
                _stageLogged = true;
                var spawns = stage.SpawnPointParent;
                var root = spawns?.parent;
                var restart = root?.Find("ReStartPoint");
                MelonLogger.Msg($"[Trace] stage spawn points: SpawnPointParent={Children(spawns)} | ReStartPoint={Children(restart)} | its parent={Children(root)}");
            }
            catch (Exception e)
            {
                _stageLogged = true;
                MelonLogger.Warning($"[Trace] spawn points: {e.Message}");
            }
        }

        private static string Children(Transform t)
        {
            if (t == null) return "missing";
            var sb = new StringBuilder(t.name).Append('(').Append(t.childCount).Append("):");
            for (int i = 0; i < t.childCount && i < 24; i++) sb.Append(i > 0 ? "," : " ").Append(t.GetChild(i).name);
            return sb.ToString();
        }

        public static string StateName(ImtStateMachine<MultiplayGameManager> sm)
        {
            if (sm == null) return "null";
            if (!sm.Running) return "(not started)";
            return sm.CurrentStateName ?? "?";
        }

        private static void HookUnityLog()
        {
            if (_logCallback != null) return;
            try
            {
                _logCallback = new Action<string, string, LogType>(OnUnityLog);
                Application.add_logMessageReceivedThreaded(_logCallback);
            }
            catch (Exception e) { MelonLogger.Warning($"[Trace] Unity log hook failed: {e.Message}"); }
        }

        // Called on whichever thread logged, so it only queues.
        private static void OnUnityLog(string condition, string stackTrace, LogType type)
        {
            if (!_capturing || type == LogType.Log || type == LogType.Warning) return;
            if (UnityErrors.Count < 200) UnityErrors.Enqueue($"{type}: {condition}\n{stackTrace}");
        }

        private static void DrainUnityErrors()
        {
            while (UnityErrors.TryDequeue(out string error))
            {
                int cut = error.IndexOf('\n');
                string head = cut < 0 ? error : error.Substring(0, cut);
                SeenErrors.TryGetValue(head, out int seen);
                SeenErrors[head] = seen + 1;
                if (seen > 0 || SeenErrors.Count > 40) continue;
                MelonLogger.Warning($"[Unity] {(error.Length > 1500 ? error.Substring(0, 1500) + " ..." : error.TrimEnd())}");
            }
        }
    }
}
