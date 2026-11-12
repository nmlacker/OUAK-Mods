using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MelonLoader;
using MelonLoader.Utils;
using OUAKModAPI;

namespace OUAKMultiplayer
{
    /// <summary>Saves each finished match as a readable text file in UserData/OUAKModMenu/Matches.</summary>
    internal static class MatchLog
    {
        public static string LastPath { get; private set; }

        public static void Save(int map, List<RunResult> standings)
        {
            try
            {
                string dir = Path.Combine(MelonEnvironment.UserDataDirectory, "OUAKModMenu", "Matches");
                Directory.CreateDirectory(dir);
                string mapName = Votes.StageName(map);
                var now = DateTime.Now;
                string file = Path.Combine(dir, $"{now:yyyy-MM-dd_HH-mm-ss}_{Safe(mapName)}.txt");

                bool timeAttack = standings.Exists(r => r.TimeAttack);
                var sb = new StringBuilder();
                sb.AppendLine("Once Upon a KATAMARI - online match (OUAK Mod Menu)");
                sb.AppendLine($"Date:    {now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"Stage:   {mapName}");
                var sys = Ranking.Current;
                sb.AppendLine($"Ranking: \"{sys.Title}\": {Ranking.Describe(sys)}");
                sb.AppendLine("         (first difference decides; equal on all = shared place; RTA to 0.1 s, IGT and the stage clock to 0.01 s; DQ last)");
                var kind = (ScoreKind)(standings.FirstOrDefault(r => r.ScoreKind != 0)?.ScoreKind ?? 0);
                sb.AppendLine($"Stage score here: {StageScore.KindName(kind)}{(timeAttack ? " (time attack)" : "")}");
                sb.AppendLine($"Start:   {(SyncRun.RaceStart ? "race (shared 3-2-1, then anyone can skip the King's talk)" : "strict (shared 3-2-1, everyone hears the King's whole talk)")}");
                sb.AppendLine($"Host:    {(NetSession.Mode == NetSession.Role.Host ? NetSession.MyName : Votes.NameOf(0))}");
                sb.AppendLine($"Players: {standings.Count}");
                sb.AppendLine();
                sb.AppendLine("Place  Player            Status  Rank          Size           Stage score     Size Time    Stage time    Real time     IGT           Win at IGT    Win at RTA    Check");
                sb.AppendLine("-----  ----------------  ------  ------------  -------------  --------------  -----------  ------------  ------------  ------------  ------------  ------------  ---------------------");
                var places = Ranking.StandingsPlaces(standings);
                // Rank: 5 of 5 = the game's top badge (all five of the stage's targets reached) … 1 of 5 = its lowest.
                for (int i = 0; i < standings.Count; i++)
                {
                    var r = standings[i];
                    string star = r.Predicted ? "*" : "";
                    sb.AppendLine(string.Format("{0,-6} {1,-17} {2,-7} {3,-13} {4,-14} {5,-15} {6,-12} {7,-13} {8,-13} {9,-13} {10,-13} {11,-13} {12}",
                        places.TryGetValue(r.Id, out int pl) ? pl : i + 1, Votes.NameOf(r.Id), r.Status, StageRank.LogText(r), KatamariState.SizeText(r.Size),
                        StageScore.Text((ScoreKind)r.ScoreKind, r.Score),
                        r.SizeTime >= 0f ? KatamariState.ClockText(r.SizeTime) : "-",
                        KatamariState.ClockText(r.Time) + star,
                        r.RealTime >= 0f ? KatamariState.ClockText(r.RealTime) + star : "-",
                        RunClocks.Clock(r.Igt), RunClocks.Clock(r.ClearIgt), RunClocks.Clock(r.ClearRta),
                        SyncRun.VerdictText(r.Verdict) + (r.Restarted ? ", restarted" : "") + (r.Forced && r.Cleared ? ", cut off after clearing" : "")));
                }
                sb.AppendLine();
                sb.AppendLine("Places 1-3 wear the crown, the biscuit and the wash tin.");
                sb.AppendLine("Status: CLEAR = beat the stage, FAIL = didn't, CUT = ended by Sudden Death (still playing after the stage had ended for the first player, and didn't reach the next growth size in time), DQ = left the race before finishing (listed last).");
                sb.AppendLine("Rank, Size and Size Time are the game's own result values: Rank = its stage rating, the badge on its");
                sb.AppendLine("result screen (5 of 5 = top badge, all five of the stage's targets reached; 1 of 5 = lowest),");
                sb.AppendLine("Size Time = the stage clock when the goal was reached (- if it wasn't).");
                sb.AppendLine("* predicted time attack time for a CUT player: their time + the fastest finish - 30 s.");
                sb.AppendLine("Stage time is each player's own stage clock when their run ended (it stops while they pause).");
                sb.AppendLine("Real time (RTA) is from the start signal to finishing, on the host's clock, pauses included.");
                sb.AppendLine("IGT is the time each player actually played the run: from the start signal to finishing, without the pause menu,");
                sb.AppendLine("the clear demo or a restart's wait; a frozen stage clock (the Timer freebie) doesn't stop it.");
                sb.AppendLine("Win at IGT / RTA: when the player reached the stage's win condition (the game's own clear, the moment of Size Time).");

                // After-action report.
                sb.AppendLine();
                sb.AppendLine("After-action report");
                sb.AppendLine("Player            Objects  Dashes  Quick turns  Freebies  Distance");
                sb.AppendLine("----------------  -------  ------  -----------  --------  --------");
                foreach (var r in standings)
                    sb.AppendLine(string.Format("{0,-17} {1,-8} {2,-7} {3,-12} {4,-9} {5}",
                        Votes.NameOf(r.Id), r.Objects, r.Dashes, r.Turns, r.Freebies, $"{r.Distance:0} m"));
                var steps = new SortedSet<int>();
                foreach (var r in standings) foreach (var st in r.Steps) steps.Add(st.size);
                if (steps.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Time to each growth size (stage clock)");
                    var head = new StringBuilder("Player           ");
                    foreach (int s in steps) head.Append($" {KatamariState.SizeText(s),-12}");
                    sb.AppendLine(head.ToString());
                    foreach (var r in standings)
                    {
                        var line = new StringBuilder($"{Votes.NameOf(r.Id),-17}");
                        foreach (int s in steps)
                        {
                            int at = r.Steps.FindIndex(st => st.size == s);
                            line.Append($" {(at >= 0 ? KatamariState.ClockText(r.Steps[at].time) : "-"),-12}");
                        }
                        sb.AppendLine(line.ToString());
                    }
                }
                File.WriteAllText(file, sb.ToString());
                LastPath = file;
                MelonLogger.Msg($"[Online] Match saved: {file}");
            }
            catch (Exception e) { MelonLogger.Warning($"[Online] Couldn't save the match log: {e.Message}"); }
        }

        private static string Safe(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? "match")
                sb.Append(char.IsLetterOrDigit(c) ? c : c == ' ' ? '-' : '_');
            return sb.Length > 0 ? sb.ToString() : "match";
        }
    }
}
