using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Il2CppApp.KatamariSin;
using MelonLoader;
using UnityEngine;
using OUAKModAPI;

namespace OUAKMultiplayer
{
    /// <summary>What a stage rates you on, in its own terms (see <see cref="StageScore"/>).</summary>
    internal enum ScoreKind : byte { None, Size, Count, Time, JustSize, Price, JustPrice, Favor, Watered, Ducks }

    /// <summary>
    /// The stage's own score, the number its rating (MainGameManager.CheckClearRate) compares with the stage's five
    /// targets (StageClearRateData RateNumber). Traced in GameAssembly 2026-11-06: CheckClearRate switches on
    /// _inStageRuleID and reads
    /// <list type="bullet">
    /// <item>rules 1, 6, 21, 31, 41: the clear time (_clearTime once cleared, else _playTime), lower is better;</item>
    /// <item>rules 2, 3, 20, 104: StageMonoManager.currentTypeMono, the count of the stage's target objects ("20 food items");</item>
    /// <item>rule 5: |_targetNumber - _katamariSize|, how close to the asked size (Just Right), lower is better;</item>
    /// <item>rule 51: |_targetNumber - _currentPrice|, how close to the asked price, lower is better;</item>
    /// <item>rule 100: _currentPrice; 101: _pharaohMissionFavor; 107: _wateredDesertCount; 110: _duckMissionClearedCount;</item>
    /// <item>rules 99 (always the top badge), 105 (_cowBear_clearedMonoID's list index), 108 (sweetness) and 111 (cleaning
    /// ratio) have no plain number here: only their rating counts;</item>
    /// <item>every other rule: _katamariSize, the diameter.</item>
    /// </list>
    /// The rating itself (0 = top badge … 4) is the coarse version of the same measure, so it's compared first.
    /// </summary>
    internal static class StageScore
    {
        public static ScoreKind KindOf(int rule) => rule switch
        {
            1 or 6 or 21 or 31 or 41 => ScoreKind.Time,
            2 or 3 or 20 or 104 => ScoreKind.Count,
            5 => ScoreKind.JustSize,
            51 => ScoreKind.JustPrice,
            100 => ScoreKind.Price,
            101 => ScoreKind.Favor,
            107 => ScoreKind.Watered,
            110 => ScoreKind.Ducks,
            99 or 105 or 108 or 111 => ScoreKind.None,
            _ => ScoreKind.Size,
        };

        public static bool LowerIsBetter(ScoreKind k) => k is ScoreKind.Time or ScoreKind.JustSize or ScoreKind.JustPrice;

        /// <summary>The stage's rating right now (CheckClearRate: 0 = top badge, 4 the lowest), its kind and its number (-1 = none yet).</summary>
        public static bool Read(MainGameManager gm, out sbyte rate, out ScoreKind kind, out int value)
        {
            rate = -1;
            kind = ScoreKind.None;
            value = -1;
            if (gm == null || GameRefs.IsMultiplay) return false;
            try
            {
                rate = (sbyte)Mathf.Clamp(gm.CheckClearRate(), 0, 15);
                kind = KindOf(gm._inStageRuleID);
                value = kind switch
                {
                    ScoreKind.Size => gm.KatamariSize,
                    ScoreKind.Count => gm.MonoManager != null ? gm.MonoManager.currentTypeMono : -1,
                    ScoreKind.Time => gm._hasCleared ? Mathf.RoundToInt(gm._clearTime * 1000f) : -1,
                    ScoreKind.JustSize => Math.Abs(gm._targetNumber - gm.KatamariSize),
                    ScoreKind.JustPrice => Math.Abs(gm._targetNumber - gm._currentPrice),
                    ScoreKind.Price => gm._currentPrice,
                    ScoreKind.Favor => gm._pharaohMissionFavor,
                    ScoreKind.Watered => gm._wateredDesertCount,
                    ScoreKind.Ducks => gm._duckMissionClearedCount,
                    _ => -1,
                };
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// The rating a size earns on this stage, the way CheckClearRate rates size stages: the count of the five targets
        /// at or below the size, 5 - count, with 0 or 1 target both giving 4. -1 if the targets can't be read.
        /// </summary>
        public static sbyte RateForSize(MainGameManager gm, int size)
        {
            try
            {
                var list = gm?.AllStageClearData?.list;
                int idx = gm != null ? gm._stageIdx : -1;
                if (list == null || idx < 0 || idx >= list.Count || list[idx]?.RateNumber == null) return -1;
                var nums = list[idx].RateNumber;
                int count = 0;
                for (int i = 0; i < nums.Length && i < 5 && nums[i] <= size; i++) count++;
                return (sbyte)(5 - Math.Max(1, count));
            }
            catch { return -1; }
        }

        public static string KindName(ScoreKind k) => k switch
        {
            ScoreKind.Size => "diameter",
            ScoreKind.Count => "target objects rolled up",
            ScoreKind.Time => "clear time",
            ScoreKind.JustSize => "how close to the asked size",
            ScoreKind.JustPrice => "how close to the asked price",
            ScoreKind.Price => "value",
            ScoreKind.Favor => "the Pharaoh's favour",
            ScoreKind.Watered => "desert watered",
            ScoreKind.Ducks => "ducks' missions",
            _ => "the stage's rating",
        };

        /// <summary>"12cm 3mm", "18 objects", "1:02.50", "off by 4mm"...</summary>
        public static string Text(ScoreKind k, int v)
        {
            if (v < 0 && k != ScoreKind.None) return "-";
            return k switch
            {
                ScoreKind.Size => KatamariState.SizeText(v),
                ScoreKind.Time => KatamariState.ClockText(v / 1000f),
                ScoreKind.JustSize => v == 0 ? "exact" : "off by " + KatamariState.SizeText(v),
                ScoreKind.JustPrice => v == 0 ? "exact" : "off by " + v,
                ScoreKind.None => "",
                _ => v.ToString(),
            };
        }
    }

    /// <summary>What a ranking system compares, one after another; the first difference decides.</summary>
    internal enum RankKey : byte
    {
        ReachedWin = 0, StageScore = 1, GameRank = 2, WinRta = 3, WinIgt = 4, WinStage = 5,
        EndRta = 6, EndIgt = 7, Diameter = 8, Objects = 9, Freebies = 10, Dashes = 11,
    }

    internal struct RankRule
    {
        public RankKey Key;
        /// <summary>Turned round: the other end of this measure comes first.</summary>
        public bool Reverse;
        public RankRule(RankKey key, bool reverse = false) { Key = key; Reverse = reverse; }
    }

    internal sealed class RankingSystem
    {
        public string Title = "";
        public readonly List<RankRule> Rules = new();
        public bool Preset;

        public RankingSystem Copy(string title) { var s = new RankingSystem { Title = title }; s.Rules.AddRange(Rules); return s; }
    }

    /// <summary>One player's numbers for ranking, from a finished result or from their live state.</summary>
    internal sealed class RankInput
    {
        public byte Id;
        public bool Dq, Reached, Final;
        public sbyte Rate = -1;
        public ScoreKind Kind;
        public int Score = -1, Size, Objects = -1, Freebies = -1, Dashes = -1;
        public float WinRta = -1f, WinIgt = -1f, WinStage = -1f, EndRta = -1f, EndIgt = -1f;

        public static RankInput From(RunResult r)
        {
            var e = new RankInput
            {
                Id = r.Id, Final = true, Dq = r.Disqualified, Reached = r.ClearIgt >= 0f || r.SizeTime >= 0f || r.Cleared,
                Rate = r.Rank, Kind = (ScoreKind)r.ScoreKind, Score = r.Score, Size = r.Size,
                Objects = r.Objects, Freebies = r.Freebies, Dashes = r.Dashes,
                WinRta = r.ClearRta, WinIgt = r.ClearIgt, WinStage = r.SizeTime, EndRta = r.RealTime, EndIgt = r.Igt,
            };
            // On size stages the score is the size shown (the same number; this keeps them from ever disagreeing).
            if (e.Kind == ScoreKind.Size) e.Score = e.Size;
            // A time-attack runner cut off by Sudden Death: their predicted time stands in for the clear time.
            if (e.Kind == ScoreKind.Time && e.Score < 0 && r.Predicted) e.Score = Mathf.RoundToInt(r.Time * 1000f);
            return e;
        }

        public static RankInput From(byte id, in KatamariState s) => new()
        {
            Id = id, Reached = s.ReachedWin, Rate = s.StageRate, Kind = (ScoreKind)s.ScoreKind, Size = s.Size,
            Score = (ScoreKind)s.ScoreKind == ScoreKind.Size ? s.Size : s.Score,
            WinRta = s.ClearRta, WinIgt = s.ClearIgt, WinStage = s.ClearStage,
        };
    }

    /// <summary>
    /// How players are placed in a shared run (the user's design, 2026-11-06). A ranking system is a title and an
    /// ordered list of measures; players are compared on the first, and the next one only decides between players
    /// equal on all before it. Players equal on every measure share the place (1, 1, 3). Leaving the race (DQ)
    /// always comes last. Times count as equal to their precision: RTA to a tenth of a second (it crosses the
    /// network), IGT and the stage clock to a hundredth.
    /// <para>Presets: <b>Standard</b> (reached the win condition, the stage's own score, time to the win condition by
    /// RTA with IGT deciding, final diameter) and <b>Speedrun</b> (reached the win condition, then only the time to it:
    /// RTA, IGT deciding). Players make their own (title + measures), saved in their preferences. The host's choice is
    /// the session's: sent with the lobby settings, used for the live places and the Results, and shown on the
    /// Results so everyone sees how they were ranked.</para>
    /// </summary>
    internal static class Ranking
    {
        public const int MaxRules = 12, MaxTitle = 32, MaxCustom = 16;

        public static readonly RankingSystem Standard = new()
        {
            Title = "Standard", Preset = true,
            Rules = { new(RankKey.ReachedWin), new(RankKey.StageScore), new(RankKey.WinRta), new(RankKey.WinIgt), new(RankKey.Diameter) },
        };

        public static readonly RankingSystem Speedrun = new()
        {
            Title = "Speedrun", Preset = true,
            Rules = { new(RankKey.ReachedWin), new(RankKey.WinRta), new(RankKey.WinIgt) },
        };

        public static readonly List<RankingSystem> Custom = new();
        public static readonly RankKey[] AllKeys = (RankKey[])Enum.GetValues(typeof(RankKey));

        /// <summary>The host's (and your own) chosen system, by title.</summary>
        public static string ActiveTitle = "Standard";

        private static RankingSystem _received;
        private static MelonPreferences_Entry<string> _activePref, _customPref;

        public static IEnumerable<RankingSystem> All => new[] { Standard, Speedrun }.Concat(Custom);

        public static RankingSystem Active => All.FirstOrDefault(s => s.Title == ActiveTitle) ?? Standard;

        /// <summary>The system in force: the host's in a session you joined, otherwise your own choice.</summary>
        public static RankingSystem Current => NetSession.Mode == NetSession.Role.Client ? _received ?? Standard : Active;

        // ---------------------------------------------------------------- comparing

        public static int Compare(RankingSystem sys, RankInput a, RankInput b)
        {
            if (a.Dq != b.Dq) return a.Dq ? 1 : -1;
            foreach (var rule in sys.Rules)
            {
                int c = CompareKey(rule.Key, a, b);
                if (rule.Reverse) c = -c;
                if (c != 0) return c;
            }
            return 0;
        }

        private static int CompareKey(RankKey key, RankInput a, RankInput b) => key switch
        {
            RankKey.ReachedWin => a.Reached == b.Reached ? 0 : a.Reached ? -1 : 1,
            RankKey.StageScore => CompareScore(a, b),
            RankKey.GameRank => CompareRate(a, b),
            RankKey.WinRta => CompareTime(a.WinRta, b.WinRta, 10f),
            RankKey.WinIgt => CompareTime(a.WinIgt, b.WinIgt, 100f),
            RankKey.WinStage => CompareTime(a.WinStage, b.WinStage, 100f),
            RankKey.EndRta => CompareTime(a.EndRta, b.EndRta, 10f),
            RankKey.EndIgt => CompareTime(a.EndIgt, b.EndIgt, 100f),
            RankKey.Diameter => b.Size.CompareTo(a.Size),
            RankKey.Objects => b.Objects.CompareTo(a.Objects),
            RankKey.Freebies => CompareFewer(a.Freebies, b.Freebies),
            RankKey.Dashes => CompareFewer(a.Dashes, b.Dashes),
            _ => 0,
        };

        private static int CompareRate(RankInput a, RankInput b)
        {
            int ra = a.Rate < 0 ? int.MaxValue : a.Rate, rb = b.Rate < 0 ? int.MaxValue : b.Rate;
            return ra.CompareTo(rb);
        }

        /// <summary>The stage's rating first (0 = top badge), then its exact number in its own direction; no number yet = last.</summary>
        private static int CompareScore(RankInput a, RankInput b)
        {
            int c = CompareRate(a, b);
            if (c != 0 || a.Kind != b.Kind || a.Kind == ScoreKind.None) return c;
            bool ha = a.Score >= 0, hb = b.Score >= 0;
            if (ha != hb) return ha ? -1 : 1;
            if (!ha) return 0;
            return StageScore.LowerIsBetter(a.Kind) ? a.Score.CompareTo(b.Score) : b.Score.CompareTo(a.Score);
        }

        /// <summary>Earlier first, to the given precision (10 = tenths: floored, so equal tenths tie); unknown (-1) last.</summary>
        private static int CompareTime(float a, float b, float perSecond)
        {
            bool ha = a >= 0f, hb = b >= 0f;
            if (ha != hb) return ha ? -1 : 1;
            if (!ha) return 0;
            return Math.Floor(a * perSecond).CompareTo(Math.Floor(b * perSecond));
        }

        private static int CompareFewer(int a, int b)
        {
            bool ha = a >= 0, hb = b >= 0;
            if (ha != hb) return ha ? -1 : 1;
            return a.CompareTo(b);
        }

        /// <summary>Sorts the standings by the system in force (stable: equal players keep their order).</summary>
        public static void Sort(List<RunResult> list)
        {
            var sys = Current;
            var sorted = list.Select(r => (r, e: RankInput.From(r))).ToList();
            var cmp = Comparer<RankInput>.Create((x, y) => Compare(sys, x, y));
            var ordered = sorted.OrderBy(t => t.e, cmp).Select(t => t.r).ToList();
            list.Clear();
            list.AddRange(ordered);
        }

        /// <summary>Places for players in order (shared on a full tie: 1, 1, 3), by id.</summary>
        public static Dictionary<byte, int> Places(RankingSystem sys, List<RankInput> players)
        {
            var cmp = Comparer<RankInput>.Create((x, y) => Compare(sys, x, y));
            var ordered = players.OrderBy(p => p, cmp).ToList();
            var places = new Dictionary<byte, int>();
            for (int i = 0; i < ordered.Count; i++)
                places[ordered[i].Id] = i > 0 && Compare(sys, ordered[i - 1], ordered[i]) == 0 ? places[ordered[i - 1].Id] : i + 1;
            return places;
        }

        /// <summary>Places in the final (or so far) standings, by the system in force.</summary>
        public static Dictionary<byte, int> StandingsPlaces(List<RunResult> list) => Places(Current, list.Select(RankInput.From).ToList());

        // ---------------------------------------------------------------- live places

        private static Dictionary<byte, int> _live = new();
        private static float _nextLive;

        /// <summary>
        /// Live places in the run going on: finished players by their results, the rest by their live numbers (size and
        /// stage score so far, when they reached the win condition). Refreshed four times a second.
        /// </summary>
        public static Dictionary<byte, int> LivePlaces()
        {
            if (Time.unscaledTime < _nextLive) return _live;
            _nextLive = Time.unscaledTime + 0.25f;
            try { _live = Places(Current, LiveInputs()); }
            catch { _live = new(); }
            return _live;
        }

        /// <summary>Everyone in the run going on: finished players' results, the rest's live numbers. Empty outside a run.</summary>
        public static List<RankInput> LiveInputs()
        {
            var players = new List<RankInput>();
            if (SyncRun.RunMap == 0 || SyncRun.Phase is not (RunPhase.Running or RunPhase.Finished)) return players;
            foreach (var r in SyncRun.Standings) players.Add(RankInput.From(r));
            if (players.All(p => p.Id != NetSession.MyId) && Ghosts.TryLocalState(out var me) && Ghosts.CurrentMapKey == SyncRun.RunMap)
                players.Add(RankInput.From(NetSession.MyId, me));
            float now = Time.unscaledTime;
            foreach (var p in NetSession.Peers.Values)
                if (players.All(x => x.Id != p.Id) && p.HasState && now - p.StateAt < 3f && p.MapKey == SyncRun.RunMap)
                    players.Add(RankInput.From(p.Id, p.State));
            return players;
        }

        // ---------------------------------------------------------------- words

        public static string KeyName(RankKey k) => k switch
        {
            RankKey.ReachedWin => "Reached the win condition",
            RankKey.StageScore => "Stage score",
            RankKey.GameRank => "The game's rank badge",
            RankKey.WinRta => "Time to the win condition (RTA)",
            RankKey.WinIgt => "Time to the win condition (IGT)",
            RankKey.WinStage => "Time to the win condition (stage clock)",
            RankKey.EndRta => "Run time to the end (RTA)",
            RankKey.EndIgt => "Run time to the end (IGT)",
            RankKey.Diameter => "Diameter",
            RankKey.Objects => "Objects rolled up",
            RankKey.Freebies => "Freebies used",
            RankKey.Dashes => "Dashes",
            _ => k.ToString(),
        };

        public static string KeyHelp(RankKey k) => k switch
        {
            RankKey.ReachedWin => "Whether they met the stage's goal (\"20 food items\", \"15 cm\"...) during the run.",
            RankKey.StageScore => "The stage's own measure: diameter on size stages, the count on \"N items\" stages, value on price stages, " +
                                  "how close to the asked size or price on Just Right stages, clear time on time stages. Its rank badge counts first.",
            RankKey.GameRank => "The rank badge the game gives for the stage (5 steps).",
            RankKey.WinRta => "Real time from GO to the moment they reached the win condition. Counts to a tenth of a second.",
            RankKey.WinIgt => "In-game time (paused time left out) to the win condition. Counts to a hundredth of a second.",
            RankKey.WinStage => "The stage's own clock when they reached the win condition (the game's \"Size Time\").",
            RankKey.EndRta => "Real time from GO to the end of their run. Only known once they've finished.",
            RankKey.EndIgt => "In-game time of their whole run. Only known once they've finished.",
            RankKey.Diameter => "Katamari size at the end (live: right now).",
            RankKey.Objects => "Objects rolled up in the run. Only known once they've finished.",
            RankKey.Freebies => "Freebies used in the run. Only known once they've finished.",
            RankKey.Dashes => "Dashes in the run. Only known once they've finished.",
            _ => "",
        };

        /// <summary>Which end comes first: "reached first", "earlier first"...</summary>
        public static string Direction(RankRule r)
        {
            string[] pair = r.Key switch
            {
                RankKey.ReachedWin => new[] { "reached first", "not reached first" },
                RankKey.StageScore => new[] { "best first", "worst first" },
                RankKey.GameRank => new[] { "best badge first", "lowest badge first" },
                RankKey.Diameter => new[] { "bigger first", "smaller first" },
                RankKey.Objects => new[] { "more first", "fewer first" },
                RankKey.Freebies or RankKey.Dashes => new[] { "fewer first", "more first" },
                _ => new[] { "faster first", "slower first" },
            };
            return pair[r.Reverse ? 1 : 0];
        }

        /// <summary>"Reached the win condition › Stage score › …", with directions where they're turned round.</summary>
        public static string Describe(RankingSystem s) =>
            string.Join(" › ", s.Rules.Select(r => KeyName(r.Key) + (r.Reverse ? $" ({Direction(r)})" : "")));

        // ---------------------------------------------------------------- saving

        public static void Init(MelonPreferences_Category cat)
        {
            _activePref = cat.CreateEntry("RankingSystem", "Standard", "Ranking system", "Hosting: the title of the ranking system the session uses (Standard, Speedrun or one of your own).");
            _customPref = cat.CreateEntry("RankingCustom", "", "Own ranking systems",
                "Title~measure,measure,... with | between systems; ! in front of a measure turns it round. " +
                "Measures: win, score, badge, winrta, winigt, winstage, endrta, endigt, size, objects, freebies, dashes.");
            Custom.Clear();
            foreach (var part in (_customPref.Value ?? "").Split('|'))
            {
                var s = Parse(part);
                if (s != null && Custom.Count < MaxCustom && All.All(x => x.Title != s.Title)) Custom.Add(s);
            }
            ActiveTitle = All.Any(s => s.Title == _activePref.Value) ? _activePref.Value : "Standard";
        }

        public static void Save()
        {
            if (_activePref == null) return;
            _activePref.Value = ActiveTitle;
            _customPref.Value = string.Join("|", Custom.Select(Format));
        }

        private static readonly string[] Tokens = { "win", "score", "badge", "winrta", "winigt", "winstage", "endrta", "endigt", "size", "objects", "freebies", "dashes" };

        private static string Format(RankingSystem s) =>
            CleanTitle(s.Title) + "~" + string.Join(",", s.Rules.Select(r => (r.Reverse ? "!" : "") + Tokens[(int)r.Key]));

        private static RankingSystem Parse(string text)
        {
            int sep = text.IndexOf('~');
            if (sep <= 0) return null;
            var s = new RankingSystem { Title = CleanTitle(text.Substring(0, sep)) };
            if (s.Title.Length == 0) return null;
            foreach (var raw in text.Substring(sep + 1).Split(','))
            {
                string t = raw.Trim();
                bool rev = t.StartsWith("!");
                int idx = Array.IndexOf(Tokens, rev ? t.Substring(1) : t);
                if (idx >= 0 && s.Rules.Count < MaxRules && s.Rules.All(r => (int)r.Key != idx)) s.Rules.Add(new RankRule((RankKey)idx, rev));
            }
            return s;
        }

        /// <summary>A title that's safe in the preferences file and on the wire: printable, no separators, at most 32 characters.</summary>
        public static string CleanTitle(string title)
        {
            var sb = new StringBuilder();
            foreach (char c in title ?? "")
                if (!char.IsControl(c) && c != '|' && c != '~' && c != '"' && c != '\\' && sb.Length < MaxTitle) sb.Append(c);
            return sb.ToString().Trim();
        }

        /// <summary>A title not yet used: "My ranking", "My ranking 2"...</summary>
        public static string FreeTitle(string baseTitle)
        {
            string t = CleanTitle(baseTitle);
            if (t.Length == 0) t = "My ranking";
            if (All.All(s => s.Title != t)) return t;
            for (int i = 2; ; i++)
            {
                string c = $"{(t.Length > MaxTitle - 4 ? t.Substring(0, MaxTitle - 4) : t)} {i}";
                if (All.All(s => s.Title != c)) return c;
            }
        }

        // ---------------------------------------------------------------- the Settings packet

        /// <summary>Host: the system in force, after the lobby settings (title, then each measure: key | 0x80 when turned round).</summary>
        public static PacketWriter Write(PacketWriter w)
        {
            var s = Active;
            int n = Math.Min(s.Rules.Count, MaxRules);
            w.Str(s.Title).U8((byte)n);
            for (int i = 0; i < n; i++) w.U8((byte)((int)s.Rules[i].Key | (s.Rules[i].Reverse ? 0x80 : 0)));
            return w;
        }

        public static int Hash() => HashCode.Combine(ActiveTitle, Describe(Active));

        /// <summary>Client: the host's system.</summary>
        public static void Read(PacketReader r)
        {
            var s = new RankingSystem { Title = CleanTitle(r.Str()) };
            int n = Math.Min((int)r.U8(), MaxRules);
            for (int i = 0; i < n; i++)
            {
                byte b = r.U8();
                int key = b & 0x7F;
                if (key < AllKeys.Length && s.Rules.All(x => (int)x.Key != key)) s.Rules.Add(new RankRule((RankKey)key, (b & 0x80) != 0));
            }
            if (s.Title.Length == 0) s.Title = "Host's ranking";
            s.Preset = (s.Title == Standard.Title && Describe(s) == Describe(Standard)) || (s.Title == Speedrun.Title && Describe(s) == Describe(Speedrun));
            bool changed = _received == null || _received.Title != s.Title || Describe(_received) != Describe(s);
            _received = s;
            if (changed) MelonLogger.Msg($"[Online] The host ranks by \"{s.Title}\": {Describe(s)}.");
        }

        public static void ForgetHost() => _received = null;

        /// <summary>What the last Settings packet carried (self-test).</summary>
        public static RankingSystem ReceivedForTest => _received;
    }
}
