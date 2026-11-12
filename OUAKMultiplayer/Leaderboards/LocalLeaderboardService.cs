using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OUAKMultiplayer.Leaderboards
{
    /// <summary>
    /// Keeps run records in a folder on this PC (runs.jsonl, one JSON record per line, appended) and works the boards
    /// out from them by the rules it's given. Only this PC's records are in it, so its boards are "this PC's" boards.
    /// It never connects to anything.
    /// </summary>
    public sealed class LocalLeaderboardService : ILeaderboardService
    {
        private readonly string _file;
        private readonly string _youId;
        private readonly List<RunRecord> _runs = new();
        private readonly HashSet<string> _ids = new();
        private bool _loaded;
        private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

        public BoardRules Rules { get; }
        public string Name => "local";
        public bool IsOnline => false;
        public string Folder { get; }
        public int Count { get { Load(); return _runs.Count; } }

        /// <param name="folder">Where runs.jsonl lives (made if missing).</param>
        /// <param name="rules">How the boards count (all undecided unless the caller says otherwise).</param>
        /// <param name="youId">This PC's player id, marked on the boards.</param>
        public LocalLeaderboardService(string folder, BoardRules rules, string youId = null)
        {
            Folder = folder;
            _file = Path.Combine(folder, "runs.jsonl");
            Rules = rules ?? new BoardRules();
            _youId = youId;
        }

        private void Load()
        {
            if (_loaded) return;
            _loaded = true;
            if (!File.Exists(_file)) return;
            foreach (var line in File.ReadLines(_file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var r = JsonSerializer.Deserialize<RunRecord>(line, Json);
                    if (r != null && !string.IsNullOrEmpty(r.EventId) && _ids.Add(r.EventId)) _runs.Add(r);
                }
                catch { /* a damaged line is skipped, the rest still counts */ }
            }
        }

        /// <summary>Why a record can't be kept (it's empty or incomplete, or an unknown schema); null when it can.</summary>
        public static string Check(RunRecord r)
        {
            if (r == null) return "empty record";
            if (string.IsNullOrEmpty(r.EventId)) return "no event id";
            if (r.Schema != 1) return $"unknown schema {r.Schema}";
            if (r.Player == null || string.IsNullOrEmpty(r.Player.Id)) return "no player";
            if (r.Size < 0 || r.Objects < 0) return "negative size or count";
            if (r.Presents == null) return "no present list";
            return null;
        }

        public Task<SubmitResult> SubmitRunsAsync(IReadOnlyList<RunRecord> runs, CancellationToken ct = default)
        {
            Load();
            var res = new SubmitResult();
            var lines = new List<string>();
            foreach (var r in runs ?? Array.Empty<RunRecord>())
            {
                string why = Check(r);
                if (why != null) { res.Rejected++; res.Reasons.Add(why); continue; }
                if (!_ids.Add(r.EventId)) { res.Duplicates++; continue; }
                _runs.Add(r);
                lines.Add(JsonSerializer.Serialize(r, Json));
                res.Accepted++;
            }
            if (lines.Count > 0)
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllLines(_file, lines);
            }
            return Task.FromResult(res);
        }

        public Task<BoardPage> GetBoardAsync(Board board, BoardQuery query, CancellationToken ct = default)
        {
            Load();
            query ??= new BoardQuery();
            var rows = Rows(board);
            var page = new BoardPage
            {
                Board = board, Period = query.Period, AsOfUtc = DateTime.UtcNow, Source = Name, Rules = Rules.ToString(),
                Total = rows.Sum(r => r.Value), TotalEntries = rows.Count,
            };
            // Ranked by value, ties share the place (1, 1, 3).
            var ordered = rows.OrderByDescending(r => r.Value).ThenBy(r => r.Key, StringComparer.Ordinal).ToList();
            for (int i = 0; i < ordered.Count; i++)
                ordered[i].Rank = i > 0 && ordered[i].Value == ordered[i - 1].Value ? ordered[i - 1].Rank : i + 1;
            int start = query.Offset;
            if (!string.IsNullOrEmpty(query.AroundPlayerId))
            {
                int at = ordered.FindIndex(e => e.Key == query.AroundPlayerId);
                if (at >= 0) start = Math.Max(0, at - query.Limit / 2);
            }
            page.Entries = ordered.Skip(Math.Max(0, start)).Take(Math.Max(1, query.Limit)).ToList();
            return Task.FromResult(page);
        }

        public async Task<PlayerSummary> GetPlayerAsync(string playerId, CancellationToken ct = default)
        {
            Load();
            var mine = _runs.Where(r => r.Player.Id == playerId).ToList();
            var forCookies = Only(Rules.CookieRuns).Where(r => r.Player.Id == playerId).ToList();
            var forKatamari = Only(Rules.KatamariRuns).Where(r => r.Player.Id == playerId).ToList();
            var s = new PlayerSummary
            {
                Player = mine.LastOrDefault()?.Player ?? new PlayerRef { Id = playerId },
                Runs = mine.Count,
                Cookies = forCookies.Sum(r => (long)Rules.Cookies.CookiesFor(r)),
                KatamariShare = (long)KatamariValue(forKatamari),
            };
            foreach (var b in new[] { Board.EveryonesKatamari, Board.Cookies })
            {
                var page = await GetBoardAsync(b, new BoardQuery { Limit = int.MaxValue }, ct);
                var me = page.Entries.FirstOrDefault(e => e.Key == playerId);
                if (me != null) s.Ranks[b] = me.Rank;
            }
            return s;
        }

        // ---------------------------------------------------------------- counting

        private List<BoardEntry> Rows(Board board) => board switch
        {
            Board.EveryonesKatamari => Rules.Katamari == KatamariRule.Undecided ? new() : PerPlayer(Only(Rules.KatamariRuns), KatamariValue),
            Board.Cookies => Rules.Cookies is UndecidedCookies ? new() : PerPlayer(Only(Rules.CookieRuns), rs => rs.Sum(r => (double)Rules.Cookies.CookiesFor(r))),
            Board.Cousins => CousinRows(),
            Board.Presents => PresentRows(),
            _ => new(),
        };

        private List<RunRecord> Only(Func<RunRecord, bool> counts) => counts == null ? _runs : _runs.Where(counts).ToList();

        private List<BoardEntry> PerPlayer(List<RunRecord> runs, Func<List<RunRecord>, double> value) =>
            runs.GroupBy(r => r.Player.Id).Select(g =>
            {
                var list = g.ToList();
                return new BoardEntry { Key = g.Key, Name = list.Last().Player.Name, Value = value(list), IsYou = g.Key == _youId };
            }).ToList();

        private double KatamariValue(List<RunRecord> runs) => Rules.Katamari switch
        {
            KatamariRule.FinalSizeOfEveryRun => runs.Sum(r => (double)r.Size),
            KatamariRule.BestSizePerStage => runs.GroupBy(r => r.Stage).Sum(g => (double)g.Max(r => r.Size)),
            KatamariRule.ObjectsRolledUp => runs.Sum(r => (double)r.Objects),
            _ => 0d,
        };

        private static RunRecord LatestOf(IEnumerable<RunRecord> runs)
        {
            RunRecord last = null;
            foreach (var r in runs) if (last == null || r.PlayedUtc >= last.PlayedUtc) last = r;
            return last;
        }

        /// <summary>Custom cousins are each player's own creation: they're counted together as one entry.</summary>
        private static string CousinKey(RunRecord r) => r.Cousin == 999 ? "custom" : r.Cousin.ToString();

        private List<BoardEntry> CousinRows()
        {
            var counted = new List<RunRecord>();
            if (Rules.Cousins == CousinRule.LatestCousinPerPlayer)
                foreach (var g in Only(Rules.CousinRuns).GroupBy(r => r.Player.Id)) counted.Add(LatestOf(g));
            else if (Rules.Cousins == CousinRule.RunsPlayedAs)
                counted.AddRange(Only(Rules.CousinRuns));
            return counted.Where(r => r.Cousin >= 0).GroupBy(CousinKey)
                .Select(g => new BoardEntry { Key = g.Key, Name = g.Key == "custom" ? "Custom cousins" : $"Cousin {g.Key}", Value = g.Count() }).ToList();
        }

        private List<BoardEntry> PresentRows()
        {
            var counted = new List<int>();
            var runs = Only(Rules.PresentRuns);
            if (Rules.Presents == PresentRule.WornPerPlayer)
                foreach (var g in runs.GroupBy(r => r.Player.Id)) counted.AddRange(LatestOf(g).Presents.Distinct());
            else if (Rules.Presents == PresentRule.WornInRuns)
                foreach (var r in runs) counted.AddRange(r.Presents.Distinct());
            else if (Rules.Presents == PresentRule.GivenByStages)
                foreach (var r in runs) if (r.PresentGiven >= 0) counted.Add(r.PresentGiven);
            return counted.Where(p => p >= 0).GroupBy(p => p)
                .Select(g => new BoardEntry { Key = g.Key.ToString(), Name = $"Present {g.Key}", Value = g.Count() }).ToList();
        }
    }
}
