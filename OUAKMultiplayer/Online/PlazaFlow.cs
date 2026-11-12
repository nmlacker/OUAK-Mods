using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Il2Cpp;
using Il2CppApp.KatamariSin;
using Il2CppApp.KatamariSin.InputStatus;
using MelonLoader;
using UnityEngine;
using OUAKModAPI;

namespace OUAKMultiplayer
{
    /// <summary>Where a stage was picked in a plaza: the era (GlobalSaveData._stageIndex), the fan (SelectHirobaObjectBase._objectId) and the page of its stage screen.</summary>
    internal struct PlazaSpot
    {
        public int Plaza, Object, Page;

        /// <summary>Packed into a vote's second argument. The top bit marks it as set; 0 means no spot (a stage picked from the menu).</summary>
        public int Pack() => int.MinValue | (Plaza & 0xFF) | ((Page & 0xF) << 8) | ((Object & 0x7FFFF) << 12);

        public static bool TryUnpack(int v, out PlazaSpot s)
        {
            s = new PlazaSpot { Plaza = v & 0xFF, Page = (v >> 8) & 0xF, Object = (v >> 12) & 0x7FFFF };
            return v < 0;
        }

        public override string ToString() => $"plaza {Plaza}, fan {Object}, page {Page}";
    }

    /// <summary>
    /// Presses the game's UI buttons for the mod, and holds back real presses the session handles itself.
    /// Both go through UIInputController.IsDownButton, which the plaza, a fan's stage screen and the King's talk
    /// read. A press of ours lasts exactly one frame: the first frame anything asks for that button, every caller
    /// that frame sees it down. (A fan's speech reads the pad directly, so PlazaFlow skips it instead.)
    /// </summary>
    internal static class VirtualPad
    {
        private static readonly Dictionary<eKeyMap, int> Presses = new();
        private static readonly Dictionary<eKeyMap, float> Armed = new();
        /// <summary>Presses made as the player would (self-test): held back like real ones.</summary>
        private static readonly HashSet<eKeyMap> AsPlayer = new();
        private static int _count;

        public static bool Patched { get; private set; }
        /// <summary>How many of our presses the game has read.</summary>
        public static int Read { get; private set; }

        public static void Init(HarmonyLib.Harmony harmony)
        {
            try
            {
                harmony.Patch(AccessTools.Method(typeof(UIInputController), nameof(UIInputController.IsDownButton)),
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(VirtualPad), nameof(BeforeIsDown))),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(VirtualPad), nameof(AfterIsDown))));
                Patched = true;
            }
            catch (Exception e) { MelonLogger.Warning($"[Online] Plaza button presses unavailable: {e.Message}"); }
        }

        /// <param name="asPlayer">Pressed the way a player would (the self-test): the session may hold it back like a real press.</param>
        public static void Press(eKeyMap key, bool asPlayer = false)
        {
            Presses[key] = -1;
            Armed[key] = Time.unscaledTime;
            if (asPlayer) AsPlayer.Add(key);
            else AsPlayer.Remove(key);
            _count = Presses.Count;
        }

        /// <summary>True while a press of ours (not one made as the player) is down this frame.</summary>
        public static bool IsOurs(eKeyMap key) => _count > 0 && !AsPlayer.Contains(key) && Presses.TryGetValue(key, out int f) && f == Time.frameCount;

        public static void Tick()
        {
            if (_count == 0) return;
            float now = Time.unscaledTime;
            int frame = Time.frameCount;
            foreach (var k in Presses.Keys.ToList())
            {
                int f = Presses[k];
                // Pressed and gone, or never read (nothing asked for that button): drop it.
                if ((f >= 0 && f < frame) || (f < 0 && now - Armed[k] > 2f))
                {
                    Presses.Remove(k);
                    Armed.Remove(k);
                    AsPlayer.Remove(k);
                }
            }
            _count = Presses.Count;
        }

        private static bool BeforeIsDown(eKeyMap __0, ref bool __result)
        {
            if (_count == 0) return true;
            try
            {
                if (!Presses.TryGetValue(__0, out int f)) return true;
                int frame = Time.frameCount;
                if (f < 0)
                {
                    Presses[__0] = f = frame;
                    Read++;
                }
                if (f != frame) return true;
                __result = true;
                return false;
            }
            catch { return true; }
        }

        private static void AfterIsDown(eKeyMap __0, ref bool __result)
        {
            if (!__result || !PlazaFlow.Holding) return;
            try
            {
                if (IsOurs(__0)) return;
                if (PlazaFlow.HoldBack(__0)) __result = false;
            }
            catch { }
        }
    }

    /// <summary>
    /// Picking a stage with a fan in a plaza, in a session: the multiplayer version of the plaza's stage start.
    /// 1. Go on a fan's stage screen doesn't start the stage: the screen closes and the pick becomes a vote (the
    ///    host chooses go now or vote, as with StageGate).
    /// 2. When it passes, everyone is brought to that fan (loading its plaza if they're elsewhere) and the game
    ///    opens its stage screen for them on that stage. They choose their music there (Start opens the music
    ///    list) and see the stage one last time; Go marks them ready.
    /// 3. Once everyone is ready, or 30 s after everyone got there (travelling counts for 20 s at most), the host
    ///    says go. Each game presses Go on its screen, the plaza flies up to the King as usual, and his talk is
    ///    skipped straight to his stage start (KingTalkSwich 99: curtain, then MainGame). Anyone not on the screen
    ///    by then loads the stage directly. From there it runs as before (skip-the-fly-over vote, shared start).
    /// The game's own flow, from its plaza code: walking (SelectHirobaController SELECTHIROBA), Select near a fan
    /// (its SelectHirobaObjectBase._characterAreaIn) opens its speech (UI FANMESSAGE); when that ends,
    /// ActiveTalkUI opens the stage screen (UI SELECTGAME). There Cancel backs out and Go (Select, or a click on
    /// the button) closes the screen, sets UI OUSAMADEMO and _playDemo: the camera flies up to the King, KingDemo,
    /// SelectHirobaKingInitislize (his _selectIndex = the screen's _nowPageIndex), then KingTalkSwich 0, 1, (2), 99.
    /// </summary>
    internal static class PlazaFlow
    {
        public enum Phase { Idle, Gathering, Starting }

        /// <summary>A player's state in a gathering, as the host counts it.</summary>
        public const byte OnTheWay = 0, OnScreen = 1, IsReady = 2, NotComing = 3;
        public const float TravelTime = 20f, MaxGather = 150f;
        /// <summary>How long everyone has once they're all at the stage screen (a lobby setting, 30 s by default).</summary>
        public static float ReadyTime => Lobby.ReadySeconds;
        private const SelectHirobaUIController.UI_STATUS UiWalking = SelectHirobaUIController.UI_STATUS.MAINGAME;
        private const SelectHirobaUIController.UI_STATUS UiFanSpeech = SelectHirobaUIController.UI_STATUS.FANMESSAGE;
        private const SelectHirobaUIController.UI_STATUS UiStageScreen = SelectHirobaUIController.UI_STATUS.SELECTGAME;

        /// <summary>Getting yourself onto one fan's stage screen.</summary>
        private sealed class Target
        {
            public int Object = -1, Stage, Page;
            public float SettleUntil, ApproachAt, PressAt, SkipAt, FanSince, CloseAt;
            public int Approaches;
            public SelectHirobaObjectBase Fan;
            public string Last;
        }

        private sealed class Gathering
        {
            public byte Id;
            public int Key, Stage;
            public PlazaSpot Spot;
            public MapEntry Entry, Plaza;
            public float HostBegan, Deadline, Heard, NextSend, NextBroadcast, LoadAt;
            public bool Go;
            public int GoBroadcasts;
            public byte Mine;
            public readonly Dictionary<byte, byte> States = new();
            public readonly Target Target = new();
        }

        private static Gathering _g;
        private static byte _nextId = 1, _doneId;
        public static Phase Now { get; private set; }

        // Starting: our Go on the screen, the King's start, or a direct load.
        private static float _startAt, _kingUntil;
        private static bool _pressed, _direct, _directDone, _kingStarted;

        // A Go held back by the input hook, acted on at the next tick (never inside the hook).
        private static bool _picked;
        private static PlazaSpot _pickSpot;
        private static int _pickStage;

        private static GUIStyle _title, _line, _small;
        private static Texture2D _panel;
        private static readonly List<string> Events = new();
        private static float _eventsFrom;

        public static string LastPick { get; private set; } = "";
        /// <summary>The stage everyone's getting ready for, or "a stage".</summary>
        public static string StageNow => _g?.Entry?.Name ?? "a stage";
        public static byte MyState => _g?.Mine ?? OnTheWay;
        public static int StartingKey => Now == Phase.Starting && _g != null ? _g.Key : 0;

        public static void Init(HarmonyLib.Harmony harmony) => VirtualPad.Init(harmony);

        /// <summary>Whether the input hook should ask <see cref="HoldBack"/> about real presses.</summary>
        public static bool Holding => NetSession.Connected && (Now != Phase.Idle || NetSession.Peers.Count > 0);

        // ---------------------------------------------------------------- the input hook

        /// <summary>
        /// A real press of a button the plaza reads (from the hook, after the game's own check said it's down).
        /// True holds it back. Idle: Go on a fan's stage screen becomes a pick for the session. Gathering: Go on
        /// the screen marks you ready, and Back stays on it. Starting: no Go of your own, and no backing out of
        /// the King's flight.
        /// </summary>
        public static bool HoldBack(eKeyMap key)
        {
            bool go = key == eKeyMap.Select || key == eKeyMap.MouseLeft;
            bool back = key == eKeyMap.Cancel || key == eKeyMap.MouseRight;
            if (!go && !back) return false;
            switch (Now)
            {
                case Phase.Idle:
                {
                    if (!go || _picked || !TakesInput(out var mgr, out var talk)) return false;
                    if (!TryPick(mgr, talk, out var spot, out int stage)) return false;
                    _picked = true;
                    _pickSpot = spot;
                    _pickStage = stage;
                    return true;
                }
                case Phase.Gathering:
                {
                    if (!TakesInput(out _, out var talk) || _g == null) return false;
                    // Another fan's screen: no other stage while this one's getting ready (Back still leaves it).
                    if (talk._objectID != _g.Spot.Object) return go;
                    if (go && _g.Mine < IsReady)
                    {
                        _g.Mine = IsReady;
                        _g.NextSend = 0f;
                        Note("ready");
                    }
                    return true;
                }
                default:
                    return back || TakesInput(out _, out _);
            }
        }

        /// <summary>A fan's stage screen is up and its own buttons work: what the plaza's SELECTGAME state checks before Go and Back.</summary>
        private static bool TakesInput(out SelectHirobaManager mgr, out SelectHirabaTalkOptionController talk)
        {
            if (!OnStageScreen(out mgr, out talk)) return false;
            var fav = mgr._uIController._favoriteBgmActivator;
            return fav == null || !fav.favoriteBgmActiveFlg;   // the music list has the buttons while it's open
        }

        /// <summary>A fan's stage screen is up (its music list may be open over it).</summary>
        private static bool OnStageScreen(out SelectHirobaManager mgr, out SelectHirabaTalkOptionController talk)
        {
            talk = null;
            mgr = SingletonMonoBehaviour<SelectHirobaManager>.instance;
            if (mgr == null || GameRefs.Main != null) return false;
            var ui = mgr._uIController;
            if (ui == null || ui._uiStaus != UiStageScreen) return false;
            talk = ui._talkOptionController;
            return talk != null && talk.GetActive;
        }

        private static bool MusicListOpen(SelectHirobaManager mgr)
        {
            var fav = mgr?._uIController?._favoriteBgmActivator;
            return fav != null && fav.favoriteBgmActiveFlg;
        }

        /// <summary>The music list (the plaza's sound gallery, opened with Start on a stage screen) is open.</summary>
        public static bool MusicOpen
        {
            get
            {
                try { return MusicListOpen(SingletonMonoBehaviour<SelectHirobaManager>.instance); }
                catch { return false; }
            }
        }

        /// <summary>The music list has finished opening and takes its buttons (its manager's UPDATE / WAIT state, which its Back needs).</summary>
        public static bool MusicReady
        {
            get
            {
                try
                {
                    var fav = SingletonMonoBehaviour<SelectHirobaManager>.instance?._uIController?._favoriteBgmActivator;
                    int s = fav?.favoriteBgmManager != null ? (int)fav.favoriteBgmManager.favoriteBgmState : -1;
                    return fav != null && fav.favoriteBgmActiveFlg && (s == 6 || s == 7) && (int)fav.dialogSate == 0;
                }
                catch { return false; }
            }
        }

        public static string DescribeMusic()
        {
            try
            {
                var fav = SingletonMonoBehaviour<SelectHirobaManager>.instance?._uIController?._favoriteBgmActivator;
                if (fav == null) return "no music list here";
                var mgr = fav.favoriteBgmManager;
                return $"{(fav.favoriteBgmActiveFlg ? "open" : "closed")}, list state {(mgr != null ? mgr.favoriteBgmState.ToString() : "?")}, " +
                       $"dialog {fav.dialogSate}, confirm {fav.IsConfirmDialog()}, stage's own {fav.isKadai}";
            }
            catch (Exception e) { return "? " + e.Message; }
        }

        private static bool TryPick(SelectHirobaManager mgr, SelectHirabaTalkOptionController talk, out PlazaSpot spot, out int stage)
        {
            spot = default;
            stage = 0;
            // Another Adventure's map has its own start (AADemo); the ending's fan (40) its own too.
            if ((int)mgr.GetMap == 11) return false;
            int obj = talk._objectID;
            if (obj == 40) return false;
            var ids = talk._idStages;
            int page = talk._nowPageIndex;
            if (ids == null || page < 0 || page >= ids.Length) return false;
            stage = ids[page];
            if (EntryFor(stage) == null) return false;
            int plaza = PlazaIndex();
            if (plaza < 0 || plaza > 255) return false;
            spot = new PlazaSpot { Plaza = plaza, Object = obj, Page = page };
            return true;
        }

        /// <summary>The spot of the stage screen you were just on, packed (0 if there's none): for a King's start that wasn't picked through the screen.</summary>
        public static int SpotNow(int stage)
        {
            try
            {
                var mgr = SingletonMonoBehaviour<SelectHirobaManager>.instance;
                var talk = mgr?._uIController?._talkOptionController;
                if (talk == null || (int)mgr.GetMap == 11) return 0;
                var ids = talk._idStages;
                int page = talk._nowPageIndex;
                if (ids == null || page < 0 || page >= ids.Length || ids[page] != stage) return 0;
                return new PlazaSpot { Plaza = PlazaIndex(), Object = talk._objectID, Page = page }.Pack();
            }
            catch { return 0; }
        }

        // ---------------------------------------------------------------- per frame

        public static void Tick()
        {
            VirtualPad.Tick();
            if (!NetSession.Connected)
            {
                if (_g != null || Now != Phase.Idle) Reset("you left the session");
                _picked = false;
                return;
            }
            if (_picked) HandlePick();
            if (_g == null) return;
            float now = Time.unscaledTime;
            if (NetSession.Mode == NetSession.Role.Host) HostTick(now);
            else ClientTick(now);
            if (_g == null) return;
            if (Now == Phase.Gathering) GatherTick(now);
            else if (Now == Phase.Starting) StartTick(now);
        }

        private static void HandlePick()
        {
            _picked = false;
            var entry = EntryFor(_pickStage);
            if (entry == null) return;
            var spot = _pickSpot;
            LastPick = $"{entry.Name} ({spot})";
            bool host = NetSession.Mode == NetSession.Role.Host;
            // A vote can't start now: stay on the screen and say why.
            string blocked = host
                ? (StageGate.ChoiceOpen ? "Choose go now or vote for your last pick first." : null)
                : Votes.Open ? "A vote is already running."
                : Votes.CooldownLeft > 0f ? $"Your last vote didn't pass: you can start another in {Mathf.CeilToInt(Votes.CooldownLeft)} s."
                : null;
            if (blocked != null)
            {
                Note($"Go on {entry.Name}'s stage screen held: {blocked}");
                ModTasks.Toast(blocked, true);
                return;
            }
            Note($"Go on {entry.Name}'s stage screen, held for the session ({spot})");
            // Back out of the screen the game's own way, then the session decides.
            VirtualPad.Press(eKeyMap.Cancel);
            if (host) StageGate.OpenFromPlaza(entry, spot);
            else ModTasks.Run(() => Votes.Propose(VoteKind.GoToStage, Ghosts.KeyFor(entry), spot.Pack()), toast: true);
        }

        // ---------------------------------------------------------------- host

        /// <summary>Host: a stage picked with a fan is on (a passed vote, or go now): everyone gathers at that fan's stage screen.</summary>
        public static string HostBeginGather(MapEntry entry, PlazaSpot spot)
        {
            if (NetSession.Mode != NetSession.Role.Host) return "Only the host starts that.";
            byte id = _nextId;
            _nextId = (byte)(_nextId == 255 ? 1 : _nextId + 1);
            var g = Begin(id, entry, spot);
            if (g == null) return $"Couldn't find the plaza for {entry.Name}.";
            g.HostBegan = SyncRun.HostNow;
            BroadcastGather();
            return $"Everyone's heading to {entry.Name}'s stage screen to pick their music.";
        }

        public static void HostOnState(byte who, byte id, byte state)
        {
            var g = _g;
            if (NetSession.Mode != NetSession.Role.Host || g == null || g.Id != id || state > NotComing) return;
            if (!g.States.TryGetValue(who, out var old) || old != state)
            {
                g.States[who] = state;
                Note($"{Votes.NameOf(who)}: {StateText(state)}");
            }
        }

        private static void HostTick(float now)
        {
            var g = _g;
            g.States[NetSession.MyId] = g.Mine;
            foreach (byte id in g.States.Keys.ToList())
                if (id != NetSession.MyId && !NetSession.Peers.ContainsKey(id)) g.States.Remove(id);
            foreach (byte id in NetSession.Peers.Keys)
                if (!g.States.ContainsKey(id)) g.States[id] = OnTheWay;
            float hostNow = SyncRun.HostNow;
            if (!g.Go)
            {
                var coming = g.States.Values.Where(s => s != NotComing).ToList();
                bool arrived = coming.All(s => s >= OnScreen);
                bool ready = coming.All(s => s == IsReady);
                if (g.Deadline <= 0f && (arrived || hostNow - g.HostBegan >= TravelTime))
                {
                    g.Deadline = hostNow + ReadyTime;
                    g.NextBroadcast = 0f;
                    Note(arrived ? $"everyone's at the stage screen: {ReadyTime:0} s to get ready" : $"still waiting for some: {ReadyTime:0} s from now");
                }
                if (ready || (g.Deadline > 0f && hostNow >= g.Deadline) || hostNow - g.HostBegan >= MaxGather)
                {
                    g.Go = true;
                    g.NextBroadcast = 0f;
                    Note(ready ? "everyone's ready: go" : "time's up: go");
                }
            }
            if (now >= g.NextBroadcast && (!g.Go || g.GoBroadcasts < 12))
            {
                g.NextBroadcast = now + (g.Go ? 0.25f : 1f);
                BroadcastGather();
                if (g.Go) g.GoBroadcasts++;
            }
            if (g.Go && Now == Phase.Gathering) BeginStart(now);
        }

        private static void BroadcastGather()
        {
            var g = _g;
            if (g == null) return;
            var w = Proto.Begin(Msg.Gather, NetSession.MyId).U8(g.Id).I32(g.Key).I32(g.Spot.Pack()).F32(g.Deadline)
                .U8((byte)(g.Go ? 1 : 0)).U8((byte)Math.Min(g.States.Count, NetSession.MaxPlayers));
            foreach (var kv in g.States.Take(NetSession.MaxPlayers)) w.U8(kv.Key).U8(kv.Value);
            NetSession.Broadcast(w);
        }

        // ---------------------------------------------------------------- clients

        public static void ClientOnGather(byte id, int key, int spotPacked, float deadline, bool go, List<(byte id, byte state)> states)
        {
            if (NetSession.Mode != NetSession.Role.Client || !PlazaSpot.TryUnpack(spotPacked, out var spot)) return;
            if (_g == null || _g.Id != id)
            {
                // A gathering that's over here already (the host keeps sending go for a moment).
                if (id == _doneId) return;
                var entry = StageCatalog.Entries.FirstOrDefault(e => e.Kind == MapKind.Stage && Ghosts.KeyFor(e) == key);
                if (entry == null) return;
                if (Begin(id, entry, spot) == null) return;
            }
            var g = _g;
            g.Heard = Time.unscaledTime;
            g.Deadline = deadline;
            g.States.Clear();
            foreach (var (pid, st) in states) g.States[pid] = st;
            if (go && !g.Go)
            {
                g.Go = true;
                if (Now == Phase.Gathering) BeginStart(Time.unscaledTime);
            }
        }

        private static void ClientTick(float now)
        {
            var g = _g;
            if (now - g.Heard > 10f && Now == Phase.Gathering)
            {
                Reset("the host stopped the gathering");
                return;
            }
            if (now >= g.NextSend)
            {
                g.NextSend = now + 1f;
                NetSession.SendToHost(Proto.Begin(Msg.GatherState, NetSession.MyId).U8(g.Id).U8(g.Mine));
            }
        }

        // ---------------------------------------------------------------- everyone

        private static Gathering Begin(byte id, MapEntry entry, PlazaSpot spot)
        {
            var plaza = PlazaFor(spot.Plaza);
            if (plaza == null) return null;
            if (_g != null) Reset("a new gathering");
            _g = new Gathering
            {
                Id = id, Entry = entry, Key = Ghosts.KeyFor(entry), Stage = (int)entry.Stage, Spot = spot, Plaza = plaza,
                Heard = Time.unscaledTime,
            };
            _g.Target.Object = spot.Object;
            _g.Target.Stage = (int)entry.Stage;
            _g.Target.Page = spot.Page;
            bool coming = NetSession.Mode == NetSession.Role.Host || NetSession.FollowHost;
            _g.Mine = coming && GameRefs.InGameSession ? OnTheWay : NotComing;
            Now = Phase.Gathering;
            _pressed = _direct = _directDone = _kingStarted = false;
            Events.Clear();
            _eventsFrom = Time.unscaledTime;
            Note($"gathering for {entry.Name} at {spot}");
            ModTasks.Toast(_g.Mine == NotComing
                ? $"The others are getting ready for {entry.Name} (you don't follow the host's stages)."
                : $"Getting ready for {entry.Name}: pick your music, then press Go.");
            return _g;
        }

        public static void Reset(string why)
        {
            if (_g != null) Note($"ended: {why}");
            if (_g != null) _doneId = _g.Id;
            _g = null;
            Now = Phase.Idle;
            _pressed = _direct = _directDone = _kingStarted = false;
        }

        private static void GatherTick(float now)
        {
            var g = _g;
            if (g.Mine == NotComing) return;
            if (!InPlaza(g.Spot.Plaza, out var mgr))
            {
                g.Target.SettleUntil = now + 2f;
                // Somewhere else (a stage, the ship, another plaza): go to that plaza first.
                if (now >= g.LoadAt && SceneSettled())
                {
                    g.LoadAt = now + 25f;
                    Note($"loading the {g.Plaza.Name} plaza");
                    var plaza = g.Plaza;
                    ModTasks.Run(() => StageGate.Approved(() => SceneLoader.LoadPlaza(plaza)), toast: true);
                }
                return;
            }
            bool on = DriveTo(g.Target, mgr, now, lockPage: true);
            if (on && g.Mine == OnTheWay)
            {
                g.Mine = OnScreen;
                g.NextSend = 0f;
                Note("on the stage screen");
            }
        }

        /// <summary>
        /// Gets you onto the target fan's stage screen, a step at a time: talk to the fan (stand where the game
        /// puts you for its talk, let only that fan be in reach, press Select), skip its speech, then keep the
        /// screen on the target's stage. True while you're on it.
        /// </summary>
        private static bool DriveTo(Target t, SelectHirobaManager mgr, float now, bool lockPage)
        {
            var ui = mgr._uIController;
            var ctrl = mgr._hirobaController;
            if (ui == null || ctrl == null) return false;
            var state = ui._uiStaus;
            if (state == UiStageScreen)
            {
                var talk = ui._talkOptionController;
                if (talk != null && talk.GetActive)
                {
                    t.FanSince = 0f;
                    // Someone else's screen: back out of it first.
                    if (t.Object >= 0 && talk._objectID != t.Object)
                    {
                        if (now >= t.CloseAt && TakesInput(out _, out _))
                        {
                            t.CloseAt = now + 1.5f;
                            Step(t, $"on fan {talk._objectID}'s screen, not {t.Object}'s: backing out");
                            VirtualPad.Press(eKeyMap.Cancel);
                        }
                        return false;
                    }
                    if (lockPage) LockPage(talk, t);
                    return true;
                }
            }
            if (state == UiFanSpeech)
            {
                SkipFanSpeech(t, ctrl, ui, now);
                return false;
            }
            t.FanSince = 0f;
            if (now < t.SettleUntil) return false;
            // Walking: talk to the fan.
            if (ctrl._gameState != SelectHirobaController.GameState.SELECTHIROBA || state != UiWalking) return false;
            if (t.Fan != null && t.PressAt > 0f && now >= t.PressAt)
            {
                t.PressAt = 0f;
                PressFan(t, mgr);
                return false;
            }
            if (now < t.ApproachAt) return false;
            t.ApproachAt = now + 3f;
            if (t.Approaches >= 6)
            {
                if (t.Approaches++ == 6) Step(t, "couldn't open the fan's stage screen");
                return false;
            }
            t.Approaches++;
            var fan = FindFan(mgr._selectHirobaDataManager, t.Object, t.Stage);
            if (fan == null)
            {
                Step(t, $"no fan with {StageName(t.Stage)} here");
                return false;
            }
            t.Fan = fan;
            if (t.Object < 0) t.Object = fan._objectId;
            // Stand where the game puts you to talk to this fan; press Select a moment later.
            try
            {
                var move = ctrl._characterManager?.GetCharacterController?.GetCharacter;
                var back = fan.BackPosition;
                if (move != null && back.sqrMagnitude > 0.01f) move.SetCharacterPosition = back;
            }
            catch (Exception e) { Step(t, "couldn't move you to the fan: " + e.Message); }
            t.PressAt = now + 0.4f;
            Step(t, $"walking up to fan {fan._objectId} ({fan.ObjectName}), try {t.Approaches}");
            return false;
        }

        private static void PressFan(Target t, SelectHirobaManager mgr)
        {
            try
            {
                // Only this fan in reach, so the plaza's Select opens its talk (it takes the first fan it finds in reach).
                var list = mgr._selectHirobaDataManager?._objects;
                if (list != null)
                    for (int i = 0; i < list.Count; i++)
                    {
                        var o = list[i];
                        if (o != null) o._characterAreaIn = o.Pointer == t.Fan.Pointer;
                    }
                VirtualPad.Press(eKeyMap.Select);
                Step(t, "pressed Select at the fan");
            }
            catch (Exception e) { Step(t, "couldn't talk to the fan: " + e.Message); }
        }

        /// <summary>The fan's speech, skipped the way the plaza's own hold-to-skip does it (KingMessageFade.ForcedReset, FanMessageCreate.TerminateText, IsSkip).</summary>
        private static void SkipFanSpeech(Target t, SelectHirobaController ctrl, SelectHirobaUIController ui, float now)
        {
            if (t.FanSince == 0f) t.FanSince = now;
            if (now - t.FanSince < 0.8f || now < t.SkipAt) return;
            // The plaza waits for the camera to reach the fan before it runs the speech.
            if (ctrl._camera != null && !ctrl._camera.CameraMoveCompletion) return;
            t.SkipAt = now + 2f;
            try
            {
                var fan = ui._fanMessageController;
                fan?.GetKingMessageFade?.ForcedReset();
                fan?.GetMessageCreate?.TerminateText(true);
                ui._talkOptionController?._skipTalkObjectController?.GetSkipObject?.SkipActiveChange(false);
                var speech = ctrl._selectHirobaFanMessageController;
                if (speech != null) speech.IsSkip = true;
                Step(t, "skipped the fan's speech");
            }
            catch (Exception e) { Step(t, "couldn't skip the fan's speech: " + e.Message); }
        }

        /// <summary>The screen stays on the target's stage (the King starts the screen's page).</summary>
        private static void LockPage(SelectHirabaTalkOptionController talk, Target t)
        {
            int want = PageOf(talk, t.Stage);
            if (want < 0) want = t.Page;
            if (want >= 0 && talk._nowPageIndex != want) talk._nowPageIndex = want;
        }

        private static int PageOf(SelectHirabaTalkOptionController talk, int stage)
        {
            var ids = talk._idStages;
            if (ids == null) return -1;
            for (int i = 0; i < ids.Length; i++)
                if (ids[i] == stage) return i;
            return -1;
        }

        private static SelectHirobaObjectBase FindFan(SelectHirobaDataManager data, int objectId, int stage)
        {
            var list = data?._objects;
            if (list == null) return null;
            SelectHirobaObjectBase byId = null, byStage = null;
            for (int i = 0; i < list.Count; i++)
            {
                var o = list[i];
                if (o == null || !o._isRelease) continue;
                if (objectId >= 0 && o._objectId == objectId) byId = o;
                if (byStage == null && HasStage(o, stage)) byStage = o;
            }
            return byId ?? byStage;
        }

        private static bool HasStage(SelectHirobaObjectBase o, int stage)
        {
            if (o._stageID == stage) return true;
            var ids = o._stagesID;
            if (ids == null) return false;
            for (int i = 0; i < ids.Length; i++)
                if (ids[i] == stage) return true;
            return false;
        }

        // ---------------------------------------------------------------- go

        private static void BeginStart(float now)
        {
            var g = _g;
            Now = Phase.Starting;
            _startAt = now;
            _pressed = _direct = _directDone = _kingStarted = false;
            if (g.Mine == NotComing)
            {
                Reset("not coming along");
                return;
            }
            Note("go");
        }

        private static void StartTick(float now)
        {
            var g = _g;
            if (GameRefs.Main != null && Ghosts.CurrentMapKey == g.Key)
            {
                Note($"in {g.Entry.Name} ({now - _startAt:0.0} s after go)");
                Reset("started");
                return;
            }
            if (!_pressed && !_direct)
            {
                if (OnStageScreen(out var mgr, out var talk) && talk._objectID == g.Spot.Object && !MusicListOpen(mgr))
                {
                    LockPage(talk, g.Target);
                    _pressed = true;
                    _kingUntil = now + 20f;
                    VirtualPad.Press(eKeyMap.Select);
                    Note("pressed Go on the stage screen");
                }
                // Not on the screen (still on the way, or in the music list a few seconds on): straight to the stage.
                else if (now - _startAt > (MusicListOpen(mgr) ? 5f : 0.5f))
                {
                    _direct = true;
                    Note("not on the stage screen: loading the stage directly");
                }
            }
            // The King's start never came (the screen didn't take the Go): load it directly.
            if (_pressed && !_direct && !_kingStarted && now - _startAt > 10f)
            {
                _direct = true;
                Note("the King's stage start didn't come: loading the stage directly");
            }
            if (_direct && !_directDone && SceneSettled())
            {
                _directDone = true;
                var entry = g.Entry;
                ModTasks.Run(() => StageGate.Approved(() => SceneLoader.LoadStage(entry)), toast: true);
            }
            if (now - _startAt > 45f) Reset("the stage never started");
        }

        /// <summary>
        /// From StageGate's hook on SelectHirobaKingController.KingTalkSwich. While our Go is under way, his talk
        /// is skipped straight to his stage start (99), on our stage. Null when it isn't ours.
        /// </summary>
        public static bool? KingTalk(SelectHirobaKingController king, int state)
        {
            if (Now != Phase.Starting || _g == null || !_pressed || Time.unscaledTime > _kingUntil) return null;
            if (state != 99)
            {
                // Once his stage start has run (it loads MainGame and moves on to 100), leave him be.
                if (_kingStarted || state > 99) return null;
                king._talkState = 99;
                Note($"skipped the King's talk (at its step {state})");
                return false;
            }
            if (!_kingStarted)
            {
                _kingStarted = true;
                var stages = king._stages;
                if (stages != null)
                    for (int i = 0; i < stages.Length; i++)
                        if (stages[i] == _g.Stage) { king._selectIndex = i; break; }
                Note("the King's stage start (curtain, then the stage)");
            }
            // He waits for his text to finish before starting anything.
            var text = king.GetKingTextController;
            if (text != null && text.GetKingText) king.OusaDemoBack();
            return true;
        }

        // ---------------------------------------------------------------- helpers

        private static bool InPlaza(int index, out SelectHirobaManager mgr)
        {
            mgr = SingletonMonoBehaviour<SelectHirobaManager>.instance;
            return mgr != null && GameRefs.Main == null && PlazaIndex() == index;
        }

        private static int PlazaIndex()
        {
            try { return GameRefs.Save != null ? GameRefs.Save._stageIndex : -1; }
            catch { return -1; }
        }

        /// <summary>Somewhere a load can start from (not halfway through another).</summary>
        private static bool SceneSettled()
        {
            if (GameRefs.Main != null || SingletonMonoBehaviour<SelectHirobaManager>.instance != null) return true;
            string scene = SceneLoader.ActiveScene;
            return scene == "OujiShip";
        }

        private static MapEntry EntryFor(int stage) => StageCatalog.Entries.FirstOrDefault(e => e.Kind == MapKind.Stage && (int)e.Stage == stage);

        private static MapEntry PlazaFor(int index)
        {
            foreach (var e in StageCatalog.Entries)
            {
                if (e.Kind != MapKind.Plaza || string.IsNullOrEmpty(e.PlazaKey)) continue;
                try { if (SelectHirobaManager.GetStageIndex(e.PlazaKey) == index) return e; } catch { }
            }
            return null;
        }

        private static string StageName(int stage) => EntryFor(stage)?.Name ?? $"stage {stage}";

        public static string StateText(byte s) => s switch
        {
            OnTheWay => "on the way",
            OnScreen => "choosing music",
            IsReady => "ready",
            _ => "not coming",
        };

        private static void Step(Target t, string what)
        {
            if (t.Last == what) return;
            t.Last = what;
            Note(what);
        }

        private static void Note(string what)
        {
            string line = $"+{Time.unscaledTime - _eventsFrom:0.0}s {what}";
            Events.Add(line);
            if (Events.Count > 40) Events.RemoveAt(0);
            MelonLogger.Msg("[Online] Plaza: " + what);
        }

        public static string Timeline() => string.Join(" | ", Events);

        // ---------------------------------------------------------------- self-test

        private static Target _test;

        /// <summary>Self-test: talk to the fan with this stage, the way a gathering does it.</summary>
        public static string TestTalk(int stage)
        {
            _test = new Target { Stage = stage };
            _eventsFrom = Time.unscaledTime;
            Events.Clear();
            return $"talking to the fan with {StageName(stage)}";
        }

        /// <summary>Self-test: one step towards that fan's stage screen; true once you're on it.</summary>
        public static bool TestDrive(out string screen)
        {
            screen = null;
            if (_test == null || !InPlaza(PlazaIndex(), out var mgr)) return false;
            if (!DriveTo(_test, mgr, Time.unscaledTime, lockPage: true)) return false;
            if (!OnStageScreen(out _, out var talk)) return false;
            var ids = talk._idStages;
            int p = talk._nowPageIndex;
            string stage = ids != null && p >= 0 && p < ids.Length ? StageName(ids[p]) : "?";
            screen = $"fan {talk._objectID}, page {p} of {ids?.Length ?? 0} ({stage}), music list {(MusicListOpen(mgr) ? "open" : "closed")}";
            return ids != null && p >= 0 && p < ids.Length && ids[p] == _test.Stage;
        }

        public static string DescribeUi()
        {
            try
            {
                var mgr = SingletonMonoBehaviour<SelectHirobaManager>.instance;
                if (mgr == null) return "not in a plaza";
                var ui = mgr._uIController;
                var ctrl = mgr._hirobaController;
                var talk = ui?._talkOptionController;
                var king = ctrl?._selectHirobaKingController;
                var fan = ui?._fanMessageController;
                string page = "";
                if (talk != null)
                {
                    var ids = talk._idStages;
                    int p = talk._nowPageIndex;
                    page = ids != null && p >= 0 && p < ids.Length ? $" page {p}/{ids.Length} = {StageName(ids[p])}" : $" page {p}";
                }
                return $"plaza {ctrl?._gameState}, UI {ui?._uiStaus}, screen {(talk != null && talk.GetActive ? "up" : "down")} (fan {talk?._objectID}{page})" +
                       $", music list {(MusicListOpen(mgr) ? "open" : "closed")}, fan speech {(fan != null && fan.GetFanText ? "on" : "off")}" +
                       $", King {(king != null && king._isActive ? "talking, step " + king._talkState : "away")}, our presses read {VirtualPad.Read}";
            }
            catch (Exception e) { return "? " + e.Message; }
        }

        public static string Describe()
        {
            var g = _g;
            if (g == null) return $"{Now}, nothing gathering";
            string who = string.Join(", ", g.States.Select(kv => $"{Votes.NameOf(kv.Key)} {StateText(kv.Value)}"));
            float left = g.Deadline > 0f ? Mathf.Max(0f, g.Deadline - SyncRun.HostNow) : -1f;
            return $"{Now} for {g.Entry.Name} at {g.Spot}: me {StateText(g.Mine)}; {who}; {(left >= 0f ? $"{left:0} s left" : "waiting for everyone")}{(g.Go ? ", go" : "")}";
        }

        // ---------------------------------------------------------------- overlay

        /// <summary>At the top of the screen while gathering: the stage, what to do, who's ready, the time left.</summary>
        public static void OnGUI()
        {
            var g = _g;
            if (g == null || g.Mine == NotComing) return;
            if (_title == null || _panel == null)
            {
                _title = Style(18, Color.white, FontStyle.Bold);
                _line = Style(14, new Color(0.9f, 0.93f, 1f), FontStyle.Normal);
                _small = Style(13, new Color(0.78f, 0.85f, 1f), FontStyle.Normal);
                _panel = PlayerBar.Tex(new Color(0.08f, 0.06f, 0.14f, 0.88f));
            }
            float w = 640f, h = 84f;
            float x = (Screen.width / Votes.GuiScale() - w) / 2f, y = 16f;
            GUI.DrawTexture(new Rect(x, y, w, h), _panel);
            GUI.Label(new Rect(x, y + 6f, w, 24f), Now == Phase.Starting ? $"Starting {g.Entry.Name}…" : $"Next: {g.Entry.Name}", _title);
            string what = Now == Phase.Starting ? (MusicOpen ? "Close the music list: the stage is starting!" : "Here we go!")
                : g.Mine switch
                {
                    OnTheWay => "Heading to the stage screen…",
                    OnScreen => "Pick your music (Start opens the music list), then press Go when you're ready.",
                    _ => "You're ready. Waiting for the others…",
                };
            GUI.Label(new Rect(x, y + 32f, w, 20f), what, _line);
            int coming = g.States.Values.Count(s => s != NotComing), ready = g.States.Values.Count(s => s == IsReady);
            float left = g.Deadline > 0f ? Mathf.Max(0f, g.Deadline - SyncRun.HostNow) : -1f;
            string tail = g.Go ? "go!" : left >= 0f ? $"starts in {Mathf.CeilToInt(left)} s" : "waiting for everyone to get there";
            string names = string.Join("  ·  ", g.States.Where(kv => kv.Value != NotComing)
                .Select(kv => $"{Votes.NameOf(kv.Key)}: {StateText(kv.Value)}"));
            GUI.Label(new Rect(x, y + 56f, w, 20f), $"{ready} of {coming} ready  ·  {tail}     {names}", _small);
        }

        private static GUIStyle Style(int size, Color color, FontStyle font)
        {
            var s = new GUIStyle();
            s.fontSize = size;
            s.fontStyle = font;
            s.alignment = TextAnchor.MiddleCenter;
            s.normal.textColor = color;
            return s;
        }
    }
}
