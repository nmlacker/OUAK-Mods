using System;
using System.Collections.Generic;

namespace OUAKMultiplayer.Leaderboards
{
    // The data that goes to (and comes back from) the leaderboard servers, which don't exist yet (the user, 2026-11-11:
    // "all this information on VPS servers, which will be rolled out later"). Design: LEADERBOARDS.md next to this
    // mod's README. Every type here is plain data (JSON-friendly): no game objects.

    /// <summary>The four leaderboards (the user's list, inspired by Beautiful Katamari).</summary>
    public enum Board
    {
        /// <summary>Every player's katamari size added up, with each player's share ranked.</summary>
        EveryonesKatamari,
        /// <summary>How many cookies each player has in the Multiplayer mod.</summary>
        Cookies,
        /// <summary>Which cousins are popular.</summary>
        Cousins,
        /// <summary>Which presents are popular.</summary>
        Presents,
    }

    /// <summary>Where a run was played.</summary>
    public enum RunMode
    {
        /// <summary>A story stage on your own (no session).</summary>
        Solo = 0,
        /// <summary>A stage played together in an OUAK Multiplayer session (the mod's own connection).</summary>
        Session = 1,
        /// <summary>Katamari Ball offline against CPU princes (this mod's Katamari Ball tab or the ship's terminal).</summary>
        KatamariBallOffline = 2,
    }

    /// <summary>Who a record is about. Never an email or anything beyond what the game already shows other players.</summary>
    public sealed class PlayerRef
    {
        /// <summary>"steam" (SteamID64 in <see cref="Id"/>) or "install" (this install's random id, for copies without Steam).</summary>
        public string Kind { get; set; } = "";
        public string Id { get; set; } = "";
        /// <summary>The name shown in sessions (the player's choice, 16 characters at most).</summary>
        public string Name { get; set; } = "";
    }

    /// <summary>
    /// One finished run, as the player's own game saw it: the raw facts every leaderboard rule being considered needs
    /// (none of the open rules is applied here). Recorded when the run ends, kept locally, uploaded later in batches.
    /// Sizes are the game's units (tenths of a millimetre, MainGameManager.KatamariSize).
    /// </summary>
    public sealed class RunRecord
    {
        /// <summary>Unique per run (a new GUID): uploading the same record twice counts once.</summary>
        public string EventId { get; set; } = "";
        public int Schema { get; set; } = 1;
        public string ModVersion { get; set; } = "";
        public DateTime PlayedUtc { get; set; }
        public PlayerRef Player { get; set; } = new();

        public RunMode Mode { get; set; }
        /// <summary>Session runs: the host's identity and the run's number, the same for everyone in that run; else null.</summary>
        public string SessionRunId { get; set; }
        public int PlayersInRun { get; set; } = 1;
        /// <summary>Session runs: how many of the others were real players (not the loopback test bot).</summary>
        public int OtherHumans { get; set; }
        /// <summary>Session runs: the host's checks found nothing odd (SyncRun's Verdict 0). Solo runs: false (no one checked).</summary>
        public bool HostVerified { get; set; }
        public int Verdict { get; set; }
        /// <summary>Session runs: the place under the session's ranking system (Ranking), 0 if none.</summary>
        public int Place { get; set; }
        /// <summary>Session runs: they left the race before finishing (DQ).</summary>
        public bool Disqualified { get; set; }

        /// <summary>The stage (the game's eStage number) and its internal name ("Stage_Big1").</summary>
        public int Stage { get; set; }
        public string StageName { get; set; } = "";

        public int Size { get; set; }
        public int Objects { get; set; }
        public bool ReachedWin { get; set; }
        /// <summary>The game's stage rating (CheckClearRate: 0 = top badge … 4), -1 unknown.</summary>
        public int Rating { get; set; } = -1;
        /// <summary>The stage's own score (StageScore: kind and number, -1 none).</summary>
        public int ScoreKind { get; set; }
        public int Score { get; set; } = -1;
        public float Igt { get; set; } = -1f;
        public float Rta { get; set; } = -1f;
        public float WinIgt { get; set; } = -1f;
        public float WinRta { get; set; } = -1f;

        /// <summary>The cousin played (the game's character id; 999 = a custom cousin, with its slot in <see cref="CustomCousin"/>).</summary>
        public int Cousin { get; set; } = -1;
        public int CustomCousin { get; set; } = -1;
        /// <summary>The presents worn during the run (the game's present ids, GlobalSaveData._wearingPresent).</summary>
        public int[] Presents { get; set; } = Array.Empty<int>();
        /// <summary>The present the stage gave (Game.ResultData.PresentID), -1 none or unknown.</summary>
        public int PresentGiven { get; set; } = -1;
    }

    /// <summary>A page of a leaderboard: who (or which cousin / present) is where.</summary>
    public sealed class BoardQuery
    {
        /// <summary>"all" for all time. Other periods ("week", "month") only if the user wants them (open question).</summary>
        public string Period { get; set; } = "all";
        public int Offset { get; set; }
        public int Limit { get; set; } = 50;
        /// <summary>Optional: return the page around this player instead (their rank in the middle).</summary>
        public string AroundPlayerId { get; set; }
    }

    public sealed class BoardEntry
    {
        public int Rank { get; set; }
        /// <summary>A player's id, or a cousin / present id for the popularity boards.</summary>
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public double Value { get; set; }
        public bool IsYou { get; set; }
    }

    public sealed class BoardPage
    {
        public Board Board { get; set; }
        public string Period { get; set; } = "all";
        /// <summary>Everyone's Katamari: the size of everyone's katamari together. Other boards: the sum of the values.</summary>
        public double Total { get; set; }
        public int TotalEntries { get; set; }
        public List<BoardEntry> Entries { get; set; } = new();
        public DateTime AsOfUtc { get; set; }
        /// <summary>Which service answered ("local" for the stand-in) and the rules it counted by.</summary>
        public string Source { get; set; } = "";
        public string Rules { get; set; } = "";
    }

    public sealed class SubmitResult
    {
        public int Accepted { get; set; }
        public int Duplicates { get; set; }
        public int Rejected { get; set; }
        public List<string> Reasons { get; set; } = new();
    }

    public sealed class PlayerSummary
    {
        public PlayerRef Player { get; set; } = new();
        public int Runs { get; set; }
        public long Cookies { get; set; }
        public long KatamariShare { get; set; }
        public Dictionary<Board, int> Ranks { get; set; } = new();
    }
}
