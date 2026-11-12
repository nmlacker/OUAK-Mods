using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Il2CppApp.KatamariSin;
using MelonLoader;
using MelonLoader.Utils;
using OUAKModAPI;

namespace OUAKMultiplayer.Leaderboards
{
    /// <summary>
    /// What each player's game records for the leaderboards: one <see cref="RunRecord"/> per finished run, built from
    /// the run's own numbers (SyncRun's result, the session's ranking, your look) and kept in the local outbox
    /// (UserData/OUAKMultiplayer/Leaderboards). Nothing is sent anywhere: the servers don't exist yet.
    /// <para>Off by default ([OUAKModMenu_Online] LeaderboardRecording = false) until the user decides the rules (which
    /// runs count, how cookies are earned...). Session runs are recorded when the session's results are final (your place
    /// and the host's checks are known then); solo story stages when the stage ends.</para>
    /// </summary>
    internal static class LeaderboardRecorder
    {
        public static bool Enabled;
        /// <summary>The outbox's folder; the self-test points it elsewhere.</summary>
        public static string Folder = Path.Combine(MelonEnvironment.UserDataDirectory, "OUAKMultiplayer", "Leaderboards");
        public static RunRecord Last { get; private set; }
        public static int Recorded { get; private set; }

        private static MelonPreferences_Entry<bool> _pref;
        private static LocalLeaderboardService _outbox;
        private static string _outboxFolder;
        private static readonly HashSet<string> _done = new();
        private static bool _hooked;

        public static void Init(MelonPreferences_Category cat)
        {
            _pref = cat.CreateEntry("LeaderboardRecording", false, "Record runs for the leaderboards",
                "Keep a record of each finished run for the (future) online leaderboards, on this PC only. Off until the leaderboard rules are decided.");
            Enabled = _pref.Value;
            if (_hooked) return;
            _hooked = true;
            GameEvents.StageEnded += OnStageEnded;
        }

        public static LocalLeaderboardService Outbox
        {
            get
            {
                if (_outbox == null || _outboxFolder != Folder)
                {
                    _outboxFolder = Folder;
                    _outbox = new LocalLeaderboardService(Folder, LeaderboardRules.Decided, MyId());
                }
                return _outbox;
            }
        }

        // ---------------------------------------------------------------- when runs end

        /// <summary>A session's results are final: your own run, with its place and the host's checks.</summary>
        public static void OnFinalStandings(byte runId, int map, List<RunResult> standings)
        {
            if (!Enabled) return;
            try
            {
                var mine = standings.FirstOrDefault(r => r.Id == NetSession.MyId);
                if (mine == null) return;
                string key = $"{HostKey()}:{runId}:{map}";
                if (!_done.Add(key)) return;
                var places = Ranking.StandingsPlaces(standings);
                var rec = Build(mine, map);
                rec.Mode = RunMode.Session;
                rec.SessionRunId = key;
                rec.PlayersInRun = standings.Count;
                rec.OtherHumans = standings.Count(s => s.Id != NetSession.MyId && NetSession.Peers.TryGetValue(s.Id, out var p) && p.Identity.Kind != PlayerIdentity.Bot);
                rec.HostVerified = mine.Verdict == 0;
                rec.Verdict = mine.Verdict;
                rec.Place = places.TryGetValue(mine.Id, out int p) ? p : 0;
                rec.Disqualified = mine.Disqualified;
                Store(rec);
            }
            catch (Exception e) { MelonLogger.Warning($"[Leaderboards] Recording the run failed: {e.Message}"); }
        }

        /// <summary>A story stage ended outside a session (solo).</summary>
        private static void OnStageEnded(MainGameManager gm, bool cleared)
        {
            if (!Enabled || NetSession.Connected || gm == null || GameRefs.IsMultiplay) return;
            try
            {
                var r = new RunResult
                {
                    Size = gm.KatamariSize, Time = gm._playTime, Cleared = cleared,
                    SizeTime = gm._hasCleared ? gm._clearTime : -1f,
                };
                RunStats.ReadRank(r);
                if (StageScore.Read(gm, out _, out var kind, out int score)) { r.ScoreKind = (byte)kind; r.Score = score; }
                var rec = Build(r, 1000 + (int)gm.TargetStage);
                rec.Mode = RunMode.Solo;
                rec.ReachedWin = gm._hasCleared;
                Store(rec);
            }
            catch (Exception e) { MelonLogger.Warning($"[Leaderboards] Recording the stage failed: {e.Message}"); }
        }

        // ---------------------------------------------------------------- building a record

        public static RunRecord Build(RunResult r, int map)
        {
            var rec = new RunRecord
            {
                EventId = Guid.NewGuid().ToString("N"),
                ModVersion = MultiplayerCore.Version,
                PlayedUtc = DateTime.UtcNow,
                Player = new PlayerRef { Kind = MyKind(), Id = MyId(), Name = NetSession.MyName ?? "" },
                Stage = map >= 1000 ? map - 1000 : map,
                StageName = StageNameOf(map),
                Size = r.Size,
                Objects = r.Objects,
                ReachedWin = r.ClearIgt >= 0f || r.SizeTime >= 0f || r.Cleared,
                Rating = r.Rank,
                ScoreKind = r.ScoreKind,
                Score = r.Score,
                Igt = r.Igt,
                Rta = r.RealTime,
                WinIgt = r.ClearIgt,
                WinRta = r.ClearRta,
            };
            ReadLook(rec);
            return rec;
        }

        /// <summary>The cousin and presents from your look (what the others are shown): NetworkUserInfo's own fields.</summary>
        private static void ReadLook(RunRecord rec)
        {
            var look = NetSession.MyLook ?? Looks.LocalLook(NetSession.MyName ?? "");
            if (look == null) return;
            try
            {
                var ui = new NetworkUserInfo();
                ui.SetByteData(look, 0);
                rec.Cousin = (int)ui.characterID;
                rec.CustomCousin = rec.Cousin == 999 ? ui.customPlayerIndex : -1;
                var worn = ui.wearingPresent;
                if (worn != null)
                {
                    var list = new List<int>();
                    for (int i = 0; i < worn.Length; i++) if (worn[i] >= 0) list.Add(worn[i]);
                    rec.Presents = list.ToArray();
                }
            }
            catch { }
        }

        private static void Store(RunRecord rec)
        {
            var res = Outbox.SubmitRunsAsync(new[] { rec }).GetAwaiter().GetResult();
            if (res.Accepted == 0)
            {
                MelonLogger.Warning($"[Leaderboards] Run not kept: {string.Join(", ", res.Reasons)}");
                return;
            }
            Last = rec;
            Recorded++;
            MelonLogger.Msg($"[Leaderboards] Recorded {rec.Mode} run on {rec.StageName}: {KatamariState.SizeText(rec.Size)}, {rec.Objects} objects, " +
                            $"cousin {rec.Cousin}, presents [{string.Join(",", rec.Presents)}]{(rec.Mode == RunMode.Session ? $", place {rec.Place}, verified {rec.HostVerified}" : "")}.");
        }

        private static string MyKind() => NetSession.MyIdentity.Kind == PlayerIdentity.SteamAccount ? "steam" : "install";

        private static string MyId() => NetSession.MyIdentity.Value.ToString();

        private static string HostKey()
        {
            try
            {
                if (NetSession.Mode == NetSession.Role.Host) return MyId();
                return NetSession.Peers.TryGetValue(0, out var host) ? host.Identity.Value.ToString() : "host";
            }
            catch { return "host"; }
        }

        private static string StageNameOf(int map)
        {
            try
            {
                var e = StageCatalog.Entries.FirstOrDefault(x => Ghosts.KeyFor(x) == map);
                return e?.Internal ?? Votes.StageName(map);
            }
            catch { return map.ToString(); }
        }
    }
}
