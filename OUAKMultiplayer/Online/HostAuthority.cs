using System;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using Il2CppApp.KatamariSin;
using MelonLoader;
using OUAKModAPI;
using UnityEngine;

namespace OUAKMultiplayer
{
    /// <summary>
    /// The host decides for the session: when the host restarts a stage everyone restarts, and when the host goes
    /// back to the Plaza everyone goes back. In a session, the pause menu's Retry and back-to-Plaza open a choice
    /// instead of the game's Yes/No:
    /// <list type="bullet">
    /// <item>Host, Retry: <b>Force</b> (everyone restarts now), <b>Vote</b>, or <b>Only me</b>.</item>
    /// <item>Host, Plaza: <b>Force</b> (everyone goes back) or <b>Vote</b>.</item>
    /// <item>Everyone else, Retry: <b>Vote</b> or <b>Only me</b>; Plaza: <b>Vote</b> or <b>Only me</b> (leave the race alone).</item>
    /// </list>
    /// Vote rules (the host enforces them; everyone's game mirrors them to grey out what isn't allowed):
    /// each player may call one restart vote and one plaza vote per stage. Once a restart vote passes, nobody can
    /// call another on that stage; after one fails, the next can start 30 s later. Restarts keep the stage the
    /// same; going to another stage or through a hub starts a new one. A passed vote is carried out like Force.
    /// </summary>
    internal static class HostAuthority
    {
        public const float FailedVoteCooldown = 30f;

        // ---- the vote ledger for the stage the session is on
        private static int _visitKey;
        private static readonly HashSet<(byte by, VoteKind kind)> _called = new();
        private static readonly HashSet<VoteKind> _passed = new();
        private static readonly Dictionary<VoteKind, float> _cooldownUntil = new();

        // ---- the host's own actions
        private static float _forcedRestartUntil, _onlyMeUntil;
        private static float _lastPlazaSent = -100f, _plazaRepeatAt;
        private static int _plazaRepeats, _plazaIndex = -1;
        private static int _lastStageKey;
        private static bool _wasInStage;

        // ---- clients
        private static float _lastPlazaHandled = -100f;

        public static void Init()
        {
            PauseMenu.Interceptors.Add(OnPauseChoice);
        }

        /// <summary>Forget everything (left the session).</summary>
        public static void Reset()
        {
            _visitKey = 0;
            ResetLedger();
            _forcedRestartUntil = _onlyMeUntil = 0f;
            _plazaRepeats = 0;
            _wasInStage = false;
            _lastStageKey = 0;
            if (Prompt.Active && (Prompt.CurrentTitle == RestartTitle || Prompt.CurrentTitle == PlazaTitle)) Prompt.Close();
        }

        private static void ResetLedger()
        {
            _called.Clear();
            _passed.Clear();
            _cooldownUntil.Clear();
        }

        public static void Tick()
        {
            if (!NetSession.Connected) return;
            // A new stage (or a hub in between) starts a new ledger; restarts and reloads of the same stage don't.
            int key = Ghosts.CurrentMapKey;
            if (HubPresence.HubKey != 0)
            {
                if (_visitKey != 0) { _visitKey = 0; ResetLedger(); }
            }
            else if (key != 0 && key != _visitKey)
            {
                _visitKey = key;
                ResetLedger();
            }

            if (NetSession.Mode == NetSession.Role.Host) HostTick();
        }

        private static void HostTick()
        {
            float now = Time.unscaledTime;
            // HostPlaza goes out a few times (UDP).
            if (_plazaRepeats > 0 && now >= _plazaRepeatAt)
            {
                _plazaRepeats--;
                _plazaRepeatAt = now + 0.5f;
                PlazaSends++;
                NetSession.Broadcast(Proto.Begin(Msg.HostPlaza, NetSession.MyId).I32(_plazaIndex));
            }
            // The host reached a plaza from a stage some other way (the result screen, a hotkey, the Levels tab):
            // the others come along too.
            int key = Ghosts.CurrentMapKey;
            bool inStage = key != 0 && GameRefs.Main != null;
            if (inStage) _lastStageKey = key;
            bool inPlaza = HubPresence.HubKey != 0 && UnityEngine.SceneManagement.SceneManager.GetSceneByName("SelectHiroba").isLoaded;
            if (_wasInStage && inPlaza && now - _lastPlazaSent > 15f && NetSession.Peers.Count > 0)
            {
                MelonLogger.Msg("[Online] You went back to the Plaza: everyone comes along.");
                SendPlaza();
            }
            if (inStage) _wasInStage = true;
            else if (inPlaza) _wasInStage = false;
        }

        // ---------------------------------------------------------------- the pause menu

        private const string RestartTitle = "Restart the stage", PlazaTitle = "Back to the Plaza";

        /// <summary>How many choices the last prompt offered (self-test).</summary>
        public static int LastOptionCount { get; private set; }
        /// <summary>HostPlaza packets sent (self-test).</summary>
        public static int PlazaSends { get; private set; }

        /// <summary>Self-test: the choice the pause menu's Retry / back-to-Plaza opens, shown without the pause menu.</summary>
        public static bool TestPrompt(PauseAction action) => OnPauseChoice(null, action);

        private static bool OnPauseChoice(UIPauseMenu menu, PauseAction action)
        {
            // Alone, or on a multiplayer map (the game's own match flow): the game's own Yes / No.
            if (!NetSession.Connected || NetSession.Peers.Count == 0 || GameRefs.IsMultiplay) return false;
            bool host = NetSession.Mode == NetSession.Role.Host;
            var options = new List<PromptOption>();
            if (action == PauseAction.Retry)
            {
                if (host)
                    options.Add(new PromptOption("Force: restart for everyone", () => ForceRestart(menu),
                        "Everyone on this stage restarts with you right now, and the race starts over together."));
                options.Add(VoteOption(VoteKind.Restart, "Vote: ask everyone to restart", menu,
                    "Everyone gets a yes / no popup; if more than half say yes, the whole session restarts."));
                options.Add(new PromptOption("Only me: restart just my game", () => RestartAlone(menu),
                    "Only your game restarts; the others keep playing. Your result is marked \"restarted\"."));
                options.Add(new PromptOption("Cancel", null, "Back to the pause menu."));
                Prompt.Show(RestartTitle, host
                    ? "You're the host: you can restart for everyone, ask everyone, or restart alone."
                    : "Restart for everyone (needs a vote), or restart alone?", options);
            }
            else
            {
                if (host)
                    options.Add(new PromptOption("Force: everyone back to the Plaza", () => ForcePlaza(menu),
                        "Everyone leaves the stage with you and goes to your plaza."));
                options.Add(VoteOption(VoteKind.ReturnToPlaza, "Vote: ask everyone to go back", menu,
                    "Everyone gets a yes / no popup; if more than half say yes, the whole session goes back to the Plaza."));
                if (!host)
                    options.Add(new PromptOption("Only me: leave the race (disqualified)", () => LeaveAlone(menu),
                        SyncRun.RaceOn && SyncRun.MyResult == null
                            ? "You go back by yourself and are disqualified from this race (DQ, listed last). The others keep playing; you can't start votes until it's over. The host's next stage brings you along again."
                            : "You go back by yourself; the others carry on. The host's next stage brings you along again."));
                options.Add(new PromptOption("Cancel", null, "Back to the pause menu."));
                Prompt.Show(PlazaTitle, host
                    ? "You're the host: you can take everyone back to the Plaza, or ask them first."
                    : "Ask everyone to go back to the Plaza together, or leave by yourself?", options);
            }
            LastOptionCount = options.Count;
            return true;
        }

        private static PromptOption VoteOption(VoteKind kind, string label, UIPauseMenu menu, string detail)
        {
            bool ok = CanCall(kind, NetSession.MyId, out string why, out _);
            if (ok && SyncRun.LeftThisRun) { ok = false; why = "You left this race: you can start votes again once it's over."; }
            return new PromptOption
            {
                Label = label,
                Detail = detail,
                Enabled = ok,
                DisabledReason = why,
                Chosen = () =>
                {
                    PauseMenu.Resume(menu);
                    ModTasks.Run(() => Votes.Propose(kind, Ghosts.CurrentMapKey), toast: true);
                },
            };
        }

        private static void RestartAlone(UIPauseMenu menu)
        {
            _onlyMeUntil = Time.unscaledTime + 8f;
            ModTasks.Run(() => PauseMenu.Confirm(menu, PauseAction.Retry) + " (only you)", toast: true);
        }

        private static void LeaveAlone(UIPauseMenu menu)
        {
            bool dq = SyncRun.RaceOn && SyncRun.MyResult == null;
            // The DQ result goes out first, while the run is still this stage's.
            SyncRun.LeaveRace();
            ModTasks.Run(() => PauseMenu.Confirm(menu, PauseAction.Plaza) + (dq ? " (only you: disqualified from this race)" : " (only you)"), toast: true);
        }

        // ---------------------------------------------------------------- host: carrying it out

        /// <summary>Host: restart the stage for everyone (Force, or a restart vote that passed).</summary>
        public static void ForceRestart(UIPauseMenu menu = null)
        {
            if (NetSession.Mode != NetSession.Role.Host) return;
            var gm = GameRefs.Main;
            bool playing = gm != null && GameRefs.InStage && !gm._isInClearDemo && !gm._isInGameOver && !gm._isInRestartWait;
            if (playing)
            {
                // SyncRun.OnStageCycle sees the restart as a new run for everyone (and tells them).
                _forcedRestartUntil = Time.unscaledTime + 8f;
                if (menu == null && GameEvents.Paused) menu = PauseMenu.Current;
                ModTasks.Run(() => "Restarting the stage for everyone: " + (menu != null ? PauseMenu.Confirm(menu, PauseAction.Retry) : StageControl.RestartStage()), toast: true);
                return;
            }
            // Ending or on its results (the game won't restart a stage that's ending): load it again, which
            // announces a new run that everyone joins.
            var entry = Ghosts.CurrentEntry();
            if (entry == null) { ModTasks.Toast("You're not on a stage to restart.", true); return; }
            ModTasks.Run(() => "Starting the stage again for everyone: " + StageGate.Approved(() => SceneLoader.Load(entry)), toast: true);
        }

        /// <summary>SyncRun: was this restart the host's Force (a new run for everyone)? Answers once.</summary>
        public static bool TakeForcedRestart()
        {
            bool yes = Time.unscaledTime < _forcedRestartUntil;
            _forcedRestartUntil = 0f;
            return yes;
        }

        /// <summary>SyncRun: was this restart "Only me" (nobody else restarts)? Answers once.</summary>
        public static bool TakeOnlyMe()
        {
            bool yes = Time.unscaledTime < _onlyMeUntil;
            _onlyMeUntil = 0f;
            return yes;
        }

        /// <summary>Host: everyone back to the Plaza (Force, or a plaza vote that passed).</summary>
        public static void ForcePlaza(UIPauseMenu menu = null)
        {
            if (NetSession.Mode != NetSession.Role.Host) return;
            SendPlaza();
            if (menu == null && GameEvents.Paused) menu = PauseMenu.Current;
            ModTasks.Run(() => "Everyone back to the Plaza: " + LeaveStage(menu), toast: true);
        }

        private static void SendPlaza()
        {
            _plazaIndex = GameRefs.Save != null ? GameRefs.Save._stageIndex : -1;
            _lastPlazaSent = Time.unscaledTime;
            _plazaRepeats = 3;
            _plazaRepeatAt = 0f;
            _wasInStage = false;
        }

        /// <summary>Leaves the stage for the plaza the way the game does from wherever it is (pause menu, playing, results).</summary>
        private static string LeaveStage(UIPauseMenu menu)
        {
            var gm = GameRefs.Main;
            if (gm == null) return "already out of the stage";
            if (menu != null && GameEvents.Paused) return PauseMenu.Confirm(menu, PauseAction.Plaza);
            if (!gm._isInClearDemo && !gm._isInGameOver && GameRefs.InStage) return StageControl.ReturnToPlaza();
            // On the result screen or a game over: load the plaza itself.
            var plaza = PlazaEntry(GameRefs.Save != null ? GameRefs.Save._stageIndex : -1);
            if (plaza == null) return StageControl.ReturnToPlaza();
            return StageGate.Approved(() => SceneLoader.LoadPlaza(plaza));
        }

        private static MapEntry PlazaEntry(int index)
        {
            if (index < 0) return null;
            foreach (var e in StageCatalog.Entries)
            {
                if (e.Kind != MapKind.Plaza) continue;
                int i;
                try { i = SelectHirobaManager.GetStageIndex(e.PlazaKey); } catch { continue; }
                if (i == index) return e;
            }
            return null;
        }

        // ---------------------------------------------------------------- clients: following the host

        /// <summary>Client: the host restarted for everyone and you're playing that stage: restart here (closing the pause menu the game's way).</summary>
        public static string RestartHere()
        {
            CloseOurPrompt();
            if (GameEvents.Paused && PauseMenu.Current != null) return PauseMenu.Confirm(PauseMenu.Current, PauseAction.Retry);
            return StageControl.RestartStage();
        }

        /// <summary>Client: the host sent everyone back to the Plaza (<paramref name="plazaIndex"/> = the host's plaza).</summary>
        public static void ClientOnHostPlaza(int plazaIndex)
        {
            float now = Time.unscaledTime;
            if (now - _lastPlazaHandled < 8f) return;
            var gm = GameRefs.Main;
            if (gm == null || GameRefs.IsMultiplay || !gm.PlayerInitiated) return;   // not in a story stage: nothing to leave
            _lastPlazaHandled = now;
            if (!NetSession.FollowHost)
            {
                NetSession.Note("The host went back to the Plaza (you're not following the host).");
                return;
            }
            // Land in the host's plaza, if you have it.
            var plaza = PlazaEntry(plazaIndex);
            if (plaza != null && (!plaza.NeedsAA || GameRefs.OwnsAA) && GameRefs.Save != null) GameRefs.Save._stageIndex = plazaIndex;
            CloseOurPrompt();
            MelonLogger.Msg("[Online] The host went back to the Plaza: following.");
            ModTasks.Run(() => "The host went back to the Plaza: " + LeaveStage(GameEvents.Paused ? PauseMenu.Current : null), toast: true);
        }

        private static void CloseOurPrompt()
        {
            if (Prompt.Active && (Prompt.CurrentTitle == RestartTitle || Prompt.CurrentTitle == PlazaTitle)) Prompt.Close();
        }

        // ---------------------------------------------------------------- vote rules

        /// <summary>
        /// May <paramref name="by"/> call a <paramref name="kind"/> vote now? Restart and plaza votes: one each per
        /// player per stage, none after a restart vote passed, 30 s after a failed one. Other kinds: always.
        /// </summary>
        public static bool CanCall(VoteKind kind, byte by, out string why, out int waitSeconds)
        {
            why = null;
            waitSeconds = 0;
            if (kind is not (VoteKind.Restart or VoteKind.ReturnToPlaza)) return true;
            string what = kind == VoteKind.Restart ? "restart" : "back-to-the-Plaza";
            if (_passed.Contains(kind))
            {
                why = $"A {what} vote already passed on this stage.";
                return false;
            }
            if (_called.Contains((by, kind)))
            {
                why = by == NetSession.MyId ? $"You've already called a {what} vote on this stage." : $"{Votes.NameOf(by)} already called a {what} vote on this stage.";
                return false;
            }
            if (_cooldownUntil.TryGetValue(kind, out float until) && SyncRun.HostNow < until)
            {
                waitSeconds = Mathf.CeilToInt(until - SyncRun.HostNow);
                why = $"The last {what} vote failed: the next one can start in {waitSeconds} s.";
                return false;
            }
            return true;
        }

        /// <summary>Host: is the vote's stage the one the host is on (votes for an old stage don't count)?</summary>
        public static bool ValidTarget(VoteKind kind, int mapKey) =>
            Ghosts.CurrentMapKey != 0 && GameRefs.Main != null && (kind == VoteKind.ReturnToPlaza || mapKey == Ghosts.CurrentMapKey);

        /// <summary>A vote started (everyone's game sees it): that player has used their call on this stage.</summary>
        public static void OnVoteStarted(VoteKind kind, byte by)
        {
            if (kind is VoteKind.Restart or VoteKind.ReturnToPlaza) _called.Add((by, kind));
        }

        /// <summary>A vote ended (everyone's game sees it).</summary>
        public static void OnVoteFinished(VoteKind kind, bool passed)
        {
            if (kind is not (VoteKind.Restart or VoteKind.ReturnToPlaza)) return;
            if (passed) _passed.Add(kind);
            else _cooldownUntil[kind] = SyncRun.HostNow + FailedVoteCooldown;
        }

        /// <summary>Self-test, outside a session: the ledger's rules.</summary>
        public static string SelfCheck(out bool ok)
        {
            var called = new HashSet<(byte, VoteKind)>(_called);
            var passed = new HashSet<VoteKind>(_passed);
            var cool = new Dictionary<VoteKind, float>(_cooldownUntil);
            try
            {
                ResetLedger();
                bool first = CanCall(VoteKind.Restart, 1, out _, out _);
                OnVoteStarted(VoteKind.Restart, 1);
                bool again = !CanCall(VoteKind.Restart, 1, out _, out _);
                bool otherBlocked = CanCall(VoteKind.Restart, 2, out _, out _);
                OnVoteFinished(VoteKind.Restart, false);
                bool cooldown = !CanCall(VoteKind.Restart, 2, out string why2, out int wait) && wait > 25;
                _cooldownUntil[VoteKind.Restart] = SyncRun.HostNow - 1f;
                bool afterCooldown = CanCall(VoteKind.Restart, 2, out _, out _);
                OnVoteStarted(VoteKind.Restart, 2);
                OnVoteFinished(VoteKind.Restart, true);
                bool lockedForAll = !CanCall(VoteKind.Restart, 3, out _, out _) && !CanCall(VoteKind.Restart, 0, out _, out _);
                bool plazaStillOpen = CanCall(VoteKind.ReturnToPlaza, 1, out _, out _);
                bool othersFree = CanCall(VoteKind.Kick, 1, out _, out _);
                ok = first && again && otherBlocked && cooldown && afterCooldown && lockedForAll && plazaStillOpen && othersFree;
                return $"first call {first}, same player again refused {again}, another player allowed before the result {otherBlocked}, " +
                       $"30 s after a failed one {cooldown} ({why2}), allowed after the cooldown {afterCooldown}, nobody after one passed {lockedForAll}, " +
                       $"plaza vote unaffected {plazaStillOpen}, other votes unaffected {othersFree}";
            }
            finally
            {
                ResetLedger();
                foreach (var c in called) _called.Add(c);
                foreach (var p in passed) _passed.Add(p);
                foreach (var kv in cool) _cooldownUntil[kv.Key] = kv.Value;
            }
        }
    }
}
