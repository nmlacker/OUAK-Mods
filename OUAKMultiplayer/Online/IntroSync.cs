using System;
using HarmonyLib;
using Il2CppApp.KatamariSin;
using MelonLoader;
using UnityEngine;
using OUAKModAPI;

namespace OUAKMultiplayer
{
    /// <summary>
    /// The start of a stage played together. A stage start (traced, see StageStartTrace) goes:
    ///   curtain closes (TransitionCurtainController, the wavy transition) → scroll loading → opening fly-over
    ///   behind the curtain, which then opens (MainGameManager demo cameras, ~10 s) → EndDemoCameraWithFade →
    ///   MainGameManager.Update calls RequestStartMessage → the King's opening talk; the stage clock starts when
    ///   that talk ends or is skipped (GlobalUIController.SkipNowMessage). A restart skips the fly-over.
    /// During an online run (SyncRun.ShouldGate) this game:
    /// - ends the fly-over early with the game's own EndDemoCameraWithFade if the skip vote passed;
    /// - holds RequestStartMessage (Update keeps the prince frozen meanwhile), closes the curtain to black and
    ///   reports Ready; on the host's start time (after a 3 s countdown) it opens the curtain and lets the King
    ///   begin, so the King's talk starts for everyone at once;
    /// - in strict mode blocks skipping that opening talk, so everyone's clock starts together.
    /// Offline matches have no King's talk: they join the run without the black screen.
    /// </summary>
    internal static class IntroSync
    {
        private enum Step { None, Intro, Closing, OnBlack, WaitStart, Released }

        private static Step _step;
        private static int _cycleMap;
        private static bool _wasRestartWait;
        private static float _lastCycleAt = -100f;
        private static bool _lastMsgDone, _bypass;
        private static float _closingSince, _nextSkip, _releasedAt;
        private static GlobalUIController _ui;
        private static TransitionCurtainController _curtain;

        public static bool OnBlack => _step is Step.OnBlack or Step.WaitStart;

        /// <summary>This game is at the start of <paramref name="map"/> (the fly-over, the curtain, held on black), not yet playing.</summary>
        public static bool BeforeStart(int map) =>
            map != 0 && _cycleMap == map && _step is Step.Intro or Step.Closing or Step.OnBlack or Step.WaitStart;

        public static void Init(HarmonyLib.Harmony harmony)
        {
            try
            {
                harmony.Patch(AccessTools.Method(typeof(MainGameManager), nameof(MainGameManager.RequestStartMessage)),
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(IntroSync), nameof(BeforeStartMessage))));
                harmony.Patch(AccessTools.Method(typeof(GlobalUIController), nameof(GlobalUIController.SkipNowMessage)),
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(IntroSync), nameof(BeforeSkipMessage))));
            }
            catch (Exception e) { MelonLogger.Warning($"[Online] Start sync patches failed: {e.Message}"); }
        }

        /// <summary>The gate: hold the King's opening talk until the shared start.</summary>
        private static bool BeforeStartMessage()
        {
            try
            {
                if (_bypass || _step == Step.Released || !SyncRun.ShouldGate(Ghosts.CurrentMapKey)) return true;
                if (_step is Step.None or Step.Intro)
                {
                    _step = Step.Closing;
                    _closingSince = Time.unscaledTime;
                    CloseCurtain();
                    MelonLogger.Msg("[Online] At the start: holding the King's talk until everyone is here.");
                }
                return false;
            }
            catch { return true; }
        }

        /// <summary>Strict start: the King's opening talk plays in full for everyone.</summary>
        private static bool BeforeSkipMessage()
        {
            try
            {
                var gm = GameRefs.Main;
                bool opening = gm != null && !gm.StartMessageDone && _step == Step.Released;
                return !(opening && !SyncRun.RaceStart && NetSession.Connected && SyncRun.Phase == RunPhase.Running);
            }
            catch { return true; }
        }

        public static void Tick()
        {
            var gm = GameRefs.Main;
            int map = Ghosts.CurrentMapKey;
            if (gm == null || map == 0)
            {
                _step = Step.None;
                _cycleMap = 0;
                _lastMsgDone = false;
                _wasRestartWait = false;
                return;
            }

            bool msgDone = gm.StartMessageDone;
            bool restartWait = gm._isInRestartWait;
            bool fresh = map != _cycleMap;
            float t = Time.unscaledTime;
            // A restart: the King's talk starts over (it had ended), or the game's restart begins (MainGameManager.RestartStage
            // sets _isInRestartWait first thing; this also catches a restart made while the King was still talking, which
            // the first sign misses). Seen as it begins, so a new run is set up before the King's talk is asked for again
            // and the start can be held. One restart, one cycle.
            bool restart = !fresh && ((_lastMsgDone && !msgDone) || (!_wasRestartWait && restartWait)) && t - _lastCycleAt > 6f;
            _lastMsgDone = msgDone;
            _wasRestartWait = restartWait;
            if (fresh || restart)
            {
                _lastCycleAt = t;
                _cycleMap = map;
                _step = Step.Intro;
                if (NetSession.Connected) SyncRun.OnStageCycle(map, fresh);
                // Matches have no King's talk to hold at: they're ready straight away.
                if (GameRefs.IsMultiplay && SyncRun.ShouldGate(map))
                {
                    SyncRun.LocalReady(map);
                    _step = Step.WaitStart;
                }
            }

            float now = Time.unscaledTime;
            switch (_step)
            {
                case Step.Intro:
                    if (SyncRun.SkipIntroVoted && SyncRun.ShouldGate(map) && gm._isInDemoCamera && !gm._isInDemoFade && now >= _nextSkip)
                    {
                        _nextSkip = now + 0.5f;
                        gm.EndDemoCameraWithFade();
                        MelonLogger.Msg("[Online] Skipping the opening fly-over (vote passed).");
                    }
                    break;
                case Step.Closing:
                    HoldKatamari(gm);
                    if (CurtainClosed() || now - _closingSince > 2.5f)
                    {
                        _step = Step.OnBlack;
                        SyncRun.LocalReady(map);
                    }
                    break;
                case Step.OnBlack:
                    // The game already gives control here; nobody gets a head start while it's black.
                    HoldKatamari(gm);
                    if (SyncRun.ReleaseNow()) Release(gm, now);
                    break;
                case Step.WaitStart:
                    if (SyncRun.ReleaseNow()) _step = Step.Released;
                    break;
                case Step.Released:
                    // The game retries RequestStartMessage itself; if it hasn't begun shortly after, start it.
                    if (_releasedAt > 0f && now - _releasedAt > 0.4f)
                    {
                        _releasedAt = 0f;
                        if (!gm.StartDemoInitiated && !GameRefs.IsMultiplay)
                        {
                            _bypass = true;
                            try { gm.RequestStartMessage(); }
                            finally { _bypass = false; }
                        }
                    }
                    break;
            }
        }

        private static void HoldKatamari(MainGameManager gm)
        {
            try { gm.GetCore()?.FreezeInput(0.25f); } catch { }
        }

        private static void Release(MainGameManager gm, float now)
        {
            _step = Step.Released;
            _releasedAt = now;
            OpenCurtain();
            MelonLogger.Msg("[Online] Shared start: the King's talk begins.");
        }

        private static void CloseCurtain()
        {
            try
            {
                if (_ui == null) _ui = UnityEngine.Object.FindObjectOfType<GlobalUIController>();
                if (_ui != null) _ui.CloseCurtain(false);
            }
            catch (Exception e) { MelonLogger.Warning($"[Online] Couldn't close the curtain: {e.Message}"); }
        }

        private static void OpenCurtain()
        {
            try
            {
                var c = Curtain();
                if (c != null && !c.IsCurtainOpen && !c.IsCurtainOpening) c.CurtainOpen(0.5f);
            }
            catch (Exception e) { MelonLogger.Warning($"[Online] Couldn't open the curtain: {e.Message}"); }
        }

        private static bool CurtainClosed()
        {
            try
            {
                var c = Curtain();
                return c == null || c.IsCurtainClose;
            }
            catch { return true; }
        }

        private static TransitionCurtainController Curtain()
        {
            if (_curtain == null) _curtain = UnityEngine.Object.FindObjectOfType<TransitionCurtainController>();
            return _curtain;
        }
    }
}
