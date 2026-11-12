using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Il2CppApp.KatamariSin;
using Il2CppCriWare;
using OUAKModAPI;
using UnityEngine;

namespace OUAKSongSelector
{
    /// <summary>
    /// songtest.flag with "timer": your file plays in a stage, the Timer freebie is used (the game greys the screen and
    /// pauses its music with SoundManager.PauseBGMInStage), the pause menu is opened and closed while the freebie still
    /// runs, and the freebie ends. Every frame goes to timertest.csv: the game's music-pause calls, the stage channel,
    /// the freebie's remaining time, your file's position and the mixer level.
    /// Your file must stop the moment the game's music stops, stay stopped while the Timer runs (also after the pause
    /// menu), and go on from the same position when the game's music comes back.
    /// </summary>
    internal static partial class SongTest
    {
        private static bool _timer;
        private static StreamWriter _csv;
        private static readonly List<string> _events = new();
        private static readonly HookSet TimerHooks = new("ouak.songselector.timertest");
        private static string FileLong => TestDir + "/SongTest Long.wav";
        private static string RefLong => TrackRef.File(FileLong);

        private static void InstallTimerHooks()
        {
            var t = typeof(SongTest);
            Msg("timer hooks: " + string.Join(", ", new[]
            {
                $"PauseBGMInStage {TimerHooks.Prefix(typeof(SoundController_CRI), "PauseBGMInStage", t, nameof(EvPause))}",
                $"UnpauseBGMInStage {TimerHooks.Prefix(typeof(SoundController_CRI), "UnpauseBGMInStage", t, nameof(EvUnpause))}",
                $"ImmediatePauseBGMInStage {TimerHooks.Prefix(typeof(SoundController_CRI), "ImmediatePauseBGMInStage", t, nameof(EvImmediate))}",
                $"PlayActionCue {TimerHooks.Prefix(typeof(SoundController_CRI), "PlayActionCue", t, nameof(EvAction))}",
                $"RecheckTimerSaturation {TimerHooks.Prefix(typeof(MainGameManager), "RecheckTimerSaturation", t, nameof(EvRecheck))}",
                $"SuspendItemEffect {TimerHooks.Prefix(typeof(MainGameManager), "SuspendItemEffect", t, nameof(EvSuspend))}",
                $"ResumeItemEffect {TimerHooks.Prefix(typeof(MainGameManager), "ResumeItemEffect", t, nameof(EvResume))}",
                $"EndItemEffect {TimerHooks.Prefix(typeof(MainGameManager), "EndItemEffect", t, nameof(EvEnd))}",
            }));
        }

        private static void Ev(string what) { try { _events.Add(what); Msg($"event {what} (file t {F(CustomAudio.Time)}, playing {CustomAudio.IsPlaying})"); } catch { } }
        public static void EvPause() => Ev("CRI.PauseBGMInStage");
        public static void EvUnpause() => Ev("CRI.UnpauseBGMInStage");
        public static void EvImmediate() => Ev("CRI.ImmediatePauseBGMInStage");
        public static void EvAction(string __0) => Ev("CRI.PlayActionCue " + __0);
        public static void EvRecheck() => Ev("MGM.RecheckTimerSaturation");
        public static void EvSuspend() => Ev("MGM.SuspendItemEffect");
        public static void EvResume() => Ev("MGM.ResumeItemEffect");
        public static void EvEnd() => Ev("MGM.EndItemEffect");

        private static IEnumerable<float> TimerSetup()
        {
            TakeBackup();
            Directory.CreateDirectory(Path.Combine(Catalog.MusicFolder, TestDir));
            File.WriteAllBytes(Path.Combine(Catalog.MusicFolder, FileLong), MakeWav(60.0, 330f, 2, 44100, 16));
            Msg(Catalog.RescanMusic());
            var st = Store.Settings;
            st.Playlists.Clear();
            st.Use = new Assignments();
            st.Playlists.Add(new Playlist { Name = "Timer", Tracks = { RefLong }, Repeat = RepeatMode.One });
            st.Use.PerStage["Stage_Big1"] = "Timer";
            st.ShowNowPlaying = false;
            Store.Save();
            InstallTimerHooks();
            yield return 0.2f;
        }

        // Plaza: nothing to do.
        private static IEnumerable<float> TimerPlaza() { yield return 0.5f; }

        private sealed class Sample { public float T; public bool Playing; public float FileT; public float Rms; public bool ChPaused; public float Effect; public float Clock; public string Ev; }
        private static readonly List<Sample> _samples = new();

        private static IEnumerable<float> TimerStage()
        {
            var gm = GameRefs.Main;
            foreach (var w in WaitFor(() => Player.Current == RefLong && CustomAudio.IsPlaying, 15f)) yield return w;
            Check("your file plays in the stage", _waitOk);
            yield return 2f;
            _csv = new StreamWriter(Path.Combine(Dir, "timertest.csv"));
            _csv.WriteLine("t,file_playing,file_t,rms,ch1_paused,ch1_status,item_effect_timer,clock_left,events");
            float t0 = Time.unscaledTime;
            int evAt = 0;
            void Row()
            {
                string ev = string.Join(" | ", _events.Skip(evAt)); evAt = _events.Count;
                var src = Source(1);
                string status = "?"; bool paused = false;
                try { status = src?.status.ToString(); paused = src != null && src.IsPaused(); } catch { }
                float eff = 0, clock = 0;
                try { eff = GameRefs.Main._itemEffectTimer; clock = Katamari.TimeLeft; } catch { }
                var s = new Sample { T = Time.unscaledTime - t0, Playing = CustomAudio.IsPlaying, FileT = CustomAudio.Time, Rms = Rms(), ChPaused = paused, Effect = eff, Clock = clock, Ev = ev };
                _samples.Add(s);
                _csv.WriteLine(string.Join(",", F(s.T), s.Playing, F(s.FileT), F(s.Rms), paused, status, F(eff), F(clock), "\"" + ev + "\""));
            }
            // 1 s before, then the Timer.
            for (float end = Time.unscaledTime + 1f; Time.unscaledTime < end;) { Row(); yield return 0f; }
            Msg("using the Timer freebie");
            gm.EndItemEffect(true);
            gm.SetInventoryItem(eInstageItemType.Timer, false, true);
            gm.ActivateItemEffect();
            float used = Time.unscaledTime - t0;
            for (float end = Time.unscaledTime + 3f; Time.unscaledTime < end;) { Row(); yield return 0f; }
            Msg("pause menu: " + PauseMenu.Open());
            float menuAt = Time.unscaledTime - t0;
            for (float end = Time.unscaledTime + 1.5f; Time.unscaledTime < end;) { Row(); yield return 0f; }
            PauseMenu.Resume();
            float menuClosed = Time.unscaledTime - t0;
            // Until the freebie ends, then 3 s more.
            float limit = Time.unscaledTime + 40f, endSeen = -1f;
            while (Time.unscaledTime < limit)
            {
                Row();
                if (endSeen < 0 && _samples.Count > 2 && _samples[^1].Effect <= 0f && _samples[^2].Effect > 0f) endSeen = Time.unscaledTime;
                if (endSeen > 0 && Time.unscaledTime > endSeen + 3f) break;
                yield return 0f;
            }
            _csv.Flush(); _csv.Dispose(); _csv = null;
            TimerHooks.UnpatchAll();

            // Checks from the frames.
            var before = _samples.Where(s => s.T < used).ToList();
            var during = _samples.Where(s => s.T > used + 0.3f && s.Effect > 0.2f && (s.T < menuAt || s.T > menuClosed + 0.3f)).ToList();
            var after = _samples.Where(s => endSeen > 0 && s.T > endSeen - t0 + 0.5f).ToList();
            var firstPauseCall = _samples.FirstOrDefault(s => s.T >= used && s.Ev.Contains("PauseBGMInStage"));
            var firstStopped = _samples.FirstOrDefault(s => s.T >= used && !s.Playing);
            Msg($"Timer used at {F(used)} s; first PauseBGMInStage frame {(firstPauseCall == null ? "none" : F(firstPauseCall.T))}; your file first stopped at {(firstStopped == null ? "never" : F(firstStopped.T))}; " +
                $"pause menu {F(menuAt)}-{F(menuClosed)} s; freebie ended {(endSeen > 0 ? F(endSeen - t0) : "not seen")}");
            Msg($"frames: before {before.Count}, during the Timer (menu excluded) {during.Count} (playing in {during.Count(s => s.Playing)}), after {after.Count} (playing in {after.Count(s => s.Playing)})");
            Check("your file plays before the Timer", before.Count > 0 && before.All(s => s.Playing));
            Check("your file stops within a frame of the game pausing its music",
                firstPauseCall != null && firstStopped != null && firstStopped.T - firstPauseCall.T <= 0.05f, $"{(firstStopped != null && firstPauseCall != null ? F(firstStopped.T - firstPauseCall.T) : "?")} s");
            Check("your file stays stopped while the Timer runs (also after the pause menu)", during.Count > 10 && during.All(s => !s.Playing && s.Rms < 0.001f),
                $"{during.Count(s => s.Playing)} of {during.Count} frames playing");
            float stopT = during.Count > 0 ? during[0].FileT : -1f;
            bool samePlace = during.Count > 0 && during.All(s => Math.Abs(s.FileT - stopT) < 0.02f);
            Check("its position holds while stopped", samePlace, $"held at {F(stopT)} s");
            // The game's own stage channel (the carrier under your file): with the API's TimerMusic patch it must stay paused
            // after the pause menu closes while the Timer runs, and play again when the Timer ends.
            var gameAfterMenu = _samples.Where(s => s.T > menuClosed + 0.5f && s.Effect > 0.2f).ToList();
            var gameAfterEnd = _samples.Where(s => endSeen > 0 && s.T > endSeen - t0 + 0.6f).ToList();
            Msg($"game channel: after the menu, while the Timer runs: paused in {gameAfterMenu.Count(s => s.ChPaused)} of {gameAfterMenu.Count} frames; after the Timer: paused in {gameAfterEnd.Count(s => s.ChPaused)} of {gameAfterEnd.Count}");
            Check("the game's channel stays paused after the pause menu while the Timer runs", gameAfterMenu.Count > 10 && gameAfterMenu.All(s => s.ChPaused),
                $"{gameAfterMenu.Count(s => !s.ChPaused)} of {gameAfterMenu.Count} frames playing");
            Check("the game's channel plays again when the Timer ends", gameAfterEnd.Count > 10 && gameAfterEnd.All(s => !s.ChPaused),
                $"{gameAfterEnd.Count(s => s.ChPaused)} of {gameAfterEnd.Count} frames paused");
            var resumed = after.FirstOrDefault(s => s.Playing);
            Check("it goes on from the same position when the Timer ends", endSeen > 0 && after.Count > 10 && after.All(s => s.Playing) && resumed != null && resumed.FileT - stopT < 0.6f && resumed.FileT >= stopT,
                $"stopped at {F(stopT)} s, resumed at {(resumed == null ? "?" : F(resumed.FileT))} s");
            yield return 0.5f;
        }
    }
}
