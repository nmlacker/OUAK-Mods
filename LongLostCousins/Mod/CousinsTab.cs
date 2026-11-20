using System;
using System.Collections.Generic;
using ImGuiNET;
using LongLostCousins.Data;
using LongLostCousins.Dressing;
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
            ImUi.Hint("Play as a cousin the game never shipped. The mod dresses your own cousin as the one you pick here; " +
                      "the game still thinks you are the cousin you chose in the game, and nothing goes into your save.");

            ImUi.Header("Who are you?");
            if (ImGui.RadioButton("Off (the game's own cousin)", !Settings.Active)) Choose(null, null);
            if (Catalog.Cousins.Count == 0)
                ImUi.Err("The mod's list of cousins could not be read: " + (Catalog.Error ?? "it is empty"));
            foreach (var c in Catalog.Cousins)
                foreach (var s in c.Styles)
                {
                    bool on = Settings.Active && Settings.CousinId == c.Id && Settings.Style == s.Key;
                    if (ImGui.RadioButton(c.StyleLabel(s.Key) + "##" + c.Id + "/" + s.Key, on) && !on) Choose(c.Id, s.Key);
                }
            if (Settings.MemoryOnly) ImUi.Hint("The cousin test is running: this choice is not saved.");

            ImUi.Hint("The change shows the next time the game loads your cousin: the next stage, the next visit to the Plaza " +
                      "or the Prince's Ship, or a cousin change there.");
            ImUi.Hint("Dressed: your cousin in story stages and the stage's corner picture, the Plaza and the Prince's Ship " +
                      "(and the picture of you there), the stage results and the ending.");
            ImUi.Hint("Not dressed: the character select screen, online battles, OUAK Multiplayer (others there see the game's " +
                      "own cousin), the mini theme, the present collection, the Cousin Creator, the star map, Cosmo, the " +
                      "tutorial Plaza, and the stage where the game always plays the Prince.");
            ImUi.Hint("The old style plays OUAK's motions for now; her own Katamari Forever motions come later.");

            ImUi.Header("Now");
            int live = Dresser.Models.Count;
            ImGui.TextUnformatted(Settings.Active ? "Chosen: " + Settings.Describe() : "Off");
            ImGui.TextUnformatted($"Dressed now: {live} model{(live == 1 ? "" : "s")} ({Dresser.DressedCount} this session)");
            if (live > 0)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var m in Dresser.Models)
                    if (seen.Add(m.Label)) ImGui.TextUnformatted("  " + m.Label);
            }
            if (Settings.Active && !Hooks.Installed) ImUi.Err("The mod's hooks are not in place; see the log.");
            else if (Settings.Active && Hooks.Count < Hooks.Expected) ImUi.Err($"Only {Hooks.Count} of the mod's {Hooks.Expected} hooks could be made; see the log.");
            if (Dresser.LastProblem != null) ImUi.Hint("Last model left as it was: " + Dresser.LastProblem);
        }

        private static void Choose(string cousinId, string style)
        {
            bool wasActive = Settings.Active;
            Settings.Choose(cousinId, style);
            if (Settings.Active)
            {
                Hooks.Install();
                if (!wasActive) Contexts.LeaveCurrentOwnerAlone();
            }
            else Hooks.Uninstall();
        }
    }
}
