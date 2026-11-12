using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Il2CppApp.KatamariSin;
using Il2CppCriWare;
using OUAKModAPI;
using UnityEngine;

namespace OUAKSongSelector
{
    /// <summary>
    /// The functional test (songtest.flag without "survey"): two test WAVs and two playlists, saved and read back;
    /// the Plaza (your file first, song end -> game song, Next / Previous, the jukebox taking over); As Large As Possible 1
    /// (the playlist's game song instead of the game's pick, your file over a silent carrier, the pause menu, song ends,
    /// repeat one, a settings change, a retry); pictures of the Songs tab. The user's playlists.json is put back after.
    /// </summary>
    internal static partial class SongTest
    {
        private const string TestDir = "_songtest";
        private static string _backup;
        private static bool _hadBackup, _clockHeld;
        private static GameSong _inst, _locked;
        private static string FileA => TestDir + "/SongTest A.wav";
        private static string FileB => _formats ? TestDir + "/SongTest B.mp3" : TestDir + "/SongTest B.wav";
        private static string FileE => TestDir + "/SongTest E.flac";
        private static string RefE => TrackRef.File(FileE);
        /// <summary>UserData/OUAKSongSelector/testfiles/ holds an MP3, a FLAC and an OGG made outside the game (ffmpeg).</summary>
        private static bool _formats;
        private static string TestFiles => Path.Combine(Dir, "testfiles");
        private static string Shots => Path.Combine(Dir, "TestShots");

        private static string RefA => TrackRef.File(FileA);
        private static string RefB => TrackRef.File(FileB);

        // ------------------------------------------------------------------ setup at the title screen

        private static IEnumerable<float> PlaySetup()
        {
            Directory.CreateDirectory(Shots);
            TakeBackup();
            Msg($"your playlists.json {(_hadBackup ? "kept aside (also as playlists.before-test.json)" : "doesn't exist")}; it's put back after the test");

            string dir = Path.Combine(Catalog.MusicFolder, TestDir);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(Catalog.MusicFolder, FileA), MakeWav(6.0, 330f, 2, 44100, 16));
            _formats = File.Exists(Path.Combine(TestFiles, "B.mp3")) && File.Exists(Path.Combine(TestFiles, "E.flac"));
            Msg(_formats ? "other formats: MP3, FLAC and OGG test files found" : "no testfiles folder: WAV only");
            if (_formats)
            {
                File.Copy(Path.Combine(TestFiles, "B.mp3"), Path.Combine(Catalog.MusicFolder, FileB), true);
                File.Copy(Path.Combine(TestFiles, "E.flac"), Path.Combine(Catalog.MusicFolder, FileE), true);
                if (File.Exists(Path.Combine(TestFiles, "F.ogg"))) File.Copy(Path.Combine(TestFiles, "F.ogg"), Path.Combine(dir, "SongTest F.ogg"), true);
            }
            else File.WriteAllBytes(Path.Combine(Catalog.MusicFolder, FileB), MakeWav(5.0, 523f, 1, 48000, 24));
            Msg(Catalog.RescanMusic());
            Check("test files found by the rescan", Catalog.FindCustom(FileA) != null && Catalog.FindCustom(FileB) != null && (!_formats || Catalog.FindCustom(FileE) != null),
                $"types here: {string.Join(" ", Catalog.PlayableTypes)}");
            if (_formats)
            {
                bool oggListed = Catalog.FindCustom(TestDir + "/SongTest F.ogg") != null;
                bool oggRefused = Catalog.UnsupportedFiles.Contains(TestDir + "/SongTest F.ogg");
                Msg($"OGG: listed as playable {oggListed}, listed as not decodable here {oggRefused} (Windows has a .ogg handler: {MfDecoder.Extensions.Contains(".ogg")})");
                Check("an OGG goes where this Windows puts it", MfDecoder.Extensions.Contains(".ogg") ? oggListed : oggRefused);
            }

            var songs = Catalog.GameSongs;
            Msg($"game songs: {songs.Count}; e.g. {string.Join(" / ", songs.Take(3).Select(s => $"{s.Index} {s.Name} ({s.Artist})"))}; " +
                $"instrumental {songs.FirstOrDefault(s => s.Instrumental)?.Index} {songs.FirstOrDefault(s => s.Instrumental)?.Name}");
            Check("game song list with names", songs.Count >= 36 && songs.Any(s => s.Index == 1 && s.Name.Length > 0) && songs.Any(s => s.Index == 1003));

            var st = Store.Settings;
            st.Playlists.Clear();
            st.Use = new Assignments();
            var plazaTracks = new List<string> { RefA, TrackRef.Game(1), RefB };
            if (_formats) plazaTracks.Add(RefE);
            st.Playlists.Add(new Playlist { Name = "Test Plaza", Tracks = plazaTracks, Repeat = RepeatMode.All });
            Check("before a save is loaded, base songs can't play", !Catalog.Available(Catalog.FindGame(1), out string why0), why0);
            var stageTracks = new List<string> { TrackRef.Game(7), RefA, TrackRef.Game(1000) };
            st.Playlists.Add(new Playlist { Name = "Test Stage", Tracks = stageTracks, Repeat = RepeatMode.All });
            st.Use.Plaza = "Test Plaza";
            st.Use.PerStage["Stage_Big1"] = "Test Stage";
            st.CustomVolume = 0.8f;
            st.ShowNowPlaying = true;
            Store.Save();
            string json = File.ReadAllText(Store.Path);
            Store.Load();
            var r = Store.Settings;
            bool same = r.Playlists.Count == 2 && r.Playlists[0].Name == "Test Plaza" && r.Playlists[0].Tracks.SequenceEqual(plazaTracks) &&
                        r.Playlists[1].Tracks.SequenceEqual(stageTracks) && r.Playlists[1].Repeat == RepeatMode.All &&
                        r.Use.Plaza == "Test Plaza" && r.Use.PerStage.TryGetValue("Stage_Big1", out var v) && v == "Test Stage";
            Check("playlists saved to playlists.json and read back the same", same, $"{json.Length} bytes");
            Msg("playlists.json: " + json.Replace("\r", "").Replace("\n", " ").Replace("  ", ""));
            yield return 0.5f;
        }

        private static bool _backupTaken;
        private static string SafetyCopy => Path.Combine(Dir, "playlists.before-test.json");
        private static string NoFileMark => Path.Combine(Dir, "playlists.before-test.none");

        /// <summary>
        /// Keeps the user's playlists.json before a test changes the settings: in memory and on disk
        /// (playlists.before-test.json, or a .none mark when there was no file), so even a test that never finishes
        /// can't lose it. A copy left by an earlier test that didn't finish is put back first.
        /// </summary>
        private static void TakeBackup()
        {
            if (File.Exists(SafetyCopy)) { File.Copy(SafetyCopy, Store.Path, true); File.Delete(SafetyCopy); Msg("put back the playlists.json a previous unfinished test had kept aside"); }
            else if (File.Exists(NoFileMark)) { if (File.Exists(Store.Path)) File.Delete(Store.Path); File.Delete(NoFileMark); Msg("a previous unfinished test had no playlists.json to keep: removed its test settings"); }
            _hadBackup = File.Exists(Store.Path);
            _backup = _hadBackup ? File.ReadAllText(Store.Path) : null;
            if (_hadBackup) File.WriteAllText(SafetyCopy, _backup);
            else File.WriteAllText(NoFileMark, "");
            _backupTaken = true;
            Store.Load();
        }

        private static void Cleanup()
        {
            if (_clockHeld) { StageControl.HoldClock(typeof(SongTest), false); _clockHeld = false; }
            try { ModMenu.Close(); } catch { }
            // Only a test that kept the user's file aside puts it back (the gallery test never touches it).
            if (!_backupTaken) return;
            if (_hadBackup) File.WriteAllText(Store.Path, _backup);
            else if (File.Exists(Store.Path)) File.Delete(Store.Path);
            try { File.Delete(SafetyCopy); File.Delete(NoFileMark); } catch { }
            _backupTaken = false;
            Store.Load();
            try { Directory.Delete(Path.Combine(Catalog.MusicFolder, TestDir), true); } catch { }
            Catalog.RescanMusic();
            Msg("your playlists.json is back; test files removed");
        }

        private static byte[] MakeWav(double seconds, float hz, int channels, int rate, int bits)
        {
            int frames = (int)(seconds * rate), bps = bits / 8;
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + frames * channels * bps); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)channels); w.Write(rate); w.Write(rate * channels * bps); w.Write((short)(channels * bps)); w.Write((short)bits);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(frames * channels * bps);
            for (int i = 0; i < frames; i++)
            {
                double v = 0.2 * Math.Sin(2 * Math.PI * hz * i / rate);
                for (int c = 0; c < channels; c++)
                {
                    if (bits == 16) w.Write((short)(v * 32767));
                    else { int s = (int)(v * 8388607); w.Write((byte)s); w.Write((byte)(s >> 8)); w.Write((byte)(s >> 16)); }
                }
            }
            w.Flush();
            return ms.ToArray();
        }

        // ------------------------------------------------------------------ helpers

        private static string Cue(int ch) { var s = Source(ch); try { return s == null ? "-" : $"{s.cueSheet}/{LastCue[ch]}"; } catch { return "?"; } }
        private static long PTime(int ch) { try { return Source(ch)?.player?.GetTime() ?? -1; } catch { return -1; } }
        private static bool ChPlaying(int ch) { var s = Source(ch); try { return s != null && s.status == CriAtomSourceBase.Status.Playing && !s.IsPaused(); } catch { return false; } }
        private static bool ChPaused(int ch) { var s = Source(ch); try { return s != null && s.IsPaused(); } catch { return false; } }
        private static bool ChStopped(int ch) { var s = Source(ch); try { return s == null || s.status == CriAtomSourceBase.Status.Stop || s.status == CriAtomSourceBase.Status.PlayEnd; } catch { return true; } }
        private static float ChVolume(int ch) { try { return Source(ch)?.volume ?? -1f; } catch { return -1f; } }

        /// <summary>The cue a game song index plays (BgmTable entry index+35; +1000 = instrumental of the same cue).</summary>
        private static string CueOf(int idx)
        {
            var d = Ctr?.bgmTable?.GetData((idx >= 1000 ? idx - 1000 : idx) + 35);
            string f = d?.filePath ?? "";
            return f.Substring(f.LastIndexOf('/') + 1);
        }

        private static void State(string when) =>
            Msg($"{when}: place {Player.Where} {Player.StageKey} playlist {Player.Active?.Name ?? "-"} ({Player.ActiveFrom}) song {Catalog.Describe(Player.Current)} " +
                $"[{(Player.CurrentIsCustom ? "file" : "game " + Player.CurrentGameIndex)}] pos {Player.Position:0.00}/{Player.Length:0.0} s susp {Player.Suspended} stoppedByGame {Player.StoppedByGame} " +
                $"| ch0 {Cue(0)} {(ChPlaying(0) ? "playing" : ChPaused(0) ? "paused" : "stopped")} | ch1 {Cue(1)} {(ChPlaying(1) ? "playing" : ChPaused(1) ? "paused" : "stopped")} t {PTime(1)} vol {F(ChVolume(1))} " +
                $"| file {CustomAudio.Loaded ?? "-"} playing {CustomAudio.IsPlaying} t {F(CustomAudio.Time)} vol {F(CustomAudio.Source != null ? CustomAudio.Source.volume : -1f)} | RMS {F(Rms())}" +
                (Player.Status != "" ? " | " + Player.Status : ""));

        /// <summary>Waits until <paramref name="cond"/> holds (checked every 0.1 s) or the time runs out; sets _waitOk.</summary>
        private static IEnumerable<float> WaitFor(Func<bool> cond, float timeout)
        {
            float end = Time.unscaledTime + timeout;
            _waitOk = false;
            while (Time.unscaledTime < end)
            {
                bool ok = false;
                try { ok = cond(); } catch { }
                if (ok) { _waitOk = true; yield break; }
                yield return 0.1f;
            }
        }
        private static bool _waitOk;

        private static IEnumerable<float> Picture(string name)
        {
            ModMenu.CurrentTab = "songs";
            ModMenu.Open();
            yield return 1.2f;
            FrameCapture.Request(name, Shots, p => Msg("picture: " + p));
            yield return 1.5f;
            ModMenu.Close();
            yield return 0.5f;
        }

        /// <summary>The stage's song banner text (UIBgmNameDisplay.BGMText); <paramref name="refresh"/> asks the game to work it out again.</summary>
        private static string Banner(bool refresh)
        {
            try
            {
                var all = Resources.FindObjectsOfTypeAll<UIBgmNameDisplay>();
                Msg($"song banners: {all.Length}");
                foreach (var d in all)
                {
                    if (d == null || d.BGMText == null) continue;
                    if (refresh) d.CheckBGMNameText();
                    return d.BGMText.text ?? "";
                }
            }
            catch (Exception e) { return "error: " + e.Message; }
            return "(none)";
        }

        private static string Try(Func<string> f) { try { return f(); } catch (Exception e) { return "refused: " + e.Message; } }

        // ------------------------------------------------------------------ the Plaza

        private static IEnumerable<float> PlayPlaza()
        {
            // With the save loaded: an unlocked instrumental, and a base song this save hasn't unlocked (skipped by the playlist).
            var songs = Catalog.GameSongs;
            _inst = songs.FirstOrDefault(s => s.Instrumental && Catalog.Available(s, out _));
            _locked = songs.FirstOrDefault(s => s.Album == 0 && !Catalog.Available(s, out _));
            Msg($"instrumental for the test: {_inst?.Index} {_inst?.Name}; a song this save hasn't unlocked: {(_locked == null ? "none" : $"{_locked.Index} {_locked.Name}")}");
            Check("an unlocked instrumental exists", _inst != null);
            if (_locked != null) Check("a locked song is refused", !Catalog.Playable(_locked.Ref, out string why) && why.Length > 0, why);
            var ts = Store.Find("Test Stage");
            ts.Tracks = new List<string> { TrackRef.Game(7), RefA, TrackRef.Game(_inst?.Index ?? 1000) };
            if (_locked != null) ts.Tracks.Add(_locked.Ref);
            Store.Save();
            Msg("Test Stage: " + string.Join(", ", ts.Tracks.Select(Catalog.Describe)));
            foreach (var w in WaitFor(() => Player.Where == Place.Plaza && Player.Active != null, 15f)) yield return w;
            State("plaza start");
            Check("Plaza: \"Test Plaza\" plays (The Plaza)", Player.Active?.Name == "Test Plaza" && Player.ActiveFrom == "The Plaza");
            Check("Plaza: first song is your file A", Player.Current == RefA && Player.CurrentIsCustom);
            foreach (var w in WaitFor(() => CustomAudio.IsPlaying, 10f)) yield return w;
            yield return 1.5f;
            State("plaza file A");
            float rms = Rms();
            Check("Plaza: file A is heard (Unity output RMS > 0.01)", CustomAudio.IsPlaying && rms > 0.01f, $"RMS {F(rms)}");
            Check("Plaza: the theme (channel BGM) stopped", !ChPlaying(0) || ChVolume(0) < 0.05f, Cue(0));

            // File A (6 s) ends by itself -> the next song: game song 1.
            foreach (var w in WaitFor(() => Player.Current == TrackRef.Game(1), 12f)) yield return w;
            Check("Plaza: after file A ended, the next song (game 1) started", _waitOk);
            yield return 2.5f;
            State("plaza game 1");
            long t1 = PTime(1);
            yield return 1f;
            long t2 = PTime(1);
            Check("Plaza: game 1 plays on the in-stage channel (cue " + CueOf(1) + ")", LastCue[1] == CueOf(1) && ChPlaying(1) && t2 > t1, $"cue {LastCue[1]}, player time {t1} -> {t2} ms");
            Check("Plaza: your file stopped for the game song", !CustomAudio.IsPlaying);

            Msg("Next: " + Try(Player.Next));
            yield return 2f;
            State("plaza after Next");
            Check("Plaza: Next -> file B" + (_formats ? " (MP3, decoded by Windows)" : "") + ", heard, in-stage channel stopped", Player.Current == RefB && CustomAudio.IsPlaying && Rms() > 0.01f && !ChPlaying(1));
            Msg("Previous: " + Try(Player.Previous));
            yield return 2f;
            State("plaza after Previous");
            Check("Plaza: Previous -> game 1 again", Player.Current == TrackRef.Game(1) && ChPlaying(1) && LastCue[1] == CueOf(1) && !CustomAudio.IsPlaying);

            // The jukebox (or anything else) playing a song here pauses the playlist.
            Msg("the game plays song 5 itself (like its jukebox)");
            GameRefs.Sound.PlayBGMInStage(5, false);
            yield return 1f;
            State("plaza after the game's own song");
            Check("Plaza: the game's own song pauses the playlist", Player.Suspended && LastCue[1] == CueOf(5), $"cue {LastCue[1]}");
            Msg("Next: " + Try(Player.Next));
            yield return 2f;
            State("plaza after Next (back)");
            Check("Plaza: Next brings the playlist back (file B)", !Player.Suspended && Player.Current == RefB && CustomAudio.IsPlaying);

            if (_formats)
            {
                Msg("Next: " + Try(Player.Next));
                foreach (var w in WaitFor(() => Player.Current == RefE && CustomAudio.IsPlaying, 10f)) yield return w;
                yield return 1f;
                State("plaza FLAC");
                float r = Rms();
                Check("Plaza: Next -> file E (FLAC, decoded by Windows), heard", Player.Current == RefE && CustomAudio.IsPlaying && r > 0.01f, $"RMS {F(r)}, length {Player.Length:0.00} s");
            }
            foreach (var w in Picture("plaza_songs_tab")) yield return w;
        }

        // ------------------------------------------------------------------ a story stage

        private static IEnumerable<float> PlayStage()
        {
            StageControl.HoldClock(typeof(SongTest), true); _clockHeld = true;
            var gm = GameRefs.Main;
            State("stage playing");
            Check("Stage: \"Test Stage\" plays (This stage)", Player.Active?.Name == "Test Stage" && Player.ActiveFrom == "This stage");
            Check("Stage: the playlist's first song (game 7) loaded instead of the game's pick",
                Player.Current == TrackRef.Game(7) && Player.CurrentGameIndex == 7 && Player.GameStageIndex != 7, $"game's pick {Player.GameStageIndex}");
            yield return 2f;
            // The stage's CRI player time reads 0 (the action-track cues share that player): the playlist's own clock instead.
            double a = Player.Position; yield return 1f; double b = Player.Position;
            State("stage game 7");
            Check("Stage: game 7 plays (cue " + CueOf(7) + ")", LastCue[1] == CueOf(7) && ChPlaying(1) && b > a + 0.5, $"cue {LastCue[1]}, playlist clock {a:0.0} -> {b:0.0} s");
            string banner = Banner(false);
            Check("Stage: the game's song banner names the playlist's song", banner == Catalog.Describe(TrackRef.Game(7)), $"banner \"{banner}\", _nowInStageBGMIdx {GameRefs.Sound._nowInStageBGMIdx}");
            Check("Stage: the jukebox shuffle is held off while the playlist plays", GameRefs.Sound._unreleased_noShuffle, $"game's own value {Player.GameNoShuffle}");

            Msg("Next: " + Try(Player.Next));
            foreach (var w in WaitFor(() => CustomAudio.IsPlaying, 10f)) yield return w;
            yield return 1f;
            State("stage file A");
            float rms = Rms();
            string banner2 = Banner(true);
            Check("Stage: the song banner names your file while it plays", banner2 == Catalog.Describe(RefA), $"banner \"{banner2}\"");
            Check("Stage: Next -> file A heard over a silent carrier", Player.Current == RefA && CustomAudio.IsPlaying && rms > 0.01f && ChPlaying(1) && ChVolume(1) == 0f, $"RMS {F(rms)}, carrier vol {F(ChVolume(1))}");

            Msg("pause menu: " + PauseMenu.Open());
            yield return 1.5f;
            State("stage paused");
            Check("Stage: pause menu pauses the carrier and your file", ChPaused(1) && !CustomAudio.IsPlaying && Rms() < 0.001f);
            foreach (var w in Picture("stage_paused_songs_tab")) yield return w;
            PauseMenu.Resume();
            yield return 1.5f;
            State("stage resumed");
            Check("Stage: closing the pause menu resumes both", ChPlaying(1) && CustomAudio.IsPlaying && Rms() > 0.01f);

            // File A (6 s) ends -> the instrumental (VocalOff), audible again.
            int inst = _inst?.Index ?? 1000;
            foreach (var w in WaitFor(() => Player.Current == TrackRef.Game(inst), 12f)) yield return w;
            Check($"Stage: after file A ended, game {inst} (instrumental) started", _waitOk);
            yield return 2f;
            State($"stage game {inst}");
            Check($"Stage: game {inst} plays its cue {CueOf(inst)} at the game's volume", LastCue[1] == CueOf(inst) && ChPlaying(1) && ChVolume(1) > 0.1f && !CustomAudio.IsPlaying, $"cue {LastCue[1]}, vol {F(ChVolume(1))}");

            // A game song's own end: jump to 3 s before its play-through length (read from the cue sheet).
            // A locked song after it is skipped, so the playlist wraps to game 7.
            double len = Player.Length;
            Msg($"game {inst} play-through {len:0.000} s; jumping to {len - 3:0.0} s");
            Player.SeekForTest(len - 3);
            foreach (var w in WaitFor(() => Player.Current == TrackRef.Game(7), 8f)) yield return w;
            State("stage after game 1003 ended");
            Check("Stage: game song end -> next (repeat all wraps to game 7" + (_locked != null ? ", the locked song skipped" : "") + ")", _waitOk && Player.Length > 0 && LastCue[1] == CueOf(7), $"cue {LastCue[1]}");

            // Repeat one.
            var p = Store.Find("Test Stage");
            p.Repeat = RepeatMode.One; Store.Save();
            int started = Player.Started;
            Player.SeekForTest(Player.Length - 2);
            foreach (var w in WaitFor(() => Player.Started > started, 6f)) yield return w;
            State("stage repeat one");
            Check("Stage: repeat one plays the same song again", _waitOk && Player.Current == TrackRef.Game(7) && Player.Position < 4);
            p.Repeat = RepeatMode.All; Store.Save();

            // Taking the stage's playlist away brings the game's own pick back.
            Store.Settings.Use.PerStage.Remove("Stage_Big1"); Store.Save();
            yield return 2.5f;
            State("stage playlist removed");
            Check("Stage: no playlist -> the game's own pick plays again", Player.Active == null && ChPlaying(1) && LastCue[1] == CueOf(Player.GameStageIndex), $"cue {LastCue[1]}, game's pick {CueOf(Player.GameStageIndex)}");
            Check("Stage: the game's own jukebox-shuffle setting is given back", Player.GameNoShuffle.HasValue && GameRefs.Sound._unreleased_noShuffle == Player.GameNoShuffle.Value, $"now {GameRefs.Sound._unreleased_noShuffle}, game's {Player.GameNoShuffle}");
            Store.Settings.Use.PerStage["Stage_Big1"] = "Test Stage"; Store.Save();
            yield return 2.5f;
            State("stage playlist back");
            Check("Stage: choosing it again starts the playlist at once", Player.Active?.Name == "Test Stage" && Player.Current != null);

            // A retry asks for the stage music again: the playlist goes on with its next song.
            string before = Player.Current;
            int startedBefore = Player.Started;
            Msg("retry: " + Try(StageControl.RestartStage));
            foreach (var w in WaitFor(() => Player.Started > startedBefore && Player.Active != null, 30f)) yield return w;
            yield return 1f;
            State("stage after retry");
            Check("Stage: retry -> the playlist's next song", _waitOk && Player.Current != null, $"{Catalog.Describe(before)} -> {Catalog.Describe(Player.Current)}");
            // After a retry the game unpauses its music when its opening ends (skipped here like at the start).
            float until = Time.unscaledTime + 40f;
            bool skipped = false;
            while (Time.unscaledTime < until && !(ChPlaying(1) && (!Player.CurrentIsCustom || CustomAudio.IsPlaying)))
            {
                var g2 = GameRefs.Main;
                if (g2 != null && g2._isInDemoCamera && !skipped) { Msg("skipping the fly-over"); g2.EndDemoCameraWithFade(); skipped = true; }
                var ui = Il2Cpp.SingletonMonoBehaviour<GlobalUIController>.Instance;
                if (g2 != null && !g2._isInDemoCamera && ui != null && ui.CanSkipMessage) { Msg("skipping the King's talk"); ui.SkipNowMessage(); }
                yield return 0.5f;
            }
            yield return 1.5f;
            State("stage playing after retry");
            Check("Stage: after the retry the playlist's song plays once the game's music starts", ChPlaying(1) && (!Player.CurrentIsCustom || (CustomAudio.IsPlaying && Rms() > 0.01f)));
            foreach (var w in Picture("stage_songs_tab")) yield return w;
        }
    }
}
