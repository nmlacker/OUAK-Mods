using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppApp.KatamariSin;
using MelonLoader;
using UnityEngine;
using OUAKModAPI;

namespace OUAKMultiplayer
{
    internal enum RunPhase { None, Waiting, Countdown, Running, Finished }

    internal sealed class RunResult
    {
        public byte Id;
        public int Size;
        /// <summary>In-game time (the stage clock, which stops while that player pauses).</summary>
        public float Time;
        /// <summary>Real time from the shared start (curtain open) to finishing, on the host's clock; -1 if unknown.</summary>
        public float RealTime = -1f;
        public bool Cleared, TimeAttack, Restarted;
        /// <summary>Cut off: the lobby was waiting and they stopped growing for 30 s, so their game ended the stage.</summary>
        public bool Forced;
        /// <summary>Time attack time predicted for a cut-off player: their time + the fastest finish - 30 s.</summary>
        public bool Predicted;
        /// <summary>They left the race before finishing (back to the Plaza on their own): disqualified, listed last.</summary>
        public bool Disqualified;
        /// <summary>Host's checks: 1 late start, 2 size doesn't match what they streamed, 4 more game time than real time.</summary>
        public byte Verdict;
        /// <summary>
        /// The game's own stage rating, as on its result screen: MainGameManager.CheckClearRate(), stored by the
        /// game as LastStageResultData.ClearedRate and drawn as badge common_rank01 + Rank. It counts the stage's
        /// five targets reached (by that stage's rule: size, time, count, price…) and returns 5 - count, with
        /// 0-1 targets both giving 4: so 0 is the best badge and 4 the lowest. -1 if unknown (matches).
        /// </summary>
        public sbyte Rank = -1;
        /// <summary>The rank badge the stage HUD shows (MainGameManager.GetNowClearRateIcon(), an index into its RankSprites).</summary>
        public byte RankIcon;
        /// <summary>
        /// "Size Time" on the game's result screen: the stage clock when the goal was reached
        /// (MainGameManager._clearTime, set by MakeStageCleared from _playTime). -1 if the goal wasn't reached.
        /// </summary>
        public float SizeTime = -1f;
        /// <summary>In-game time of the run (RunClocks.Igt), and IGT / RTA when the win condition was reached (-1 not reached).</summary>
        public float Igt = -1f, ClearIgt = -1f, ClearRta = -1f;
        /// <summary>The stage's own score at the end (StageScore): its kind and number, -1 = none.</summary>
        public byte ScoreKind;
        public int Score = -1;
        public int Dashes, Turns, Freebies, Objects;
        public float Distance;
        public readonly List<(int size, float time)> Steps = new();

        public string Status => Disqualified ? "DQ" : Cleared ? "CLEAR" : Forced ? "CUT" : "FAIL";
        public float RaceTime => RealTime >= 0f ? RealTime : Time;

        private byte FlagByte => (byte)((Cleared ? 1 : 0) | (TimeAttack ? 2 : 0) | (Restarted ? 4 : 0) | (Forced ? 8 : 0) | (Predicted ? 16 : 0) | (Disqualified ? 32 : 0));

        public PacketWriter Write(PacketWriter w)
        {
            w.I32(Size).F32(Time).F32(RealTime).U8(FlagByte).U8((byte)Rank).U8(RankIcon).F32(SizeTime)
             .U16((ushort)Math.Clamp(Dashes, 0, 65535)).U16((ushort)Math.Clamp(Turns, 0, 65535))
             .U16((ushort)Math.Clamp(Freebies, 0, 65535)).U16((ushort)Math.Clamp(Objects, 0, 65535)).F32(Distance);
            int n = Math.Min(Steps.Count, 16);
            w.U8((byte)n);
            for (int i = 0; i < n; i++) w.I32(Steps[i].size).F32(Steps[i].time);
            w.F32(Igt).F32(ClearIgt).F32(ClearRta).U8(ScoreKind).I32(Score);
            return w;
        }

        public static RunResult Read(PacketReader r)
        {
            var res = new RunResult { Size = r.I32(), Time = r.F32(), RealTime = r.F32() };
            if (res.RealTime < 0f) res.RealTime = -1f;
            byte f = r.U8();
            res.Cleared = (f & 1) != 0;
            res.TimeAttack = (f & 2) != 0;
            res.Restarted = (f & 4) != 0;
            res.Forced = (f & 8) != 0;
            res.Predicted = (f & 16) != 0;
            res.Disqualified = (f & 32) != 0;
            res.Rank = (sbyte)Math.Clamp((int)(sbyte)r.U8(), -1, 15);
            res.RankIcon = r.U8();
            res.SizeTime = r.F32();
            if (res.SizeTime < 0f) res.SizeTime = -1f;
            res.Dashes = r.U16();
            res.Turns = r.U16();
            res.Freebies = r.U16();
            res.Objects = r.U16();
            res.Distance = r.F32();
            int n = Math.Min((int)r.U8(), 16);
            for (int i = 0; i < n; i++) res.Steps.Add((r.I32(), r.F32()));
            res.Igt = r.F32();
            res.ClearIgt = r.F32();
            res.ClearRta = r.F32();
            if (res.Igt < 0f) res.Igt = -1f;
            if (res.ClearIgt < 0f) res.ClearIgt = -1f;
            if (res.ClearRta < 0f) res.ClearRta = -1f;
            res.ScoreKind = r.U8();
            res.Score = Math.Max(-1, r.I32());
            return res;
        }
    }

    /// <summary>
    /// A stage played together, the way Katamari Ball handles it: one host owns the clock (the start moment and
    /// the standings), and each player's own game owns their katamari, size and score.
    /// - The host starting a stage (a fresh load, or a restart once the last race is over) begins a run. On a
    ///   fresh load the host opens a quick vote to skip the opening fly-over (IntroSync). Anyone restarting
    ///   their own game mid-race just carries on (their result says "restarted").
    /// - IntroSync holds each game where the King's opening talk would begin, on black, and reports Ready. When
    ///   everyone is ready (or after 30 s) the host sends Go: a start time 3 s ahead on its clock. Everyone
    ///   counts down and, at that moment, the curtain opens and the King's talk begins. Race (default): skip the
    ///   King to get going sooner. Strict: everyone hears the King's whole talk.
    /// - Clock: clients estimate the host's clock from Ping/Pong (the lowest-latency recent sample wins).
    /// - Results: when your run ends (clear demo or game over; in matches, time up) your game sends its own
    ///   final size, times, the stage rank and Size Time from its own result values, and after-action stats,
    ///   until the host's standings include them. The host
    ///   checks each result against what that player streamed and real time since the start (Verdict).
    /// - Sudden Death (the lobby settings: on/off, how long; the user's rule, 2026-11-07): only at the stage's end,
    ///   only for the slowest. Once the first player's run has ended the stage's own way (their time ran out, or
    ///   the goal ended a stage that ends on it; not by leaving or by being cut), anyone still playing (they
    ///   paused, restarted or started late) must reach their next growth size within the window (30 s by
    ///   default; it restarts at every size reached). If not, their game ends the stage and records their size;
    ///   in time attack their time is predicted as their time + the fastest finish - the window.
    ///   Off: everyone plays the stage out like single-player: their run ends when their own time runs out,
    ///   when the goal ends it, or when they leave the stage. Everyone is ranked together; the status
    ///   (CLEAR / FAIL / CUT / DQ) shows how each run ended.
    /// </summary>
    internal static class SyncRun
    {
        // ResultsTimeout: a safety only (a game that stopped sending without leaving); runs end the game's own way or by
        // Sudden Death, so the results wait for everyone still playing, like single-player stages do.
        private const float CountdownLead = 3f, ReadyTimeout = 30f, NoGoTimeout = 45f, ResultsTimeout = 600f, SkipVoteSeconds = 8f;

        /// <summary>Host option: run stages together (start, results, standings).</summary>
        public static bool StartTogether = true;

        /// <summary>
        /// Race (default): after the shared start, each player can skip the King's talk to get going sooner,
        /// like single-player. Strict: the King's opening talk can't be skipped, so everyone starts together.
        /// </summary>
        public static bool RaceStart = true;

        /// <summary>The "skip the opening fly-over" vote passed for the current stage start.</summary>
        public static bool SkipIntroVoted;

        public static RunPhase Phase { get; private set; }
        public static byte RunId { get; private set; }
        public static int RunMap { get; private set; }
        public static float StartAt { get; private set; }
        public static readonly List<RunResult> Standings = new();
        public static bool StandingsFinal { get; private set; }
        public static float StandingsShownUntil { get; private set; }
        public static RunResult MyResult { get; private set; }
        public static int ReadyCount => _readyAt.Count + (_hostReady ? 1 : 0);
        public static readonly HashSet<byte> Hurried = new();

        // Clock (clients): host time = local unscaled time + offset.
        private static float _offset, _bestRtt = float.MaxValue;
        private static readonly Queue<(float rtt, float offset)> Samples = new();

        // Host bookkeeping.
        private static readonly Dictionary<byte, float> _readyAt = new();
        private static readonly Dictionary<byte, int> _lastStreamedSize = new();
        private static bool _hostReady;
        private static float _waitingSince, _firstResultAt, _nextGo, _nextStandings, _finishedAt, _nextHurry, _hurrySince;

        // Local state.
        private static int _expectMap;
        /// <summary>Client: the run that was going when a new one was announced; its late repeats are ignored.</summary>
        private static byte _staleRun;
        // Host: a run called off before it started, told to everyone a few times.
        private static float _awaySince, _nextCancel;
        private static int _cancelRepeats;
        private static byte _cancelledRun;
        private static float _blackSince, _nextReady, _nextResult;
        private static bool _sawRestart, _hurry, _showAar;
        private static int _hurryTarget;
        private static float _hurryDeadline, _hurryBest = -1f, _hurryBestReal = -1f, _hurryWindow = 30f;

        public static float HostNow => Time.unscaledTime + (NetSession.Mode == NetSession.Role.Client ? _offset : 0f);

        /// <summary>A race is on: started or about to, and its results aren't final yet.</summary>
        public static bool RaceOn => RunMap != 0 && Phase is RunPhase.Waiting or RunPhase.Countdown or RunPhase.Running && !StandingsFinal;

        /// <summary>The run this game left before finishing (disqualified), or 0.</summary>
        public static byte LeftRunId { get; private set; }

        /// <summary>You left the race that's still on: you're disqualified and can't start votes until it's over.</summary>
        public static bool LeftThisRun => LeftRunId != 0 && LeftRunId == RunId && RaceOn;

        /// <summary>Host: has this player left the race that's still on (disqualified)?</summary>
        public static bool IsDisqualified(byte id) => RaceOn && Standings.Any(s => s.Id == id && s.Disqualified);

        private static int _dqSends;
        private static float _nextDqSend;

        /// <summary>
        /// You leave the race on your own (the pause menu's "Only me" back to the Plaza). Before finishing, that's a
        /// disqualification: your result is a DQ at the size you had, the host lists it last and the race goes on
        /// without you. Until it's over you can't start votes (nothing you do can interrupt it). Already finished:
        /// nothing changes.
        /// </summary>
        public static void LeaveRace()
        {
            if (!NetSession.Connected || !RaceOn || RunMap != Ghosts.CurrentMapKey) return;
            LeftRunId = RunId;
            if (MyResult != null) return;
            var gm = GameRefs.Main;
            RunResult r;
            try { r = gm != null ? CurrentResult(gm, false) : new RunResult { Id = NetSession.MyId }; }
            catch { r = new RunResult { Id = NetSession.MyId }; }
            r.Cleared = false;
            r.Disqualified = true;
            r.SizeTime = -1f;
            MyResult = r;
            FinishLocal();
            _dqSends = 6;
            _nextDqSend = 0f;
            MelonLogger.Msg($"[Online] You left run {RunId}: disqualified ({Describe(r)}).");
        }
        public static float BestRttMs => _bestRtt < float.MaxValue ? _bestRtt * 1000f : -1f;

        public static void Reset()
        {
            LeftRunId = 0;
            _dqSends = 0;
            Phase = RunPhase.None;
            RunMap = 0;
            _expectMap = 0;
            _readyAt.Clear();
            _lastStreamedSize.Clear();
            _hostReady = false;
            MyResult = null;
            SkipIntroVoted = false;
            ClearHurry();
            RunStats.Stop();
            _offset = 0f;
            _bestRtt = float.MaxValue;
            Samples.Clear();
        }

        private static void ClearHurry()
        {
            _hurry = false;
            Hurried.Clear();
            _hurryBest = _hurryBestReal = -1f;
            _hurrySince = 0f;
        }

        // ---------------------------------------------------------------- clock

        /// <summary>Client: a Pong from the host carrying its clock.</summary>
        public static void ClockSample(float rtt, float hostTime, float now)
        {
            if (rtt < 0f || rtt > 5f) return;
            Samples.Enqueue((rtt, hostTime + rtt * 0.5f - now));
            while (Samples.Count > 8) Samples.Dequeue();
            var best = Samples.OrderBy(s => s.rtt).First();
            _bestRtt = best.rtt;
            _offset = best.offset;
        }

        // ---------------------------------------------------------------- stage starts (from IntroSync)

        /// <summary>Your stage just started: a fresh load, or a restart of the same stage.</summary>
        public static void OnStageCycle(int map, bool fresh)
        {
            // The host's Force (or a restart vote that passed): a new run for everyone, whatever the phase.
            bool forced = !fresh && NetSession.Mode == NetSession.Role.Host && HostAuthority.TakeForcedRestart();
            // "Only me": nobody else restarts, and the race (if any) goes on.
            bool onlyMe = !fresh && !forced && HostAuthority.TakeOnlyMe();
            // Your own restart in the middle of a race: carry on, it's noted on your result.
            if (!forced && !fresh && map == RunMap && (Phase == RunPhase.Running || (onlyMe && Phase != RunPhase.None)))
            {
                _sawRestart = true;
                // Your stats start over with the stage clock (the report shows your last attempt).
                if (MyResult == null) RunStats.Begin();
                MelonLogger.Msg("[Online] You restarted your stage; the race goes on.");
                return;
            }
            // The opening fly-over (the lobby setting): always skipped, or a vote each time (never: neither).
            SkipIntroVoted = Lobby.FlyOver == Lobby.SkipAlways;
            if (NetSession.Mode == NetSession.Role.Host)
            {
                if (!StartTogether || NetSession.Peers.Count == 0 || onlyMe)
                {
                    Phase = RunPhase.None;
                    return;
                }
                BeginRun(map);
                if (fresh) NetSession.AnnounceRun();
                if (fresh && Lobby.FlyOver == Lobby.SkipVote) Votes.Start(VoteKind.SkipIntro, map, NetSession.MyId, SkipVoteSeconds);
                else if (!fresh) NetSession.BroadcastRestart();
            }
            else if (NetSession.Mode == NetSession.Role.Client && _expectMap == map && Phase != RunPhase.Running)
            {
                Phase = RunPhase.None;
                RunMap = map;
                MyResult = null;
                _sawRestart = false;
                ClearHurry();
            }
        }

        /// <summary>Should this game hold at the King's opening talk? Only when a run is coming on this map.</summary>
        public static bool ShouldGate(int map)
        {
            if (!NetSession.Connected || map == 0) return false;
            if (NetSession.Mode == NetSession.Role.Host)
                return StartTogether && NetSession.Peers.Count > 0 && RunMap == map && Phase is RunPhase.Waiting or RunPhase.Countdown;
            return _expectMap == map && Phase is RunPhase.None or RunPhase.Waiting or RunPhase.Countdown;
        }

        /// <summary>IntroSync: this game is on black at the start of <paramref name="map"/>.</summary>
        public static void LocalReady(int map)
        {
            if (NetSession.Mode == NetSession.Role.Host)
            {
                _hostReady = true;
                _waitingSince = Time.unscaledTime;
                return;
            }
            if (Phase == RunPhase.None) Phase = RunPhase.Waiting;
            RunMap = map;
            _blackSince = Time.unscaledTime;
            _nextReady = 0f;
        }

        /// <summary>IntroSync asks every frame while on black: time to open the curtain?</summary>
        public static bool ReleaseNow()
        {
            if (!NetSession.Connected) return true;
            if (Phase == RunPhase.Countdown && HostNow >= StartAt)
            {
                Phase = RunPhase.Running;
                _runningSince = Time.unscaledTime;
                RunStats.Begin();
                MelonLogger.Msg($"[Online] Start (run {RunId}).");
                return true;
            }
            if (Phase is RunPhase.Running or RunPhase.Finished) return true;
            // Nothing coming any more (the host called it off): play on.
            if (Phase == RunPhase.None && (NetSession.Mode == NetSession.Role.Host || _expectMap == 0)) return true;
            if (NetSession.Mode == NetSession.Role.Client && Phase == RunPhase.Waiting && Time.unscaledTime - _blackSince > NoGoTimeout)
            {
                MelonLogger.Warning("[Online] No start signal from the host; starting on your own.");
                _expectMap = 0;
                Phase = RunPhase.None;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Client: the host went to a map and will run it together (StartMap with the sync flag), or started it
        /// again. That's a new run: the one we were in (or saw finish) is over, even on the same stage, so this game
        /// holds at the start again. A hold already waiting at this map's start stays as it is.
        /// </summary>
        public static void ExpectRun(int map)
        {
            _expectMap = map;
            if (Phase == RunPhase.Waiting && RunMap == map) return;
            if (Phase is RunPhase.Countdown or RunPhase.Running or RunPhase.Finished) _staleRun = RunId;
            Phase = RunPhase.None;
            MyResult = null;
            _sawRestart = false;
            ClearHurry();
        }

        /// <summary>
        /// Self-test, outside a session: a client's side of runs. A new run announced on the stage it just played
        /// clears the old one; a late repeat of the old run's Go is ignored; a hold already waiting stays through
        /// another announcement; the host calling it off lets it go.
        /// </summary>
        public static string SelfCheck(out bool ok)
        {
            bool race = RaceStart;
            const int map = 999001;
            try
            {
                Reset();
                _staleRun = 0;
                ClientOnGo(4, map, HostNow - 1f, false);
                bool going = Phase == RunPhase.Countdown && RunId == 4;
                ExpectRun(map);
                bool cleared = Phase == RunPhase.None && _staleRun == 4;
                ClientOnGo(4, map, HostNow - 1f, false);
                bool ignored = Phase == RunPhase.None;
                LocalReady(map);
                bool waiting = Phase == RunPhase.Waiting;
                ExpectRun(map);
                bool kept = Phase == RunPhase.Waiting;
                ClientOnCancel(5);
                bool released = Phase == RunPhase.None && _expectMap == 0;
                ok = going && cleared && ignored && waiting && kept && released;
                return $"old run going {going}, cleared by the new one {cleared}, its late Go ignored {ignored}, held at the start {waiting}, " +
                       $"hold kept through a repeat {kept}, let go when called off {released}";
            }
            finally
            {
                Reset();
                _staleRun = 0;
                RunId = 0;
                RaceStart = race;
            }
        }

        /// <summary>Client: the host left before that run started; stop waiting for it.</summary>
        public static void ClientOnCancel(byte runId)
        {
            if (Phase is RunPhase.Running or RunPhase.Finished) return;
            if (_expectMap != 0 || Phase != RunPhase.None)
                MelonLogger.Msg($"[Online] The host left before run {runId} started: carrying on by yourself.");
            _expectMap = 0;
            Phase = RunPhase.None;
        }

        // ---------------------------------------------------------------- messages in

        public static void ClientOnGo(byte runId, int map, float startAt, bool strict)
        {
            // A late repeat of the run that was going before the host announced a new one.
            if (_staleRun != 0 && runId == _staleRun) return;
            RaceStart = !strict;
            if (RunId == runId && Phase is RunPhase.Countdown or RunPhase.Running or RunPhase.Finished) return;
            RunId = runId;
            RunMap = map;
            StartAt = startAt;
            Phase = RunPhase.Countdown;
            MyResult = null;
            Standings.Clear();
            StandingsFinal = false;
            _sawRestart = false;
            ClearHurry();
            MelonLogger.Msg($"[Online] Start signal for run {runId}: start in {startAt - HostNow:0.0} s.");
        }

        public static void HostOnReady(Peer p, int map, int objects, float now)
        {
            if (Phase is not (RunPhase.Waiting or RunPhase.Countdown or RunPhase.Running) || map != RunMap) return;
            if (_readyAt.ContainsKey(p.Id)) return;
            _readyAt[p.Id] = HostNow;
            int mine = Ghosts.IndexedObjects;
            if (objects > 0 && mine > 0 && objects != mine)
                MelonLogger.Warning($"[Online] {p.Name} has {objects} objects on this map, you have {mine}: their stage data differs (DLC or game version?), shared pickups may not line up.");
            // Arrived after the start: let them go right away; their result will say "late start".
            if (Phase is RunPhase.Countdown or RunPhase.Running) NetSession.SendToPeer(p, GoPacket());
        }

        public static void HostOnResult(Peer p, byte runId, int map, RunResult r)
        {
            if (runId != RunId || map != RunMap || Phase is not (RunPhase.Running or RunPhase.Finished)) return;
            r.Id = p.Id;
            AddResult(r, p.Name);
        }

        public static void OnStandings(byte runId, int map, List<RunResult> list, bool final)
        {
            if (runId != RunId && RunId != 0 && Phase != RunPhase.None) return;
            Standings.Clear();
            Standings.AddRange(list);
            if (final && !StandingsFinal)
            {
                StandingsFinal = true;
                // The race is over: a restart from here is a new stage start (the host's restart brings everyone).
                if (Phase == RunPhase.Running) Phase = RunPhase.Finished;
                RunStats.Stop();
                _hurry = false;
                StandingsShownUntil = Time.unscaledTime + 40f;
                _showAar = false;
                MelonLogger.Msg("[Online] Final results: " + string.Join(", ", Standings.Select(Describe)));
                MatchLog.Save(map, Standings);
                Leaderboards.LeaderboardRecorder.OnFinalStandings(runId, map, Standings);
            }
        }

        /// <summary>Everyone: Sudden Death for these players (they now have <paramref name="window"/> seconds per growth size).</summary>
        public static void OnHurry(byte runId, float best, float bestReal, List<byte> ids, float window)
        {
            if (runId != RunId) return;
            foreach (byte id in ids) Hurried.Add(id);
            if (!_hurry && ids.Contains(NetSession.MyId) && MyResult == null && Phase == RunPhase.Running) StartHurry(best, bestReal, window);
        }

        public static void TrackStreamedSize(byte id, int size) => _lastStreamedSize[id] = size;

        // ---------------------------------------------------------------- per frame

        public static void Tick()
        {
            if (!NetSession.Connected) return;
            float now = Time.unscaledTime;
            if (NetSession.Mode == NetSession.Role.Host) HostTick(now);
            else ClientTick(now);
            LocalTick(now);
            ReadToggleInput();
        }

        private static void HostTick(float now)
        {
            // You left before the start (back to a plaza, another stage...): call the run off, so nobody waits on it.
            if (Phase is RunPhase.Waiting or RunPhase.Countdown && RunMap != 0 && Ghosts.CurrentMapKey != RunMap)
            {
                if (_awaySince <= 0f) _awaySince = now;
                else if (now - _awaySince > 1.5f) CancelRun();
            }
            else _awaySince = 0f;
            if (_cancelRepeats > 0 && now >= _nextCancel)
            {
                _cancelRepeats--;
                _nextCancel = now + 0.5f;
                NetSession.Broadcast(Proto.Begin(Msg.RunCancel, NetSession.MyId).U8(_cancelledRun));
            }

            switch (Phase)
            {
                case RunPhase.Waiting:
                {
                    if (!_hostReady) break;
                    var expected = NetSession.Peers.Keys.ToList();
                    bool all = expected.All(id => _readyAt.ContainsKey(id));
                    if (all || now - _waitingSince > ReadyTimeout)
                    {
                        if (!all) MelonLogger.Warning($"[Online] Starting without {expected.Count(id => !_readyAt.ContainsKey(id))} player(s) who didn't reach the start in time.");
                        StartAt = HostNow + CountdownLead;
                        Phase = RunPhase.Countdown;
                        _nextGo = 0f;
                    }
                    break;
                }
                case RunPhase.Countdown:
                case RunPhase.Running:
                    // Repeat Go for a while so a dropped packet or a late arrival still gets it.
                    if (now >= _nextGo && HostNow < StartAt + 10f)
                    {
                        _nextGo = now + 1f;
                        NetSession.Broadcast(GoPacket());
                    }
                    break;
            }

            // Players who left the stage on their own before finishing (their game is in a hub now): disqualified.
            if (Phase == RunPhase.Running && !StandingsFinal)
            {
                foreach (byte id in _readyAt.Keys.ToList())
                {
                    if (!NetSession.Peers.TryGetValue(id, out var leaver) || !LeftRun(leaver, now) || Standings.Any(s => s.Id == id)) continue;
                    AddResult(new RunResult
                    {
                        Id = id,
                        Size = _lastStreamedSize.TryGetValue(id, out int sz) ? sz : 0,
                        Time = StartAt > 0f ? HostNow - StartAt : 0f,
                        RealTime = StartAt > 0f ? HostNow - StartAt : -1f,
                        Disqualified = true,
                    }, leaver.Name);
                }
            }

            if (Phase is RunPhase.Running or RunPhase.Finished && Standings.Count > 0)
            {
                // Players who left the session, or the stage (back to a plaza on their own), don't hold up the results.
                var participants = _readyAt.Keys.Where(id => NetSession.Peers.TryGetValue(id, out var pp) && !LeftRun(pp, now)).Append(NetSession.MyId).ToList();
                var missing = participants.Where(id => Standings.All(s => s.Id != id)).ToList();
                if (!StandingsFinal && (missing.Count == 0 || now - _firstResultAt > ResultsTimeout))
                {
                    StandingsFinal = true;
                    StandingsShownUntil = now + 40f;
                    _showAar = false;
                    Phase = RunPhase.Finished;
                    _finishedAt = now;
                    MelonLogger.Msg("[Online] Final results: " + string.Join(", ", Standings.Select(Describe)));
                    MatchLog.Save(RunMap, Standings);
                    Leaderboards.LeaderboardRecorder.OnFinalStandings(RunId, RunMap, Standings);
                }
                // Sudden Death (the lobby setting): only once the stage has ended for someone the game's own way (time up,
                // or the goal on a stage that ends on it; leaving the race or being cut doesn't count). Then whoever is
                // still playing is behind: rush them (repeated a few times in case a packet drops). Off: no rush at all.
                bool stageEnded = Standings.Any(s => !s.Disqualified && !s.Forced);
                bool rush = Lobby.SuddenDeathOn && stageEnded;
                if (!StandingsFinal && missing.Count > 0 && rush && now >= _nextHurry)
                {
                    if (_hurrySince <= 0f) _hurrySince = now;
                    if (now - _hurrySince < 10f)
                    {
                        _nextHurry = now + 2f;
                        float window = Lobby.SuddenDeathSeconds;
                        var finished = Standings.Where(s => s.Cleared).ToList();
                        float best = finished.Count > 0 ? finished.Min(s => s.Time) : -1f;
                        float bestReal = finished.Count > 0 ? finished.Where(s => s.RealTime >= 0f).Select(s => s.RealTime).DefaultIfEmpty(-1f).Min() : -1f;
                        var w = Proto.Begin(Msg.Hurry, NetSession.MyId).U8(RunId).F32(best).F32(bestReal).U8((byte)missing.Count);
                        foreach (byte id in missing) w.U8(id);
                        w.U8((byte)Mathf.Clamp(window, 5f, 255f));
                        NetSession.Broadcast(w);
                        OnHurry(RunId, best, bestReal, missing, window);
                    }
                }
                if (now >= _nextStandings && (!StandingsFinal || now - _finishedAt < 10f))
                {
                    _nextStandings = now + 1f;
                    NetSession.Broadcast(StandingsPacket());
                }
            }
        }

        /// <summary>
        /// They left the run's stage: they're walking a hub (plaza or ship) right now. Hub packets only come from a
        /// game that's in a hub, and only fresh ones sent after the race started count (a player's last hub packet
        /// from before the stage stays on record).
        /// </summary>
        private static bool LeftRun(Peer p, float now) =>
            p.HubKey != 0 && p.HubAt > 0f && now - p.HubAt < 3f && _runningSince > 0f && p.HubAt > _runningSince + 1f;

        /// <summary>When this game saw the current run start (local clock).</summary>
        private static float _runningSince;

        private static void ClientTick(float now)
        {
            if (Phase != RunPhase.Waiting || !IntroSync.OnBlack || now < _nextReady) return;
            _nextReady = now + 1f;
            NetSession.SendToHost(Proto.Begin(Msg.Ready, NetSession.MyId).I32(RunMap).I32(Ghosts.IndexedObjects));
        }

        /// <summary>Count your stats, catch the end of your run (or the hurry cut-off), and report it.</summary>
        private static void LocalTick(float now)
        {
            // You left the race: your DQ goes to the host (a few times, UDP) from wherever you are now.
            if (_dqSends > 0 && now >= _nextDqSend && MyResult != null && MyResult.Disqualified)
            {
                if (Standings.Any(s => s.Id == NetSession.MyId)) _dqSends = 0;
                else
                {
                    _dqSends--;
                    _nextDqSend = now + 0.5f;
                    if (NetSession.Mode == NetSession.Role.Host) AddResult(MyResult, NetSession.MyName);
                    else NetSession.SendToHost(ResultPacket(MyResult));
                }
            }
            if (Phase != RunPhase.Running || Ghosts.CurrentMapKey != RunMap || RunMap == 0) return;
            if (MyResult == null)
            {
                RunStats.Tick();
                if (_hurry) HurryTick(now);
                if (MyResult == null) CatchEnd();
                return;
            }
            if (now < _nextResult || Standings.Any(s => s.Id == NetSession.MyId)) return;
            _nextResult = now + 0.5f;
            if (NetSession.Mode == NetSession.Role.Host) AddResult(MyResult, NetSession.MyName);
            else NetSession.SendToHost(ResultPacket(MyResult));
        }

        private static void CatchEnd()
        {
            try
            {
                var gm = GameRefs.Main;
                if (gm == null) return;
                if (gm._isInRestartWait) _sawRestart = true;
                bool ended = gm._isInClearDemo || gm._isInGameOver;
                if (!ended && GameRefs.IsMultiplay)
                {
                    var mgm = GameRefs.Multiplay;
                    ended = mgm != null && gm._startTime > 0f && mgm.playTime >= gm._startTime;
                }
                if (!ended) return;
                MyResult = CurrentResult(gm, gm._hasCleared);
                FinishLocal();
                MelonLogger.Msg($"[Online] Your run ended: {Describe(MyResult)}.");
            }
            catch (Exception e) { MelonLogger.Warning($"[Online] Reading your result failed: {e.Message}"); }
        }

        private static RunResult CurrentResult(MainGameManager gm, bool cleared)
        {
            var r = new RunResult
            {
                Id = NetSession.MyId,
                Size = gm.KatamariSize,
                Time = GameRefs.IsMultiplay && GameRefs.Multiplay != null ? GameRefs.Multiplay.playTime : gm._playTime,
                RealTime = StartAt > 0f ? HostNow - StartAt : -1f,
                Cleared = cleared,
                TimeAttack = Mathf.Clamp(gm.CheckIsTimerRule(), -1, 1) == 1,
                Restarted = _sawRestart,
                Igt = RunClocks.Igt,
                ClearIgt = RunClocks.ClearIgt,
                ClearRta = RunClocks.ClearRta,
            };
            RunStats.ReadRank(r, log: true);
            RunStats.Fill(r);
            if (StageScore.Read(gm, out _, out var kind, out int score)) { r.ScoreKind = (byte)kind; r.Score = score; }
            return r;
        }

        private static void FinishLocal()
        {
            RunStats.Stop();
            _hurry = false;
            _nextResult = 0f;
        }

        // ---------------------------------------------------------------- hurry (the lobby is waiting on you)

        private static void StartHurry(float best, float bestReal, float window)
        {
            var gm = GameRefs.Main;
            if (gm == null) return;
            _hurry = true;
            _hurryBest = best;
            _hurryBestReal = bestReal;
            _hurryWindow = Mathf.Max(5f, window);
            _hurryTarget = NextGrowth(gm.KatamariSize);
            _hurryDeadline = Time.unscaledTime + _hurryWindow;
            MelonLogger.Msg($"[Online] Sudden Death: reach {KatamariState.SizeText(_hurryTarget)} within {_hurryWindow:0} s.");
        }

        private static void HurryTick(float now)
        {
            var gm = GameRefs.Main;
            if (gm == null) return;
            int size = gm.KatamariSize;
            if (size >= _hurryTarget)
            {
                _hurryTarget = NextGrowth(size);
                _hurryDeadline = now + _hurryWindow;
            }
            if (now <= _hurryDeadline || gm._isInClearDemo || gm._isInGameOver) return;

            // Cut off: record where they are, predict a time attack time, and end the stage.
            var r = CurrentResult(gm, gm._hasCleared);
            r.Forced = true;
            if (r.TimeAttack && _hurryBest >= 0f)
            {
                r.Time = r.Time + _hurryBest - _hurryWindow;
                if (r.RealTime >= 0f && _hurryBestReal >= 0f) r.RealTime = r.RealTime + _hurryBestReal - _hurryWindow;
                r.Predicted = true;
            }
            MyResult = r;
            FinishLocal();
            MelonLogger.Msg($"[Online] Out of time to grow: your run was ended at {KatamariState.SizeText(r.Size)}.");
            if (!GameRefs.IsMultiplay) ModTasks.Run(() => "Run ended (the lobby was waiting): " + StageControl.CompleteTimer(), toast: true);
        }

        /// <summary>
        /// The next growth size (the next camera step), or 25 % bigger once past the last one. Past the stage's last
        /// step the game answers with a placeholder (measured 2026-11-07: 200000 m at 36 cm in As Large As Possible 1),
        /// which nobody could reach: anything past 100 km counts as "no next step".
        /// </summary>
        private static int NextGrowth(int size)
        {
            int next = RunStats.NextStepSize();
            return next > size && next < 1_000_000_000 ? next : (int)(size * 1.25f) + 1;
        }

        public static bool HurryActive(out int target, out float left)
        {
            target = _hurryTarget;
            left = Mathf.Max(0f, _hurryDeadline - Time.unscaledTime);
            return _hurry && MyResult == null;
        }

        // ---------------------------------------------------------------- host

        private static void CancelRun()
        {
            _cancelledRun = RunId;
            Phase = RunPhase.None;
            _hostReady = false;
            _readyAt.Clear();
            _awaySince = 0f;
            _cancelRepeats = 3;
            _nextCancel = 0f;
            MelonLogger.Msg($"[Online] Run {RunId} called off: you left before it started.");
        }

        private static void BeginRun(int map)
        {
            _cancelRepeats = 0;
            _awaySince = 0f;
            RunId = (byte)(RunId == 255 ? 1 : RunId + 1);
            RunMap = map;
            Phase = RunPhase.Waiting;
            _hostReady = false;
            _waitingSince = Time.unscaledTime;
            _readyAt.Clear();
            Standings.Clear();
            StandingsFinal = false;
            MyResult = null;
            StartAt = 0f;
            _sawRestart = false;
            ClearHurry();
            MelonLogger.Msg($"[Online] Run {RunId}: waiting for {NetSession.Peers.Count} player(s) to reach the start.");
        }

        private static void AddResult(RunResult r, string name)
        {
            if (Standings.Any(s => s.Id == r.Id)) return;
            if (Standings.Count == 0) _firstResultAt = Time.unscaledTime;
            // Checks: late start, size vs. what they streamed, and game time vs. real time since the start.
            byte v = 0;
            if (r.Id != NetSession.MyId && (!_readyAt.TryGetValue(r.Id, out float readyAt) || readyAt > StartAt)) v |= 1;
            if (r.Id != NetSession.MyId && _lastStreamedSize.TryGetValue(r.Id, out int streamed) && r.Size > streamed * 1.15f + 50) v |= 2;
            if (!r.Predicted && StartAt > 0f && r.Time > HostNow - StartAt + 2f) v |= 4;
            r.Verdict = v;
            Standings.Add(r);
            Ranking.Sort(Standings);
            MelonLogger.Msg($"[Online] Result from {name}: {Describe(r)}.");
            if (r.Disqualified) Chat.Notice($"{name} left the race: disqualified.");
        }

        /// <summary>Everyone ranked together, CLEAR or not, by the session's ranking system (Ranking: the host's choice; DQ last).</summary>
        public static void Sort(List<RunResult> list) => Ranking.Sort(list);

        private static PacketWriter GoPacket() =>
            Proto.Begin(Msg.Go, NetSession.MyId).U8(RunId).I32(RunMap).F32(StartAt).U8((byte)(RaceStart ? 0 : 1));

        public static PacketWriter ResultPacket(RunResult r) => r.Write(Proto.Begin(Msg.Result, NetSession.MyId).U8(RunId).I32(RunMap));

        private static PacketWriter StandingsPacket()
        {
            var w = Proto.Begin(Msg.Standings, NetSession.MyId).U8(RunId).I32(RunMap).U8((byte)(StandingsFinal ? 1 : 0)).U8((byte)Standings.Count);
            foreach (var r in Standings) r.Write(w.U8(r.Id)).U8(r.Verdict);
            return w;
        }

        public static string Describe(RunResult r) =>
            $"#{r.Id} {r.Status} {KatamariState.SizeText(r.Size)} {KatamariState.ClockText(r.Time)}" +
            $"{(r.RealTime >= 0f ? $" (real {KatamariState.ClockText(r.RealTime)})" : "")}{(r.Predicted ? " predicted" : "")}" +
            $"{(r.Rank >= 0 ? $" rank {StageRank.Score(r)}/5" : "")}{(r.SizeTime >= 0f ? $" size time {KatamariState.ClockText(r.SizeTime)}" : "")}" +
            $"{(r.Verdict != 0 ? $" (check {r.Verdict})" : "")}";

        public static string VerdictText(byte v)
        {
            if (v == 0) return "verified";
            var parts = new List<string>();
            if ((v & 1) != 0) parts.Add("late start");
            if ((v & 2) != 0) parts.Add("size doesn't match");
            if ((v & 4) != 0) parts.Add("time doesn't add up");
            return string.Join(", ", parts);
        }

        // ---------------------------------------------------------------- estimates for the player bar

        /// <summary>
        /// A player's clock between their updates: their last reported value moved on by the time since it
        /// arrived (a few frames old at most while packets flow), unless they say it's stopped (KatamariState
        /// FlagClockStopped: paused, the King talking). "~" marks it once updates have stopped.
        /// </summary>
        public static string EstimateTime(Peer p)
        {
            if (!p.HasState || p.State.Map == 0) return "";
            float since = Time.unscaledTime - p.StateAt;
            var s = p.State;
            // Their clock is stopped (paused, the King talking...): exactly what they sent, not run on and pulled back.
            float age = s.ClockStopped ? 0f : since;
            string text = KatamariState.TimeText(s.TimerRule, s.TimeLeft - age, s.Elapsed + age);
            return since > 1f && text.Length > 0 ? "~" + text : text;
        }

        // ---------------------------------------------------------------- overlays

        private static GUIStyle _big, _bigShadow, _info, _warn;
        private static Texture2D _panel;

        /// <summary>Frames the results / after-action report were drawn (for the self-test).</summary>
        /// <summary>Frames the Results screen drew each view (self-test; counted by HudWindows).</summary>
        public static int StandingsFrames, AarFrames;

        /// <summary>The results show the after-action report instead of the standings (Tab / D-pad toggles it).</summary>
        public static bool ShowAar { get => _showAar; set => _showAar = value; }

        /// <summary>Tab, or the D-pad left/right, switches the results between standings and the after-action report.</summary>
        private static void ReadToggleInput()
        {
            if (!StandingsFinal || Time.unscaledTime > StandingsShownUntil || TextInput.IsTyping) return;
            try { if (Input.GetKeyDown(KeyCode.Tab)) { _showAar = !_showAar; return; } } catch { }
            try
            {
                var pad = UnityEngine.InputSystem.Gamepad.current;
                if (pad != null && (pad.dpad.left.wasPressedThisFrame || pad.dpad.right.wasPressedThisFrame)) _showAar = !_showAar;
            }
            catch { }
        }

        public static void OnGUI()
        {
            if (!NetSession.Connected || Event.current.type != EventType.Repaint) return;
            EnsureStyles();
            float sw = Screen.width, sh = Screen.height, scale = Mathf.Clamp(sh / 1080f, 1f, 3f);
            bool here = Ghosts.CurrentMapKey != 0 && Ghosts.CurrentMapKey == RunMap;

            if (here && IntroSync.OnBlack && Phase == RunPhase.Waiting)
            {
                string text = NetSession.Mode == NetSession.Role.Host
                    ? $"Waiting for everyone to reach the start… ({ReadyCount}/{NetSession.Peers.Count + 1})"
                    : "Waiting for everyone to reach the start…";
                Centered(new Rect(0f, sh * 0.42f, sw, 40f * scale), text, _info, scale);
            }
            else if (here && Phase == RunPhase.Countdown && StartAt - HostNow > 0f)
            {
                Centered(new Rect(0f, sh * 0.32f, sw, 140f * scale), Mathf.CeilToInt(StartAt - HostNow).ToString(), _big, scale, _bigShadow);
            }

            if (here && HurryActive(out int target, out float left))
                Centered(new Rect(0f, sh * 0.12f, sw, 40f * scale), $"Sudden Death! Reach {KatamariState.SizeText(target)} within {left:0} s", _warn, scale);
            else if (MyResult != null && !StandingsFinal && here)
                Centered(new Rect(0f, sh * 0.18f, sw, 40f * scale), $"Finished! Waiting for the others… ({Standings.Count}/{NetSession.Peers.Count + 1})", _info, scale);
            // The Results screen (standings / after-action report) is drawn with Dear ImGui: HudWindows.DrawResults.
        }

        private static void Centered(Rect r, string text, GUIStyle style, float scale, GUIStyle shadow = null)
        {
            var old = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
            try
            {
                var rr = new Rect(r.x / scale, r.y / scale, r.width / scale, r.height / scale);
                if (shadow != null) GUI.Label(new Rect(rr.x + 3f, rr.y + 3f, rr.width, rr.height), text, shadow);
                else GUI.DrawTexture(new Rect(rr.center.x - 300f, rr.y, 600f, rr.height), _panel);
                GUI.Label(rr, text, style);
            }
            finally { GUI.matrix = old; }
        }

        private static void EnsureStyles()
        {
            if (_big != null && _panel != null) return;
            _big = Style(120, new Color(1f, 0.9f, 0.3f), FontStyle.Bold, TextAnchor.MiddleCenter);
            _bigShadow = Style(120, new Color(0f, 0f, 0f, 0.8f), FontStyle.Bold, TextAnchor.MiddleCenter);
            _info = Style(18, Color.white, FontStyle.Bold, TextAnchor.MiddleCenter);
            _warn = Style(19, new Color(1f, 0.55f, 0.35f), FontStyle.Bold, TextAnchor.MiddleCenter);
            _panel = PlayerBar.Tex(new Color(0.06f, 0.05f, 0.1f, 0.88f));
        }

        public static GUIStyle Tag(Color bg)
        {
            var s = Style(12, Color.white, FontStyle.Bold, TextAnchor.MiddleCenter);
            s.normal.background = PlayerBar.Tex(bg);
            return s;
        }

        private static GUIStyle Style(int size, Color color, FontStyle font, TextAnchor align)
        {
            var s = new GUIStyle();
            s.fontSize = size;
            s.fontStyle = font;
            s.alignment = align;
            s.normal.textColor = color;
            return s;
        }
    }
}
