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

        public static void Init()
        {
            _tab = ModMenu.AddTab("longlostcousins", "Long Lost Cousins", Draw, order: 650, owner: "Long Lost Cousins");
        }

        private static void Draw()
        {
            ImUi.Title("Long Lost Cousins");
            ImUi.Hint("Play as a cousin the game never shipped. Burda is also one more cousin on the game's own select screen " +
                      "(The Prince & Cousins, page 4): picking her there makes you Burda, and the game's save keeps the Prince " +
                      "in her place. Picking her here dresses whichever cousin you are now. Either way nothing new goes into your save.");

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
            if (Settings.OnScreen) ImUi.Hint("Picked on the select screen." + (Chosen.On ? "" :
                " She is off for now: the game's save holds another cousin than the Prince. Pick her again on the select screen, or here."));

            ImUi.Hint("The change shows the next time the game loads your cousin: the next stage, the next visit to the Plaza " +
                      "or the Prince's Ship, or a cousin change there.");
            ImUi.Hint("Dressed: your cousin in story stages and the stage's corner picture, the Plaza and the Prince's Ship " +
                      "(and the picture of you there), the stage results and the ending.");
            ImUi.Hint("On the select screen her slot shows the style picked here (the next time the screen opens).");
            ImUi.Hint("Not dressed: the select screen's own pictures, online battles, OUAK Multiplayer (others there see the game's " +
                      "own cousin), the mini theme, the present collection, the Cousin Creator, the star map, Cosmo, the " +
                      "tutorial Plaza, and the stage where the game always plays the Prince.");
            ImUi.Hint("The old style plays OUAK's motions for now; her own Katamari Forever motions come later.");

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
            if (!SelectScreen.Ready) ImUi.Err("Burda could not be added to the game's select screen; see the log. She can still be picked here.");
            else if (SelectScreen.ScopeFailed) ImUi.Err("The select screen's marker hooks could not be made; see the log. Its current-cousin marker stays on the Prince.");
            if (Dresser.LastProblem != null) ImUi.Hint("Last model left as it was: " + Dresser.LastProblem);
        }

        private static void Choose(string cousinId, string style)
        {
            bool wasOn = Chosen.On;
            // A style change keeps where she was picked: picked on the select screen and worn, she still needs the Prince in
            // the save. Picked here while she is off for now, she is picked here: worn whatever cousin the save holds.
            Settings.Choose(cousinId, style, onScreen: cousinId != null && Settings.OnScreen && Chosen.On && Settings.CousinId == cousinId);
            Chosen.Refresh();
            if (Chosen.On && !wasOn) Contexts.LeaveCurrentOwnerAlone();
        }
    }
}
