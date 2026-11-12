using System;
using System.Collections.Generic;
using System.Linq;
using MelonLoader;
using UnityEngine;
using OUAKModAPI;

namespace OUAKMultiplayer
{
    internal enum VoteKind : byte { SkipIntro = 1, Kick = 2, GoToStage = 3, RandomStage = 4, Restart = 5, ReturnToPlaza = 6 }

    internal sealed class Vote
    {
        public byte Id, By;
        public VoteKind Kind;
        public int Arg;
        /// <summary>GoToStage: where it was picked in a plaza (PlazaSpot.Pack), or 0 when picked from the menu.</summary>
        public int Arg2;
        /// <summary>On the host's clock.</summary>
        public float Deadline;
        public bool? Mine;
        public bool Done, Passed;
        public int Yes, No, Total;
        public float HideAt;
        /// <summary>Host: the result goes out a few times (UDP); clients close a vote whose result never came.</summary>
        public int Repeats;
        public float NextRepeat;
        /// <summary>Host only: everyone's ballot so far.</summary>
        public readonly Dictionary<byte, bool> Ballots = new();
    }

    /// <summary>
    /// Player votes, run by the host: anyone proposes (VoteAsk), the host announces it (VoteStart) and counts
    /// the ballots (VoteCast). It passes with more than half of all players in the session (anyone who doesn't
    /// answer in time counts as no; the proposer counts as yes). The host then applies it: kick, go to a stage
    /// (picked with a fan in a plaza: everyone gathers at that fan's stage screen first, PlazaFlow), or a random
    /// stage (everyone follows the host's map as usual). Answer with Y / N, the D-pad (up = yes,
    /// down = no), or by clicking the popup. Whoever started a vote that didn't pass waits 30 s before starting
    /// another (the host enforces it and tells them: VoteDenied).
    /// </summary>
    internal static class Votes
    {
        private const float Duration = 20f, ResultShown = 5f;
        /// <summary>Whoever started a vote that didn't pass waits this long before starting another (a lobby setting, 30 s by default).</summary>
        public static float LostCooldown => Lobby.CooldownSeconds;
        private static byte _nextId = 1;
        /// <summary>Host: when each player may start a vote again (host clock).</summary>
        private static readonly Dictionary<byte, float> _lostUntil = new();
        /// <summary>Yours (local clock).</summary>
        private static float _myCooldownUntil;

        public static Vote Active { get; private set; }

        /// <summary>Why the host last turned a vote request down (self-test).</summary>
        public static string LastRefusal { get; private set; } = "";

        private const string LeftRaceWhy = "You left this race (disqualified): you can start votes again once it's over.";
        public static bool Open => Active != null && !Active.Done;

        /// <summary>Seconds until you may start a vote again (0 = now).</summary>
        public static float CooldownLeft => Mathf.Max(0f, _myCooldownUntil - Time.unscaledTime);

        /// <summary>Client: the host turned down your vote request (a rule, or your last one didn't pass).</summary>
        public static void OnDenied(byte seconds, string reason)
        {
            if (!string.IsNullOrEmpty(reason))
            {
                ModTasks.Toast(reason, true);
                ModTasks.SetStatus(reason, true);
                return;
            }
            _myCooldownUntil = Mathf.Max(_myCooldownUntil, Time.unscaledTime + seconds);
            ModTasks.Run(() => $"Your last vote didn't pass: you can start another in {seconds} s.", toast: true);
        }

        public static string Describe(VoteKind kind, int arg) => kind switch
        {
            VoteKind.SkipIntro => "skip the opening fly-over",
            VoteKind.Kick => $"remove {NameOf((byte)arg)} from the session",
            VoteKind.GoToStage => $"play {StageName(arg)}",
            VoteKind.RandomStage => "play a random stage",
            VoteKind.Restart => "restart the stage for everyone",
            VoteKind.ReturnToPlaza => "go back to the Plaza together",
            _ => "?",
        };

        public static string NameOf(byte id)
        {
            if (id == NetSession.MyId) return NetSession.MyName;
            return NetSession.Peers.TryGetValue(id, out var p) ? p.Name : $"Player {id + 1}";
        }

        public static string StageName(int mapKey)
        {
            foreach (var e in StageCatalog.Entries)
                if ((e.Kind == MapKind.Stage || e.Kind == MapKind.Multiplayer) && Ghosts.KeyFor(e) == mapKey) return e.Name;
            return "that stage";
        }

        // ---------------------------------------------------------------- proposing and casting

        /// <summary>Anyone: ask for a vote. The host starts it right away; a client asks the host.</summary>
        public static string Propose(VoteKind kind, int arg, int arg2 = 0)
        {
            if (!NetSession.Connected) throw new InvalidOperationException("Host or join a session first.");
            if (Open) throw new InvalidOperationException("A vote is already running.");
            // Nothing a player who left the race does may interrupt it.
            if (SyncRun.LeftThisRun) throw new InvalidOperationException(LeftRaceWhy);
            if (!HostAuthority.CanCall(kind, NetSession.MyId, out string why, out _)) throw new InvalidOperationException(why);
            if (CooldownLeft > 0f && kind is not (VoteKind.Restart or VoteKind.ReturnToPlaza))
                throw new InvalidOperationException($"Your last vote didn't pass: you can start another in {Mathf.CeilToInt(CooldownLeft)} s.");
            if (NetSession.Mode == NetSession.Role.Host)
            {
                Start(kind, arg, NetSession.MyId, arg2: arg2);
                return $"Vote started: {Describe(kind, arg)}.";
            }
            NetSession.SendToHost(Proto.Begin(Msg.VoteAsk, NetSession.MyId).U8((byte)kind).I32(arg).I32(arg2));
            return $"Asked the host for a vote: {Describe(kind, arg)}.";
        }

        public static void Cast(bool yes)
        {
            var v = Active;
            if (v == null || v.Done || v.Mine != null) return;
            v.Mine = yes;
            if (NetSession.Mode == NetSession.Role.Host) HostOnCast(NetSession.MyId, v.Id, yes);
            else NetSession.SendToHost(Proto.Begin(Msg.VoteCast, NetSession.MyId).U8(v.Id).U8((byte)(yes ? 1 : 0)));
        }

        // ---------------------------------------------------------------- host

        public static void Start(VoteKind kind, int arg, byte by, float seconds = Duration, int arg2 = 0)
        {
            if (NetSession.Mode != NetSession.Role.Host || Open) return;
            // A player who left the race (disqualified) can't interrupt it: no votes from them until it's over.
            if (by != NetSession.MyId && kind != VoteKind.SkipIntro && SyncRun.IsDisqualified(by))
            {
                LastRefusal = LeftRaceWhy;
                if (NetSession.Peers.TryGetValue(by, out var gone))
                    NetSession.SendToPeer(gone, Proto.Begin(Msg.VoteDenied, NetSession.MyId).U8(0).Str(LeftRaceWhy));
                MelonLogger.Msg($"[Online] {NameOf(by)}'s vote to {Describe(kind, arg)} was refused: they left the race.");
                return;
            }
            // Restart and plaza votes: one each per player per stage, none after one passed, 30 s after one failed.
            if (kind is VoteKind.Restart or VoteKind.ReturnToPlaza)
            {
                string why = null;
                int waitFor = 0;
                if (!HostAuthority.ValidTarget(kind, arg)) why = "The host isn't on that stage any more.";
                else HostAuthority.CanCall(kind, by, out why, out waitFor);
                if (why != null)
                {
                    if (by != NetSession.MyId && NetSession.Peers.TryGetValue(by, out var asker))
                        NetSession.SendToPeer(asker, Proto.Begin(Msg.VoteDenied, NetSession.MyId).U8((byte)Mathf.Clamp(waitFor, 0, 255)).Str(why));
                    MelonLogger.Msg($"[Online] {NameOf(by)}'s vote to {Describe(kind, arg)} was refused: {why}");
                    LastRefusal = why;
                    return;
                }
            }
            else if (!Valid(kind, arg)) return;
            // Lost a vote recently: not yet (the skip-intro vote the host starts by itself doesn't count).
            else if (kind != VoteKind.SkipIntro && _lostUntil.TryGetValue(by, out float until) && SyncRun.HostNow < until)
            {
                int wait = Mathf.CeilToInt(until - SyncRun.HostNow);
                if (by != NetSession.MyId && NetSession.Peers.TryGetValue(by, out var peer))
                    NetSession.SendToPeer(peer, Proto.Begin(Msg.VoteDenied, NetSession.MyId).U8((byte)Mathf.Clamp(wait, 1, 255)).Str(""));
                MelonLogger.Msg($"[Online] {NameOf(by)} has to wait {wait} s before starting another vote.");
                return;
            }
            var v = new Vote { Id = _nextId, Kind = kind, Arg = arg, Arg2 = arg2, By = by, Deadline = SyncRun.HostNow + seconds };
            _nextId = (byte)(_nextId == 255 ? 1 : _nextId + 1);
            v.Ballots[by] = true;
            if (by == NetSession.MyId) v.Mine = true;
            Active = v;
            HostAuthority.OnVoteStarted(kind, by);
            NetSession.Broadcast(Proto.Begin(Msg.VoteStart, NetSession.MyId).U8(v.Id).U8((byte)kind).I32(arg).F32(v.Deadline).U8(by).I32(arg2));
            MelonLogger.Msg($"[Online] {NameOf(by)} started a vote: {Describe(kind, arg)}.");
        }

        private static bool Valid(VoteKind kind, int arg) => kind switch
        {
            VoteKind.Kick => arg != NetSession.MyId && NetSession.Peers.ContainsKey((byte)arg),
            VoteKind.GoToStage => StageCatalog.Entries.Any(e => e.Kind == MapKind.Stage && Ghosts.KeyFor(e) == arg),
            _ => true,
        };

        public static void HostOnCast(byte who, byte voteId, bool yes)
        {
            var v = Active;
            if (v == null || v.Done || v.Id != voteId) return;
            v.Ballots[who] = yes;
        }

        // ---------------------------------------------------------------- clients

        public static void ClientOnStart(byte voteId, VoteKind kind, int arg, float deadline, byte by, int arg2)
        {
            if (Active != null && Active.Id == voteId) return;
            Active = new Vote { Id = voteId, Kind = kind, Arg = arg, Arg2 = arg2, Deadline = deadline, By = by, Mine = by == NetSession.MyId ? true : null };
            HostAuthority.OnVoteStarted(kind, by);
        }

        public static void ClientOnResult(byte voteId, bool passed, int yes, int no, int total)
        {
            var v = Active;
            if (v == null || v.Id != voteId || v.Done) return;
            Finish(v, passed, yes, no, total);
        }

        // ---------------------------------------------------------------- per frame

        public static void Tick()
        {
            var v = Active;
            if (v == null) return;
            if (v.Done)
            {
                if (NetSession.Mode == NetSession.Role.Host && v.Repeats > 0 && Time.unscaledTime >= v.NextRepeat)
                {
                    v.Repeats--;
                    v.NextRepeat = Time.unscaledTime + 0.6f;
                    NetSession.Broadcast(ResultPacket(v));
                }
                if (Time.unscaledTime > v.HideAt) Active = null;
                return;
            }
            // Client: the host's result never arrived (or the host is gone): don't stay stuck on an open vote.
            if (NetSession.Mode == NetSession.Role.Client && SyncRun.HostNow > v.Deadline + 4f)
            {
                MelonLogger.Msg($"[Online] No result came for the vote to {Describe(v.Kind, v.Arg)}; closing it.");
                v.Done = true;
                v.HideAt = Time.unscaledTime + 1f;
                return;
            }
            if (v.Mine == null && !TextInput.IsTyping) ReadInput();
            if (NetSession.Mode != NetSession.Role.Host) return;

            int total = NetSession.Peers.Count + 1;
            if (v.Ballots.Count < total && SyncRun.HostNow < v.Deadline) return;
            int yes = v.Ballots.Values.Count(b => b);
            bool passed = yes * 2 > total;
            Finish(v, passed, yes, total - yes, total);
            NetSession.Broadcast(ResultPacket(v));
            v.Repeats = 2;
            v.NextRepeat = Time.unscaledTime + 0.6f;
        }

        private static PacketWriter ResultPacket(Vote v) =>
            Proto.Begin(Msg.VoteResult, NetSession.MyId).U8(v.Id).U8((byte)(v.Passed ? 1 : 0))
                .U8((byte)v.Yes).U8((byte)v.No).U8((byte)v.Total);

        private static void Finish(Vote v, bool passed, int yes, int no, int total)
        {
            v.Done = true;
            v.Passed = passed;
            v.Yes = yes;
            v.No = no;
            v.Total = total;
            v.HideAt = Time.unscaledTime + ResultShown;
            MelonLogger.Msg($"[Online] Vote to {Describe(v.Kind, v.Arg)}: {(passed ? "passed" : "failed")} ({yes} of {total} yes).");
            HostAuthority.OnVoteFinished(v.Kind, passed);
            // Restart and plaza votes have their own rules (HostAuthority); the others the lost-vote wait.
            if (!passed && v.Kind is not (VoteKind.SkipIntro or VoteKind.Restart or VoteKind.ReturnToPlaza) && LostCooldown > 0f)
            {
                if (NetSession.Mode == NetSession.Role.Host) _lostUntil[v.By] = SyncRun.HostNow + LostCooldown;
                if (v.By == NetSession.MyId) _myCooldownUntil = Time.unscaledTime + LostCooldown;
            }
            if (passed) Apply(v);
        }

        private static void Apply(Vote v)
        {
            if (v.Kind == VoteKind.SkipIntro)
            {
                SyncRun.SkipIntroVoted = true;
                return;
            }
            if (NetSession.Mode != NetSession.Role.Host) return;
            switch (v.Kind)
            {
                case VoteKind.Kick:
                    NetSession.Kick((byte)v.Arg);
                    break;
                case VoteKind.Restart:
                    HostAuthority.ForceRestart();
                    break;
                case VoteKind.ReturnToPlaza:
                    HostAuthority.ForcePlaza();
                    break;
                case VoteKind.GoToStage:
                {
                    var entry = StageCatalog.Entries.FirstOrDefault(e => e.Kind == MapKind.Stage && Ghosts.KeyFor(e) == v.Arg);
                    if (entry == null) break;
                    // Picked with a fan: everyone gathers at its stage screen first (music, ready), then it starts.
                    if (entry.Kind == MapKind.Stage && PlazaSpot.TryUnpack(v.Arg2, out var spot))
                        ModTasks.Run(() => "Vote passed: " + PlazaFlow.HostBeginGather(entry, spot), toast: true);
                    else
                        ModTasks.Run(() => "Vote passed: " + StageGate.Approved(() => SceneLoader.Load(entry)), toast: true);
                    break;
                }
                case VoteKind.RandomStage:
                {
                    var pool = StageCatalog.Entries.Where(e => e.Kind == MapKind.Stage && (!e.NeedsAA || GameRefs.OwnsAA)).ToList();
                    if (pool.Count == 0) break;
                    var entry = pool[UnityEngine.Random.Range(0, pool.Count)];
                    ModTasks.Run(() => $"Random stage: {entry.Name}. " + StageGate.Approved(() => SceneLoader.Load(entry)), toast: true);
                    break;
                }
            }
        }

        /// <summary>Y / N keys, or the D-pad (up = yes, down = no).</summary>
        private static void ReadInput()
        {
            try
            {
                if (Input.GetKeyDown(KeyCode.Y)) { Cast(true); return; }
                if (Input.GetKeyDown(KeyCode.N)) { Cast(false); return; }
            }
            catch { }
            try
            {
                var pad = UnityEngine.InputSystem.Gamepad.current;
                if (pad == null) return;
                if (pad.dpad.up.wasPressedThisFrame) Cast(true);
                else if (pad.dpad.down.wasPressedThisFrame) Cast(false);
            }
            catch { }
        }

        // The popup itself is drawn with Dear ImGui: HudWindows.DrawVote.

        public static float GuiScale() => Mathf.Clamp(Screen.height / 1080f, 1f, 3f);
    }
}
