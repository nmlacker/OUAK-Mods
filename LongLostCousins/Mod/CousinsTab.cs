using System;
using System.Collections.Generic;
using ImGuiNET;
using LongLostCousins.Data;
using LongLostCousins.Dressing;
using LongLostCousins.Select;
using OUAKModAPI;

namespace LongLostCousins
{
    /// <summary>The "Long Lost Cousins" tab of the mod window: which cousin to be, what that covers, and what the mod did.</summary>
    internal static class CousinsTab
    {
        private static IDisposable _tab;
        // What the player types for the Forever folder; emptied once used, so the tab never shows a folder already set.
        private static string _foreverInput = "";

        public static void Init()
        {
            _tab = ModMenu.AddTab("longlostcousins", "Long Lost Cousins", Draw, order: 650, owner: "Long Lost Cousins");
        }

        private static void Draw()
        {
            ImUi.Title("Long Lost Cousins");
            ImUi.Hint("Play as a cousin the game never shipped. Each one is also a cousin on the game's own select screen " +
                      "(The Prince & Cousins, page 4). Picking one there or here makes you that cousin, and the game's save keeps your own " +
                      "cousin: nothing in the save changes. Under the cousin you wear the game builds the Prince, whoever your own cousin is.");

            ImUi.Header("Who are you?");
            if (ImGui.RadioButton("Off (the game's own cousin)", !Settings.Active)) Choose(null, null);
            if (Catalog.Cousins.Count == 0)
                ImUi.Err("The mod's list of cousins could not be read: " + (Catalog.Error ?? "it is empty"));
            foreach (var c in Catalog.Cousins)
                foreach (var s in c.Styles)
                {
                    bool on = Settings.Active && Settings.CousinId == c.Id && Settings.Style == s.Key;
                    // While she is off for now (a save change), her own button turns her back on.
                    if (ImGui.RadioButton(c.StyleLabel(s.Key) + "##" + c.Id + "/" + s.Key, on) && (!on || !Chosen.On)) Choose(c.Id, s.Key);
                }
            if (Settings.MemoryOnly) ImUi.Hint("The cousin test is running: this choice is not saved.");
            if (Settings.OnScreen) ImUi.Hint("Picked on the select screen.");
            ImUi.Hint("The cousin you picked comes off by itself when your save's cousin changes: another cousin picked on the select screen, a " +
                      "swap in the Plaza, the Cousin Creator, or another save slot loaded. Your own cousin is back then.");

            ImUi.Hint("The change shows the next time the game loads your cousin: the next stage, the next visit to the Plaza " +
                      "or the Prince's Ship, or a cousin change there.");
            ImUi.Hint("Dressed: your cousin in story stages and the stage's corner picture, the Plaza and the Prince's Ship " +
                      "(and the picture of you there), the stage results and the ending.");
            ImUi.Hint("On the select screen each cousin's slot shows the style picked here (the next time the screen opens).");
            ImUi.Hint("Not dressed: the select screen's own pictures, online battles, OUAK Multiplayer (others there see the game's " +
                      "own cousin), the mini theme, the present collection, the Cousin Creator, the star map, Cosmo, the " +
                      "tutorial Plaza, and the stage where the game always plays the Prince.");
            ImUi.Hint("The new style moves with the Prince's own motions. The old style moves with Katamari Forever's motions, read " +
                      "from your own copy of Katamari Forever (set its folder below), and with this game's motions where Forever has none.");

            ImUi.Header("Katamari Forever's motions (old style)");
            if (ForeverMotions.Status == ForeverStatus.Ready) ImUi.Ok(ForeverMotions.Describe());
            else ImUi.Hint(ForeverMotions.Describe());
            ImUi.Hint("The folder is your PS3 disc folder of Katamari Forever (the one holding PS3_GAME, or PS3_GAME or USRDIR itself). " +
                      "The mod reads four files from it at each start and keeps nothing; the folder stays in settings.json and is never shown or logged.");
            ImGui.TextUnformatted(Settings.ForeverFolder != null ? "Folder: set" : "Folder: not set");
            ImGui.SetNextItemWidth(-1f);
            ImGui.InputTextWithHint("##llcforever", "paste the Katamari Forever folder here", ref _foreverInput, 1024);
            if (ImUi.Button("Use this folder", !string.IsNullOrWhiteSpace(_foreverInput), ImUi.Px(160f)))
            {
                Settings.SetForeverFolder(_foreverInput);
                _foreverInput = "";
                ForeverMotions.Start("a folder set in the tab");
            }
            ImGui.SameLine();
            if (ImUi.Button("Forget the folder", Settings.ForeverFolder != null, ImUi.Px(160f)))
            {
                Settings.SetForeverFolder(null);
                ForeverMotions.Start("the folder cleared in the tab");
            }
            ImGui.SameLine();
            if (ImUi.Button("Read it again", Settings.ForeverFolder != null && ForeverMotions.Status != ForeverStatus.Reading, ImUi.Px(160f)))
                ForeverMotions.Start("read again from the tab");

            ImUi.Header("Now");
            int live = Dresser.Models.Count;
            ImGui.TextUnformatted(Settings.Active ? "Chosen: " + Settings.Describe() + (Chosen.On ? "" : " (not worn now)") : "Off");
            ImGui.TextUnformatted($"Dressed now: {live} model{(live == 1 ? "" : "s")} ({Dresser.DressedCount} this session)");
            if (live > 0)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var m in Dresser.Models)
                    if (seen.Add(m.Label)) ImGui.TextUnformatted("  " + m.Label);
            }
            if (Chosen.On && !Hooks.Installed) ImUi.Err("The mod's hooks are not in place; see the log.");
            else if (Chosen.On && Hooks.Count < Hooks.Expected) ImUi.Err($"Only {Hooks.Count} of the mod's {Hooks.Expected} hooks could be made; see the log.");
            if (!SelectScreen.Ready) ImUi.Err("The cousins could not be added to the game's select screen; see the log. They can still be picked here, " +
                                              "but on the ship, in the Plaza, on stages and in the ending they are only shown while your own cousin is the Prince.");
            else if (SelectScreen.ScopeFailed) ImUi.Err("The select screen's marker hooks could not be made; see the log. Its current-cousin marker stays " +
                                                        "on your own cousin, and on the ship and in the Plaza the cousin you wear is only shown while that cousin is the Prince.");
            else if (Settings.Active && PrinceBase.Installed && PrinceBase.Count < PrinceBase.Expected)
                ImUi.Err($"Only {PrinceBase.Count} of the {PrinceBase.Expected} hooks that build the Prince under the cousin you wear could be made; see the log.");
            if (Dresser.LastProblem != null) ImUi.Hint("Last model left as it was: " + Dresser.LastProblem);
        }

        /// <summary>
        /// The tab's pick (also the test's): a cousin is recorded with the save's cousin now (taken later when no slot is
        /// loaded); off takes her off and gives the game's run-time cousin id back. A style change keeps where she was picked.
        /// </summary>
        internal static void Choose(string cousinId, string style)
        {
            bool wasOn = Chosen.On;
            if (cousinId == null)
            {
                if (Settings.Active) Chosen.TakeOff("turned off in the tab");
                else Settings.Choose(null, style);
            }
            else
            {
                int saved = Settings.NotRecorded, custom = Settings.NotRecorded;
                if (GameRefs.InGameSession && Chosen.Effective(out int id, out int c) && Chosen.IsStorableId(id)) { saved = id; custom = c; }
                bool onScreen = Settings.OnScreen && Chosen.On && Settings.CousinId == cousinId;
                Settings.Choose(cousinId, style, onScreen, saved, custom);
            }
            Chosen.Refresh();
            if (Chosen.On && !wasOn) Contexts.LeaveCurrentOwnerAlone();
        }
    }
}
