using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Il2CppCriWare;
using OUAKModAPI;
using UnityEngine;

namespace OUAKSongSelector
{
    /// <summary>
    /// songtest.flag with "overlap": counts how many songs CRI is playing (streaming voices and sequences in use) while a
    /// playlist changes songs again and again (Next, a song's end, game song &lt;-&gt; your file), in the Plaza and in a stage.
    /// One song playing must stay one song.
    /// </summary>
    internal static partial class SongTest
    {
        private static bool _overlap;
        private static (int voices, int seq) _base;

        /// <summary>CRI's own counters: streaming voices in use (standard pool) and sequences in use (a game song is one sequence).</summary>
        private static (int voices, int seq) Counters()
        {
            int v = -1, s = -1;
            try { v = CriAtomExVoicePool.GetNumUsedVoices(CriAtomExVoicePool.VoicePoolId.StandardStreaming).numUsedVoices; } catch (Exception e) { Msg("voices: " + e.Message); }
            try { CriAtomExDebug.GetResourcesInfo(out var info); s = (int)info.sequenceUsage.useCount; } catch (Exception e) { Msg("sequences: " + e.Message); }
            return (v, s);
        }

        private static string CounterText(string when)
        {
            var c = Counters();
            string t = $"{when}: streaming voices {c.voices}, sequences {c.seq} | song {Catalog.Describe(Player.Current)} [{(Player.CurrentIsCustom ? "file" : "game " + Player.CurrentGameIndex)}] " +
                       $"cue {LastCue[1]} ch1 {(ChPlaying(1) ? "playing" : ChPaused(1) ? "paused" : "stopped")} vol {F(ChVolume(1))} file playing {CustomAudio.IsPlaying}";
            Msg(t);
            return t;
        }

        /// <summary>After a change: no more songs than before it (a game song = base count; your file = no CRI song left).</summary>
        private static void CheckOne(string what)
        {
            var c = Counters();
            // The Plaza runs other sequences too (measured: 6 with one song, 5 with none): your file there = one fewer.
            int wantSeq = Player.CurrentIsCustom && Player.Where == Place.Plaza ? _base.seq - 1 : _base.seq;
            int wantVoices = Player.CurrentIsCustom && Player.Where == Place.Plaza ? 0 : _base.voices;
            Check(what, c.voices <= wantVoices && c.seq <= wantSeq, $"voices {c.voices} (one song: {wantVoices}), sequences {c.seq} (one song: {wantSeq}), {Catalog.Describe(Player.Current)}");
        }

        private static IEnumerable<float> OverlapSetup()
        {
            TakeBackup();
            Directory.CreateDirectory(Path.Combine(Catalog.MusicFolder, TestDir));
            File.WriteAllBytes(Path.Combine(Catalog.MusicFolder, FileA), MakeWav(6.0, 330f, 2, 44100, 16));
            Msg(Catalog.RescanMusic());
            var st = Store.Settings;
            st.Playlists.Clear();
            st.Use = new Assignments();
            st.Playlists.Add(new Playlist { Name = "Overlap", Tracks = { TrackRef.Game(1), TrackRef.Game(2), TrackRef.Game(4), RefA, TrackRef.Game(5) }, Repeat = RepeatMode.All });
            st.Use.Global = "Overlap";
            st.ShowNowPlaying = false;
            Store.Save();
            CounterText("title, nothing playing");
            yield return 0.5f;
        }

        private static IEnumerable<IEnumerable<float>> Switches(string where)
        {
            // Next, twice.
            for (int i = 0; i < 2; i++)
            {
                Msg($"{where} Next: " + Try(Player.Next));
                yield return Wait(2.5f);
                CounterText($"{where} after Next {i + 1}");
                CheckOne($"{where}: one song after Next {i + 1}");
            }
            // Five song ends in a row (the whole playlist: game -> game, game -> your file, your file -> game, the wrap):
            // each song jumps to 2 s before its end and the playlist moves on by itself.
            for (int i = 0; i < 5; i++)
            {
                string before = Player.Current;
                bool wasFile = Player.CurrentIsCustom;
                if (wasFile) yield return WaitUntil(() => CustomAudio.IsPlaying, 10f);
                Msg($"{where}: jumping to the end of {Catalog.Describe(before)} ({Player.Length:0.0} s)");
                Player.SeekForTest(Math.Max(0, Player.Length - 2));
                if (!wasFile) { yield return Wait(1.0f); CounterText($"{where} after the jump"); CheckOne($"{where}: one song after jumping inside a song"); }
                int started = Player.Started;
                yield return WaitUntil(() => Player.Started > started, 8f);
                yield return WaitUntil(() => !Player.CurrentIsCustom || CustomAudio.IsPlaying, 10f);
                yield return Wait(2.5f);
                string kind = $"{(wasFile ? "your file" : "a game song")} ended into {(Player.CurrentIsCustom ? "your file" : "a game song")}";
                CounterText($"{where} after {kind}");
                CheckOne($"{where}: one song after {kind} ({i + 1})");
            }
            Msg($"{where} Previous: " + Try(Player.Previous));
            yield return Wait(2.5f);
            CounterText($"{where} after Previous");
            CheckOne($"{where}: one song after Previous");
        }

        private static IEnumerable<float> Wait(float s) { yield return s; }
        private static IEnumerable<float> WaitUntil(Func<bool> cond, float timeout) => WaitFor(cond, timeout);

        private static IEnumerable<float> OverlapPlaza()
        {
            foreach (var w in WaitFor(() => Player.Where == Place.Plaza && Player.Current == TrackRef.Game(1) && ChPlaying(1), 15f)) yield return w;
            yield return 3f;
            _base = Counters();
            CounterText("plaza, the playlist's first song (one song)");
            Check("plaza: a song is counted", _base.voices > 0 || _base.seq > 0, $"voices {_base.voices}, sequences {_base.seq}");
            foreach (var step in Switches("plaza")) foreach (var w in step) yield return w;
        }

        private static IEnumerable<float> OverlapStage()
        {
            StageControl.HoldClock(typeof(SongTest), true); _clockHeld = true;
            yield return 3f;
            var b = Counters();
            CounterText("stage playing (one song)");
            // The stage's own song may differ in tracks from the Plaza's: take the stage's count as one song.
            if (!Player.CurrentIsCustom) _base = b;
            foreach (var step in Switches("stage")) foreach (var w in step) yield return w;
        }
    }
}
