using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2CppApp.KatamariSin;
using MelonLoader;
using UnityEngine;
using OUAKModAPI;

namespace OUAKMultiplayer
{
    /// <summary>
    /// Diagnostic for the stage intro (to place the online start hold correctly): for 120 s after a stage
    /// (MainGame) loads, logs "[StageStart +t]" lines for the intro calls (scroll intro LoadKingGameController,
    /// GlobalUIController load/curtain/King message, TransitionCurtainController, MainGameManager demo camera
    /// and start message) and every change of the stage's start flags, curtain state and time scale.
    /// </summary>
    internal static class StageStartTrace
    {
        /// <summary>
        /// Off: the intro is mapped (see IntroSync). Each traced method is a Harmony hook, and a game error inside
        /// a hooked method can't be caught across the mod's code, so keep hooks to what's needed.
        /// </summary>
        public static readonly bool Enabled = false;

        private static float _since = -1f;
        private static string _lastScene = "";
        private static string _lastFlags = "";
        private static float _nextPoll;
        private static GlobalUIController _ui;
        private static TransitionCurtainController _curtain;
        private static LoadKingGameController _scroll;

        public static void Init(HarmonyLib.Harmony harmony)
        {
            var postfix = new HarmonyMethod(AccessTools.Method(typeof(StageStartTrace), nameof(AfterCall)));
            int ok = 0;
            foreach (var (type, names) in new (Type, string[])[]
            {
                (typeof(GlobalUIController), new[] { "StartLoadGame", "EndLoadGame", "CloseCurtain", "SkipNowMessage", "SkipNowMessageImmediate", "CallKingMessageDemo" }),
                (typeof(TransitionCurtainController), new[] { "CurtainClose", "CurtainOpen" }),
                (typeof(LoadKingGameController), new[] { "LoadingStart", "IsLoadEnd" }),
                (typeof(MainGameManager), new[] { "StartUI", "RequestStartMessage", "StartDemoCamera", "EndDemoCamera", "EndDemoCameraWithFade" }),
            })
            {
                foreach (var name in names)
                {
                    try
                    {
                        var m = AccessTools.Method(type, name);
                        if (m == null) continue;
                        harmony.Patch(m, postfix: postfix);
                        ok++;
                    }
                    catch (Exception e) { MelonLogger.Warning($"[StageStart] can't trace {type.Name}.{name}: {e.Message}"); }
                }
            }
            MelonLogger.Msg($"[StageStart] tracing {ok} intro calls.");
        }

        private static void AfterCall(MethodBase __originalMethod)
        {
            if (_since < 0f) return;
            try { Log($"call {__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}"); } catch { }
        }

        public static void Tick()
        {
            string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (scene != _lastScene)
            {
                _lastScene = scene;
                if (scene == "MainGame")
                {
                    _since = Time.unscaledTime;
                    _lastFlags = "";
                    _ui = null;
                    _curtain = null;
                    _scroll = null;
                    Log("MainGame loaded");
                }
            }
            if (_since < 0f) return;
            if (Time.unscaledTime - _since > 120f) { Log("trace window over"); _since = -1f; return; }
            if (Time.unscaledTime < _nextPoll) return;
            _nextPoll = Time.unscaledTime + 0.1f;
            try
            {
                string flags = Flags();
                if (flags == _lastFlags) return;
                _lastFlags = flags;
                Log(flags);
            }
            catch (Exception e) { Log("poll failed: " + e.Message); _since = -1f; }
        }

        private static string Flags()
        {
            var parts = new List<string>();
            var gm = GameRefs.Main;
            if (gm != null)
            {
                parts.Add($"loading={gm.isStageLoading} loaded={gm.isStageLoaded} player={gm.PlayerInitiated} running={gm.IsGameRunning}");
                parts.Add($"startDemo={gm.StartDemoInitiated} startMsgDone={gm.StartMessageDone} demo={gm._isInDemo} demoCam={gm._isInDemoCamera} demoFade={gm._isInDemoFade}");
                parts.Add($"clear={gm._isInClearDemo} over={gm._isInGameOver}");
            }
            else parts.Add("no MainGameManager");
            if (_ui == null) _ui = UnityEngine.Object.FindObjectOfType<GlobalUIController>();
            if (_ui != null) parts.Add($"kingMsg={_ui.IsInDemoMessage} canSkip={_ui.CanSkipMessage}");
            if (_curtain == null) _curtain = UnityEngine.Object.FindObjectOfType<TransitionCurtainController>();
            if (_curtain != null) parts.Add($"curtain={_curtain.Status}");
            if (_scroll == null) _scroll = UnityEngine.Object.FindObjectOfType<LoadKingGameController>();
            if (_scroll != null) parts.Add($"scroll={(_scroll.gameObject.activeInHierarchy ? "on" : "off")} loop={_scroll._loadingLoop}");
            parts.Add($"timeScale={Time.timeScale:0.##}");
            return string.Join(" | ", parts);
        }

        private static void Log(string s) => MelonLogger.Msg($"[StageStart +{Time.unscaledTime - _since:0.00}] {s}");
    }
}
