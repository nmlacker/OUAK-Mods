using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OUAKMultiplayer.Leaderboards
{
    /// <summary>
    /// What the mod needs from a leaderboard server, whatever is behind it: the VPS servers the user will roll out
    /// later (an HTTP client written to the contract in LEADERBOARDS.md), or the local stand-in
    /// (<see cref="LocalLeaderboardService"/>) that keeps everything on this PC. The UI and the recorder only ever talk
    /// to this interface. No implementation here calls any server.
    /// </summary>
    public interface ILeaderboardService
    {
        /// <summary>"local", or the server's name.</summary>
        string Name { get; }
        bool IsOnline { get; }

        /// <summary>Hands finished runs over (in batches; the same EventId twice counts once).</summary>
        Task<SubmitResult> SubmitRunsAsync(IReadOnlyList<RunRecord> runs, CancellationToken ct = default);

        /// <summary>A page of one leaderboard.</summary>
        Task<BoardPage> GetBoardAsync(Board board, BoardQuery query, CancellationToken ct = default);

        /// <summary>One player's totals and places.</summary>
        Task<PlayerSummary> GetPlayerAsync(string playerId, CancellationToken ct = default);
    }

    // ---------------------------------------------------------------- the open rules (the user decides them)
    //
    // How each board counts is the user's call ("Don't make assumptions about design decisions I'll be providing
    // later"). Each rule below lists the options found so far; "Undecided" is the only value set by default, and a
    // board whose rule is undecided stays empty. LEADERBOARDS.md explains each option.

    /// <summary>What "Everyone's Katamari" adds up.</summary>
    public enum KatamariRule
    {
        Undecided = 0,
        /// <summary>The final size of every run.</summary>
        FinalSizeOfEveryRun,
        /// <summary>Each player's best size on each stage, added up (playing a stage again only counts if it beats the old best).</summary>
        BestSizePerStage,
        /// <summary>The number of objects rolled up (Beautiful Katamari's world katamari was everything every player rolled up).</summary>
        ObjectsRolledUp,
    }

    /// <summary>What makes a cousin popular.</summary>
    public enum CousinRule
    {
        Undecided = 0,
        /// <summary>One vote per player: the cousin they played last.</summary>
        LatestCousinPerPlayer,
        /// <summary>Every run counts for the cousin it was played as.</summary>
        RunsPlayedAs,
    }

    /// <summary>What makes a present popular.</summary>
    public enum PresentRule
    {
        Undecided = 0,
        /// <summary>One vote per player for each present they wore on their last run.</summary>
        WornPerPlayer,
        /// <summary>Every run counts for each present worn in it.</summary>
        WornInRuns,
        /// <summary>The presents the stages gave out.</summary>
        GivenByStages,
    }

    /// <summary>The rules a service counts by. Everything starts undecided.</summary>
    public sealed class BoardRules
    {
        public KatamariRule Katamari { get; set; }
        public CousinRule Cousins { get; set; }
        public PresentRule Presents { get; set; }
        public ICookieRule Cookies { get; set; } = new UndecidedCookies();

        /// <summary>Which runs each board counts (null = every run).</summary>
        public Func<RunRecord, bool> KatamariRuns { get; set; }
        public Func<RunRecord, bool> CookieRuns { get; set; }
        public Func<RunRecord, bool> CousinRuns { get; set; }
        public Func<RunRecord, bool> PresentRuns { get; set; }

        public override string ToString() => $"katamari {Katamari}, cookies {Cookies?.Id ?? "none"}, cousins {Cousins}, presents {Presents}";
    }

    /// <summary>How many cookies a run earns. The rule itself is open (LEADERBOARDS.md lists the options).</summary>
    public interface ICookieRule
    {
        string Id { get; }
        int CookiesFor(RunRecord run);
    }

    /// <summary>
    /// The rules the user has decided (2026-11-12): Everyone's Katamari adds up the final size of every run, a cousin's
    /// popularity is every run played as it, a present's is every run it was worn in. Cookies: only for runs played in a
    /// Multiplayer session against at least one other real player (not Katamari Ball, not solo stages); how many is still
    /// open (the user asked what the game's own formula is first).
    /// </summary>
    public static class LeaderboardRules
    {
        public static BoardRules Decided => new()
        {
            Katamari = KatamariRule.FinalSizeOfEveryRun,
            Cousins = CousinRule.RunsPlayedAs,
            Presents = PresentRule.WornInRuns,
            Cookies = new BeautifulKatamariCookies(),
            // The user, 2026-11-12: "Only Cookies should be tracked in our multiplayer sessions; Cousins, Presents, and
            // Everybody's Katamari should be trackable in Singleplayer only for now."
            CookieRuns = EarnsCookies,
            KatamariRuns = r => r.Mode == RunMode.Solo,
            CousinRuns = r => r.Mode == RunMode.Solo,
            PresentRuns = r => r.Mode == RunMode.Solo,
        };

        /// <summary>The user's condition for cookies: a session run with at least one other real player.</summary>
        public static bool EarnsCookies(RunRecord run) => run != null && run.Mode == RunMode.Session && run.OtherHumans >= 1;
    }

    /// <summary>No cookies until the user decides how they're earned.</summary>
    public sealed class UndecidedCookies : ICookieRule
    {
        public string Id => "undecided";
        public int CookiesFor(RunRecord run) => 0;
    }
}
