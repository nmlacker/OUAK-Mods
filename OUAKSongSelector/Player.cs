using System;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using Il2CppApp.KatamariSin;
using Il2CppCriWare;
using OUAKModAPI;
using UnityEngine;

namespace OUAKSongSelector
{
    public enum Place { None, Stage, Plaza }

    /// <summary>
    /// Plays playlists where the game plays its place music. How the game does it (traced in GameAssembly.dll, measured in game):
    /// <list type="bullet">
    /// <item>Story stage: MainGameManager.CheckInStageBGM (stage start and every retry, CorRestartStage) picks a song
    /// (GlobalManager.GetInstageBGM = the fan's-talk choice, or the jukebox shuffle) and calls SoundManager.PlayBGMInStageAwait
    /// -> SoundController_CRI.PlayBGMInStage(idx, setAwait, loadOnly) -> loaded on CRI channel BGM_INSTAGE (criAS[1]) and
    /// paused; the game unpauses it when the opening fly-over ends, pauses it in the pause menu, stops it at the results.</item>
    /// <item>Plaza: SelectHirobaSoundController.StageBGM -> SoundManager.PlayBGM(theme) on channel BGM (criAS[0]); the
    /// fan's stage screen switches to PlayBGM(79); the jukebox plays songs with PlayBGMInStage, which stops channel BGM.</item>
    /// <item>A song index is FavoriteBgmInfo.bgmId (BgmTable entry index+35); +1000 plays its instrumental (VocalOff label).</item>
    /// </list>
    /// Game songs: the place's request gets our index instead (stage), or we play it with PlayBGMInStage like the jukebox
    /// (Plaza). Your WAV songs: a Unity AudioSource; in a stage the game's own song keeps playing silently on its channel
    /// as a "carrier", and the WAV follows it (paused, resumed, stopped, faded exactly when the game does that to its music).
    /// </summary>
    public static class Player
    {
        private static readonly HookSet Hooks = new("ouak.songselector");
        public static bool HooksOk { get; private set; }
        public const string GameMusic = "@game";

        // ------------------------------------------------------------------ the game's sound objects

        internal static SoundController_CRI Ctr
        {
            get
            {
                SoundController_CRI c = null;
                try { c = GameRefs.Sound?.ctr; } catch { }
                return c != null ? c : null;
            }
        }

        /// <summary>The CRI source of a channel: criAS holds each channel's sources in a row (channelCount [1,1,7,1]).</summary>
        internal static CriAtomSource ChannelSource(int channel)
        {
            var ctr = Ctr;
            if (ctr == null) return null;
            var count = ctr.channelCount;
            var all = ctr.criAS;
            if (count == null || all == null || channel >= count.Length) return null;
            int at = 0;
            for (int i = 0; i < channel; i++) at += count[i];
            var s = at < all.Length ? all[at] : null;
            return s != null ? s : null;
        }

        private static bool SourcePlaying(CriAtomSource s, out bool paused, out bool stopped)
        {
            paused = false; stopped = true;
            if (s == null) return false;
            CriAtomSourceBase.Status st;
            try { st = s.status; paused = s.IsPaused(); } catch { return false; }
            stopped = st == CriAtomSourceBase.Status.Stop || st == CriAtomSourceBase.Status.PlayEnd || st == CriAtomSourceBase.Status.Error;
            return !stopped && !paused;
        }

        // ------------------------------------------------------------------ state

        public static Place Where { get; private set; }
        /// <summary>The stage (eStage name) while <see cref="Where"/> is Stage.</summary>
        public static string StageKey { get; private set; } = "";
        public static Playlist Active { get; private set; }
        /// <summary>Why <see cref="Active"/> plays here ("Everywhere", "All stages", "This stage", "The Plaza", "Play now").</summary>
        public static string ActiveFrom { get; private set; } = "";
        public static string Current { get; private set; }
        public static bool CurrentIsCustom { get; private set; }
        /// <summary>Seconds into the current song (game songs: counted while the game's channel plays).</summary>
        public static double Position { get; private set; }
        /// <summary>Length of one play-through in seconds, or 0 if unknown.</summary>
        public static double Length { get; private set; }
        /// <summary>The game plays something else here for now (the jukebox, the fan's stage screen, a preview).</summary>
        public static bool Suspended { get; private set; }
        /// <summary>The game stopped the place's music (results, leaving); the playlist waits for the next start.</summary>
        public static bool StoppedByGame { get; private set; }
        public static string Status { get; private set; } = "";
        /// <summary>Songs started by the playlist so far (for the test).</summary>
        public static int Started { get; private set; }
        public static event Action<string> SongStarted;

        private static int _gameStageIdx = -1, _plazaThemeIdx = -1, _currentGameIdx = -1, _substituted = -1;
        private static bool _ours, _inCheck, _inTheme, _carrierSeen, _customStarted;
        private static string _playNow;
        private static Place _playNowPlace;
        private static float _fadeLeft = -1f, _fadeTotal = 1f;
        private static int _seenVersion = -1;
        private static SelectHirobaSoundController _plazaCtl;

        private sealed class Cursor { public List<string> Order = new(); public int Pos = -1; public string Signature = ""; }
        private static readonly Dictionary<string, Cursor> _cursors = new(StringComparer.OrdinalIgnoreCase);
        private static readonly System.Random _rng = new();

        // ------------------------------------------------------------------ setup

        public static void Init()
        {
            var t = typeof(Player);
            bool a = Hooks.Prefix(typeof(MainGameManager), "CheckInStageBGM", t, nameof(PreCheckInStage));
            bool b = Hooks.Postfix(typeof(MainGameManager), "CheckInStageBGM", t, nameof(PostCheckInStage));
            bool c = Hooks.Prefix(typeof(SelectHirobaSoundController), "StageBGM", t, nameof(PreTheme));
            bool d = Hooks.Postfix(typeof(SelectHirobaSoundController), "StageBGM", t, nameof(PostTheme));
            bool e = Hooks.Prefix(typeof(SoundController_CRI), "PlayBGMInStage", t, nameof(PrePlayInStage));
            bool f = Hooks.Prefix(typeof(SoundController_CRI), "PlayBGM", t, nameof(PrePlayBgm));
            bool g = Hooks.Prefix(typeof(SoundController_CRI), "Stop", t, nameof(PreStop));
            // SoundManager.PlayBGMInStageAwait stores the index it was given in _nowInStageBGMIdx after the CRI call, and the
            // stage's song banner (UIBgmNameDisplay.CheckBGMNameText) names that one: point both at what really plays.
            bool h = Hooks.Postfix(typeof(SoundManager), "PlayBGMInStageAwait", t, nameof(PostAwait));
            bool i = Hooks.Postfix(typeof(UIBgmNameDisplay), "CheckBGMNameText", t, nameof(PostBannerText));
            // The game's other music pause: SoundManager.PauseBGMInStage -> SoundController_CRI.PauseBGMInStage plays an action
            // cue (no CRI pause); MainGameManager.RecheckTimerSaturation (the Timer freebie's stopped clock) and
            // ResumeItemEffect call it, EndItemEffect / SuspendItemEffect / QuitPauseMenu call ResumeBGMInStage ->
            // UnpauseBGMInStage. Your files follow these calls too.
            bool j = Hooks.Prefix(typeof(SoundController_CRI), "PauseBGMInStage", t, nameof(PreGamePause));
            bool k = Hooks.Prefix(typeof(SoundController_CRI), "ImmediatePauseBGMInStage", t, nameof(PreGamePause));
            bool l = Hooks.Prefix(typeof(SoundController_CRI), "UnpauseBGMInStage", t, nameof(PreGameUnpause));
            // A pause of yours stays when the game unpauses its stage channel (pause menu closed, Timer over): same frame.
            bool m = Hooks.Postfix(typeof(SoundController_CRI), "UnpauseBGMInStage", t, nameof(PostGameUnpause));
            HooksOk = a && b && c && d && e && f && g && j && k && l && m;
            SongSelectorMod.Log.Info($"Hooks: CheckInStageBGM {a && b}, StageBGM {c && d}, PlayBGMInStage {e}, PlayBGM {f}, Stop {g}, song banner {h && i}, game pause {j && k && l && m}");
        }

        // ------------------------------------------------------------------ hooks (small, catch everything)

        public static void PreCheckInStage() { _inCheck = true; }
        public static void PostCheckInStage() { _inCheck = false; }
        public static void PreTheme(SelectHirobaSoundController __instance) { _inTheme = true; try { _plazaCtl = __instance; } catch { } }
        public static void PostTheme() { _inTheme = false; }

        public static void PreGamePause()
        {
            try
            {
                if (_ours) return;
                _gameMusicPaused = true;
                // Your file stops at once (not a frame later in Watch).
                if (Where == Place.Stage && CurrentIsCustom && CustomAudio.IsPlaying) { CustomAudio.Pause(); _pausedByCarrier = true; }
            }
            catch { }
        }

        public static void PreGameUnpause() { try { if (!_ours) _gameMusicPaused = false; } catch { } }

        public static void PostGameUnpause()
        {
            try
            {
                if (_ours || !_userPaused || Active == null || CurrentIsCustom || Where == Place.None) return;
                _ours = true;
                try { Ctr?.Pause(SoundController_CRI.eChannel.BGM_INSTAGE); } finally { _ours = false; }
            }
            catch { }
        }

        public static void PostAwait(SoundManager __instance)
        {
            try { if (_substituted >= 0) { __instance._nowInStageBGMIdx = _substituted; _substituted = -1; } } catch { }
        }

        public static void PostBannerText(UIBgmNameDisplay __instance)
        {
            try
            {
                if (Where != Place.Stage || Active == null || Current == null || !CurrentIsCustom) return;
                var t = __instance.BGMText;
                if (t != null) t.text = Catalog.Describe(Current);
            }
            catch { }
        }

        public static bool PrePlayInStage(ref int __0)
        {
            try
            {
                if (_ours) return true;
                if (_inCheck) return OnStageRequest(ref __0);
                if (Active != null && !Suspended && Where != Place.None)
                {
                    // The game brought back the song we were playing (e.g. after an options preview): carry on.
                    if (!CurrentIsCustom && __0 == _currentGameIdx) { Position = 0; return true; }
                    Suspend("the game played another song (" + __0 + ")");
                }
            }
            catch (Exception e) { SongSelectorMod.Log.Warn("PlayBGMInStage hook: " + e.Message); }
            return true;
        }

        public static bool PrePlayBgm(int __0)
        {
            try
            {
                if (_ours) return true;
                if (_inTheme) return OnPlazaThemeRequest(__0);
                if (Where == Place.Plaza && Active != null && !Suspended) Suspend("the Plaza's own music (" + __0 + ")");
            }
            catch (Exception e) { SongSelectorMod.Log.Warn("PlayBGM hook: " + e.Message); }
            return true;
        }

        public static void PreStop(SoundController_CRI.eChannel __0, float __1)
        {
            try
            {
                if (_ours || Active == null || Suspended) return;
                // The Plaza stops its theme (channel BGM) before it changes music or scene: a playlist song played on the
                // in-stage channel stops with it (done next frame, outside this hook).
                if (Where == Place.Plaza && __0 == SoundController_CRI.eChannel.BGM && !CurrentIsCustom) { _stopInStage = Mathf.Max(0.05f, __1); return; }
                if (!CurrentIsCustom) return;
                // In a stage the carrier channel's stop (results, leaving) fades your song out the same way.
                bool mine = (Where == Place.Stage && __0 == SoundController_CRI.eChannel.BGM_INSTAGE) ||
                            (Where == Place.Plaza && __0 == SoundController_CRI.eChannel.BGM);
                if (mine && _fadeLeft < 0f && CustomAudio.IsPlaying) { _fadeTotal = Mathf.Max(0.05f, __1); _fadeLeft = _fadeTotal; }
            }
            catch { }
        }

        // ------------------------------------------------------------------ requests from the game

        private static bool OnStageRequest(ref int idx)
        {
            var gm = GameRefs.Main;
            if (gm == null || GameRefs.IsMultiplay) return true;
            string key = gm.TargetStage.ToString();
            if (Where != Place.Stage || StageKey != key) EnterPlace(Place.Stage, key);
            _gameStageIdx = idx;
            // GetShufflePlayLastBGM has just set the game's own value for this stage start: keep it to give back later.
            try { _gameNoShuffle = GameRefs.Sound?._unreleased_noShuffle; } catch { _gameNoShuffle = null; }
            StoppedByGame = false; Suspended = false; _carrierSeen = false; _gameMusicPaused = false;
            var p = Resolve(out string from);
            // The game loads the stage's song onto the stage-music channel without clearing it (measured: songs a playlist
            // played in the Plaza went on under the stage's song), so clear it when the playlist takes over or left a song there.
            if (p != null || _ownsStageChannel) StopStageChannelNow();
            if (p == null) { Idle(""); return true; }
            Active = p; ActiveFrom = from;
            string track = NextTrack(p, +1, out bool ended);
            if (track == null) { Idle(ended ? "" : $"\"{p.Name}\" has no song that can play here."); return true; }
            if (TrackRef.IsGame(track, out int gi))
            {
                SongSelectorMod.Log.Info($"Stage {key}: playlist \"{p.Name}\" ({from}) -> {Catalog.Describe(track)} (game {gi}) instead of {idx}");
                idx = gi;
                _substituted = gi;
                BeginGameSong(track, gi);
            }
            else
            {
                // The game's own song loads as the silent carrier; the WAV follows it.
                SongSelectorMod.Log.Info($"Stage {key}: playlist \"{p.Name}\" ({from}) -> {Catalog.Describe(track)} (your file; carrier {idx})");
                BeginCustomSong(track);
            }
            return true;
        }

        private static bool OnPlazaThemeRequest(int theme)
        {
            if (Where != Place.Plaza) EnterPlace(Place.Plaza, SceneLoader.ActiveScene);
            _plazaThemeIdx = theme;
            StoppedByGame = false;
            var p = Resolve(out string from);
            if (p == null) { Idle(""); return true; }
            if (Suspended && Active == p && Current != null) { Resume(); return false; }
            Suspended = false;
            Active = p; ActiveFrom = from;
            string track = NextTrack(p, +1, out bool ended);
            if (track == null) { Idle(ended ? "" : $"\"{p.Name}\" has no song that can play here."); return true; }
            SongSelectorMod.Log.Info($"Plaza: playlist \"{p.Name}\" ({from}) -> {Catalog.Describe(track)} instead of the theme {theme}");
            Play(track);
            return false;
        }

        // ------------------------------------------------------------------ places and playlists

        private static void EnterPlace(Place where, string key)
        {
            _gameMusicPaused = false; _userPaused = false;
            CustomAudio.Unload();
            Where = where; StageKey = where == Place.Stage ? key : "";
            Active = null; Current = null; CurrentIsCustom = false; Suspended = false; StoppedByGame = false;
            _currentGameIdx = -1; _carrierSeen = false; _customStarted = false; _fadeLeft = -1f;
            if (_playNowPlace != where) _playNow = null;
            Status = "";
        }

        private static void LeavePlace()
        {
            _gameMusicPaused = false; _userPaused = false;
            GiveBackShuffle();
            if (Where == Place.Plaza && _ownsStageChannel && !Suspended) StopStageChannelNow();
            if (Where == Place.None) return;
            CustomAudio.Unload();
            Where = Place.None; StageKey = ""; Active = null; Current = null; CurrentIsCustom = false; Suspended = false;
            _playNow = null; _plazaCtl = null;
            Status = "";
        }

        /// <summary>The playlist for the current place: the most specific choice wins.</summary>
        public static Playlist Resolve(out string from) => Resolve(Where, StageKey, out from);

        public static Playlist Resolve(Place where, string stageKey, out string from)
        {
            from = "";
            if (where == Place.None) return null;
            if (_playNow != null && _playNowPlace == where) { var pn = Store.Find(_playNow); if (pn != null) { from = "Play now"; return pn; } }
            var u = Store.Settings.Use;
            if (where == Place.Stage && !string.IsNullOrEmpty(stageKey) && u.PerStage.TryGetValue(stageKey, out var n) && !string.IsNullOrEmpty(n))
            {
                if (n == GameMusic) return null;
                var p = Store.Find(n); if (p != null) { from = "This stage"; return p; }
            }
            if (where == Place.Stage) { var p = Store.Find(u.Stages); if (p != null) { from = "All stages"; return p; } }
            if (where == Place.Plaza) { var p = Store.Find(u.Plaza); if (p != null) { from = "The Plaza"; return p; } }
            var g = Store.Find(u.Global);
            if (g != null) { from = "Everywhere"; return g; }
            return null;
        }

        private static Cursor CursorFor(Playlist p)
        {
            string sig = string.Join("|", p.Tracks) + (p.Shuffle ? "#s" : "");
            if (_cursors.TryGetValue(p.Name, out var c) && c.Signature == sig) return c;
            c = new Cursor { Signature = sig, Order = p.Tracks.ToList() };
            if (p.Shuffle) Shuffle(c.Order, null);
            // Keep going from the song playing now when the list changes under it.
            if (Current != null && Active == p) c.Pos = c.Order.IndexOf(Current);
            _cursors[p.Name] = c;
            return c;
        }

        private static void Shuffle(List<string> list, string notFirst)
        {
            for (int i = list.Count - 1; i > 0; i--) { int j = _rng.Next(i + 1); (list[i], list[j]) = (list[j], list[i]); }
            if (notFirst != null && list.Count > 1 && list[0] == notFirst) { (list[0], list[list.Count - 1]) = (list[list.Count - 1], list[0]); }
        }

        /// <summary>The next (or previous) playable song; at the end, wraps unless the playlist's repeat is Off.</summary>
        private static string NextTrack(Playlist p, int dir, out bool ended)
        {
            ended = false;
            var c = CursorFor(p);
            int n = c.Order.Count;
            if (n == 0) return null;
            for (int tries = 0; tries < n; tries++)
            {
                int np = c.Pos + dir;
                if (np >= n)
                {
                    if (p.Repeat == RepeatMode.Off && dir > 0 && c.Pos >= 0) { ended = true; c.Pos = -1; return null; }
                    if (p.Shuffle) Shuffle(c.Order, Current);
                    np = 0;
                }
                if (np < 0) np = n - 1;
                c.Pos = np;
                if (Catalog.Playable(c.Order[np], out _)) return c.Order[np];
            }
            return null;
        }

        // ------------------------------------------------------------------ playing

        private static void BeginGameSong(string track, int gameIdx)
        {
            if (CurrentIsCustom) CustomAudio.Stop();
            _fadeLeft = -1f;
            Current = track; CurrentIsCustom = false; _currentGameIdx = gameIdx;
            Position = 0; Length = 0;
            try
            {
                var data = Ctr?.bgmTable?.GetData((gameIdx >= 1000 ? gameIdx - 1000 : gameIdx) + 35);
                string cue = data?.filePath; cue = cue?.Substring(cue.LastIndexOf('/') + 1);
                long ms = AcbInfo.EndMs(cue);
                Length = ms > 0 ? ms / 1000.0 : 0;
            }
            catch { }
            Started++;
            Status = "";
            Announce(track);
        }

        private static void BeginCustomSong(string track)
        {
            CustomAudio.Stop();
            Current = track; CurrentIsCustom = true;
            Position = 0; Length = 0; _customStarted = false; _pausedByCarrier = false; _fadeLeft = -1f;
            TrackRef.IsFile(track, out string rel);
            string err = CustomAudio.Request(rel);
            Status = err != null ? $"Can't play {rel}: {err}" : "";
            Started++;
            Announce(track);
        }

        /// <summary>Starts loading the playlist's next song if it's one of your files, so it's ready when this one ends.</summary>
        private static void PreloadNext()
        {
            try
            {
                var p = Active;
                if (p == null || p.Repeat == RepeatMode.One) return;
                var c = CursorFor(p);
                for (int k = 1; k <= c.Order.Count; k++)
                {
                    string t = c.Order[(c.Pos + k) % c.Order.Count];
                    if (!Catalog.Playable(t, out _)) continue;
                    if (TrackRef.IsFile(t, out string rel) && t != Current) CustomAudio.Request(rel);
                    return;
                }
            }
            catch { }
        }

        private static void Announce(string track)
        {
            PreloadNext();
            try { SongStarted?.Invoke(track); } catch { }
            if (Store.Settings.ShowNowPlaying && Where != Place.None) ModTasks.Toast("♪ " + Catalog.Describe(track));
        }

        /// <summary>
        /// Starts a game song on the stage-music channel (BGM_INSTAGE) the way the game's jukebox does
        /// (FavoriteBgmPlayer.PlayBgm: StopBGMInStageImmediate, then PlayBGMInStage). SoundController_CRI.PlayBGMInStage only
        /// stops channel BGM (traced: IsPlaying(0) / Stop(0, 1 s)) and starts the new cue on the same CRI player, so a song
        /// already on BGM_INSTAGE would go on playing under the new one. The immediate stop also cancels a fade still running
        /// on that channel (SoundController_CRI.Stop calls StopCoroutine), which would otherwise stop the new song at its end.
        /// A channel the game had paused (pause menu) stays paused.
        /// </summary>
        private static void StartOnStageChannel(int idx, int startMs = 0)
        {
            var sm = GameRefs.Sound;
            if (sm == null) return;
            var src = ChannelSource(1);
            bool keepPaused = false;
            try { keepPaused = Where == Place.Stage && src != null && src.IsPaused() && src.status == CriAtomSourceBase.Status.Playing; } catch { }
            if (GameHoldsMusic()) keepPaused = true;
            _ours = true;
            _ownsStageChannel = true;
            try
            {
                sm.StopBGMInStageImmediate();
                if (src != null && startMs > 0) src.startTime = startMs;
                sm.PlayBGMInStage(idx, false);
                if (keepPaused) Ctr?.Pause(SoundController_CRI.eChannel.BGM_INSTAGE);
            }
            finally
            {
                _ours = false;
                if (src != null && startMs > 0) { try { src.startTime = 0; } catch { } }
            }
        }

        /// <summary>Starts a song now (Next / Previous / song end / Play now).</summary>
        private static void Play(string track)
        {
            if (track == null) return;
            if (TrackRef.IsGame(track, out int gi))
            {
                BeginGameSong(track, gi);
                StartOnStageChannel(gi);
            }
            else
            {
                BeginCustomSong(track);
                _ours = true;
                try
                {
                    if (Where == Place.Plaza)
                    {
                        // Like the jukebox: the Plaza theme and any in-stage song stop while yours plays.
                        Ctr?.Stop(SoundController_CRI.eChannel.BGM, 1f);
                        Ctr?.Stop(SoundController_CRI.eChannel.BGM_INSTAGE, 1f);
                        _ownsStageChannel = false;
                    }
                    else if (Where == Place.Stage)
                    {
                        // Keep (or bring back) a carrier song on the stage's channel for your file to follow.
                        SourcePlaying(ChannelSource(1), out _, out bool stopped);
                        if (stopped && _gameStageIdx >= 0) StartOnStageChannel(_gameStageIdx);
                    }
                }
                finally { _ours = false; }
            }
        }

        private static void Suspend(string why)
        {
            Suspended = true;
            _fadeLeft = -1f;
            if (CurrentIsCustom) CustomAudio.Pause();
            Status = "Paused for " + why + ".";
            SongSelectorMod.Log.Info("Playlist paused: " + why);
        }

        private static void Resume()
        {
            Suspended = false; Status = "";
            if (Current == null) return;
            SongSelectorMod.Log.Info($"Playlist back: {Catalog.Describe(Current)} at {Position:0.0} s");
            if (CurrentIsCustom)
            {
                _ours = true;
                try { if (Where == Place.Plaza) Ctr?.Stop(SoundController_CRI.eChannel.BGM_INSTAGE, 1f); }
                finally { _ours = false; }
                if (_customStarted) CustomAudio.UnPause();
                return;
            }
            // A game song picks up where it was (whatever the game played meanwhile on that channel stops).
            double keep = Position;
            StartOnStageChannel(_currentGameIdx, (int)(keep * 1000));
            Position = keep;
        }

        /// <summary>Back to the game's own music for this place (playlist ended, or none chosen any more).</summary>
        private static void HandBack(string why)
        {
            bool wasPlaying = Current != null && !Suspended && !StoppedByGame;
            CustomAudio.Stop();
            Idle(why);
            if (!wasPlaying) return;
            if (Where == Place.Stage && _gameStageIdx >= 0) { StartOnStageChannel(_gameStageIdx); return; }
            _ours = true;
            try
            {
                if (Where == Place.Plaza && _plazaThemeIdx >= 0)
                {
                    Ctr?.Stop(SoundController_CRI.eChannel.BGM_INSTAGE, 1f);
                    _ownsStageChannel = false;
                    GameRefs.Sound?.PlayBGM(_plazaThemeIdx, false);
                }
            }
            finally { _ours = false; }
        }

        private static void Idle(string why)
        {
            GiveBackShuffle();
            Active = null; Current = null; CurrentIsCustom = false; _currentGameIdx = -1; Suspended = false;
            Position = 0; Length = 0; Status = why;
        }

        private static void SongEnded()
        {
            var p = Active;
            if (p == null) return;
            SongSelectorMod.Log.Info($"Song ended: {Catalog.Describe(Current)} after {Position:0.0} s");
            if (p.Repeat == RepeatMode.One && Current != null && Catalog.Playable(Current, out _)) { Play(Current); return; }
            string next = NextTrack(p, +1, out bool ended);
            if (next == null) { HandBack(ended ? $"\"{p.Name}\" finished; the game's music is back." : $"\"{p.Name}\" has nothing playable."); return; }
            Play(next);
        }

        // ------------------------------------------------------------------ controls (call from Update, e.g. ModTasks.Run)

        public static bool CanControl => Active != null && Where != Place.None && !StoppedByGame;

        public static string Next()
        {
            if (!CanControl) throw new InvalidOperationException("No playlist is playing here.");
            Suspended = false; _userPaused = false;
            string t = NextTrack(Active, +1, out bool ended);
            if (t == null && ended) { CursorFor(Active).Pos = -1; t = NextTrack(Active, +1, out _); }
            if (t == null) throw new InvalidOperationException("Nothing in this playlist can play here.");
            Play(t);
            return "♪ " + Catalog.Describe(t);
        }

        public static string Previous()
        {
            if (!CanControl) throw new InvalidOperationException("No playlist is playing here.");
            Suspended = false; _userPaused = false;
            if (Position > 3 && Current != null) { Play(Current); return "♪ " + Catalog.Describe(Current) + " (from the start)"; }
            string t = NextTrack(Active, -1, out _);
            if (t == null) throw new InvalidOperationException("Nothing in this playlist can play here.");
            Play(t);
            return "♪ " + Catalog.Describe(t);
        }

        /// <summary>Plays one song of a playlist here now (makes that playlist the one playing here until you leave).</summary>
        public static string PlayNow(Playlist p, string track = null)
        {
            if (Where == Place.None) throw new InvalidOperationException("Go to a story stage or the Plaza first.");
            if (p == null) throw new InvalidOperationException("No playlist.");
            _playNow = p.Name; _playNowPlace = Where;
            Active = p; ActiveFrom = "Play now"; Suspended = false; StoppedByGame = false; _userPaused = false;
            var c = CursorFor(p);
            if (track != null)
            {
                int i = c.Order.IndexOf(track);
                if (i < 0) throw new InvalidOperationException("That song isn't in the playlist.");
                if (!Catalog.Playable(track, out string why)) throw new InvalidOperationException(why);
                c.Pos = i;
            }
            else track = NextTrack(p, +1, out _);
            if (track == null) throw new InvalidOperationException($"Nothing in \"{p.Name}\" can play here.");
            Play(track);
            return "♪ " + Catalog.Describe(track);
        }

        /// <summary>Stops the playlist here and gives the place its own music back.</summary>
        public static string StopHere()
        {
            if (Active == null) throw new InvalidOperationException("No playlist is playing here.");
            _playNow = GameMusic; _playNowPlace = Where;
            HandBack("Stopped; the game's music is back until you leave this place.");
            return "Playlist stopped here.";
        }

        // ------------------------------------------------------------------ every frame

        public static void Tick()
        {
            CustomAudio.Tick();
            try { Library.Tick(); } catch (Exception e) { SongSelectorMod.Log.Warn("Library: " + e.Message); }
            try { Watch(); }
            catch (Exception e) { SongSelectorMod.Log.Warn("Player: " + e.Message); }
        }

        private static void Watch()
        {
            // The Plaza stopped its theme (leaving, or changing music): a playlist game song on the in-stage channel stops too.
            if (_stopInStage > 0f)
            {
                float fade = _stopInStage; _stopInStage = 0f;
                _ours = true;
                try { Ctr?.Stop(SoundController_CRI.eChannel.BGM_INSTAGE, fade); }
                finally { _ours = false; }
                if (!Suspended && Active != null) { StoppedByGame = true; Status = "The Plaza stopped its music."; }
            }

            // Still in the place?
            if (Where == Place.Stage && (GameRefs.Main == null || GameRefs.IsMultiplay)) LeavePlace();
            if (Where == Place.Plaza && (_plazaCtl == null || !SceneLoader.ActiveScene.StartsWith("Map_"))) LeavePlace();
            if (Where == Place.None) return;

            // Settings changed: a different playlist (or none) for this place starts now.
            if (_seenVersion != Store.Version)
            {
                _seenVersion = Store.Version;
                var want = Resolve(out string from);
                if (want != Active && !StoppedByGame && (Current != null || want != null) && (Where == Place.Plaza ? _plazaThemeIdx >= 0 : _gameStageIdx >= 0))
                {
                    if (want == null) HandBack("");
                    else { Active = want; ActiveFrom = from; Suspended = false; var t = NextTrack(want, +1, out _); if (t != null) Play(t); }
                }
                else if (want == Active) ActiveFrom = from;
            }

            if (Active == null || Current == null) return;
            // The game stopped the place's music (results, a retry): wait for its next music request.
            if (StoppedByGame) { if (CurrentIsCustom && CustomAudio.IsPlaying) CustomAudio.Stop(); return; }
            var sm = GameRefs.Sound;
            // Keep the game's own shuffle (jukebox play mode) from changing songs under the playlist.
            if (sm != null && Where == Place.Stage) { try { sm._unreleased_noShuffle = true; _forcedNoShuffle = true; } catch { } }
            if (Suspended) return;

            float dt = Time.unscaledDeltaTime;
            var ch1 = ChannelSource(1);
            bool playing = SourcePlaying(ch1, out bool paused, out bool stopped);

            bool hold = GameHoldsMusic();
            if (!CurrentIsCustom)
            {
                // Your pause holds even when the game unpauses its channel (pause menu closed, Timer over).
                if (_userPaused && playing && !hold) { _ours = true; try { Ctr?.Pause(SoundController_CRI.eChannel.BGM_INSTAGE); } finally { _ours = false; } playing = false; }
                else if (!_userPaused && paused && !stopped && !hold && !GameEvents.Paused && Time.unscaledTime < _resumeWatchUntil)
                {
                    SongSelectorMod.Log.Info("A late pause cue held the song after Play: resumed again.");
                    ResumeStageChannel();
                }
                // A game song's clock follows what's heard (the game itself un-pauses its music when the pause menu closes,
                // even while the Timer still stops the clock: measured).
                if (playing) Position += dt;
                if (playing) _carrierSeen = true;
                else if (stopped && _carrierSeen && Where == Place.Stage) { StoppedByGame = true; Status = "The game stopped the music (results or leaving)."; return; }
                if (Length > 0 && Position >= Length) SongEnded();
                return;
            }

            // Your file.
            TrackRef.IsFile(Current, out string rel);
            string err = CustomAudio.ErrorOf(rel);
            if (err != null) { Status = $"Can't play {rel}: {err}"; SongEnded(); return; }
            if (!CustomAudio.IsReady(rel)) return;
            if (CustomAudio.Loaded != rel) { CustomAudio.Load(rel); _customStarted = false; }
            if (Length <= 0) Length = CustomAudio.Length;

            bool carrierOk;
            if (Where == Place.Stage)
            {
                // The carrier stays silent; your file does what the game does to it.
                if (ch1 != null) { try { ch1.volume = 0f; } catch { } }
                if (playing) _carrierSeen = true;
                if (stopped && _carrierSeen && _fadeLeft < 0f) { CustomAudio.Stop(); StoppedByGame = true; Status = "The game stopped the music (results or leaving)."; return; }
                carrierOk = playing && !hold;
            }
            else carrierOk = true;
            if (_userPaused) carrierOk = false;

            float vol = Store.Settings.CustomVolume * GameVolume();
            if (_fadeLeft >= 0f)
            {
                _fadeLeft -= dt;
                vol *= Mathf.Clamp01(_fadeLeft / _fadeTotal);
                if (_fadeLeft <= 0f) { CustomAudio.Stop(); _fadeLeft = -1f; StoppedByGame = true; Status = "The game stopped the music."; return; }
            }
            CustomAudio.SetVolume(vol);

            if (carrierOk && !_customStarted) { CustomAudio.Play(); _customStarted = true; _lastFileTime = 0f; }
            else if (carrierOk && _customStarted && !CustomAudio.IsPlaying && !_pausedByCarrier)
            {
                if (_lastFileTime >= Length - 0.25f)
                {
                    // Reached its end.
                    Position = Length;
                    SongEnded();
                    return;
                }
                // Stopped by something else before its end: go on from where it was.
                CustomAudio.Play();
                CustomAudio.SetTime(_lastFileTime);
            }
            else if (!carrierOk && _customStarted && CustomAudio.IsPlaying) { CustomAudio.Pause(); _pausedByCarrier = true; }
            else if (carrierOk && _pausedByCarrier) { CustomAudio.UnPause(); _pausedByCarrier = false; }
            if (CustomAudio.IsPlaying) _lastFileTime = CustomAudio.Time;
            Position = CustomAudio.Time;
        }

        private static bool _pausedByCarrier;
        private static float _lastFileTime;
        /// <summary>The game paused its stage music with SoundManager.PauseBGMInStage (an action cue, not a CRI pause).</summary>
        private static bool _gameMusicPaused;
        private static bool _userPaused;

        /// <summary>The Timer freebie's stopped clock is on (MainGameManager._itemEffectTimer counting down for Timer / SP_Timer).</summary>
        public static bool TimerRunning
        {
            get
            {
                try
                {
                    var gm = GameRefs.Main;
                    if (gm == null || GameRefs.IsMultiplay) return false;
                    var it = gm._nowInventoryItem;
                    return (it == eInstageItemType.Timer || it == eInstageItemType.SP_Timer) && gm._itemEffectTimer > 0f;
                }
                catch { return false; }
            }
        }

        /// <summary>The game holds its stage music: paused with PauseBGMInStage, or the Timer freebie's clock stop.</summary>
        private static bool GameHoldsMusic() => Where == Place.Stage && (_gameMusicPaused || TimerRunning);

        /// <summary>You paused the playlist (Pause control). The game's own pauses come on top of it.</summary>
        public static bool UserPaused => _userPaused;

        /// <summary>Pauses the playlist's music here (until Play).</summary>
        public static string PauseMusic()
        {
            if (!CanControl) throw new InvalidOperationException("No playlist is playing here.");
            if (_userPaused) return "Already paused.";
            _userPaused = true;
            if (CurrentIsCustom) { if (CustomAudio.IsPlaying) { CustomAudio.Pause(); _pausedByCarrier = true; } }
            else { _ours = true; try { Ctr?.Pause(SoundController_CRI.eChannel.BGM_INSTAGE); } finally { _ours = false; } }
            return "Music paused.";
        }

        /// <summary>Plays: carries on after Pause, or starts the playlist chosen for this place.</summary>
        public static string PlayMusic()
        {
            if (Where == Place.None) throw new InvalidOperationException("Go to a story stage or the Plaza first.");
            if (_userPaused && Active != null && !StoppedByGame)
            {
                _userPaused = false;
                // The game's own pauses stay: the pause menu (it unpauses the channel itself) and the Timer.
                if (!CurrentIsCustom && !GameEvents.Paused && !GameHoldsMusic())
                {
                    // Like the game's own resume (UnpauseBGMInStage = UnPause + the Action_Resume cue). A pause cue the game
                    // played while you had paused (pause menu: Action_Pause) can still land a moment later (measured: 0.25 s),
                    // so for a second the stage channel is resumed again if it goes quiet with nothing holding it.
                    ResumeStageChannel();
                    _resumeWatchUntil = Time.unscaledTime + 1.2f;
                }
                return "Music playing.";
            }
            if (Active != null && !StoppedByGame) return "Already playing.";
            if (_playNow == GameMusic && _playNowPlace == Where) _playNow = null;
            var p = Resolve(out string from);
            if (p == null) throw new InvalidOperationException("No playlist is chosen for this place.");
            _userPaused = false;
            return PlayNow(p);
        }

        private static float _resumeWatchUntil;

        private static void ResumeStageChannel()
        {
            _ours = true;
            try { Ctr?.UnpauseBGMInStage(); }
            catch { }
            finally { _ours = false; }
        }

        public static string TogglePlayPause() => _userPaused || Active == null ? PlayMusic() : PauseMusic();

        /// <summary>Repeat for the playlist playing here (saved with the playlist).</summary>
        public static string SetRepeat(RepeatMode mode)
        {
            if (Active == null) throw new InvalidOperationException("No playlist is playing here.");
            Active.Repeat = mode; Store.Save();
            return mode switch { RepeatMode.All => "Loop: the whole playlist.", RepeatMode.One => "Loop: this song.", _ => "Loop off: the playlist plays once." };
        }

        /// <summary>Loop the playlist on / off.</summary>
        public static string ToggleLoop() => SetRepeat(Active != null && Active.Repeat == RepeatMode.All ? RepeatMode.Off : RepeatMode.All);

        /// <summary>Loop the song playing on / off (off = loop the playlist).</summary>
        public static string ToggleLoopOne() => SetRepeat(Active != null && Active.Repeat == RepeatMode.One ? RepeatMode.All : RepeatMode.One);

        /// <summary>Shuffle the playlist playing here on / off (saved with the playlist; the song playing goes on).</summary>
        public static string SetShuffle(bool on)
        {
            if (Active == null) throw new InvalidOperationException("No playlist is playing here.");
            Active.Shuffle = on; Store.Save();
            return on ? "Shuffle on." : "Shuffle off.";
        }

        public static string ToggleShuffle() => SetShuffle(Active == null || !Active.Shuffle);
        /// <summary>A song this mod started may still be on the stage-music channel (it isn't stopped by the game's next PlayBGMInStage).</summary>
        private static bool _ownsStageChannel;

        /// <summary>Stops the stage-music channel now (cancels a fade still running on it too).</summary>
        private static void StopStageChannelNow()
        {
            _ours = true;
            try { Ctr?.Stop(SoundController_CRI.eChannel.BGM_INSTAGE, 0f); }
            catch { }
            finally { _ours = false; _ownsStageChannel = false; }
        }
        private static bool? _gameNoShuffle;
        private static bool _forcedNoShuffle;

        /// <summary>The jukebox shuffle is held off while a playlist plays in a stage; this gives the game its own setting back.</summary>
        private static void GiveBackShuffle()
        {
            if (!_forcedNoShuffle) return;
            _forcedNoShuffle = false;
            try { var sm = GameRefs.Sound; if (sm != null && _gameNoShuffle.HasValue) sm._unreleased_noShuffle = _gameNoShuffle.Value; } catch { }
        }
        private static float _stopInStage;

        /// <summary>Test only: jumps the current song to <paramref name="seconds"/> (game songs restart there with the CRI start time).</summary>
        internal static void SeekForTest(double seconds)
        {
            if (Current == null) return;
            if (CurrentIsCustom) { CustomAudio.SetTime((float)seconds); Position = seconds; return; }
            StartOnStageChannel(_currentGameIdx, (int)(seconds * 1000));
            Position = seconds;
        }

        internal static int CurrentGameIndex => _currentGameIdx;
        internal static int GameStageIndex => _gameStageIdx;
        internal static bool? GameNoShuffle => _gameNoShuffle;

        /// <summary>The game's music volume as a 0..1 factor (Options: BGM and master, 0-10 each).</summary>
        /// <summary>Test only: the volume factor for your files, regardless of the game's settings (null = the settings).</summary>
        internal static float? TestVolume;

        public static float GameVolume()
        {
            if (TestVolume.HasValue) return TestVolume.Value;
            try
            {
                var s = GameRefs.Save;
                if (s == null) return 0.8f;
                return Mathf.Clamp01(s.GetBGMVolumeSetting() / 10f) * Mathf.Clamp01(s.GetMasterVolumeSetting() / 10f);
            }
            catch { return 0.8f; }
        }
    }
}
