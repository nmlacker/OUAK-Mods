using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Il2Cpp;
using Il2CppApp.KatamariSin;
using Il2CppApp.KatamariSin.InputStatus;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using OUAKModAPI;

namespace OUAKMultiplayer
{
    /// <summary>
    /// Online smoke test, armed by creating UserData/OUAKModMenu/onlinetest.flag before launch (the flag is
    /// deleted when read). At the title screen, without loading or writing any save: creates one ghost ball,
    /// hosts on 127.0.0.1:27887 with the loopback bot (an external test client may join too), then joins an
    /// external test host on 127.0.0.1:27888. If the flag's text contains "map", it then hosts again with the bot
    /// and waits up to 4 minutes for a stage or match started some other way (by you, or trainingtest.flag);
    /// 12 s in, it logs the ghost, the bot's shared pickups and the object index. Lines start with "[OnlineTest]".
    /// "run [Stage_X]" (default Stage_Bugyousyo) plays a whole run instead (RunTest): see that method.
    /// </summary>
    internal static class OnlineSelfTest
    {
        private const int HostPort = 27887, JoinPort = 27888;
        private static bool _armed, _mapPhase, _mapOnly, _run;
        private static string _runStage = "Stage_Bugyousyo";
        private static int _step;
        private static float _next, _mapSince = -1f, _mapDeadline;
        // Teleport through the player bar (plaza): where you were sent, how far you were, how tall you are.
        private static Vector3 _tpTarget;
        private static float _tpBefore, _tpTall;
        private static bool _teleportOk;
        private static string _teleportResult = "not run";
        private static string _tpPrompt = "";

        private static string FlagPath => Path.Combine(MelonEnvironment.UserDataDirectory, "OUAKModMenu", "onlinetest.flag");

        public static void CheckFlag()
        {
            try
            {
                if (!File.Exists(FlagPath)) return;
                string text = File.ReadAllText(FlagPath);
                _mapPhase = text.IndexOf("map", StringComparison.OrdinalIgnoreCase) >= 0;
                _mapOnly = text.IndexOf("maponly", StringComparison.OrdinalIgnoreCase) >= 0;
                var words = text.Split(new[] { ' ', ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                _run = words.Any(w => w.Equals("run", StringComparison.OrdinalIgnoreCase));
                // "music": also open the music list on the gathering's stage screen, and close it again.
                _musicTest = words.Any(w => w.Equals("music", StringComparison.OrdinalIgnoreCase));
                _runStage = words.FirstOrDefault(w => w.StartsWith("Stage_", StringComparison.Ordinal)) ?? _runStage;
                File.Delete(FlagPath);
                _armed = true;
                Log(_run ? $"armed (full run on {_runStage})" : _mapOnly ? "armed (map phase only)" : _mapPhase ? "armed (with map phase)" : "armed");
            }
            catch (Exception e) { MelonLogger.Warning($"[OnlineTest] {e.Message}"); }
        }

        public static void Tick()
        {
            if (!_armed || Time.unscaledTime < _next) return;
            float now = Time.unscaledTime;
            if (_run)
            {
                try { RunTest(now); }
                catch (Exception e) { Quit($"FAIL at run step {_step}: {e.Message}"); }
                return;
            }
            try
            {
                switch (_step)
                {
                    case 0:
                        string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                        if (scene != "SelectData" && scene != "Title") { _next = now + 1f; return; }
                        Log($"patches: pickup sync={Ghosts.PatchActive}");
                        Log("visuals: " + Ghosts.ProbeVisuals());
                        if (_mapOnly) { StartMapPhase(now); break; }
                        Log(NetSession.Host(HostPort, "SelfTest host", "", thisPcOnly: true, remember: false));
                        Log(NetSession.StartBot());
                        _step = 1;
                        _next = now + 8f;
                        break;
                    case 1:
                        Log($"host: peers=[{Peers()}] info=\"{NetSession.Info}\"");
                        NetSession.Leave("Self-test: hosting done.");
                        Log(NetSession.Join("127.0.0.1", JoinPort, "SelfTest client", "", remember: false));
                        _step = 2;
                        _next = now + 8f;
                        break;
                    case 2:
                        Log($"client: connected={NetSession.Connected} id={NetSession.MyId} ping={NetSession.HostPingMs:0}ms peers=[{Peers()}] info=\"{NetSession.Info}\"");
                        NetSession.Leave("Self-test: joining done.");
                        if (!_mapPhase) { Finish(); break; }
                        StartMapPhase(now);
                        break;
                    default:
                        _next = now + 1f;
                        if (Ghosts.CurrentMapKey == 0)
                        {
                            if (now > _mapDeadline) { Log("no stage or match started within 4 minutes"); Finish(); }
                            return;
                        }
                        if (_mapSince < 0f) { _mapSince = now; Log($"map {Ghosts.CurrentMapKey} started"); }
                        if (now - _mapSince < 12f) return;
                        Log($"in map {Ghosts.CurrentMapKey}: ghosts shown={Ghosts.ShownGhosts} cousins shown={Ghosts.CousinsShown} " +
                            $"stuck copies={Ghosts.StuckCopies} peers=[{Peers()}] " +
                            $"objects indexed={Ghosts.IndexedObjects} hidden by the bot={Ghosts.HiddenByOthers} sent={Ghosts.SentPickups}");
                        Finish();
                        break;
                }
            }
            catch (Exception e)
            {
                Log($"step {_step} failed: {e.Message}");
                _step++;
                _next = now + 1f;
                if (_step > 3) Finish();
            }
        }

        // ---------------------------------------------------------------- full run

        private static string _lastPhase;
        private static float _lastStatus;

        /// <summary>
        /// A whole online run in one launch: backs the saves up and loads the last-used slot (as the title's
        /// continue does), hosts on this PC with the loopback bot, starts the stage and lets the shared start play
        /// out (skip vote, hold on black, countdown, King's talk). Nobody plays, so when the bot finishes (90 s
        /// after the start) this game is hurried and, not growing, cut off 30 s later. Then it shows the results
        /// and the after-action report for a few seconds each, logs what was drawn, and quits.
        /// </summary>
        private static void RunTest(float now)
        {
            switch (_step)
            {
                case 0:
                {
                    var ctrl = GameRefs.SaveCtrl;
                    if (SceneLoader.ActiveScene != "SelectData" || GameRefs.Global == null || GameRefs.Save == null ||
                        ctrl == null || !ctrl.ActionWait() || ctrl.GameData?._SystemData == null)
                    {
                        _next = now + 1f;
                        return;
                    }
                    LoadLastUsedSlot(ctrl);
                    _step = 1;
                    _mapDeadline = now + 60f;
                    _next = now + 1f;
                    return;
                }
                case 1:
                    if (SceneLoader.ActiveScene != "OujiShip" || !GameRefs.InGameSession)
                    {
                        if (now > _mapDeadline) throw new InvalidOperationException($"the save never reached the Prince's Ship (scene {SceneLoader.ActiveScene})");
                        _next = now + 0.5f;
                        return;
                    }
                    // The client side of joining and of runs, checked offline first (the loopback bot isn't a full client).
                    Log("roster check: " + NetSession.SelfTestRoster(out _rosterOk));
                    Log("run handshake check: " + SyncRun.SelfCheck(out _runCheckOk));
                    SyncRun.StartTogether = true;
                    SyncRun.RaceStart = true;
                    // Sharing on for the run (the bot rolls things up), put back as it was at the end; not saved.
                    _prevShare = NetSession.SharePickups;
                    NetSession.SharePickups = true;
                    Log(NetSession.Host(HostPort, "SelfTest host", "", thisPcOnly: true, remember: false));
                    Log(NetSession.StartBot());
                    _step = 2;
                    _mapDeadline = now + 30f;
                    _next = now + 5f;
                    return;
                case 2:
                {
                    if (NetSession.Peers.Count == 0)
                    {
                        if (now > _mapDeadline) throw new InvalidOperationException("the loopback bot never joined");
                        _next = now + 1f;
                        return;
                    }
                    // Free roam on the ship first: the bot walks around you there. Chat there first.
                    _step = 10;
                    _next = now + 4f;
                    return;
                }
                case 10:
                    // Chat: a message to everyone (the bot answers it), and the box, which locks your controls while open.
                    _chatBefore = Chat.Received;
                    Log("chat: " + Chat.Send("Hello from the self-test!"));
                    Chat.OpenBox();
                    _step = 11;
                    _next = now + 2.5f;
                    return;
                case 11:
                    _chatLocked = Chat.PadsLocked && Chat.IsOpen;
                    _chatReplied = Chat.Received > _chatBefore && Chat.LastText.Contains("Got it");
                    Log($"chat: box open {Chat.IsOpen}, controls locked {Chat.PadsLocked}; reply {(_chatReplied ? "came: " + Chat.LastText : "didn't come")}");
                    FrameCapture.Request("hud_chat");
                    _step = 12;
                    _next = now + 1f;
                    return;
                case 12:
                    Log("frame: " + FrameCapture.Last);
                    Chat.CloseBox();
                    _step = 13;
                    _next = now + 0.5f;
                    return;
                case 13:
                    _chatUnlocked = !Chat.PadsLocked && !Chat.IsOpen;
                    Log($"chat: box closed, controls back {_chatUnlocked}");
                    _step = 14;
                    _next = now + 1f;
                    return;
                case 14:
                    // Quick chat (what D-pad down opens): the list on screen, holding the controls; then line 1 sent.
                    if (!QuickChat.CanOpen)
                    {
                        _quickResult = "couldn't open (" + QuickChat.Describe() + ")";
                        _step = 40;
                        _next = now + 0.5f;
                        return;
                    }
                    QuickChat.Open();
                    _step = 15;
                    _next = now + 0.6f;
                    return;
                case 15:
                    _quickLocked = QuickChat.IsOpen && Chat.PadsLocked;
                    FrameCapture.Request("hud_quickchat");
                    _chatBefore = Chat.Received;
                    _step = 16;
                    _next = now + 0.8f;
                    return;
                case 16:
                    Log("quick chat: " + QuickChat.SendLine(0) + " (" + QuickChat.LastSent + ")");
                    _step = 17;
                    _next = now + 2.5f;
                    return;
                case 17:
                {
                    bool replied = Chat.Received > _chatBefore && Chat.LastText.Contains("Got it") && Chat.LastText.Contains(QuickChat.LastSent);
                    _quickOk = _quickLocked && replied && !QuickChat.IsOpen;
                    _quickResult = $"list shown with the controls held {_quickLocked}, line sent \"{QuickChat.LastSent}\", the bot answered {(replied ? Chat.LastText : "no")}, closed after {!QuickChat.IsOpen}; picture {FrameCapture.Last}";
                    Log($"{(_quickOk ? "PASS" : "FAIL")}: quick chat: {_quickResult}");
                    _step = 40;
                    _next = now + 1f;
                    return;
                }
                case 40:
                {
                    // Voice chat, without any microphone: the codec, what Unity's sound side has (no recording), then
                    // the bot talks with a made-up voice, played here through a Unity AudioSource at volume 0.
                    Log(Voice.CodecCheck(out _codecOk));
                    Log("voice, Unity's side (nothing recorded): " + Voice.Probe(out _probeOk));
                    _prevListen = Voice.Listen;
                    Voice.Listen = true;
                    Voice.TestVolume = 0f;
                    Log("voice: " + NetSession.BotTalk(2.4f));
                    _step = 41;
                    _next = now + 1.4f;
                    return;
                }
                case 41:
                    _botTalked = Voice.IsTalking(NetSession.BotId);
                    _played1 = Voice.PlayedSamples(NetSession.BotId);
                    Log($"voice, 1.4 s in: {Voice.Describe(NetSession.BotId)}");
                    FrameCapture.Request("hud_voice");
                    _step = 42;
                    _next = now + 0.5f;
                    return;
                case 42:
                {
                    long played2 = Voice.PlayedSamples(NetSession.BotId);
                    _playHeadMoved = _played1 > 4000 && played2 - _played1 >= 3000;
                    Log($"voice, 0.5 s later: {(played2 - _played1) / (float)Voice.Rate:0.00} s more played | frame: {FrameCapture.Last}");
                    _step = 43;
                    _next = now + 2.5f;
                    return;
                }
                case 43:
                    _botStopped = !Voice.IsPlaying(NetSession.BotId) && !Voice.IsTalking(NetSession.BotId);
                    _heard1 = Voice.HeardPackets(NetSession.BotId);
                    _echoesBefore = NetSession.BotEchoes;
                    Log($"voice, after the bot's talk: {Voice.Describe(NetSession.BotId)}");
                    // Your side: a made-up voice through your encoder and packets (no microphone); the bot says it back.
                    Log("voice: you " + Voice.TestTalk(1.5f));
                    _step = 44;
                    _next = now + 4.6f;
                    return;
                case 44:
                {
                    int echoed = Voice.HeardPackets(NetSession.BotId) - _heard1;
                    bool echoOk = NetSession.BotEchoes > _echoesBefore && echoed >= 30;
                    _voiceOk = _codecOk && _probeOk && _botTalked && _playHeadMoved && _botStopped && _heard1 >= 50 && echoOk;
                    _voiceResult = $"codec {(_codecOk ? "ok" : "BAD")}, Unity's microphone calls {(_probeOk ? "ok" : "FAILED")}, the bot's voice heard " +
                                   $"({_heard1} packets, talking {_botTalked}, play head moving {_playHeadMoved}, stopped after {_botStopped}), " +
                                   $"yours sent {Voice.FramesSent} and said back ({echoed} packets)";
                    Log($"voice: {_voiceResult} | {Voice.Describe(NetSession.BotId)}");
                    _step = 20;
                    _next = now + 1f;
                    return;
                }
                case 20:
                {
                    Log("ship: " + HubPresence.Describe());
                    Log("where: " + WhereAll());
                    Log("bot's cousin on the ship: " + PoseCheck("ship", HubPresence.FirstCousinModel()));
                    // The game's own frame with the player bar over it (no desktop capture).
                    FrameCapture.Request("hud_ship");
                    var plaza = PlazaEntry();
                    if (plaza == null)
                    {
                        Log("no plaza to try");
                        _step = 22;
                        _next = now + 0.5f;
                        return;
                    }
                    Log($"going to {plaza.Name}: {SceneLoader.Load(plaza)}");
                    _step = 21;
                    _mapDeadline = now + 45f;
                    _next = now + 10f;
                    return;
                }
                case 21:
                    if (HubPresence.HubKey < 3000 || HubPresence.HubKey >= HubPresence.ShipKey)
                    {
                        if (now > _mapDeadline) throw new InvalidOperationException($"the plaza never loaded (scene {SceneLoader.ActiveScene})");
                        _next = now + 1f;
                        return;
                    }
                    Log("plaza: " + HubPresence.Describe());
                    Log("frame: " + FrameCapture.Last);
                    // The ship -> plaza change: everyone else's ghosts and cousins were switched off while it loaded.
                    _guardOk = SceneSwitchGuard.Holds > 0;
                    _guardResult = $"{SceneSwitchGuard.Holds} hold(s), last: {SceneSwitchGuard.LastReason}";
                    Log($"{(_guardOk ? "PASS" : "FAIL")}: scene change guard on the way to the plaza: {_guardResult}");
                    Log("bot's cousin in the plaza: " + PoseCheck("plaza", HubPresence.FirstCousinModel()));
                    _step = 31;
                    _next = now + 1f;
                    return;
                case 31:
                {
                    // Teleport through the player bar: onto the bot's spot (it walks circles 2.5 heights round you).
                    byte bot = NetSession.BotId;
                    if (!NetSession.Peers.TryGetValue(bot, out var bp) || bp.HubKey != HubPresence.HubKey)
                    {
                        Log("teleport: the bot isn't in this plaza, skipped");
                        _teleportResult = "skipped: the bot wasn't in the plaza";
                        _step = 45;
                        _next = now + 0.5f;
                        return;
                    }
                    HubPresence.TryLocal(out var before, out float tall);
                    _tpTarget = bp.HubPos;
                    _tpBefore = (before.Pos - bp.HubPos).magnitude;
                    _tpTall = tall;
                    // What a click on the bot's slot does: the prompt, then its Teleport button.
                    Teleport.Offer(bot);
                    string title = Prompt.CurrentTitle ?? "";
                    bool chosen = Prompt.Active && title.StartsWith("Teleport to") && Prompt.Choose(0);
                    _tpPrompt = $"offered {Teleport.CanOffer(bot)}, prompt \"{title}\", Teleport chosen {chosen}";
                    Log("teleport: " + _tpPrompt);
                    if (!chosen) { Prompt.Close(); Log("teleport (direct): " + Teleport.To(bot)); }
                    _step = 32;
                    _next = now + 1.2f;
                    return;
                }
                case 32:
                {
                    HubPresence.TryLocal(out var after, out _);
                    var d = after.Pos - _tpTarget;
                    float flat = new Vector2(d.x, d.z).magnitude;
                    bool ok = flat < Mathf.Max(0.05f, _tpTall * 0.5f);
                    _teleportOk = ok && _tpPrompt.Contains("Teleport chosen True");
                    _teleportResult = $"{_tpPrompt}; {flat:0.###} from the bot's spot after (was {_tpBefore:0.###}), {_tpTall:0.###} tall";
                    Log($"teleport {(ok ? "OK" : "FAILED")}: {_tpBefore:0.###} from the bot's spot before, {flat:0.###} after (sideways), height off {d.y:0.###}, you're {_tpTall:0.###} tall | {Teleport.Last}");
                    FrameCapture.Request("hud_teleport");
                    _step = 45;
                    _next = now + 1f;
                    return;
                }
                case 45:
                    // Speech bubbles: the bot's answer shows over its cousin (and it talks, so the talking marks show too).
                    _bubblesBefore = Chat.BubblesDrawn;
                    _bubbleTries++;
                    Log($"chat for a speech bubble (try {_bubbleTries}): {Chat.Send("Testing the speech bubbles")}; {NetSession.BotTalk(4f)}");
                    _bubbleDeadline = now + 7f;
                    _step = 46;
                    _next = now + 1f;
                    return;
                case 46:
                    if (Chat.BubblesDrawn > _bubblesBefore && Chat.LastBubble.StartsWith("Got it"))
                    {
                        // Let it fade in first.
                        _step = 47;
                        _next = now + 0.4f;
                        return;
                    }
                    if (now > _bubbleDeadline)
                    {
                        if (_bubbleTries < 3) { _step = 45; _next = now + 0.5f; return; }
                        _bubbleResult = "no bubble was drawn (the bot's cousin never came into view?)";
                        Log("speech bubbles: " + _bubbleResult);
                        _step = 22;
                        _next = now + 1f;
                        return;
                    }
                    _next = now + 0.1f;
                    return;
                case 47:
                    FrameCapture.Request("hud_bubble");
                    _bubbleOk = true;
                    _bubbleResult = $"the bot's answer in a bubble over its cousin ({Chat.BubblesDrawn - _bubblesBefore} frames): {Chat.LastBubble}";
                    Log("PASS: speech bubble: " + _bubbleResult + $"; bot talking {Voice.IsTalking(NetSession.BotId)}");
                    _step = 48;
                    _next = now + 1f;
                    return;
                case 48:
                    Log("frame: " + FrameCapture.Last);
                    _step = 49;
                    _next = now + 1f;
                    return;
                case 49:
                {
                    // A cousin swap in the plaza: you now walk as another cousin (the plaza's ModelLoader.PlayerNow, as the
                    // game's own swap sets it; the save's choice is left alone). Your look must change and the bot, which
                    // copies your look, must be rebuilt as that cousin on your screen.
                    var save = GameRefs.Save;
                    _walkBefore = Looks.WalkingCharacter();
                    _saveBefore = save != null ? save.GetSelectPlayerId : -1;
                    int now0 = _walkBefore >= 0 ? _walkBefore : _saveBefore;
                    _swapTo = now0 == 1 ? 2 : 1;
                    _rebuildsBefore = HubPresence.LookRebuilds;
                    _changesBefore = NetSession.LookChanges;
                    var loader = UnityEngine.Object.FindObjectOfType<ModelLoader>();
                    if (loader != null && loader.LoadPlayCharacter) { loader.PlayerNow = _swapTo; _swapWay = "the plaza's ModelLoader.PlayerNow"; }
                    else if (save != null) { save.SetSelectPlayerId(_swapTo); _swapWay = "the save's choice (no ModelLoader here)"; }
                    Log($"cousin swap: walking {_walkBefore}, save {_saveBefore} -> character {_swapTo} through {_swapWay}");
                    _swapDeadline = now + 15f;
                    _step = 50;
                    _next = now + 1f;
                    return;
                }
                case 50:
                {
                    NetSession.Peers.TryGetValue(NetSession.BotId, out var bp);
                    int mine = Looks.CharacterOf(NetSession.MyLook), bots = Looks.CharacterOf(bp?.Look);
                    bool rebuilt = HubPresence.LookRebuilds > _rebuildsBefore && HubPresence.CousinOf(NetSession.BotId) != null;
                    if ((mine != _swapTo || bots != _swapTo || !rebuilt) && now < _swapDeadline) { _next = now + 0.5f; return; }
                    _cousinOk = mine == _swapTo && bots == _swapTo && rebuilt && NetSession.LookChanges > _changesBefore;
                    _cousinResult = $"through {_swapWay}: your look says character {mine}, the bot's {bots}, its cousin rebuilt {rebuilt} " +
                                    $"({HubPresence.LookRebuilds - _rebuildsBefore}x), your look changes {NetSession.LookChanges - _changesBefore}";
                    Log($"{(_cousinOk ? "PASS" : "FAIL")}: cousin swap in the plaza: {_cousinResult}");
                    FrameCapture.Request("hud_cousin_swap");
                    // Back as you were.
                    var loader = UnityEngine.Object.FindObjectOfType<ModelLoader>();
                    if (_swapWay.StartsWith("the plaza") && loader != null) loader.PlayerNow = _walkBefore;
                    else if (GameRefs.Save != null && _saveBefore >= 0) GameRefs.Save.SetSelectPlayerId(_saveBefore);
                    _step = 22;
                    _next = now + 4f;
                    return;
                }
                case 22:
                {
                    Log("plaza, a moment later: " + HubPresence.Describe());
                    Log("bot's cousin in the plaza, 5 s on: " + PoseCheck("plaza", HubPresence.FirstCousinModel()));
                    Log($"portraits: {PortraitStudio.Describe()}; yours {(PortraitStudio.For(NetSession.MyLook) != null ? "taken" : "not yet")}; " +
                        FrameCapture.SaveTexture(PortraitStudio.For(NetSession.MyLook), "portrait_you"));
                    Log("where: " + WhereAll());
                    var entry = RunEntry();
                    // The menu's stage pick still asks the host go now / vote (cancelled here: a fan's pick comes next).
                    Log($"menu pick of {entry.Name}: {SceneLoader.Load(entry)}");
                    if (!StageGate.ChoiceOpen) throw new InvalidOperationException("picking a stage from the menu in a session didn't ask go now / vote");
                    StageGate.Choose(StageGate.Pick.Cancel);
                    Log("PASS: the menu's stage pick asked go now / vote (cancelled)");
                    Log(PlazaFlow.TestTalk((int)entry.Stage));
                    _step = 23;
                    _mapDeadline = now + 40f;
                    _next = now + 0.5f;
                    return;
                }
                case 23:
                {
                    // Talk to the fan with the stage, the way a gathering does: its stage screen opens on that stage.
                    if (now >= _nextUiLog) { _nextUiLog = now + 2f; Log("plaza: " + PlazaFlow.DescribeUi()); }
                    if (!PlazaFlow.TestDrive(out string screen))
                    {
                        if (now > _mapDeadline) throw new InvalidOperationException($"the fan's stage screen never opened ({PlazaFlow.DescribeUi()}; {PlazaFlow.Timeline()})");
                        _next = now + 0.1f;
                        return;
                    }
                    Log($"PASS: the fan's stage screen opened: {screen} ({PlazaFlow.Timeline()})");
                    _step = 24;
                    _next = now + 2f;
                    return;
                }
                case 24:
                    // Go, the way a player presses it: held for the session (the screen closes, the host chooses).
                    Log("pressing Go on the stage screen: " + PlazaFlow.DescribeUi());
                    VirtualPad.Press(eKeyMap.Select, asPlayer: true);
                    _step = 25;
                    _mapDeadline = now + 8f;
                    _next = now + 0.5f;
                    return;
                case 25:
                    if (!StageGate.ChoiceOpen)
                    {
                        if (now > _mapDeadline) throw new InvalidOperationException($"Go on the fan's stage screen wasn't held for the session ({PlazaFlow.DescribeUi()})");
                        _next = now + 0.25f;
                        return;
                    }
                    Log($"PASS: Go was held for the session ({PlazaFlow.LastPick}): go now / vote?");
                    _step = 26;
                    _next = now + 2f;
                    return;
                case 26:
                    Log("after Go was held (the screen should be closed): " + PlazaFlow.DescribeUi());
                    StageGate.Choose(StageGate.Pick.Vote);
                    _step = 27;
                    _mapDeadline = now + 60f;
                    _next = now + 0.5f;
                    return;
                case 27:
                    // The vote passes (the bot agrees): everyone gathers at that fan's stage screen.
                    if (now >= _nextUiLog) { _nextUiLog = now + 2f; Log($"gathering: {PlazaFlow.Describe()} | {PlazaFlow.DescribeUi()}"); }
                    if (PlazaFlow.Now != PlazaFlow.Phase.Gathering || PlazaFlow.MyState < PlazaFlow.OnScreen)
                    {
                        if (now > _mapDeadline) throw new InvalidOperationException($"never got back to the stage screen ({PlazaFlow.Describe()}; {PlazaFlow.DescribeUi()}; {PlazaFlow.Timeline()})");
                        _next = now + 0.25f;
                        return;
                    }
                    Log($"PASS: gathered at the fan's stage screen: {PlazaFlow.Describe()} | {PlazaFlow.DescribeUi()}");
                    Log("where: " + WhereAll());
                    FrameCapture.Request("hud_gather");
                    if (_musicTest)
                    {
                        // The music list: Start opens it on this screen; Back (once it's ready) closes it, choosing nothing.
                        VirtualPad.Press(eKeyMap.Start, asPlayer: true);
                        _musicTries = 0;
                        _mapDeadline = now + 12f;
                        _step = 28;
                        _next = now + 1f;
                        return;
                    }
                    _step = 29;
                    _next = now + 1f;
                    return;
                case 28:
                {
                    bool open = PlazaFlow.MusicOpen;
                    if (open) _musicOpened = true;
                    Log($"music list: {PlazaFlow.DescribeMusic()}");
                    if (open && now < _mapDeadline)
                    {
                        // Back only works once the list has finished opening (its manager's UPDATE / WAIT state).
                        if (PlazaFlow.MusicReady && _musicTries++ < 6) VirtualPad.Press(eKeyMap.Cancel, asPlayer: true);
                        _next = now + 1f;
                        return;
                    }
                    if (open) Log("the music list wouldn't close: no Go from the test (the gathering's timer will start the stage)");
                    _step = open ? 30 : 29;
                    _mapDeadline = now + 70f;
                    _next = now + 1f;
                    return;
                }
                case 29:
                    // Never pressed while the music list is open (Go there would pick a song).
                    if (PlazaFlow.MusicOpen) { _step = 30; _mapDeadline = now + 70f; _next = now + 0.5f; return; }
                    Log("frame: " + FrameCapture.Last);
                    Log("pressing Go to be ready: " + PlazaFlow.DescribeUi());
                    VirtualPad.Press(eKeyMap.Select, asPlayer: true);
                    _step = 30;
                    _mapDeadline = now + 70f;
                    _next = now + 0.5f;
                    return;
                case 30:
                {
                    // Ready, the bot too: go. The plaza flies up to the King, his talk is skipped, the stage loads.
                    if (now >= _nextUiLog) { _nextUiLog = now + 1.5f; Log($"starting: {PlazaFlow.Describe()} | {PlazaFlow.DescribeUi()} | scene {SceneLoader.ActiveScene}"); }
                    var entry = RunEntry();
                    if (GameRefs.Main == null || Ghosts.CurrentMapKey != Ghosts.KeyFor(entry))
                    {
                        if (now > _mapDeadline) throw new InvalidOperationException($"the stage never started from the fan's screen ({PlazaFlow.Describe()}; {PlazaFlow.Timeline()})");
                        _next = now + 0.25f;
                        return;
                    }
                    _fanStart = PlazaFlow.Timeline();
                    _kingPath = _fanStart.Contains("the King's stage start");
                    Log($"PASS: {entry.Name} started from the fan's stage screen ({(_kingPath ? "through the King, his talk skipped" : "loaded directly, not through the King")}): {_fanStart}");
                    _step = 3;
                    _mapDeadline = now + 150f;
                    _next = now + 1f;
                    return;
                }
                case 3:
                    LogPhase();
                    if (!_clockHeldChecked && IntroSync.OnBlack && Ghosts.TryLocalState(out var held) && held.TimerRule >= 0)
                    {
                        // Held on black: your clock isn't moving, so it goes out as stopped, and the bot's (a copy) shows exactly.
                        _clockHeldChecked = true;
                        string botTime = NetSession.Peers.TryGetValue(NetSession.BotId, out var bp) ? SyncRun.EstimateTime(bp) : "?";
                        string exact = bp != null ? KatamariState.TimeText(bp.State.TimerRule, bp.State.TimeLeft, bp.State.Elapsed) : "";
                        _clockHeldOk = held.ClockStopped;
                        _clockResult = $"held at the start: yours stopped {held.ClockStopped}; the bot's shows {botTime} (sent {exact})";
                        Log("clock: " + _clockResult);
                    }
                    if (SyncRun.Phase != RunPhase.Running)
                    {
                        if (now > _mapDeadline) throw new InvalidOperationException($"no shared start (phase {SyncRun.Phase}, on black {IntroSync.OnBlack})");
                        _next = now + 0.5f;
                        return;
                    }
                    Log($"PASS: shared start on map {Ghosts.CurrentMapKey} ({Votes.StageName(Ghosts.CurrentMapKey)})");
                    _runStartAt = now;
                    // The lobby settings for this run (put back at the end, not saved): only Drink allowed (others
                    // swapped for it), and Sudden Death on (whatever your own setting) and shorter, so the cut-off comes
                    // 15 s after the bot finishes.
                    _prevFreebies = Lobby.FreebieRule;
                    _prevSudden = Lobby.SuddenDeathTime;
                    _prevSuddenOn = Lobby.SuddenDeath;
                    Lobby.SuddenDeath = true;
                    _suddenSet = true;
                    _prevMask = Lobby.AllowedMask;
                    _prevOthers = Lobby.OtherFreebies;
                    Lobby.FreebieRule = Lobby.FreebiesAllowed;
                    Lobby.AllowedMask = 1u << (int)eInstageItemType.Drink;
                    Lobby.OtherFreebies = 0;
                    Lobby.SuddenDeathTime = 15;
                    NetSession.SettingsChanged();
                    // The leaderboard recorder (off by default): on for this run, into a throwaway folder.
                    _prevRecording = Leaderboards.LeaderboardRecorder.Enabled;
                    _prevRecordFolder = Leaderboards.LeaderboardRecorder.Folder;
                    Leaderboards.LeaderboardRecorder.Folder = Path.Combine(MelonEnvironment.UserDataDirectory, "OUAKMultiplayer", "Leaderboards", "selftest");
                    try { if (Directory.Exists(Leaderboards.LeaderboardRecorder.Folder)) Directory.Delete(Leaderboards.LeaderboardRecorder.Folder, true); } catch { }
                    Leaderboards.LeaderboardRecorder.Enabled = true;
                    _recordingSet = true;
                    Log("lobby for this run: " + Lobby.Describe());
                    _step = 4;
                    _mapDeadline = now + 240f;
                    _next = now + 1f;
                    return;
                case 4:
                    LogPhase();
                    RaceChecks(now - _runStartAt);
                    // One real roll-up through the game's own MainGameCore.GetTargetMono (what touching it does).
                    if (!_forcedPickup && now - _runStartAt > 5f)
                    {
                        _forcedPickup = true;
                        Log(ForcePickup());
                    }
                    // Grow: roll objects onto your katamari for a while, logging how the game sizes the ball and prince.
                    if (now - _runStartAt > 8f && now - _runStartAt < 30f && now >= _nextGrow)
                    {
                        _nextGrow = now + 0.25f;
                        Grow();
                    }
                    if (now - _runStartAt > 8f && now - _runStartAt < 34f && now >= _nextGrowLog)
                    {
                        _nextGrowLog = now + 4f;
                        Log(DescribeGrowth());
                    }
                    // Pictures of the ghost and of you, from the game's own world (a temporary camera, never the desktop).
                    if (!_shotsTaken && now - _runStartAt > 40f)
                    {
                        _shotsTaken = true;
                        Log(TakeShots());
                    }
                    // Your ghost settings: each must hide or show the ghost as it says (put back as they were, not saved).
                    if (now - _runStartAt > 55f) GhostSettingsCheck(now);
                    if (now - _lastStatus >= 10f)
                    {
                        _lastStatus = now;
                        bool hurry = SyncRun.HurryActive(out int target, out float left);
                        Log($"racing: size {KatamariState.SizeText(GameRefs.Main?.KatamariSize ?? 0)}, results in {SyncRun.Standings.Count}" +
                            (hurry ? $", hurried: reach {KatamariState.SizeText(target)} in {left:0} s" : ""));
                        Log(CompareSizeText());
                        Log("ghosts: " + Ghosts.DescribeGhosts());
                    }
                    if (!SyncRun.StandingsFinal)
                    {
                        if (now > _mapDeadline) throw new InvalidOperationException($"the results never became final (phase {SyncRun.Phase}, {SyncRun.Standings.Count} in)");
                        _next = now + 0.5f;
                        return;
                    }
                    Log("PASS: final results: " + string.Join(" | ", SyncRun.Standings.Select(SyncRun.Describe)));
                    LeaderboardCheck();
                    Log($"match log: {MatchLog.LastPath}");
                    Lobby.FreebieRule = _prevFreebies;
                    Lobby.SuddenDeathTime = _prevSudden;
                    Lobby.SuddenDeath = _prevSuddenOn;
                    Lobby.AllowedMask = _prevMask;
                    Lobby.OtherFreebies = _prevOthers;
                    NetSession.SettingsChanged();
                    _step = 5;
                    _next = now + 3f;
                    return;
                case 5:
                    if (!FrameCapture.Busy && !_resultsShot) { _resultsShot = true; FrameCapture.Request("hud_results"); _next = now + 5f; return; }
                    Log("frame: " + FrameCapture.Last);
                    Log("where: " + WhereAll());
                    Log($"results drawn for {SyncRun.StandingsFrames} frames; place icons: {RankIcons.Describe()}; rank badges: {StageRank.Describe()}");
                    SyncRun.ShowAar = true;
                    _step = 6;
                    _next = now + 6f;
                    return;
                case 6:
                    Log($"after-action report drawn for {SyncRun.AarFrames} frames");
                    SyncRun.ShowAar = false;
                    // The host's sharing toggle: off must put back everything the bot rolled up.
                    Log("ghosts before: " + Ghosts.DescribeGhosts());
                    _hiddenList = Ghosts.HiddenSnapshot();
                    Log($"turning sharing off with {_hiddenList.Count} object(s) hidden by the bot");
                    NetSession.SharePickups = false;
                    _step = 7;
                    _next = now + 2f;
                    return;
                case 7:
                {
                    int back = 0;
                    foreach (var m in _hiddenList) { try { if (m != null && m.gameObject.activeSelf) back++; } catch { } }
                    _restored = Ghosts.HiddenNow == 0 && !NetSession.PickupsShared && back == _hiddenList.Count;
                    Log($"sharing off: shared={NetSession.PickupsShared}, visible again {back} of {_hiddenList.Count}, still hidden {Ghosts.HiddenNow}; ghosts: {Ghosts.DescribeGhosts()}");
                    // A vote that doesn't pass: whoever started it waits 30 s before the next.
                    NetSession.BotVotesNo = true;
                    Log("vote the bot turns down: " + TryPropose());
                    // A picture of the vote popup (Dear ImGui, above the player bar).
                    ModTasks.Later(() => FrameCapture.Request("hud_vote"), 0.4f);
                    _step = 8;
                    _next = now + 2f;
                    return;
                }
                case 8:
                {
                    var last = Votes.Active;
                    string again = TryPropose();
                    _cooled = again.Contains("didn't pass");
                    Log($"that vote {(last != null && last.Done ? (last.Passed ? "passed" : "failed") : "is still open")}; another right away: {again}");
                    NetSession.BotVotesNo = false;
                    // The host's authority: the choice the pause menu's Retry opens in a session.
                    _promptShown = HostAuthority.TestPrompt(PauseAction.Retry);
                    Log($"pause menu Retry in a session: our choice shown {_promptShown} ({HostAuthority.LastOptionCount} options), the game's own Yes/No not opened");
                    _step = 81;
                    _next = now + 1f;
                    return;
                }
                case 81:
                {
                    if (!FrameCapture.Busy && !_promptShot) { _promptShot = true; FrameCapture.Request("hud_prompt"); _next = now + 0.8f; return; }
                    bool shown = Prompt.Active && Prompt.CurrentTitle == "Restart the stage";
                    bool cancelled = Prompt.Choose(HostAuthority.LastOptionCount - 1);
                    _promptOk = _promptShown && shown && HostAuthority.LastOptionCount == 4 && cancelled && !Prompt.Active;
                    Log($"{(_promptOk ? "PASS" : "FAIL")}: the host's choice: Force, Vote, Only me, Cancel ({HostAuthority.LastOptionCount} shown {shown}, cancelled {cancelled}); picture: {FrameCapture.Last}");
                    Log("vote rules: " + HostAuthority.SelfCheck(out _rulesOk));
                    // A restart vote the bot agrees with: passes, and the stage starts again for everyone.
                    _runBefore = SyncRun.RunId;
                    Log("restart vote (the bot says yes): " + TryVote(VoteKind.Restart));
                    _step = 82;
                    _mapDeadline = now + 70f;
                    _next = now + 1f;
                    return;
                }
                case 82:
                {
                    bool newRun = SyncRun.RunId != _runBefore && GameRefs.InStage && Ghosts.CurrentMapKey != 0 && !Votes.Open;
                    if (!newRun)
                    {
                        if (now > _mapDeadline) { Log($"FAIL: the restart vote didn't start a new run (run {SyncRun.RunId}, phase {SyncRun.Phase}, vote {Votes.Active?.Passed})"); _step = 99; }
                        _next = now + 0.5f;
                        return;
                    }
                    _voteRestartOk = true;
                    Log($"PASS: the restart vote passed and the stage started again for everyone (run {_runBefore} -> {SyncRun.RunId})");
                    string again = TryVote(VoteKind.Restart);
                    _lockOk = again.Contains("already passed");
                    Log($"{(_lockOk ? "PASS" : "FAIL")}: another restart vote on this stage: {again}");
                    _step = 83;
                    _mapDeadline = now + 70f;
                    _next = now + 0.5f;
                    return;
                }
                case 83:
                {
                    // Once playing, the host's Force: an in-place restart that's a new run for everyone.
                    if (SyncRun.Phase != RunPhase.Running || !Katamari.Playing)
                    {
                        if (now > _mapDeadline) { Log($"FAIL: the new run never got going (phase {SyncRun.Phase})"); _step = 99; }
                        _next = now + 0.5f;
                        return;
                    }
                    _runBefore = SyncRun.RunId;
                    HostAuthority.ForceRestart();
                    _step = 84;
                    _mapDeadline = now + 30f;
                    _next = now + 1f;
                    return;
                }
                case 84:
                {
                    if (SyncRun.RunId == _runBefore)
                    {
                        if (now > _mapDeadline) { Log("FAIL: Force didn't restart the run"); _step = 99; }
                        _next = now + 0.5f;
                        return;
                    }
                    _forceRestartOk = true;
                    Log($"PASS: Force restarted the stage in place as a new run for everyone (run {_runBefore} -> {SyncRun.RunId}, phase {SyncRun.Phase})");
                    _step = 90;
                    _mapDeadline = now + 60f;
                    _next = now + 3f;
                    return;
                }
                // The real pause menu: Retry opens our choice instead of the game's Yes/No; Cancel, then Resume; then "Only me".
                case 90:
                {
                    // Racing, past the start, and the game itself would take a pause press now.
                    var g = GameRefs.Main;
                    bool canPause = false;
                    try { canPause = g != null && g.StartMessageDone && g.CheckPauseInputAllowed(); } catch { }
                    if (!Katamari.Playing || SyncRun.Phase != RunPhase.Running || !canPause)
                    {
                        if (now > _mapDeadline) { Log($"FAIL: the game never allowed a pause after the forced restart (playing {Katamari.Playing}, phase {SyncRun.Phase}, allowed {canPause})"); _step = 85; }
                        _next = now + 0.5f;
                        return;
                    }
                    _pauseOpened = PauseMenu.Open();
                    _pauseTries++;
                    _step = 91;
                    _next = now + 1.5f;
                    return;
                }
                case 91:
                {
                    var menu = PauseMenu.Current;
                    bool paused = GameEvents.Paused && menu != null && GameRefs.Main != null && GameRefs.Main._isInPause;
                    Log($"pause menu opened {_pauseOpened} (try {_pauseTries}): paused {paused}");
                    if (!paused && _pauseTries < 3) { _step = 90; _next = now + 1f; return; }
                    if (!paused) { _pauseOk = false; _step = 85; _next = now + 1f; return; }
                    menu.OpenRetryConfirm();
                    _gameConfirmOpened = menu.IsInConfirm;
                    _step = 92;
                    _next = now + 1f;
                    return;
                }
                case 92:
                {
                    if (!FrameCapture.Busy && !_pauseShot) { _pauseShot = true; FrameCapture.Request("hud_pause_prompt"); _next = now + 0.8f; return; }
                    bool ours = Prompt.Active && Prompt.CurrentTitle == "Restart the stage";
                    Log($"pause menu Retry: our choice {ours} ({HostAuthority.LastOptionCount} options), the game's own Yes/No opened {_gameConfirmOpened}; picture {FrameCapture.Last}");
                    _pausePromptOk = ours && !_gameConfirmOpened && HostAuthority.LastOptionCount == 4;
                    Prompt.Choose(HostAuthority.LastOptionCount - 1);   // Cancel: back to the pause menu
                    _step = 93;
                    _next = now + 1f;
                    return;
                }
                case 93:
                {
                    bool stillPaused = GameEvents.Paused && GameRefs.Main != null && GameRefs.Main._isInPause;
                    Log($"after Cancel: the pause menu is still open {stillPaused}; resuming the game's way");
                    _cancelOk = stillPaused && !Prompt.Active;
                    PauseMenu.Resume();
                    _step = 94;
                    _next = now + 1.5f;
                    return;
                }
                case 94:
                {
                    var gm = GameRefs.Main;
                    _resumeOk = !GameEvents.Paused && gm != null && !gm._isInPause;
                    Log($"{(_resumeOk ? "PASS" : "FAIL")}: PauseMenu.Resume unpaused the game the game's way (paused {GameEvents.Paused}, game {gm?._isInPause})");
                    // Once more, this time "Only me": just this game restarts, the race goes on.
                    _runBefore = SyncRun.RunId;
                    if (!_restartHooked) { _restartHooked = true; GameEvents.StageRestarted += _ => _restartsSeen++; }
                    _restartsBefore = _restartsSeen;
                    PauseMenu.Open();
                    _step = 95;
                    _next = now + 1.5f;
                    return;
                }
                case 95:
                {
                    var menu = PauseMenu.Current;
                    if (!GameEvents.Paused || menu == null) { Log("FAIL: the pause menu didn't open again"); _step = 85; _next = now + 1f; return; }
                    menu.OpenRetryConfirm();
                    _step = 96;
                    _next = now + 1f;
                    return;
                }
                case 96:
                    Prompt.Choose(2);   // Only me
                    _step = 97;
                    _mapDeadline = now + 25f;
                    _next = now + 3f;
                    return;
                case 97:
                {
                    var gm = GameRefs.Main;
                    bool restarted = gm != null && _restartsSeen > _restartsBefore && !GameEvents.Paused;
                    if (!restarted && now < _mapDeadline) { _next = now + 0.5f; return; }
                    _onlyMeOk = restarted && SyncRun.RunId == _runBefore;
                    Log($"{(_onlyMeOk ? "PASS" : "FAIL")}: \"Only me\" restarted just this game through the pause menu (clock {gm?._playTime:0.0} s, run {_runBefore} -> {SyncRun.RunId}, phase {SyncRun.Phase})");
                    _pauseOk = _pausePromptOk && _cancelOk && _resumeOk && _onlyMeOk;
                    _step = 100;
                    _next = now + 2f;
                    return;
                }
                // A player who leaves the race alone: disqualified, listed last, and can't interrupt the race with a vote.
                case 100:
                    Log("the bot leaves the race: " + NetSession.BotLeaveRace());
                    _step = 101;
                    _mapDeadline = now + 8f;
                    _next = now + 1f;
                    return;
                case 101:
                {
                    var dq = SyncRun.Standings.FirstOrDefault(s => s.Id == NetSession.BotId);
                    if (dq == null && now < _mapDeadline) { _next = now + 0.5f; return; }
                    bool listedLast = dq != null && SyncRun.Standings.Last() == dq;
                    _dqListed = dq != null && dq.Disqualified && dq.Status == "DQ" && listedLast && SyncRun.IsDisqualified(NetSession.BotId);
                    Log($"{(_dqListed ? "PASS" : "FAIL")}: the bot is disqualified ({(dq != null ? SyncRun.Describe(dq) : "no result")}), listed last {listedLast}; the race goes on (phase {SyncRun.Phase})");
                    Log("the bot asks for a random stage mid-race: " + NetSession.BotPropose(VoteKind.RandomStage, 0));
                    _step = 102;
                    _next = now + 1.5f;
                    return;
                }
                case 102:
                {
                    _dqBlocked = !Votes.Open && Votes.LastRefusal.Contains("left this race") && SyncRun.Phase == RunPhase.Running;
                    Log($"{(_dqBlocked ? "PASS" : "FAIL")}: the disqualified bot couldn't interrupt the race (vote open {Votes.Open}, refusal \"{Votes.LastRefusal}\", phase {SyncRun.Phase})");
                    _dqOk = _dqListed && _dqBlocked;
                    _step = 85;
                    _next = now + 2f;
                    return;
                }
                case 85:
                    // The host's Force back to the Plaza: everyone is told (HostPlaza), the host leaves.
                    HostAuthority.ForcePlaza();
                    _step = 86;
                    _mapDeadline = now + 45f;
                    _next = now + 1f;
                    return;
                case 86:
                {
                    bool inPlaza = UnityEngine.SceneManagement.SceneManager.GetSceneByName("SelectHiroba").isLoaded && GameRefs.Main == null;
                    if (!inPlaza)
                    {
                        if (now > _mapDeadline) { Log("FAIL: Force didn't take you back to the Plaza"); _step = 99; }
                        _next = now + 0.5f;
                        return;
                    }
                    _forcePlazaOk = HostAuthority.PlazaSends > 0;
                    Log($"{(_forcePlazaOk ? "PASS" : "FAIL")}: Force took you back to the Plaza and told everyone ({HostAuthority.PlazaSends} HostPlaza packet(s))");
                    _step = 60;
                    _afterMatchDeadline = now + 25f;
                    _next = now + 3f;
                    return;
                }
                case 60:
                {
                    // Back in the plaza after the match (the user's playtest, 2026-11-08: players sometimes didn't show up):
                    // you're the plaza's own walking character, not one of its cousins, and the bot shows.
                    bool inPlaza = HubPresence.HubKey >= 3000 && HubPresence.HubKey < HubPresence.ShipKey;
                    // Yours: the plaza's CharacterManager, or the player model its ModelLoader made, with your cousin's model
                    // loaded (the body sent is its rig, e.g. OUJI47_D_Custom, not the bare SelectHirobaOujiSetting object).
                    bool mine = (HubPresence.LocalSource.Contains("CharacterManager") || HubPresence.LocalSource.Contains("your own model"))
                                && HubPresence.TryLocal(out _, out _) && HubPresence.LocalBody != "no model yet" && !HubPresence.LocalBody.StartsWith("SelectHiroba");
                    var cousin = HubPresence.FirstCousinModel();
                    if ((!inPlaza || !mine || cousin == null) && now < _afterMatchDeadline) { _next = now + 0.5f; return; }
                    _afterMatchOk = inPlaza && mine && cousin != null;
                    _afterMatchResult = $"in a plaza {inPlaza} ({HubPresence.HubKey}), you from {HubPresence.LocalSource} ({HubPresence.LocalBody}), the bot's cousin shown {cousin != null} | {HubPresence.Describe()}";
                    Log($"{(_afterMatchOk ? "PASS" : "FAIL")}: the plaza after the match: {_afterMatchResult}");
                    FrameCapture.Request("hub_after_match");
                    _step = 99;
                    _next = now + 2f;
                    return;
                }
                default:
                {
                    bool drawn = SyncRun.StandingsFrames > 0 && SyncRun.AarFrames > 0;
                    bool picked = _hiddenList.Count > 0;
                    if (_musicTest) Log($"music list on the gathering's stage screen: {(_musicOpened ? "opened with Start" : "didn't open")}");
                    string fan = _kingPath ? "ready, the King's talk skipped" : "ready, but the stage was loaded directly (the King's start wasn't tried)";
                    bool portrait = PortraitStudio.For(NetSession.MyLook) != null;
                    bool chatOk = _chatReplied && _chatLocked && _chatUnlocked && _quickOk;
                    Voice.TestVolume = null;
                    Voice.Listen = _prevListen;
                    Log($"checks: quick turns: {_turnResult} | freebie list: {_freebieResult} | bot's cousin: {_animResult} | portraits: {PortraitStudio.Describe()} | " +
                        $"chat: reply {_chatReplied}, locked while open {_chatLocked}, released {_chatUnlocked}, quick chat {_quickOk} | speech bubbles: {_bubbleResult} | voice: {_voiceResult} | " +
                        $"roster {_rosterOk}, run handshake {_runCheckOk} | clock: {_clockResult} | pose: {_poseResult} | last frame: {FrameCapture.Last}");
                    bool syncOk = _rosterOk && _runCheckOk && _clockHeldOk && _clockRunOk && _poseMirrorOk && _poseHeldOk;
                    bool authorityOk = _promptOk && _rulesOk && _voteRestartOk && _lockOk && _forceRestartOk && _forcePlazaOk && _pauseOk && _dqOk;
                    Log($"host authority: choice {_promptOk}, vote rules {_rulesOk}, restart vote {_voteRestartOk}, no second vote {_lockOk}, Force restart {_forceRestartOk}, " +
                        $"pause menu (choice {_pausePromptOk}, cancel {_cancelOk}, resume {_resumeOk}, only me {_onlyMeOk}), leaving the race (DQ listed last {_dqListed}, can't interrupt {_dqBlocked}), Force Plaza {_forcePlazaOk}; identity: {Identity()}");
                    Log($"teleport: {(_teleportOk ? "OK" : "FAILED")}, {_teleportResult}");
                    Log($"timers: {(_timersOk ? "OK" : "FAILED")}, {_timerResult}");
                    Log($"ranking: {(_rankingOk ? "OK" : "FAILED")}, {_rankingResult}; Results: {SyncRun.Standings.Count} ranked by \"{Ranking.Current.Title}\", places {string.Join(",", Ranking.StandingsPlaces(SyncRun.Standings).Values)}");
                    Quit(drawn && _restored && picked && _cooled && _turnsOk && _freebieOk && _animOk && portrait && chatOk && _voiceOk && _bubbleOk && syncOk && authorityOk && _teleportOk && _timersOk && _rankingOk && _suddenOk && _guardOk && _cousinOk && _sizeOk && _afterMatchOk && _boardsOk
                        ? $"{(_kingPath ? "PASS" : "INCOMPLETE")}: the host's authority (Force / Vote / Only me, one restart vote per stage, Force restart and Plaza, leaving the race = DQ that can't interrupt it), identities, the roster and run handshakes, the stopped-clock flag, prince poses mirrored (live and held), free roam, chat, quick chat, speech bubbles, voice chat (made-up voices, no microphone), a fan's stage pick (held Go, vote, gathering at its stage screen, {fan}), shared start, quick turns counted once each, " +
                          "the run timers (IGT, RTA, the win-condition moment, mirrored by the bot), live places by the ranking system and a custom one over the wire, teleport to a player in the plaza, a cousin swap in the plaza shown on the bot, ghosts off during scene changes, the host's freebie list (swap and take away), Sudden Death (15 s, only once the stage had ended for the bot), the bot's cousin animating, portraits, results, report, the sharing toggle and the vote cooldown all worked without a crash"
                        : !drawn ? "FAIL: the results weren't drawn"
                        : !picked ? "INCOMPLETE: the bot didn't roll anything up, so the toggle had nothing to put back"
                        : !_restored ? "FAIL: turning sharing off didn't put the objects back"
                        : !chatOk ? $"FAIL: chat (reply {_chatReplied}, locked while open {_chatLocked}, released {_chatUnlocked}, quick chat: {_quickResult})"
                        : !_voiceOk ? $"FAIL: voice chat: {_voiceResult}"
                        : !_bubbleOk ? $"FAIL: speech bubbles: {_bubbleResult}"
                        : !_teleportOk ? $"FAIL: teleport through the player bar: {_teleportResult}"
                        : !_timersOk ? $"FAIL: the run timers: {_timerResult}"
                        : !_rankingOk ? $"FAIL: ranking: {_rankingResult}"
                        : !_suddenOk ? $"FAIL: Sudden Death at the stage's end: {_suddenResult}"
                        : !_guardOk ? $"FAIL: scene change guard: {_guardResult}"
                        : !_cousinOk ? $"FAIL: cousin swap in the plaza: {_cousinResult}"
                        : !_sizeOk ? $"FAIL: the ghost's size: {_sizeResult}"
                        : !_afterMatchOk ? $"FAIL: the plaza after the match: {_afterMatchResult}"
                        : !_boardsOk ? $"FAIL: leaderboards: {_boardsResult}"
                        : !authorityOk ? $"FAIL: host authority (choice {_promptOk}, rules {_rulesOk}, restart vote {_voteRestartOk}, no second vote {_lockOk}, Force restart {_forceRestartOk}, pause menu {_pauseOk}, leaving the race {_dqOk}, Force Plaza {_forcePlazaOk})"
                        : !_rosterOk ? "FAIL: a roster before Welcome still lists you twice"
                        : !_runCheckOk ? "FAIL: the client's run handshake (see 'run handshake check')"
                        : !(_clockHeldOk && _clockRunOk) ? $"FAIL: the stopped-clock flag: {_clockResult}"
                        : !(_poseMirrorOk && _poseHeldOk) ? $"FAIL: prince poses: {_poseResult}"
                        : !_turnsOk ? $"FAIL: quick turns: {_turnResult}"
                        : !_freebieOk ? $"FAIL: the freebie list: {_freebieResult}"
                        : !_animOk ? $"FAIL: the bot's cousin: {_animResult}"
                        : !portrait ? $"FAIL: no portrait of your cousin ({PortraitStudio.Describe()})"
                        : "FAIL: a lost vote didn't start the cooldown");
                    return;
                }
            }
        }

        private static bool _restored, _cooled, _musicOpened, _musicTest, _kingPath;
        private static bool _codecOk, _probeOk, _botTalked, _playHeadMoved, _botStopped, _voiceOk, _prevListen, _bubbleOk, _stageBubbleSent;
        private static long _played1;
        private static int _heard1, _echoesBefore, _bubblesBefore, _bubbleTries, _stageBubblesBefore;
        private static float _bubbleDeadline;
        private static string _voiceResult = "not tried", _bubbleResult = "not tried";
        private static bool _rosterOk, _runCheckOk, _clockHeldChecked, _clockHeldOk, _clockRunChecked, _clockRunOk, _poseMirrorOk, _poseHeldOk;
        private static int _poseStep;
        private static string _clockResult = "", _poseResult = "not tried";
        private static int _musicTries;
        private static float _nextUiLog;
        private static string _fanStart = "";

        private static MapEntry RunEntry() =>
            StageCatalog.Entries.FirstOrDefault(e => e.Kind == MapKind.Stage && e.Internal == _runStage)
            ?? throw new InvalidOperationException($"there's no stage called {_runStage}");

        // ---------------------------------------------------------------- race checks

        private static int _prevFreebies, _prevSudden = 30, _turnsStarted, _prevOthers, _chatBefore;
        private static bool _prevSuddenOn = true, _suddenSet;
        private static uint _prevMask = Lobby.AllFreebies;
        private static bool _swapOk, _freebieChecked2, _chatLocked, _chatReplied, _chatUnlocked;
        // Quick chat: the list held the controls, line 1 went out and the bot answered it.
        private static bool _quickLocked, _quickOk;
        private static string _quickResult = "not run";
        private static float _nextTurnAt;
        private static bool _freebieChecked3, _sfOffOk, _timersOk, _rankingOk, _suddenChecked, _suddenOk;
        private static string _suddenResult = "Sudden Death never started";
        private static bool _guardOk, _cousinOk, _sizeChecked, _sizeOk, _afterMatchOk, _boardsOk, _prevRecording, _recordingSet;
        private static string _boardsResult = "not checked", _prevRecordFolder;
        private static string _afterMatchResult = "not reached";
        private static float _afterMatchDeadline;
        private static string _sizeResult = "not checked";
        private static string _guardResult = "never reached the plaza", _cousinResult = "not tried", _swapWay = "";
        private static int _walkBefore, _saveBefore, _swapTo, _rebuildsBefore, _changesBefore;
        private static float _swapDeadline;
        private static int _prevNear = -1;
        private static bool _prevBlocking = true;
        private static string _rankingResult = "not run";
        private static float _rankingTabAt;
        private static bool _rankingTabShot, _rankingTabClosed;
        private static int _timerCheck;
        private static string _timerResult = "not run";
        private static bool _resultsShot, _freebieGiven, _freebieChecked, _animSampled, _animChecked, _raceShot, _raceLogged, _suddenShot, _turnsOk, _freebieOk, _animOk;
        private static string _turnResult = "not tried", _freebieResult = "not tried", _animResult = "not tried";

        /// <summary>During the race: quick turns counted once each, the freebie rule, the bot's cousin animating, pictures of the frame.</summary>
        private static void RaceChecks(float t)
        {
            var gm = GameRefs.Main;
            var core = gm?.GetCore();
            if (core == null) return;
            float now = Time.unscaledTime;
            // Three quick turns, the game's own way (MainGameCore.StartAction(OujiTurn), what the input does): three counted.
            if (t > 10f && _turnsStarted < 3 && now >= _nextTurnAt && !core.InOujiTurn)
            {
                core.StartAction(MainGameCore.eSpecialAction.OujiTurn);
                _turnsStarted++;
                _nextTurnAt = now + 1.5f;
            }
            if (_turnsStarted == 3 && _turnResult == "not tried" && now >= _nextTurnAt)
            {
                _turnsOk = RunStats.Turns == 3;
                _turnResult = $"3 quick turns made, {RunStats.Turns} counted";
                Log("quick turns: " + _turnResult);
            }
            // The bot's ghost katamari drawn at its real size (not its small story core floating at its centre's height).
            if (t > 12f && !_sizeChecked && Ghosts.DrawnSize(NetSession.BotId, out float drawnR, out float realR))
            {
                _sizeChecked = true;
                _sizeOk = realR > 0f && drawnR >= realR * 0.9f && drawnR <= realR * 1.6f;
                _sizeResult = $"drawn radius {drawnR:0.###} for a katamari radius of {realR:0.###} ({(realR > 0f ? drawnR / realR : 0f):0.##}x)";
                Log($"{(_sizeOk ? "PASS" : "FAIL")}: the bot's ghost size: {_sizeResult} | {Ghosts.DescribeGhosts()}");
                FrameCapture.Request("ghost_size");
            }
            // The run's timers (RunClocks): IGT counting with the run, never ahead of RTA; then the katamari grows past the
            // stage's goal (the game's own clear, MakeStageCleared) and the win-condition moment is recorded, here and
            // as the bot shows it (it mirrors yours a second later); a picture of the player bar.
            if (t > 16f && _timerCheck == 0)
            {
                _timerCheck = 1;
                // A big katamari puts the bot's ghost "overlapping you", where your ghost settings hide it (and its
                // cousin, which the pose checks compare): keep ghosts shown for the rest of the test (put back on quitting).
                _prevNear = GhostSettings.Near;
                _prevBlocking = GhostSettings.HideBlocking;
                GhostSettings.Near = 0;
                GhostSettings.HideBlocking = false;
                float igt = RunClocks.Igt, rta = RunClocks.Rta;
                _timersOk = RunClocks.Counting && igt > 8f && igt <= rta + 0.5f;
                _timerResult = $"at run time {t:0.0} s: {RunClocks.Describe()}";
                Log("timers: " + _timerResult);
                try { gm.AddKatamariSize(3000); Log($"grew to {KatamariState.SizeText(gm.KatamariSize)} for the win condition"); }
                catch (Exception e) { Log("growing failed: " + e.Message); }
            }
            if (t > 19f && _timerCheck == 1)
            {
                _timerCheck = 2;
                NetSession.Peers.TryGetValue(NetSession.BotId, out var bot);
                bool botShows = bot != null && bot.HasState && bot.State.ReachedWin;
                bool win = gm._hasCleared && RunClocks.ClearIgt >= 0f && RunClocks.ClearIgt <= RunClocks.ClearRta + 0.5f
                           && Mathf.Abs(RunClocks.ClearStage - gm._clearTime) < 0.01f;
                _timersOk = _timersOk && win && botShows;
                _timerResult += $" | goal reached {gm._hasCleared} (Size Time {RunClocks.Clock(gm._clearTime)}): {RunClocks.Describe()}; " +
                                $"the bot shows win {botShows} (IGT {RunClocks.Clock(bot?.State.ClearIgt ?? -1f)}, badge {bot?.State.HudRank})";
                Log($"{(_timersOk ? "PASS" : "FAIL")}: timers: {_timerResult}");
                FrameCapture.Request("hud_timers");
                // Ranking: live places by Standard. Both reached the goal, so the stage score (here the diameter) decides:
                // the bigger katamari is 1st whoever reached the goal first (the bot's size is its own, not yours). Then a
                // custom system through the Settings bytes and back, and it turned round (biggest last).
                var inputs = Ranking.LiveInputs();
                var live = Ranking.Places(Ranking.Current, inputs);
                int mySize = inputs.FirstOrDefault(x => x.Id == NetSession.MyId)?.Size ?? 0;
                int botSize = inputs.FirstOrDefault(x => x.Id == NetSession.BotId)?.Size ?? 0;
                byte bigger = botSize > mySize ? NetSession.BotId : NetSession.MyId;
                byte smaller = bigger == NetSession.BotId ? NetSession.MyId : NetSession.BotId;
                bool sizeStage = RunClocks.ScoreKindNow == ScoreKind.Size;
                bool placesOk = sizeStage && live.TryGetValue(bigger, out int bigPlace) && bigPlace == 1
                                && live.TryGetValue(smaller, out int smallPlace) && smallPlace == 2;
                var test = new RankingSystem { Title = "Test: biggest last" };
                test.Rules.Add(new RankRule(RankKey.Diameter, true));
                test.Rules.Add(new RankRule(RankKey.WinIgt));
                Ranking.Custom.Add(test);
                string before = Ranking.ActiveTitle;
                Ranking.ActiveTitle = test.Title;
                var bytes = Ranking.Write(new PacketWriter()).ToArray();
                Ranking.ActiveTitle = before;
                Ranking.Custom.Remove(test);
                Ranking.Read(new PacketReader(bytes, bytes.Length));
                var back = Ranking.ReceivedForTest;
                bool wireOk = back != null && back.Title == test.Title && Ranking.Describe(back) == Ranking.Describe(test);
                // Turned round (biggest last), the smaller katamari comes first.
                var turned = back != null ? Ranking.Places(back, inputs) : null;
                bool turnedOk = turned != null && turned.TryGetValue(smaller, out int sp) && sp == 1;
                Ranking.ForgetHost();
                _rankingOk = placesOk && wireOk && turnedOk && RunClocks.ScoreKindNow != ScoreKind.None && RunClocks.ScoreNow >= 0;
                _rankingResult = $"live places by \"{Ranking.Current.Title}\": you {(live.TryGetValue(NetSession.MyId, out int a) ? a : 0)} ({KatamariState.SizeText(mySize)}), " +
                                 $"bot {(live.TryGetValue(NetSession.BotId, out int c) ? c : 0)} ({KatamariState.SizeText(botSize)}), bigger first {placesOk}; " +
                                 $"your stage score {StageScore.KindName(RunClocks.ScoreKindNow)} {StageScore.Text(RunClocks.ScoreKindNow, RunClocks.ScoreNow)} (rating {RunClocks.RateNow}), " +
                                 $"bot's {StageScore.Text((ScoreKind)(bot?.State.ScoreKind ?? 0), bot?.State.Score ?? -1)}; " +
                                 $"custom system over the wire {wireOk} ({(back != null ? Ranking.Describe(back) : "none")}), turned round: smaller first {turnedOk}";
                Log($"{(_rankingOk ? "PASS" : "FAIL")}: ranking: {_rankingResult}");
            }
            // The Ranking tab, drawn for a picture (after the player bar's), then closed again.
            if (_timerCheck == 2 && t > 20.5f && _rankingTabAt <= 0f)
            {
                ModMenu.CurrentTab = "online";
                OnlinePage.ShowRanking = true;
                ModMenu.Open();
                _rankingTabAt = t;
            }
            if (_rankingTabAt > 0f && t > _rankingTabAt + 1.5f && !_rankingTabShot)
            {
                _rankingTabShot = true;
                FrameCapture.Request("menu_ranking");
            }
            if (_rankingTabShot && !_rankingTabClosed && t > _rankingTabAt + 3f)
            {
                _rankingTabClosed = true;
                ModMenu.Close();
                if (OnlinePage.RankingFrames == 0) { _rankingOk = false; _rankingResult += "; the Ranking tab wasn't drawn"; }
                Log($"Ranking tab drawn {OnlinePage.RankingFrames} frames");
            }
            // The host's freebie list (this run: only Drink). A Magnet is swapped for a Drink; then, with "taken
            // away", a Magnet given to an empty slot leaves it empty.
            if (t > 6f && !_freebieGiven)
            {
                _freebieGiven = true;
                try { Log("only Drink allowed, giving a Magnet: " + StageControl.GiveFreebie(eInstageItemType.Magnet)); }
                catch (Exception e) { Log("giving a freebie failed: " + e.Message); }
            }
            if (_freebieGiven && !_freebieChecked && t > 8f)
            {
                _freebieChecked = true;
                _swapOk = gm._nowInventoryItem == eInstageItemType.Drink && Lobby.Swapped > 0;
                _freebieResult = $"swapped: slot {gm._nowInventoryItem} ({Lobby.DescribeFreebies()})";
                Log("freebie 2 s later: " + _freebieResult);
                Lobby.OtherFreebies = 1;
                NetSession.SettingsChanged();
                try
                {
                    Log("now taken away; emptying the slot: " + StageControl.ClearFreebie());
                    Log("giving a Magnet again: " + StageControl.GiveFreebie(eInstageItemType.Magnet));
                }
                catch (Exception e) { Log("giving a freebie failed: " + e.Message); }
            }
            if (_freebieChecked && !_freebieChecked2 && t > 10f)
            {
                _freebieChecked2 = true;
                bool takenOk = gm._nowInventoryItem == eInstageItemType.NoItem;
                // The API's reimplemented freebies follow the host's list: with only Drink allowed they're all off.
                _sfOffOk = GamePatches.StoryFreebies.Enabled && !StoryFreebies.IsOn(eInstageItemType.Shoes) && !StoryFreebies.IsOn(eInstageItemType.Crown);
                _freebieResult += $" | taken away: slot {gm._nowInventoryItem} ({Lobby.DescribeFreebies()}) | reimplemented, only Drink allowed: " +
                                  $"patch {GamePatches.StoryFreebies}, Shoes {StoryFreebies.IsOn(eInstageItemType.Shoes)}, Crown {StoryFreebies.IsOn(eInstageItemType.Crown)}";
                // Then the host ticks Shoes: it comes on for the session, Crown stays off.
                Lobby.SetAllowed(eInstageItemType.Shoes, true);
                NetSession.SettingsChanged();
            }
            if (_freebieChecked2 && !_freebieChecked3 && t > 11.5f)
            {
                _freebieChecked3 = true;
                bool shoesOn = StoryFreebies.IsOn(eInstageItemType.Shoes) && !StoryFreebies.IsOn(eInstageItemType.Crown);
                Lobby.SetAllowed(eInstageItemType.Shoes, false);
                NetSession.SettingsChanged();
                bool takenOk = _freebieResult.Contains("taken away: slot NoItem");
                _freebieOk = _swapOk && takenOk && _sfOffOk && shoesOn;
                _freebieResult += $" | Shoes ticked: Shoes {StoryFreebies.IsOn(eInstageItemType.Shoes)}, Crown {StoryFreebies.IsOn(eInstageItemType.Crown)} ({Lobby.StoryFreebieState})";
                Log("freebie list: " + _freebieResult);
            }
            // The bot's cousin pushes its ball round you: its bones move.
            if (t > 14f && !_animSampled) { _animSampled = true; PoseCheck("stage", Ghosts.FirstCousinModel()); }
            if (t > 15f && _animSampled && !_animChecked)
            {
                _animChecked = true;
                _animResult = PoseCheck("stage", Ghosts.FirstCousinModel());
                _animOk = _animResult.StartsWith("animating");
                Log("bot's cousin in the stage: " + _animResult);
            }
            if (t > 12f && !_clockRunChecked && Ghosts.TryLocalState(out var racing))
            {
                _clockRunChecked = true;
                _clockRunOk = !racing.ClockStopped;
                _clockResult += $" | racing: yours stopped {racing.ClockStopped}";
                Log("clock while racing: stopped " + racing.ClockStopped);
            }
            // The bot's cousin as a mirror of your prince's Animator (PoseSync): live, then a pose held still.
            if (t > 24f && _poseStep == 0) { _poseStep = 1; NetSession.BotMirrorPose = 1; }
            if (t > 27f && _poseStep == 1)
            {
                _poseStep = 2;
                string m = PoseMirror(out bool same, out _, out _);
                _poseMirrorOk = same;
                _poseResult = $"live copy: {(same ? "same state" : "DIFFERENT state")} ({m})";
                Log("pose: " + _poseResult);
                NetSession.BotMirrorPose = 2;
            }
            if (t > 29.5f && _poseStep == 2)
            {
                _poseStep = 3;
                string m = PoseMirror(out bool same, out float at, out float speed);
                _poseHeldOk = same && Mathf.Abs(at - 0.35f) < 0.03f && speed < 1e-3f;
                _poseResult += $" | held at 0.35: {(_poseHeldOk ? "held" : "NOT held")} ({m}) | {PosePlayer.Describe()}";
                Log("pose held: " + m);
                NetSession.BotMirrorPose = 0;
            }
            // A chat line and a talk in the stage, so the race picture shows the bubble and talking marks there.
            if (t > 16.5f && !_stageBubbleSent)
            {
                _stageBubbleSent = true;
                _stageBubblesBefore = Chat.BubblesDrawn;
                Log($"chat in the stage: {Chat.Send("A bubble in a stage")}; {NetSession.BotTalk(4f)}");
            }
            // The game's frame during the race, and under Sudden Death.
            if (t > 20f && !_raceShot) { _raceShot = true; FrameCapture.Request("hud_race"); }
            if (t > 23f && !_raceLogged)
            {
                _raceLogged = true;
                Log("frame: " + FrameCapture.Last + " | where: " + WhereAll() +
                    $" | bubbles drawn in the stage: {Chat.BubblesDrawn - _stageBubblesBefore} frames ({Chat.LastBubble}) | {Voice.Describe(NetSession.BotId)}");
            }
            // Sudden Death only at the stage's end: the first moment it's on you, the stage must already have ended for
            // someone the game's own way (the bot's finish: not a DQ, not cut).
            if (!_suddenChecked && SyncRun.HurryActive(out _, out _))
            {
                _suddenChecked = true;
                var ended = SyncRun.Standings.Where(s => !s.Disqualified && !s.Forced).ToList();
                SyncRun.HurryActive(out int goal, out _);
                int sizeNow = GameRefs.Main?.KatamariSize ?? 0;
                _suddenOk = ended.Count > 0 && goal > sizeNow && goal <= sizeNow * 2 + 100;
                _suddenResult = $"target {KatamariState.SizeText(goal)} from {KatamariState.SizeText(sizeNow)} (reachable {goal <= sizeNow * 2 + 100}); ";
                _suddenResult += $"started at run time {t:0.0} s, after the stage ended for: {(ended.Count > 0 ? string.Join(", ", ended.Select(SyncRun.Describe)) : "nobody")}";
                Log($"{(_suddenOk ? "PASS" : "FAIL")}: Sudden Death at the stage's end: {_suddenResult}");
            }
            if (!_suddenShot && SyncRun.HurryActive(out _, out float left) && left < 12f)
            {
                _suddenShot = true;
                FrameCapture.Request("hud_sudden_death");
                Log("where (Sudden Death): " + WhereAll());
            }
        }

        /// <summary>Your prince's Animator state next to the bot's cousin's.</summary>
        private static string PoseMirror(out bool same, out float theirTime, out float puppetSpeed)
        {
            same = false;
            theirTime = -1f;
            puppetSpeed = -1f;
            var model = Ghosts.FirstCousinModel();
            var puppet = model != null ? model.GetComponentInChildren<Animator>() : null;
            var mine = GameRefs.Main?.GetOuji()?._animator;
            if (puppet == null || mine == null) return "no animator to compare";
            var a = mine.IsInTransition(0) ? mine.GetNextAnimatorStateInfo(0) : mine.GetCurrentAnimatorStateInfo(0);
            var b = puppet.IsInTransition(0) ? puppet.GetNextAnimatorStateInfo(0) : puppet.GetCurrentAnimatorStateInfo(0);
            same = a.fullPathHash == b.fullPathHash;
            theirTime = b.normalizedTime;
            puppetSpeed = puppet.speed;
            return $"yours {a.fullPathHash:x8} at {a.normalizedTime:0.00} speed {mine.speed:0.##}, the bot's cousin {b.fullPathHash:x8} at {b.normalizedTime:0.00} speed {puppet.speed:0.##}";
        }

        private static string PoseCheck(string name, GameObject model)
        {
            if (model == null) return "no cousin shown";
            float d = PoseProbe.Sample(name, model);
            return d < 0f ? "first look" : d > 0.5f ? $"animating ({d:0.0} degrees of bone change)" : $"NOT animating ({d:0.00} degrees)";
        }

        private static string WhereAll() =>
            "you: " + Whereabouts.Mine() + " | " + string.Join(" | ", NetSession.Peers.Values.Select(p => $"{p.Name}: {Whereabouts.Of(p)}"));

        private static bool _promptShown, _promptShot, _promptOk, _rulesOk, _voteRestartOk, _lockOk, _forceRestartOk, _forcePlazaOk;
        private static bool _pauseOpened, _gameConfirmOpened, _pauseShot, _pausePromptOk, _cancelOk, _resumeOk, _onlyMeOk, _pauseOk;
        private static bool _restartHooked, _dqListed, _dqBlocked, _dqOk;
        private static int _pauseTries;
        private static int _restartsSeen, _restartsBefore;
        private static byte _runBefore;

        private static string TryVote(VoteKind kind)
        {
            try { return Votes.Propose(kind, Ghosts.CurrentMapKey); }
            catch (Exception e) { return "refused: " + e.Message; }
        }

        /// <summary>Who's who: your identity, everyone's as the host sees them, and a Steam ticket check of your own.</summary>
        private static string Identity()
        {
            var me = NetSession.MyIdentity;
            string peers = string.Join(", ", NetSession.Peers.Values.Select(p => $"{p.Name}: {p.Identity} {(p.Verified ? "verified" : p.VerifyNote)}"));
            string own = "no Steam";
            if (me.IsSteam)
            {
                var t = OUAKModAPI.Steam.CreateTicket();
                if (t == null) own = "no ticket";
                else
                {
                    var res = OUAKModAPI.Steam.Verify(t.Data, me.Value);
                    own = $"own ticket ({t.Data.Length} bytes) checked: {res}";
                    if (res == SteamAuthResult.OK) OUAKModAPI.Steam.EndVerify(me.Value);
                    OUAKModAPI.Steam.CancelTicket(t);
                }
            }
            return $"you {me}, peers [{peers}], {own}";
        }

        private static string TryPropose()
        {
            try
            {
                var entry = StageCatalog.Entries.FirstOrDefault(e => e.Kind == MapKind.Stage && e.Internal == _runStage);
                return entry == null ? "no stage" : Votes.Propose(VoteKind.GoToStage, Ghosts.KeyFor(entry));
            }
            catch (Exception e) { return "refused: " + e.Message; }
        }

        /// <summary>The plaza of the era you're in (else the first one), not DLC.</summary>
        private static MapEntry PlazaEntry()
        {
            int idx = 0;
            try { idx = GameRefs.Save._stageIndex; } catch { }
            var plazas = StageCatalog.Entries.Where(e => e.Kind == MapKind.Plaza && !string.IsNullOrEmpty(e.PlazaKey) && !e.NeedsAA).ToList();
            MapEntry here = null;
            foreach (var e in plazas)
            {
                try { if (SelectHirobaManager.GetStageIndex(e.PlazaKey) == idx) { here = e; break; } } catch { }
            }
            return here ?? plazas.FirstOrDefault();
        }

        private static bool _prevShare = true, _forcedPickup, _shotsTaken;
        private static float _nextGrow, _nextGrowLog;
        private static readonly System.Random Rng = new(7);
        private static int _grown;

        /// <summary>Rolls the nearest object you could up onto your ball (placed on its surface, the way contact does).</summary>
        private static void Grow()
        {
            try
            {
                var gm = GameRefs.Main;
                var core = gm?.GetCore();
                if (core == null) return;
                var c = core.transform.position;
                uint key = Ghosts.FindObjectNear(c, Mathf.Max(2f, core.KatamariRadius * 60f), gm.KatamariSize);
                if (key == 0 || !Ghosts.TryGetObject(key, out var mono)) return;
                var dir = new Vector3((float)Rng.NextDouble() - 0.5f, (float)Rng.NextDouble() - 0.3f, (float)Rng.NextDouble() - 0.5f);
                if (dir.sqrMagnitude < 1e-4f) dir = Vector3.up;
                mono.transform.position = c + dir.normalized * core.KatamariRadius;
                core.GetTargetMono(mono);
                _grown++;
            }
            catch (Exception e) { Log("grow failed: " + e.Message); _nextGrow = float.MaxValue; }
        }

        /// <summary>Our size text next to the game's own (ResultUtilityFunction.SizeToString, given millimetres).</summary>
        private static string CompareSizeText()
        {
            try
            {
                int size = GameRefs.Main?.KatamariSize ?? 0;
                string ours = KatamariState.SizeText(size);
                string game = ResultUtilityFunction.SizeToString(size / 10, false);
                string plain = new string(game.Where(char.IsLetterOrDigit).ToArray());
                return $"size text: raw {size}, ours \"{ours}\", the game's \"{game}\" → {(plain == ours ? "same" : "DIFFERENT")}";
            }
            catch (Exception e) { return "size text check failed: " + e.Message; }
        }

        private static string DescribeGrowth()
        {
            try
            {
                var gm = GameRefs.Main;
                var core = gm.GetCore();
                var ouji = gm.GetOuji();
                var ot = ouji != null ? ouji.transform : null;
                float princeScale = ot != null ? ot.lossyScale.x : 0f;
                string parent = ot != null && ot.parent != null ? ot.parent.name : "none";
                return $"growth: {_grown} rolled, size {KatamariState.SizeText(gm.KatamariSize)}, radius {core.KatamariRadius:0.####}, core scale {core.KatamariScale:0.####}, " +
                       $"prince scale {princeScale:0.######} (parent {parent}, core's scaleOuji {core.scaleOuji.x:0.######}), " +
                       $"prince per metre of size {(gm.KatamariSize > 0 ? princeScale / gm.KatamariSize * 10000f : 0f):0.######}";
            }
            catch (Exception e) { return "growth: " + e.Message; }
        }

        private static int _gsStep;
        private static float _gsNext;
        private static bool _gsShown, _gsBlocking;
        private static int _gsNear, _gsFar, _gsMax;
        private static readonly List<string> GsResults = new();

        /// <summary>Steps through the ghost settings one second apart, checking the ghost is drawn exactly when it should be.</summary>
        private static void GhostSettingsCheck(float now)
        {
            if (_gsStep > 5 || now < _gsNext) return;
            _gsNext = now + 1f;
            float gap = Ghosts.FirstGhostGap();
            bool shown = Ghosts.ShownGhosts > 0;
            switch (_gsStep++)
            {
                case 0:
                    _gsShown = GhostSettings.Shown; _gsNear = GhostSettings.Near; _gsFar = GhostSettings.Far;
                    _gsMax = GhostSettings.Max; _gsBlocking = GhostSettings.HideBlocking;
                    GhostSettings.Near = 0; GhostSettings.Far = 0; GhostSettings.HideBlocking = false;
                    GhostSettings.Shown = false;
                    break;
                case 1:
                    GsResults.Add($"off: {(shown ? "FAIL (still shown)" : "ok")}");
                    GhostSettings.Shown = true;
                    break;
                case 2:
                    GsResults.Add($"on: {(shown ? "ok" : "FAIL (not shown)")}");
                    GhostSettings.Far = 1;   // beyond 10x your size
                    break;
                case 3:
                    GsResults.Add($"far 10x (gap {gap:0.#}x): {(shown == !(gap > 10f) ? "ok" : "FAIL")} ({(shown ? "shown" : "hidden")})");
                    GhostSettings.Far = 0;
                    GhostSettings.Near = 4;  // within 10x your size
                    break;
                case 4:
                    GsResults.Add($"near 10x (gap {gap:0.#}x): {(shown == !(gap < 10f) ? "ok" : "FAIL")} ({(shown ? "shown" : "hidden")})");
                    GhostSettings.Shown = _gsShown; GhostSettings.Near = _gsNear; GhostSettings.Far = _gsFar;
                    GhostSettings.Max = _gsMax; GhostSettings.HideBlocking = _gsBlocking;
                    break;
                default:
                    Log("ghost settings: " + string.Join(" | ", GsResults));
                    break;
            }
        }

        // ---------------------------------------------------------------- test pictures

        /// <summary>
        /// Renders the ghost katamari and your own (same framing per radius) and one shot of both, into
        /// UserData/OUAKModMenu/TestShots. Only the game's 3D world is drawn (a disabled camera's Render into a
        /// texture): no desktop capture, and no stripped engine calls (PNG written here, not by EncodeToPNG).
        /// </summary>
        private static string TakeShots()
        {
            try
            {
                var core = GameRefs.Main?.GetCore();
                if (core == null) return "shots: no katamari";
                var me = core.transform.position;
                float myR = Mathf.Max(0.001f, core.KatamariRadius);
                var parts = new List<string>();
                if (Ghosts.TryFirstGhost(out var ghost, out float gR))
                {
                    parts.Add(Shot("ghost_front", ghost, gR * 7f, new Vector3(1f, 0.55f, 1f)));
                    parts.Add(Shot("ghost_back", ghost, gR * 7f, new Vector3(-1f, 0.55f, -1f)));
                    var mid = (ghost + me) * 0.5f;
                    float apart = (ghost - me).magnitude;
                    parts.Add(Shot("both", mid, Mathf.Max(apart * 1.1f, gR * 10f), new Vector3(0.2f, 0.8f, -1f)));
                }
                else parts.Add("no ghost on screen");
                parts.Add(Shot("you", me, myR * 7f, new Vector3(1f, 0.55f, 1f)));
                return "shots: " + string.Join(" | ", parts) + " | ghosts: " + Ghosts.DescribeGhosts();
            }
            catch (Exception e) { return "shots failed: " + e.Message; }
        }

        private static string Shot(string name, Vector3 target, float dist, Vector3 dir)
        {
            const int W = 960, H = 540;
            GameObject camGo = null;
            RenderTexture rt = null;
            Texture2D tex = null;
            Camera cam = null;
            var prev = RenderTexture.active;
            try
            {
                rt = new RenderTexture(W, H, 24) { hideFlags = HideFlags.HideAndDontSave };
                camGo = new GameObject("OUAK test shot camera");
                cam = camGo.AddComponent<Camera>();
                cam.enabled = false;
                var main = Camera.main;
                cam.cullingMask = main != null ? main.cullingMask : ~0;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.55f, 0.7f, 0.9f, 1f);
                cam.fieldOfView = 40f;
                cam.nearClipPlane = Mathf.Max(0.0002f, dist * 0.02f);
                cam.farClipPlane = Mathf.Max(10f, dist * 300f);
                cam.targetTexture = rt;
                camGo.transform.position = target + dir.normalized * dist;
                camGo.transform.LookAt(target);
                cam.Render();
                RenderTexture.active = rt;
                tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0f, 0f, W, H), 0, 0);
                tex.Apply();
                var px = tex.GetPixels32();
                string folder = Path.Combine(MelonEnvironment.UserDataDirectory, "OUAKModMenu", "TestShots");
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, name + ".png");
                File.WriteAllBytes(path, Png(px, W, H));
                return name + " saved";
            }
            catch (Exception e) { return $"{name} failed: {e.Message}"; }
            finally
            {
                RenderTexture.active = prev;
                try { if (cam != null) cam.targetTexture = null; } catch { }
                try { if (camGo != null) UnityEngine.Object.Destroy(camGo); } catch { }
                try { if (rt != null) UnityEngine.Object.Destroy(rt); } catch { }
                try { if (tex != null) UnityEngine.Object.Destroy(tex); } catch { }
            }
        }

        /// <summary>A plain RGBA PNG (rows flipped: ReadPixels starts at the bottom).</summary>
        internal static byte[] Png(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Color32> px, int w, int h) => OUAKModAPI.Png.Encode(px, w, h);

        private static void Chunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4];
            BigEndian(len, 0, data.Length);
            s.Write(len);
            var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            s.Write(typeBytes);
            s.Write(data);
            uint crc = Crc(Crc(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
            var c = new byte[4];
            BigEndian(c, 0, (int)crc);
            s.Write(c);
        }

        private static void BigEndian(byte[] b, int at, int v)
        {
            b[at] = (byte)(v >> 24);
            b[at + 1] = (byte)(v >> 16);
            b[at + 2] = (byte)(v >> 8);
            b[at + 3] = (byte)v;
        }

        private static uint[] _crcTable;

        private static uint Crc(uint crc, byte[] data)
        {
            if (_crcTable == null)
            {
                _crcTable = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    _crcTable[n] = c;
                }
            }
            foreach (byte b in data) crc = _crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }
        private static float _runStartAt;
        private static List<MainGameMonoBase> _hiddenList = new();

        /// <summary>Rolls up the nearest object you could, the way touching it does, and reports how it was shared.</summary>
        private static string ForcePickup()
        {
            try
            {
                var gm = GameRefs.Main;
                var core = gm?.GetCore();
                if (core == null) return "forced pickup: no katamari";
                uint key = Ghosts.FindObjectNear(core.transform.position, Mathf.Max(2f, core.KatamariRadius * 40f), gm.KatamariSize);
                if (key == 0 || !Ghosts.TryGetObject(key, out var mono)) return "forced pickup: nothing small enough nearby";
                int sentBefore = Ghosts.SentPickups;
                core.GetTargetMono(mono);
                var att = core.MonoAttachment;
                var t = mono.transform;
                bool onAttacher = att != null && t.parent == att.transform;
                return $"forced pickup of {mono.name} (needs {KatamariState.SizeText(Ghosts.RequireSize(mono))}, you're {KatamariState.SizeText(gm.KatamariSize)}): " +
                       $"on your attacher {onAttacher}, sent at once {Ghosts.SentPickups > sentBefore}, local pose {t.localPosition} / {t.localRotation.eulerAngles}";
            }
            catch (Exception e) { return "forced pickup failed: " + e.Message; }
        }

        private static void LogPhase()
        {
            string p = $"phase {SyncRun.Phase}, on black {IntroSync.OnBlack}, my result {(SyncRun.MyResult != null ? SyncRun.Describe(SyncRun.MyResult) : "none")}, " +
                       $"results in {SyncRun.Standings.Count}, final {SyncRun.StandingsFinal}, hurried [{string.Join(",", SyncRun.Hurried)}]";
            if (p == _lastPhase) return;
            _lastPhase = p;
            Log(p);
        }

        /// <summary>SelectDataUIEnter.OnUpdate for a slot with a game in progress, which goes to the ship (as TrainingTest does).</summary>
        private static void LoadLastUsedSlot(SaveDataController ctrl)
        {
            var data = ctrl.GameData;
            int slot = data._SystemData._LastUser;
            var users = data._UserData?._UserDatas;
            if (users == null || slot < 0 || slot >= users.Length) throw new InvalidOperationException($"no last-used slot ({slot})");
            var progress = users[slot]?._PriorityData;
            if (progress == null || progress._GameProgression <= 0) throw new InvalidOperationException($"slot {slot + 1} has no game in progress");

            BackupManager.Create("Online run test", $"made before the online run self-test loaded slot {slot + 1}");
            Log($"loading slot {slot + 1}");
            ctrl.SelectLoadDataToGameData(slot);
            var save = GameRefs.Save;
            save.SetUseDataLogoMonoGetFlg();
            OujiShipSponePointList.s_SponeIndex = OujiShipSponePointList.ElevatorSpone;
            save.FrontSceneSelectData = true;
            SingletonMonoBehaviour<EncorsSaveDataChecker>.Instance.CheckEncorsContentsData();
            GameRefs.Sound?.StopBGM(0.5f);
            Il2Cpp.SceneManager.LoadSceneAsync("OujiShip", UnityEngine.SceneManagement.LoadSceneMode.Single, true);
        }

        /// <summary>
        /// The leaderboard record of this run (made when the results became final) against the run's own numbers, and the
        /// local stand-in's four boards worked out from it under each rule option (undecided rules: empty boards).
        /// </summary>
        private static void LeaderboardCheck()
        {
            try
            {
                var rec = Leaderboards.LeaderboardRecorder.Last;
                var mine = SyncRun.Standings.FirstOrDefault(r => r.Id == NetSession.MyId);
                var places = Ranking.StandingsPlaces(SyncRun.Standings);
                int cousin = Looks.CharacterOf(NetSession.MyLook);
                bool recOk = rec != null && mine != null && rec.Mode == Leaderboards.RunMode.Session && rec.Size == mine.Size
                             && rec.Cousin == cousin && rec.Place == (places.TryGetValue(NetSession.MyId, out int pl) ? pl : -1)
                             && rec.PlayersInRun == SyncRun.Standings.Count && rec.StageName.Length > 0 && rec.Player.Id.Length > 0;
                string folder = Leaderboards.LeaderboardRecorder.Folder;
                var undecided = new Leaderboards.LocalLeaderboardService(folder, new Leaderboards.BoardRules(), rec?.Player.Id);
                bool emptyOk = Enum.GetValues(typeof(Leaderboards.Board)).Cast<Leaderboards.Board>()
                    .All(b => undecided.GetBoardAsync(b, null).Result.Entries.Count == 0);
                var rules = new Leaderboards.BoardRules
                {
                    Katamari = Leaderboards.KatamariRule.FinalSizeOfEveryRun, Cousins = Leaderboards.CousinRule.RunsPlayedAs,
                    Presents = Leaderboards.PresentRule.WornInRuns, Cookies = new OneCookiePerRun(),
                };
                var svc = new Leaderboards.LocalLeaderboardService(folder, rules, rec?.Player.Id);
                var kat = svc.GetBoardAsync(Leaderboards.Board.EveryonesKatamari, null).Result;
                var cook = svc.GetBoardAsync(Leaderboards.Board.Cookies, null).Result;
                var cous = svc.GetBoardAsync(Leaderboards.Board.Cousins, null).Result;
                var pres = svc.GetBoardAsync(Leaderboards.Board.Presents, null).Result;
                string myCousinKey = cousin == 999 ? "custom" : cousin.ToString();
                bool boardsOk = rec != null && kat.Total == rec.Size && kat.Entries.FirstOrDefault()?.IsYou == true
                                && cook.Total == 1 && cous.Entries.FirstOrDefault()?.Key == myCousinKey
                                && pres.Total == rec.Presents.Distinct().Count();
                // The same record again counts once.
                var again = svc.SubmitRunsAsync(new[] { rec }).Result;
                bool dupOk = again.Duplicates == 1 && again.Accepted == 0;
                // The user's cookie condition: a session run with at least one other real player; this run had only the bot.
                bool cookieRuleOk = rec != null && rec.OtherHumans == 0 && !Leaderboards.LeaderboardRules.EarnsCookies(rec);
                // The user's split: this session run (bot only) counts on none of the decided boards (cookies need another
                // real player; the other three are single player only).
                var decided = new Leaderboards.LocalLeaderboardService(folder, Leaderboards.LeaderboardRules.Decided, rec?.Player.Id);
                bool splitOk = new[] { Leaderboards.Board.EveryonesKatamari, Leaderboards.Board.Cousins, Leaderboards.Board.Presents }
                    .All(b => decided.GetBoardAsync(b, null).Result.Entries.Count == 0);
                _boardsOk = recOk && emptyOk && boardsOk && dupOk && cookieRuleOk && splitOk;
                _boardsResult = $"record {(rec == null ? "none" : $"{rec.Mode} {rec.StageName} size {KatamariState.SizeText(rec.Size)} (result {KatamariState.SizeText(mine?.Size ?? 0)}), cousin {rec.Cousin} (look {cousin}), presents [{string.Join(",", rec.Presents)}], place {rec.Place}, verified {rec.HostVerified}, {rec.Objects} objects")}; " +
                                $"undecided rules give empty boards {emptyOk}; boards: everyone's katamari {KatamariState.SizeText((int)kat.Total)}, cookies {cook.Total}, " +
                                $"top cousin {cous.Entries.FirstOrDefault()?.Key}, presents counted {pres.Total}; the same record twice counts once {dupOk}; with only the bot it earns no cookies {cookieRuleOk} (other humans {rec?.OtherHumans}); single-player-only boards leave this session run out {splitOk}";
                Log($"{(_boardsOk ? "PASS" : "FAIL")}: leaderboards (local stand-in): {_boardsResult}");
            }
            catch (Exception e)
            {
                _boardsOk = false;
                _boardsResult = "failed: " + e.Message;
                Log("FAIL: leaderboards: " + _boardsResult);
            }
        }

        /// <summary>A made-up cookie rule for the test only (the real one is the user's call).</summary>
        private sealed class OneCookiePerRun : Leaderboards.ICookieRule
        {
            public string Id => "test: one per run";
            public int CookiesFor(Leaderboards.RunRecord run) => 1;
        }

        private static void Quit(string result)
        {
            if (_recordingSet)
            {
                Leaderboards.LeaderboardRecorder.Enabled = _prevRecording;
                try { if (Directory.Exists(Leaderboards.LeaderboardRecorder.Folder)) Directory.Delete(Leaderboards.LeaderboardRecorder.Folder, true); } catch { }
                Leaderboards.LeaderboardRecorder.Folder = _prevRecordFolder;
            }
            Log(result);
            if (_prevNear >= 0) { GhostSettings.Near = _prevNear; GhostSettings.HideBlocking = _prevBlocking; }
            if (_suddenSet) Lobby.SuddenDeath = _prevSuddenOn;
            try { NetSession.Leave(null); } catch { }
            NetSession.SharePickups = _prevShare;
            _armed = false;
            Log("DONE, quitting");
            Application.Quit();
        }

        private static void StartMapPhase(float now)
        {
            Log(NetSession.Host(HostPort, "SelfTest host", "", thisPcOnly: true, remember: false));
            Log(NetSession.StartBot());
            Log("waiting for a stage or match...");
            _mapDeadline = now + 240f;
            _step = 3;
            _next = now + 1f;
        }

        private static void Finish()
        {
            NetSession.Leave(null);
            _armed = false;
            Log("DONE");
        }

        private static string Peers() => string.Join(", ", NetSession.Peers.Values.Select(p =>
            $"{p.Id}:{p.Name} state={p.HasState} map={p.MapKey} ping={p.PingMs:0}"));

        private static void Log(string s) => MelonLogger.Msg("[OnlineTest] " + s);
    }
}
