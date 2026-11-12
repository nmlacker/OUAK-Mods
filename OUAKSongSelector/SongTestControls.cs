using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OUAKModAPI;
using UnityEngine;

namespace OUAKSongSelector
{
    /// <summary>
    /// songtest.flag with "controls": the playback controls (pause / play with your file and with a game song, also across
    /// the pause menu; loop, loop one, shuffle) and the automatic library update (a file added and removed, playlists.json
    /// changed outside the game, then broken) in the Plaza and a stage.
    /// </summary>
    internal static partial class SongTest
    {
        private static bool _controls;
        private static string FileNew => TestDir + "/SongTest New.wav";

        private static IEnumerable<float> ControlsSetup()
        {
            TakeBackup();
            Directory.CreateDirectory(Path.Combine(Catalog.MusicFolder, TestDir));
            File.WriteAllBytes(Path.Combine(Catalog.MusicFolder, FileLong), MakeWav(60.0, 330f, 2, 44100, 16));
            Msg(Catalog.RescanMusic());
            var st = Store.Settings;
            st.Playlists.Clear();
            st.Use = new Assignments();
            st.Playlists.Add(new Playlist { Name = "Controls", Tracks = { RefLong, TrackRef.Game(1), TrackRef.Game(2) }, Repeat = RepeatMode.All });
            st.Use.Plaza = "Controls";
            st.Use.PerStage["Stage_Big1"] = "Controls";
            st.ShowNowPlaying = false;
            Store.Save();
            yield return 0.2f;
        }

        private static IEnumerable<float> PauseResumeFile(string where)
        {
            foreach (var w in WaitFor(() => Player.CurrentIsCustom && CustomAudio.IsPlaying, 10f)) yield return w;
            yield return 1.5f;
            Msg($"{where} Pause: " + Try(Player.PauseMusic));
            yield return 0.05f;
            float held = CustomAudio.Time;
            yield return 1.5f;
            float later = CustomAudio.Time;
            Check($"{where}: Pause stops your file and holds its place", !CustomAudio.IsPlaying && Math.Abs(later - held) < 0.02f && Rms() < 0.001f, $"{F(held)} -> {F(later)} s");
            Msg($"{where} Play: " + Try(Player.PlayMusic));
            yield return 0.3f;
            // The level only counts when the save's music volume isn't 0 (your files follow it).
            bool audible = Player.GameVolume() < 0.05f || Rms() > 0.01f;
            Check($"{where}: Play goes on from the same place", CustomAudio.IsPlaying && CustomAudio.Time >= held && CustomAudio.Time - held < 0.5f && audible,
                $"held {F(held)}, now {F(CustomAudio.Time)} s, RMS {F(Rms())}, game music volume {F(Player.GameVolume())}");
        }

        private static IEnumerable<float> PauseResumeGame(string where, bool withMenu)
        {
            foreach (var w in WaitFor(() => !Player.CurrentIsCustom && ChPlaying(1), 10f)) yield return w;
            yield return 1.5f;
            Msg($"{where} Pause: " + Try(Player.PauseMusic));
            yield return 0.3f;
            double p0 = Player.Position;
            if (withMenu)
            {
                Msg($"{where} pause menu: " + PauseMenu.Open());
                yield return 1.5f;
                PauseMenu.Resume();
            }
            yield return 1.5f;
            Check($"{where}: Pause holds the game song" + (withMenu ? " (also after the pause menu closed)" : ""), ChPaused(1) && Math.Abs(Player.Position - p0) < 0.05, $"paused {ChPaused(1)}, clock {p0:0.00} -> {Player.Position:0.00} s");
            Msg($"{where} Play: " + Try(Player.PlayMusic));
            yield return 1.0f;
            yield return 1.0f;
            Check($"{where}: Play goes on with the game song", ChPlaying(1) && Player.Position > p0 + 1.5, $"clock {p0:0.00} -> {Player.Position:0.00} s, playing {ChPlaying(1)}");
        }

        private static IEnumerable<float> ControlsPlaza()
        {
            try { Msg($"save volume settings: master {GameRefs.Save.GetMasterVolumeSetting()}, BGM {GameRefs.Save.GetBGMVolumeSetting()} -> your files' factor {F(Player.GameVolume())}"); } catch { }
            foreach (var w in WaitFor(() => Player.Where == Place.Plaza && Player.Active != null, 15f)) yield return w;
            foreach (var w in PauseResumeFile("plaza")) yield return w;
            Msg("plaza Next: " + Try(Player.Next));
            foreach (var w in PauseResumeGame("plaza", false)) yield return w;

            // Loop / loop one / shuffle, saved with the playlist.
            var p = Store.Find("Controls");
            Msg(Try(Player.ToggleLoopOne));
            bool one = p.Repeat == RepeatMode.One && File.ReadAllText(Store.Path).Contains("\"one\"");
            Msg(Try(Player.ToggleLoop));
            bool all = p.Repeat == RepeatMode.All;
            Msg(Try(Player.ToggleLoop));
            bool off = p.Repeat == RepeatMode.Off && File.ReadAllText(Store.Path).Contains("\"off\"");
            Msg(Try(Player.ToggleLoop));
            Msg(Try(Player.ToggleShuffle));
            bool sh = p.Shuffle && File.ReadAllText(Store.Path).Contains("\"shuffle\": true");
            Msg(Try(Player.ToggleShuffle));
            Check("loop one / loop / shuffle switch and are saved", one && all && off && sh && p.Repeat == RepeatMode.All && !p.Shuffle);

            // Nothing is read while nothing changes (Windows notifications, no polling).
            int scans = Library.Scans;
            yield return 6f;
            Check("no folder listing while nothing changes (6 s)", Library.Watching && Library.Scans == scans, $"watching {Library.Watching}, listings {Library.Scans - scans}");

            // A file appears and goes without Rescan.
            int updates = Library.Updates;
            File.WriteAllBytes(Path.Combine(Catalog.MusicFolder, FileNew), MakeWav(3.0, 440f, 2, 44100, 16));
            float t0 = Time.unscaledTime;
            foreach (var w in WaitFor(() => Catalog.FindCustom(FileNew) != null, 8f)) yield return w;
            Check("a new file shows up by itself", _waitOk, $"after {F(Time.unscaledTime - t0)} s; {Library.LastChange}");
            File.Delete(Path.Combine(Catalog.MusicFolder, FileNew));
            foreach (var w in WaitFor(() => Catalog.FindCustom(FileNew) == null, 8f)) yield return w;
            Check("a removed file goes by itself", _waitOk, Library.LastChange);

            // playlists.json edited outside the game.
            string good = File.ReadAllText(Store.Path);
            string edited = good.Replace("\"playlists\": [", "\"playlists\": [ { \"name\": \"From disk\", \"tracks\": [ \"game:3\" ], \"shuffle\": false, \"repeat\": \"all\" },");
            File.WriteAllText(Store.Path, edited);
            t0 = Time.unscaledTime;
            foreach (var w in WaitFor(() => Store.Find("From disk") != null, 8f)) yield return w;
            Check("a playlist added to playlists.json outside the game shows up", _waitOk && Store.Find("Controls") != null, $"after {F(Time.unscaledTime - t0)} s");
            File.WriteAllText(Store.Path, "{ \"version\": 1, \"playlists\": [ { broken");
            foreach (var w in WaitFor(() => Store.LastError.Contains("can't be read"), 8f)) yield return w;
            Check("a broken playlists.json leaves the playlists in use alone", _waitOk && Store.Find("Controls") != null && Store.Find("From disk") != null && Player.Active != null, Store.LastError);
            Store.Save();   // writes the good settings back
            yield return 3f;
            Check("saving again clears the error", Store.LastError == "" && File.ReadAllText(Store.Path).Contains("From disk"));
        }

        private static IEnumerable<float> ControlsStage()
        {
            foreach (var w in WaitFor(() => Player.Where == Place.Stage && Player.Active != null && (Player.CurrentIsCustom ? CustomAudio.IsPlaying : ChPlaying(1)), 15f)) yield return w;
            if (Player.CurrentIsCustom) { foreach (var w in PauseResumeFile("stage")) yield return w; Msg("stage Next: " + Try(Player.Next)); }
            // A game song paused by you stays paused through the pause menu.
            foreach (var w in PauseResumeGame("stage", true)) yield return w;
        }
    }
}
