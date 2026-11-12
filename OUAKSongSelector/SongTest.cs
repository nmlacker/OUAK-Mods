using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Il2Cpp;
using Il2CppApp.KatamariSin;
using Il2CppCriWare;
using MelonLoader.Utils;
using OUAKModAPI;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OUAKSongSelector
{
    /// <summary>
    /// Test only: <c>UserData/OUAKSongSelector/songtest.flag</c> runs an in-game check once, then quits the game.
    /// Words: <c>survey</c> (log how the game plays music: tables, calls, channels; Unity audio output), <c>slot N</c>.
    /// Title -> the last-used slot -> ship -> Plaza -> As Large As Possible 1 -> quit. Save writes are blocked while it runs.
    /// Lines start with [SongTest]; the long dump goes to UserData/OUAKSongSelector/survey.txt.
    /// </summary>
    internal static partial class SongTest
    {
        private static readonly ModLog Log = ModLog.For("SongTest");
        internal static string Dir => SongSelectorMod.Dir;

        private enum Phase { Off, Title, Ship, Plaza, Stage, Done }
        private static Phase _phase = Phase.Off;
        private static float _at, _deadline, _phaseStart, _seen;
        private static int _slot = -1;
        private static bool _blockSaves, _skippedFly;
        private static readonly HookSet Hooks = new("ouak.songselector.test");
        private static readonly HookSet LogHooks = new("ouak.songselector.testlog");
        private static readonly List<string> _fails = new();
        private static IEnumerator<float> _script;
        private static StreamWriter _dump;
        private static bool _play;

        internal static bool Running => _phase != Phase.Off && _phase != Phase.Done;

        internal static void CheckFlag()
        {
            string flag = Path.Combine(Dir, "songtest.flag");
            if (!File.Exists(flag)) return;
            string text = File.ReadAllText(flag);
            try { File.Delete(flag); } catch { }
            var w = text.Split(new[] { ' ', '\n', '\r', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.ToLowerInvariant()).ToList();
            _play = !w.Contains("survey");
            _overlap = w.Contains("overlap");
            _timer = w.Contains("timer");
            _controls = w.Contains("controls");
            _gallery = w.Contains("gallery");
            // "loud": your files play at a fixed level (0.8 x the mod's 0.8) whatever the game's music volume is, so the
            // mixer level can be measured; the game's settings aren't touched.
            if (w.Contains("loud")) Player.TestVolume = 0.8f;
            int i = w.IndexOf("slot");
            if (i >= 0 && i + 1 < w.Count && int.TryParse(w[i + 1], out int s)) _slot = s - 1;
            _blockSaves = true;
            bool a = Hooks.Prefix(typeof(SaveDataController), nameof(SaveDataController.SetSaveAction), typeof(SongTest), nameof(BlockSave));
            bool b = Hooks.Prefix(typeof(SaveDataController), nameof(SaveDataController.SetNewDataAction), typeof(SongTest), nameof(BlockSave), new Type[0]);
            bool c = Hooks.Prefix(typeof(SaveDataController), nameof(SaveDataController.SetNewDataAction), typeof(SongTest), nameof(BlockSave), new[] { typeof(int) });
            Msg($"armed: {(_gallery ? "gallery" : _controls ? "controls" : _timer ? "timer" : _overlap ? "overlap" : _play ? "play" : "survey")}, slot {(_slot < 0 ? "last used" : (_slot + 1).ToString())}; save writes blocked {a && b && c}");
            if (!(a && b && c)) { Msg("FAIL: save hooks missing; not running"); Hooks.UnpatchAll(); return; }
            Directory.CreateDirectory(Dir);
            if (!_play) _dump = new StreamWriter(Path.Combine(Dir, "survey.txt"), false, Encoding.UTF8);
            InstallLogHooks();
            Enter(Phase.Title, 150f);
        }

        public static bool BlockSave()
        {
            try { if (_blockSaves) { Msg("blocked a save write"); return false; } } catch { }
            return true;
        }

        private static void Msg(string s) => Log.Info("[SongTest] " + s);
        private static void D(string s) { try { _dump?.WriteLine(s); } catch { } }
        private static string F(float v) => v.ToString("0.000", CultureInfo.InvariantCulture);

        private static void Check(string what, bool ok, string detail = "")
        {
            Msg($"{(ok ? "OK  " : "FAIL")} {what}{(detail != "" ? ": " + detail : "")}");
            if (!ok) _fails.Add(what);
        }

        private static void Enter(Phase p, float timeout)
        {
            _phase = p; _phaseStart = Time.unscaledTime; _deadline = Time.unscaledTime + timeout; _at = Time.unscaledTime; _seen = 0;
            Msg($"phase {p} (scene {SceneLoader.ActiveScene})");
        }

        internal static void Tick()
        {
            if (!Running) return;
            float now = Time.unscaledTime;
            if (now < _at) return;
            try { Step(now); }
            catch (Exception e) { Finish($"FAIL in {_phase}: {e.GetType().Name}: {e.Message}\n{e.StackTrace}"); }
        }

        // A running script yields the seconds to wait before its next step.
        private static bool RunScript(float now)
        {
            if (_script == null) return true;
            if (_script.MoveNext()) { _at = now + Math.Max(0f, _script.Current); return false; }
            _script = null;
            return true;
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
                    if (_seen == 0) { _seen = now; _script = (_gallery ? GallerySetup() : _controls ? ControlsSetup() : _timer ? TimerSetup() : _overlap ? OverlapSetup() : _play ? PlaySetup() : TitleSurvey()).GetEnumerator(); }
                    if (!RunScript(now)) return;
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
                    if (_seen == 0) { _seen = now; Msg("on the ship"); DumpChannels("ship"); }
                    if (now - _seen < 6f) { _at = now + 0.5f; return; }
                    DumpChannels("ship +6s");
                    DumpSaveMusic();
                    {
                        var plaza = PickPlaza();
                        Msg($"load plaza {plaza.Name} ({plaza.PlazaKey}): " + SceneLoader.LoadPlaza(plaza));
                    }
                    Enter(Phase.Plaza, 120f);
                    return;
                case Phase.Plaza:
                    // The plaza's active scene is its era map (Map_Edo...), loaded a moment after the hub scene.
                    if (!SceneLoader.ActiveScene.StartsWith("Map_") || UnityEngine.Object.FindObjectOfType<SelectHirobaSoundController>() == null) { _at = now + 0.25f; return; }
                    if (_seen == 0) { _seen = now; Msg("in the Plaza"); _script = (_gallery ? GalleryPlaza() : _controls ? ControlsPlaza() : _timer ? TimerPlaza() : _overlap ? OverlapPlaza() : _play ? PlayPlaza() : PlazaSurvey()).GetEnumerator(); }
                    if (!RunScript(now)) return;
                    if (_gallery) { Finish(_fails.Count == 0 ? "PASS" : "FAIL: " + string.Join("; ", _fails)); return; }
                    {
                        var entry = StageCatalog.Entries.FirstOrDefault(s => s.Kind == MapKind.Stage && s.Internal == "Stage_Big1")
                                    ?? throw new InvalidOperationException("no Stage_Big1");
                        Msg("load stage: " + SceneLoader.LoadStage(entry));
                    }
                    Enter(Phase.Stage, 240f);
                    return;
                case Phase.Stage:
                    if (gm == null || !gm.PlayerInitiated) { _at = now + 0.1f; return; }
                    if (_seen == 0) { _seen = now; Msg("stage loaded"); DumpChannels("stage loaded"); }
                    if (_script == null && (!gm.StartMessageDone || !gm.IsGameRunning || !Katamari.Playing))
                    {
                        if (gm.MainUI != null && gm.MainUI.IsTipsActive()) { gm.MainUI.DeactivateTipsUI(); _at = now + 1f; return; }
                        if (!gm.StartMessageDone)
                        {
                            if (gm._isInDemoCamera && !_skippedFly && now - _seen > 6f) { Msg("skipping the fly-over"); gm.EndDemoCameraWithFade(); _skippedFly = true; DumpChannels("fly-over skipped"); }
                            var ui = SingletonMonoBehaviour<GlobalUIController>.Instance;
                            if (!gm._isInDemoCamera && ui != null && ui.CanSkipMessage && now - _seen > 8f) { Msg("skipping the King's talk"); ui.SkipNowMessage(); _at = now + 1f; return; }
                        }
                        _at = now + 0.1f; return;
                    }
                    if (_script == null) { Msg($"playing {gm.TargetStage}"); _script = (_gallery ? GalleryStage() : _controls ? ControlsStage() : _timer ? TimerStage() : _overlap ? OverlapStage() : _play ? PlayStage() : StageSurvey()).GetEnumerator(); }
                    if (!RunScript(now)) return;
                    Finish(_fails.Count == 0 ? "PASS" : "FAIL: " + string.Join("; ", _fails));
                    return;
            }
        }

        private static void Finish(string result)
        {
            _phase = Phase.Done;
            _blockSaves = false;
            _script = null;
            try { StopProbe(); } catch { }
            try { if (_play) Cleanup(); } catch (Exception e) { Msg("cleanup: " + e.Message); }
            try { _dump?.Flush(); _dump?.Dispose(); } catch { }
            _dump = null;
            Msg(result);
            Msg("DONE, quitting");
            Application.Quit();
        }

        // ------------------------------------------------------------------ scripts

        private static IEnumerable<float> TitleSurvey()
        {
            DumpTables();
            DumpChannels("title");
            foreach (var w in UnityAudioCheck("title")) yield return w;
        }

        private static IEnumerable<float> PlazaSurvey()
        {
            for (int i = 0; i < 6; i++) { DumpChannels($"plaza +{i * 2}s"); yield return 2f; }
            var ctr = Ctr;
            // The game's own jukebox plays stage songs in the Plaza through PlayBGMInStage (FavoriteBgmPlayer.PlayBgm).
            Msg("calling SoundManager.PlayBGMInStage(2, forceNoLoop true) in the Plaza");
            GameRefs.Sound.PlayBGMInStage(2, true);
            yield return 1f;
            DumpChannels("plaza after PlayBGMInStage(2) +1s");
            yield return 2f;
            DumpChannels("plaza after PlayBGMInStage(2) +3s");
            Msg("CRI.Stop(BGM, 1) as the plaza theme");
            ctr.Stop(SoundController_CRI.eChannel.BGM, 1f);
            yield return 2f;
            DumpChannels("plaza after stopping BGM");
            Msg("StopBGMInStage");
            GameRefs.Sound.StopBGMInStage();
            yield return 2f;
            DumpChannels("plaza after StopBGMInStage");
            foreach (var w in UnityAudioCheck("plaza")) yield return w;

            // The end of a song: BGM01's track event has 2001 = 222364 (ms); its waveform is 223.6 s. A run with a
            // Harmony hook on SoundManager.CriCallBack closed the game at that point; without the hook the song should loop on.
            var src = Source(1);
            src.startTime = 210000;
            Msg("PlayBGMInStage(0, noLoop false) from 210000 ms (no hook on CriCallBack)");
            GameRefs.Sound.PlayBGMInStage(0, false);
            src.startTime = 0;
            for (int i = 0; i < 18; i++) { yield return 1f; DumpChannels($"end marker +{i + 1}s"); }
            Check("game still running past the end marker", true);
            GameRefs.Sound.StopBGMInStage();
            yield return 1.5f;
        }



        private static IEnumerable<float> StageSurvey()
        {
            var g = GameRefs.Global;
            var sm = GameRefs.Sound;
            Msg($"GetInstageBGM {g.GetInstageBGM()}, _nowInStageBGMIdx {sm._nowInStageBGMIdx}, GetNowInstageBGMIdx {sm.GetNowInstageBGMIdx()}, playMode {GameRefs.Save._playModeState}");
            for (int i = 0; i < 3; i++) { DumpChannels($"stage playing +{i * 2}s"); yield return 2f; }
            Msg("calling SoundManager.PlayBGMInStage(7, forceNoLoop true)");
            sm.PlayBGMInStage(7, true);
            yield return 3f;
            DumpChannels("stage after PlayBGMInStage(7)");
            Msg("PauseBGMInStage");
            sm.PauseBGMInStage();
            yield return 1.5f;
            DumpChannels("stage paused");
            Msg("ResumeBGMInStage");
            sm.ResumeBGMInStage();
            yield return 1.5f;
            DumpChannels("stage resumed");
            // Muting the in-stage source from outside: does the volume stay at 0?
            var src = Source(1);
            if (src != null)
            {
                float v0 = src.volume;
                src.volume = 0f;
                yield return 1f;
                Msg($"in-stage source volume before {F(v0)}, set 0, one second later {F(src.volume)}");
                src.volume = v0;
            }
            try { Msg($"GetCRIVolume_BGM {F(sm.GetCRIVolume_BGM())}, BGM setting {GameRefs.Save.GetBGMVolumeSetting()}, VolumeSettingToCRIVolume(10) {F(sm.VolumeSettingToCRIVolume(10))}, channelVolume[1] {F(Ctr.channelVolume[1])}"); }
            catch (Exception e) { Msg("volume read: " + e.Message); }

            // The game's pause menu: what happens to the in-stage channel.
            Msg("opening the pause menu: " + PauseMenu.Open());
            yield return 1.5f;
            DumpChannels($"pause menu open ({PauseMenu.IsOpen}, timeScale {F(Time.timeScale)})");
            yield return 1f;
            DumpChannels("pause menu open +1s");
            PauseMenu.Resume();
            yield return 1.5f;
            DumpChannels($"pause menu closed ({PauseMenu.IsOpen})");

            foreach (var w in UnityAudioCheck("stage")) yield return w;
            yield return 1f;
        }

        // ------------------------------------------------------------------ dumps

        private static SoundController_CRI Ctr
        {
            get
            {
                SoundController_CRI c = null;
                try { c = GameRefs.Sound?.ctr; } catch { }
                if (c == null) try { c = SingletonMonoBehaviour<SoundController_CRI>.Instance; } catch { }
                return c != null ? c : null;
            }
        }

        private static CriAtomSource Source(int channel)
        {
            var ctr = Ctr;
            if (ctr == null) return null;
            // criAS holds each channel's sources one after the other: channelCount [1,1,7,1] = BGM, BGM_INSTAGE, SE, VOICE.
            var count = ctr.channelCount;
            var all = ctr.criAS;
            if (count == null || all == null || channel >= count.Length) return null;
            int at = 0;
            for (int i = 0; i < channel; i++) at += count[i];
            return at >= 0 && at < all.Length ? all[at] : null;
        }

        private static void DumpTables()
        {
            var ctr = Ctr;
            var g = GameRefs.Global;
            D("== SoundController_CRI");
            if (ctr == null) { D("(none)"); Check("SoundController_CRI found", false); return; }
            var cc = ctr.channelCount; var ci = ctr.channelCountIndex;
            D($"channelCount [{string.Join(",", cc ?? new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(0))}] channelCountIndex [{string.Join(",", ci ?? new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(0))}] criAS {ctr.criAS?.Length}");
            try { D($"DLC available [{string.Join(",", ctr.IsDLCAvailable)}]"); } catch (Exception e) { D("DLC: " + e.Message); }
            try { D($"channelVolume [{string.Join(",", ctr.channelVolume.Select(F))}] masterVolume {F(ctr.masterVolume)}"); } catch { }
            D("== BgmTable (index: filePath type loop volume fileIndex)");
            var datas = ctr.bgmTable?.GetDatas();
            int n = datas?.Length ?? 0;
            for (int i = 0; i < n; i++)
            {
                var b = datas[i];
                if (b == null) { D($"{i}: null"); continue; }
                D($"{i} (instage {i - 35}): {b.filePath} type {b.u8Type} loop {b.u8Loop} vol {F(b.f32Volume)} file {b.s16FileIndex}");
            }
            Check("BgmTable read", n > 0, $"{n} entries");
            D("== FavoriteBgmTable (number bgmId bgmNumber albumId jacket unlock | localizeId -> name | song / composition / arrange / first release)");
            var fav = g?.FavoriteBgmTable?.datas;
            int fn = fav?.Count ?? 0;
            for (int i = 0; i < fn; i++)
            {
                var f = fav[i];
                if (f == null) continue;
                string name = AssetUtil.Text(g.BgmTable, f.localizeId);
                D($"#{f.number} id {f.bgmId} no {f.bgmNumber} album {f.albumId} jacket {f.jacketNumber} unlock {f.unlockFlg} | {f.localizeId} -> {name} | {f.song} / {f.composition} / {f.arrange} / {f.firstReleaseTitle}" +
                  $" | t(song) {AssetUtil.Text(g.BgmTable, f.song)} t(first) {AssetUtil.Text(g.BgmTable, f.firstReleaseTitle)}");
            }
            Check("FavoriteBgmTable read", fn > 0, $"{fn} entries");
            try
            {
                var sh = g._shuffleBgmData;
                D($"GlobalManager: GetInstageBGM {g.GetInstageBGM()} _inStageBGMIndex {g._inStageBGMIndex} BGM_SHUFFLE_NUM {GlobalManager.BGM_SHUFFLE_NUM} shuffle [{(sh == null ? "" : string.Join(",", sh))}]");
            }
            catch (Exception e) { D("GlobalManager: " + e.Message); }
            _dump?.Flush();
        }

        private static void DumpSaveMusic()
        {
            var s = GameRefs.Save;
            if (s == null) return;
            try
            {
                var rel = s.BgmReleasables;
                var fav = s.GetFavoriteBgmListData();
                D($"== save: playModeState {s._playModeState} focusBgmId {s._focusBgmId} BGM_NUM {GlobalSaveData.BGM_NUM} FAVORITEBGM_MAX {GlobalSaveData.FAVORITEBGM_MAX}");
                D($"releasable ({rel?.Length}) [{(rel == null ? "" : string.Join("", rel.Select(x => x ? '1' : '0')))}]");
                D($"favorites [{(fav == null ? "" : string.Join(",", fav))}]");
                D($"GetInstageBGM {GameRefs.Global.GetInstageBGM()}");
            }
            catch (Exception e) { D("save music: " + e.Message); }
            _dump?.Flush();
        }

        private static void DumpChannels(string when)
        {
            var ctr = Ctr;
            if (ctr == null) { Msg($"{when}: no SoundController_CRI"); return; }
            var sb = new StringBuilder($"{when}:");
            for (int ch = 0; ch < 2; ch++)
            {
                var src = Source(ch);
                if (src == null) { sb.Append($" ch{ch} none;"); continue; }
                long t = -1;
                try { t = src.player?.GetTime() ?? -1; } catch { }
                string st = "?";
                try { st = src.status.ToString(); } catch { }
                bool paused = false;
                try { paused = src.IsPaused(); } catch { }
                sb.Append($" ch{ch} {src.cueSheet}/{src.cueName} {st}{(paused ? " PAUSED" : "")} t {t} ms vol {F(src.volume)} loop {src.loop};");
            }
            try { sb.Append($" IsPlayingBGM {ctr.IsPlayingBGM()} IsPlayingBGMInStage {ctr.IsPlayingBGMInStage()}"); } catch { }
            Msg(sb.ToString());
        }

        private static MapEntry PickPlaza()
        {
            var plazas = StageCatalog.Entries.Where(e => e.Kind == MapKind.Plaza && !e.NeedsAA).ToList();
            int cur = -1;
            try { cur = GameRefs.Save._stageIndex; } catch { }
            foreach (var p in plazas)
                try { if (SelectHirobaManager.GetStageIndex(p.PlazaKey) == cur) return p; } catch { }
            return plazas.FirstOrDefault() ?? throw new InvalidOperationException("no plaza");
        }

        // ------------------------------------------------------------------ Unity audio

        private static GameObject _probe;
        private static AudioSource _probeSrc;

        private static float Rms()
        {
            try
            {
                var buf = AudioListener.GetOutputData(1024, 0);
                double s = 0;
                for (int i = 0; i < buf.Length; i++) s += buf[i] * buf[i];
                return (float)Math.Sqrt(s / Math.Max(1, buf.Length));
            }
            catch (Exception e) { Msg("GetOutputData: " + e.Message); return -1f; }
        }

        private static IEnumerable<float> UnityAudioCheck(string where)
        {
            int listeners = UnityEngine.Object.FindObjectsOfType<AudioListener>().Length;
            int sources = UnityEngine.Object.FindObjectsOfType<AudioSource>().Length;
            Msg($"{where}: Unity AudioListeners {listeners}, AudioSources {sources}, output RMS before {F(Rms())}");
            if (_probe == null)
            {
                _probe = new GameObject("OUAKSongSelector.Probe");
                UnityEngine.Object.DontDestroyOnLoad(_probe);
                _probeSrc = _probe.AddComponent<AudioSource>();
                int rate = 48000, len = rate * 2;
                var clip = AudioClip.Create("ouak_probe_tone", len, 1, rate, false);
                var data = new float[len];
                for (int i = 0; i < len; i++) data[i] = 0.05f * Mathf.Sin(2f * Mathf.PI * 440f * i / rate);
                clip.SetData(data, 0);
                _probeSrc.clip = clip;
                _probeSrc.loop = true;
                _probeSrc.spatialBlend = 0f;
                _probeSrc.playOnAwake = false;
            }
            _probeSrc.volume = 1f;
            _probeSrc.Play();
            yield return 0.6f;
            float r1 = Rms();
            Msg($"{where}: tone playing: isPlaying {_probeSrc.isPlaying}, time {F(_probeSrc.time)}, output RMS {F(r1)} (a 0.05 sine is 0.035)");
            if (listeners == 0 && r1 <= 0.001f && _probe.GetComponent<AudioListener>() == null)
            {
                Msg($"{where}: adding an AudioListener to the probe object");
                _probe.AddComponent<AudioListener>();
                yield return 0.6f;
                Msg($"{where}: with our listener: isPlaying {_probeSrc.isPlaying}, time {F(_probeSrc.time)}, output RMS {F(Rms())}");
            }
            _probeSrc.Pause();
            yield return 0.3f;
            float tp = _probeSrc.time;
            yield return 0.4f;
            Msg($"{where}: paused: isPlaying {_probeSrc.isPlaying}, time {F(tp)} -> {F(_probeSrc.time)}, RMS {F(Rms())}");
            _probeSrc.Stop();
            yield return 0.3f;
            Msg($"{where}: stopped: isPlaying {_probeSrc.isPlaying}, RMS {F(Rms())}");
        }

        private static void StopProbe()
        {
            if (_probe != null) UnityEngine.Object.Destroy(_probe);
            _probe = null; _probeSrc = null;
        }

        // ------------------------------------------------------------------ call log

        private static void InstallLogHooks()
        {
            var t = typeof(SongTest);
            var res = new List<string>
            {
                $"CRI.PlayBGM {LogHooks.Prefix(typeof(SoundController_CRI), "PlayBGM", t, nameof(LogCriPlayBgm))}",
                $"CRI.PlayBGMInStage {LogHooks.Prefix(typeof(SoundController_CRI), "PlayBGMInStage", t, nameof(LogCriPlayBgmInStage))}",
                $"CRI.Stop {LogHooks.Prefix(typeof(SoundController_CRI), "Stop", t, nameof(LogCriStop))}",
                $"CRI.Pause {LogHooks.Prefix(typeof(SoundController_CRI), "Pause", t, nameof(LogCriPause))}",
                $"CRI.UnPause {LogHooks.Prefix(typeof(SoundController_CRI), "UnPause", t, nameof(LogCriUnPause))}",
                $"SM.PlayBGMInStageAwait {LogHooks.Prefix(typeof(SoundManager), "PlayBGMInStageAwait", t, nameof(LogSmAwait))}",
                $"MGM.CheckInStageBGM {LogHooks.Prefix(typeof(MainGameManager), "CheckInStageBGM", t, nameof(LogCheckInStage))}",
                $"Plaza.StageBGM {LogHooks.Prefix(typeof(SelectHirobaSoundController), "StageBGM", t, nameof(LogPlazaStageBgm))}",
                $"Plaza.DemoBGM {LogHooks.Prefix(typeof(SelectHirobaSoundController), "DemoBGM", t, nameof(LogPlazaDemoBgm))}",
                $"GM.SetInstageBGM {LogHooks.Prefix(typeof(GlobalManager), "SetInstageBGM", t, nameof(LogSetInstage))}",
                $"CRI.Play {LogHooks.Prefix(typeof(SoundController_CRI), "Play", t, nameof(LogCriPlay), new[] { typeof(SoundController_CRI.eChannel), typeof(string), typeof(bool), typeof(float), typeof(float), typeof(float), typeof(ulong), typeof(float) })}",
            };
            Msg("log hooks: " + string.Join(", ", res));
        }

        private static string Caller()
        {
            try
            {
                var st = new System.Diagnostics.StackTrace(2, false);
                var names = new List<string>();
                for (int i = 0; i < st.FrameCount && names.Count < 4; i++)
                {
                    var m = st.GetFrame(i)?.GetMethod();
                    if (m == null) continue;
                    string n = m.DeclaringType?.Name + "." + m.Name;
                    if (n.Contains("SongTest") || n.Contains("DMD") || n.Contains("Trampoline")) continue;
                    names.Add(n);
                }
                return string.Join(" < ", names);
            }
            catch { return "?"; }
        }

        public static void LogCriPlayBgm(int __0, bool __1, bool __2, bool __3) { try { Msg($"call CRI.PlayBGM(idx {__0}, setAwait {__1}, loadOnly {__2}, noLoop {__3}) scene {SceneLoader.ActiveScene}"); } catch { } }
        public static void LogCriPlayBgmInStage(int __0, bool __1, bool __2, bool __3) { try { Msg($"call CRI.PlayBGMInStage(idx {__0}, setAwait {__1}, loadOnly {__2}, noLoop {__3}) scene {SceneLoader.ActiveScene}"); } catch { } }
        public static void LogCriStop(SoundController_CRI.eChannel __0, float __1) { try { if ((int)__0 < 2) Msg($"call CRI.Stop({__0}, fade {F(__1)})"); } catch { } }
        public static void LogCriPause(SoundController_CRI.eChannel __0) { try { if ((int)__0 < 2) Msg($"call CRI.Pause({__0})"); } catch { } }
        public static void LogCriUnPause(SoundController_CRI.eChannel __0) { try { if ((int)__0 < 2) Msg($"call CRI.UnPause({__0})"); } catch { } }
        public static void LogSmAwait(int __0, bool __1, bool __2) { try { Msg($"call SM.PlayBGMInStageAwait(idx {__0}, isStop {__1}, noLoop {__2})"); } catch { } }
        public static void LogCheckInStage(MainGameManager __instance, bool __0) { try { Msg($"call MGM.CheckInStageBGM(setGlobal {__0}) stage {__instance.TargetStage} GetInstageBGM {GameRefs.Global?.GetInstageBGM()}"); } catch { } }
        public static void LogPlazaStageBgm() { try { Msg("call Plaza.StageBGM"); } catch { } }
        public static void LogPlazaDemoBgm() { try { Msg("call Plaza.DemoBGM"); } catch { } }
        /// <summary>The cue each music channel was last told to play (CriAtomSource.cueName stays empty for the game's music).</summary>
        internal static readonly string[] LastCue = { "", "" };
        public static void LogCriPlay(SoundController_CRI.eChannel __0, string __1, bool __2)
        {
            try { int c = (int)__0; if (c < 2) { bool action = (__1 ?? "").StartsWith("Action_") || (__1 ?? "").StartsWith("mus_"); if (!action) LastCue[c] = __1 ?? ""; Msg($"call CRI.Play({__0}, {__1}, loop {__2})"); } } catch { }
        }

        public static void LogSetInstage(int __0) { try { Msg($"call GM.SetInstageBGM({__0})"); } catch { } }
    }
}
