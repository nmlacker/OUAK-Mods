using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using OUAKModAPI;
using UnityEngine;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace OUAKMultiplayer
{
    /// <summary>
    /// The session's short-lived screens, drawn with Dear ImGui (the API's ModWindows): the vote popup above the
    /// player bar and the Results screen (standings, and the after-action report on Tab / D-pad). Neither takes the
    /// game's controls or the mouse pointer: the game keeps running (and its own results screen) underneath. They're
    /// open only while there's something to show, so Dear ImGui doesn't run at all the rest of the time.
    /// The always-on parts (player bar, name tags, bubbles, chat) stay on Unity IMGUI (Ghosts.OnGUI).
    /// </summary>
    internal static class HudWindows
    {
        private static ModWindow _vote, _results;

        /// <summary>The top of the player bar in screen pixels, from the IMGUI pass (PlayerBar.Draw) this frame.</summary>
        public static float BarTopPx;

        public static void Init()
        {
            _vote = ModWindows.Add("ouakmp.vote", DrawVote, takesControl: false, order: 10, owner: "OUAK Multiplayer");
            _results = ModWindows.Add("ouakmp.results", DrawResults, takesControl: false, order: 5, owner: "OUAK Multiplayer");
        }

        /// <summary>Every Update: open each window only while it has something to show.</summary>
        public static void Tick()
        {
            if (_vote == null) return;
            bool on = NetSession.Connected;
            _vote.Open = on && Votes.Active != null && BarTopPx > 0f;
            _results.Open = on && SyncRun.StandingsFinal && Time.unscaledTime < SyncRun.StandingsShownUntil;
        }

        private const ImGuiWindowFlags Hud = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings |
                                             ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoMove;

        private static readonly Vector4 Gold = new(1f, 0.9f, 0.3f, 1f), Head = new(0.7f, 0.75f, 0.9f, 1f), Hint = new(0.75f, 0.8f, 0.9f, 1f);

        // ---------------------------------------------------------------- vote popup

        /// <summary>Above the player bar, bottom centre. Clickable; Y / N and the D-pad work too (Votes.ReadInput).</summary>
        private static void DrawVote()
        {
            var v = Votes.Active;
            if (v == null) return;
            var vp = ImGui.GetMainViewport();
            float w = ImUi.Px(520f);
            ImGui.SetNextWindowPos(new Vector2(vp.Pos.X + vp.Size.X / 2f, BarTopPx - ImUi.Px(10f)), ImGuiCond.Always, new Vector2(0.5f, 1f));
            ImGui.SetNextWindowSizeConstraints(new Vector2(w, 0f), new Vector2(w, float.MaxValue));
            ImGui.SetNextWindowBgAlpha(0.92f);
            if (ImGui.Begin("##ouakmp.vote", Hud))
            {
                Centered($"{Votes.NameOf(v.By)} wants to {Votes.Describe(v.Kind, v.Arg)}", null, true);
                if (v.Done)
                    Centered($"Vote {(v.Passed ? "passed" : "failed")}: {v.Yes} of {v.Total} said yes.", Hint);
                else
                {
                    float left = Mathf.Max(0f, v.Deadline - SyncRun.HostNow);
                    if (v.Mine != null)
                        Centered($"You voted {(v.Mine.Value ? "yes" : "no")}. Waiting for the others… ({left:0} s)", Hint);
                    else
                    {
                        ImGui.Spacing();
                        float bw = ImUi.Px(150f), gap = ImUi.Px(16f);
                        ImGui.SetCursorPosX((ImGui.GetWindowWidth() - 2 * bw - gap) / 2f);
                        if (ImUi.Primary("Yes", true, bw)) Votes.Cast(true);
                        ImGui.SameLine(0f, gap);
                        if (ImUi.Danger("No", true, bw)) Votes.Cast(false);
                        Centered($"Y or D-pad up = yes  ·  N or D-pad down = no  ·  {left:0} s", Hint);
                    }
                }
            }
            ImGui.End();
        }

        // ---------------------------------------------------------------- results

        private static void DrawResults()
        {
            if (SyncRun.Standings.Count == 0) return;
            var vp = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(new Vector2(vp.Pos.X + vp.Size.X / 2f, vp.Pos.Y + vp.Size.Y * 0.18f), ImGuiCond.Always, new Vector2(0.5f, 0f));
            ImGui.SetNextWindowBgAlpha(0.9f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(ImUi.Px(18f), ImUi.Px(12f)));
            if (ImGui.Begin("##ouakmp.results", Hud | ImGuiWindowFlags.NoInputs))
            {
                if (SyncRun.ShowAar) { DrawAar(); SyncRun.AarFrames++; }
                else { DrawStandings(); SyncRun.StandingsFrames++; }
            }
            ImGui.End();
            ImGui.PopStyleVar();
        }

        private static void DrawStandings()
        {
            var list = SyncRun.Standings;
            Centered("Results — " + Votes.StageName(SyncRun.RunMap), Gold, true);
            ImGui.Spacing();
            bool ta = list.Any(r => r.TimeAttack);
            float icon = ImUi.Px(30f);
            const ImGuiTableFlags flags = ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.PadOuterX;
            if (ImGui.BeginTable("standings", 10, flags))
            {
                ImGui.TableSetupColumn("##place", ImGuiTableColumnFlags.WidthFixed, icon);
                ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthFixed, ImUi.Px(190f));
                ImGui.TableSetupColumn("Rank");
                ImGui.TableSetupColumn(ta ? "Time" : "Size", ImGuiTableColumnFlags.WidthFixed, ImUi.Px(110f));
                ImGui.TableSetupColumn(ta ? "Size" : "Size Time", ImGuiTableColumnFlags.WidthFixed, ImUi.Px(100f));
                // The run's timers (RunClocks): in-game time, real time from GO, and both at the stage's win condition.
                ImGui.TableSetupColumn("IGT", ImGuiTableColumnFlags.WidthFixed, ImUi.Px(84f));
                ImGui.TableSetupColumn("RTA", ImGuiTableColumnFlags.WidthFixed, ImUi.Px(84f));
                ImGui.TableSetupColumn("Win condition (IGT / RTA)", ImGuiTableColumnFlags.WidthFixed, ImUi.Px(170f));
                ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, ImUi.Px(64f));
                ImGui.TableSetupColumn("##check");
                HeaderRow();
                // Places by the session's ranking system: a full tie shares the place (1, 1, 3).
                var places = Ranking.StandingsPlaces(list);
                for (int i = 0; i < list.Count; i++)
                {
                    var r = list[i];
                    int place = places.TryGetValue(r.Id, out int pl) ? pl : i + 1;
                    ImGui.TableNextRow(ImGuiTableRowFlags.None, icon + ImUi.Px(4f));
                    ImGui.TableNextColumn();
                    var placeTex = place <= 3 ? RankIcons.Icon(place) : null;
                    if (placeTex != null) { ImUi.Image(placeTex, icon, icon); RankIcons.Drawn++; }
                    else CellText($"{place}.", icon);
                    ImGui.TableNextColumn();
                    CellText(r.Id == NetSession.MyId ? NetSession.MyName + " (you)" : Votes.NameOf(r.Id), icon);
                    ImGui.TableNextColumn();
                    var badge = StageRank.Tex(r);
                    if (badge != null) { ImUi.Image(badge, icon, icon); StageRank.Drawn++; }
                    else CellText(StageRank.Text(r), icon);
                    string size = KatamariState.SizeText(r.Size);
                    string sizeTime = r.SizeTime >= 0f ? KatamariState.ClockText(r.SizeTime) : "-";
                    string time = KatamariState.ClockText(r.RaceTime) + (r.Predicted ? "*" : "");
                    ImGui.TableNextColumn();
                    CellText(ta ? time : size, icon);
                    ImGui.TableNextColumn();
                    CellText(ta ? size : sizeTime, icon, Hint);
                    ImGui.TableNextColumn();
                    CellText(RunClocks.Clock(r.Igt), icon);
                    ImGui.TableNextColumn();
                    CellText(RunClocks.Clock(r.RealTime), icon);
                    ImGui.TableNextColumn();
                    CellText(r.ClearIgt >= 0f ? $"{RunClocks.Clock(r.ClearIgt)} / {RunClocks.Clock(r.ClearRta)}" : r.Cleared ? "-" : "not reached", icon, Hint);
                    ImGui.TableNextColumn();
                    StatusTag(r.Status, r.Cleared ? new Vector4(0.2f, 0.65f, 0.35f, 1f) : r.Forced ? new Vector4(0.85f, 0.5f, 0.15f, 1f) : new Vector4(0.7f, 0.22f, 0.22f, 1f), icon);
                    ImGui.TableNextColumn();
                    if (r.Verdict != 0) CellText("check", icon, Hint);
                }
                ImGui.EndTable();
            }
            ImGui.Spacing();
            // How everyone was ranked: the session's system, its measures in order.
            var sys = Ranking.Current;
            Centered($"Ranked by \"{sys.Title}\": {Ranking.Describe(sys)}", Gold);
            Centered("First difference decides; equal on all = shared place. RTA counts to 0.1 s, IGT and the stage clock to 0.01 s.", Hint);
            if (list.Any(r => r.ScoreKind != 0))
            {
                var k = (ScoreKind)list.First(r => r.ScoreKind != 0).ScoreKind;
                Centered($"Stage score here: {StageScore.KindName(k)} ({string.Join(", ", list.Select(r => $"{NameOf(r)} {StageScore.Text(k, r.Score)}"))})", Hint);
            }
            Centered((list.Any(r => r.Predicted) ? "* predicted (time + fastest finish - 30 s)   " : "") + "Tab / D-pad: after-action report", Hint);
        }

        /// <summary>After-action report: objects, dashes, quick turns, freebies, distance, and time to each growth size.</summary>
        private static void DrawAar()
        {
            var list = SyncRun.Standings;
            var all = new SortedSet<int>();
            foreach (var res in list)
                foreach (var st in res.Steps) all.Add(st.size);
            var steps = new List<int>(all);
            if (steps.Count > 4) steps = new List<int> { steps[0], steps[steps.Count / 3], steps[2 * steps.Count / 3], steps[steps.Count - 1] };

            Centered("After-action report", Gold, true);
            ImGui.Spacing();
            const ImGuiTableFlags flags = ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.PadOuterX;
            if (ImGui.BeginTable("aar", 6 + steps.Count, flags))
            {
                ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthFixed, ImUi.Px(200f));
                foreach (string h in new[] { "Objects", "Dashes", "Quick turns", "Freebies", "Distance" })
                    ImGui.TableSetupColumn(h, ImGuiTableColumnFlags.WidthFixed, ImUi.Px(90f));
                foreach (int s in steps) ImGui.TableSetupColumn("to " + KatamariState.SizeText(s), ImGuiTableColumnFlags.WidthFixed, ImUi.Px(84f));
                HeaderRow();
                foreach (var r in list)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(r.Id == NetSession.MyId ? NetSession.MyName + " (you)" : Votes.NameOf(r.Id));
                    foreach (string val in new[] { r.Objects.ToString(), r.Dashes.ToString(), r.Turns.ToString(), r.Freebies.ToString(), $"{r.Distance:0} m" })
                    {
                        ImGui.TableNextColumn();
                        ImGui.TextColored(Hint, val);
                    }
                    foreach (int s in steps)
                    {
                        ImGui.TableNextColumn();
                        int at = r.Steps.FindIndex(st => st.size == s);
                        ImGui.TextColored(Hint, at >= 0 ? KatamariState.ClockText(r.Steps[at].time) : "-");
                    }
                }
                ImGui.EndTable();
            }
            ImGui.Spacing();
            Centered("Times are each player's stage clock when they reached that size.   Tab / D-pad: standings", Hint);
        }

        // ---------------------------------------------------------------- bits

        /// <summary>The table's column names, small and grey.</summary>
        private static void HeaderRow()
        {
            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
            int n = ImGui.TableGetColumnCount();
            for (int c = 0; c < n; c++)
            {
                ImGui.TableSetColumnIndex(c);
                string name = ImGui.TableGetColumnName(c);
                if (!name.StartsWith("##")) ImGui.TextColored(Head, name);
            }
        }

        /// <summary>Text centred in the window (bold when <paramref name="big"/>).</summary>
        private static string NameOf(RunResult r) => r.Id == NetSession.MyId ? NetSession.MyName : Votes.NameOf(r.Id);

        private static void Centered(string text, Vector4? color, bool big = false)
        {
            if (big) ImGui.PushFont(ImUi.BigFont);
            float tw = ImGui.CalcTextSize(text).X;
            ImGui.SetCursorPosX(Mathf.Max(ImGui.GetStyle().WindowPadding.X, (ImGui.GetWindowWidth() - tw) / 2f));
            if (color.HasValue) ImGui.TextColored(color.Value, text);
            else ImGui.TextUnformatted(text);
            if (big) ImGui.PopFont();
        }

        /// <summary>Text centred vertically in a row <paramref name="rowH"/> tall.</summary>
        private static void CellText(string text, float rowH, Vector4? color = null)
        {
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (rowH - ImGui.GetTextLineHeight()) / 2f);
            if (color.HasValue) ImGui.TextColored(color.Value, text);
            else ImGui.TextUnformatted(text);
        }

        /// <summary>CLEAR / FAIL / CUT / DQ on a coloured tag.</summary>
        private static void StatusTag(string text, Vector4 bg, float rowH)
        {
            var size = ImGui.CalcTextSize(text);
            float w = ImUi.Px(60f), h = size.Y + ImUi.Px(6f);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (rowH - h) / 2f);
            var p = ImGui.GetCursorScreenPos();
            var dl = ImGui.GetWindowDrawList();
            dl.AddRectFilled(p, p + new Vector2(w, h), ImGui.GetColorU32(bg), ImUi.Px(4f));
            dl.AddText(p + new Vector2((w - size.X) / 2f, (h - size.Y) / 2f), ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f)), text);
            ImGui.Dummy(new Vector2(w, h));
        }
    }
}
