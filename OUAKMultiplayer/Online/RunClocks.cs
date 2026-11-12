using System;
using MelonLoader;
using UnityEngine;
using OUAKModAPI;

namespace OUAKMultiplayer
{
    /// <summary>
    /// Your three timers in a session run, and the moment you reach the stage's win condition:
    /// <list type="bullet">
    /// <item><b>IGT</b> (in-game time): how long you actually played this run. It counts game frames from the shared GO
    /// to the end of your run while the stage is in play: not while the game is paused (the pause menu, a one-time tip),
    /// in its clear demo, game over or a restart's wait. A frozen stage clock (the Timer freebie) doesn't stop it, and a
    /// restart of your own keeps adding to it (the user, 2026-11-06).</item>
    /// <item><b>RTA</b> (real time attack): real time from the shared GO to the end of your run, pauses and all
    /// (SyncRun's RealTime on the host's clock).</item>
    /// <item><b>Stage time</b>: the game's own clock: time left (MainGameManager._nowTime), or elapsed on time-attack stages.</item>
    /// </list>
    /// The win condition ("20 food items", "15 cm"...) is the game's own: MainGameManager.MakeStageCleared sets _hasCleared
    /// and copies the stage clock to _clearTime ("Size Time" on its result screen). The first frame _hasCleared is true in
    /// the run, IGT, RTA and the stage clock are recorded.
    /// </summary>
    internal static class RunClocks
    {
        public static float Igt { get; private set; }
        public static float ClearIgt { get; private set; } = -1f;
        public static float ClearRta { get; private set; } = -1f;
        public static float ClearStage { get; private set; } = -1f;
        public static bool Counting { get; private set; }

        // Read once a frame (Tick) for the state you send: the HUD's rank badge, the stage's rating and its own score.
        public static byte HudRankNow { get; private set; } = 255;
        public static sbyte RateNow { get; private set; } = -1;
        public static ScoreKind ScoreKindNow { get; private set; }
        public static int ScoreNow { get; private set; } = -1;

        private static int _runKey = int.MinValue;

        /// <summary>RTA so far (the finish's RealTime once your run ended); -1 before the start.</summary>
        public static float Rta
        {
            get
            {
                var mine = SyncRun.MyResult;
                if (mine != null && mine.RealTime >= 0f) return mine.RealTime;
                return SyncRun.StartAt > 0f && SyncRun.Phase == RunPhase.Running ? SyncRun.HostNow - SyncRun.StartAt : -1f;
            }
        }

        /// <summary>The live rank badge the game's HUD shows (MainGameManager.GetNowClearRateIcon()), 255 = unknown.</summary>
        public static byte HudRank()
        {
            try
            {
                var gm = GameRefs.Main;
                if (gm == null || GameRefs.IsMultiplay) return 255;
                return (byte)Mathf.Clamp(gm.GetNowClearRateIcon(), 0, 254);
            }
            catch { return 255; }
        }

        public static void Tick()
        {
            if (NetSession.Connected && GameRefs.Main != null)
            {
                HudRankNow = HudRank();
                StageScore.Read(GameRefs.Main, out var rate, out var kind, out int score);
                RateNow = rate;
                ScoreKindNow = kind;
                ScoreNow = score;
            }
            else
            {
                HudRankNow = 255;
                RateNow = -1;
                ScoreKindNow = ScoreKind.None;
                ScoreNow = -1;
            }
            int key = HashCode.Combine(SyncRun.RunId, SyncRun.RunMap);
            if (key != _runKey)
            {
                _runKey = key;
                Igt = 0f;
                ClearIgt = ClearRta = ClearStage = -1f;
            }
            var gm = GameRefs.Main;
            bool inRun = SyncRun.Phase == RunPhase.Running && SyncRun.MyResult == null && SyncRun.RunMap != 0
                         && Ghosts.CurrentMapKey == SyncRun.RunMap && gm != null;
            Counting = false;
            if (!inRun) return;
            try
            {
                Counting = gm.IsGameRunning && !GameEvents.Paused && !gm._isInClearDemo && !gm._isInGameOver && !gm._isInRestartWait;
                if (Counting) Igt += Time.deltaTime;
                if (ClearIgt < 0f && gm._hasCleared)
                {
                    ClearIgt = Igt;
                    ClearRta = Rta;
                    ClearStage = gm._clearTime;
                    MelonLogger.Msg($"[Online] Win condition reached: IGT {Clock(ClearIgt)}, RTA {Clock(ClearRta)}, stage clock {Clock(ClearStage)}.");
                }
            }
            catch { Counting = false; }
        }

        /// <summary>"1:23.45".</summary>
        public static string Clock(float seconds) => seconds < 0f ? "-" : KatamariState.ClockText(seconds);

        /// <summary>"1:23.4", for the player bar.</summary>
        public static string Short(float seconds)
        {
            if (seconds < 0f) return "-";
            float t = Mathf.Max(0f, seconds);
            return $"{(int)t / 60}:{(int)t % 60:00}.{(int)(t * 10f) % 10}";
        }

        public static string Describe() =>
            $"IGT {Clock(Igt)} (counting {Counting}), RTA {Clock(Rta)}, win condition at IGT {Clock(ClearIgt)} / RTA {Clock(ClearRta)} / stage {Clock(ClearStage)}";
    }
}
