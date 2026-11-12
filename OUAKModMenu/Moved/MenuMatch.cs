using System;
using System.Linq;
using ImGuiNET;
using UnityEngine;
using OUAKModAPI;
using Vector2 = System.Numerics.Vector2;

namespace OUAKModMenu
{
    /// <summary>The Match tab: custom rules for offline matches (CustomMatch).</summary>
    internal sealed partial class Menu
    {
        private static readonly CpuMode[] Cpus = (CpuMode[])Enum.GetValues(typeof(CpuMode));
        private static readonly string[] CpuLabels = Cpus.Select(c => c.ToString()).ToArray();

        private void DrawMatch()
        {
            ImUi.Hint("Offline matches against three CPU princes (the game's Sports Katamari / training mode). While custom rules are on, " +
                      "they apply to every offline match, including ones started from the ship's terminal. Online play is never affected.");
            if (ImGui.Checkbox("Custom rules for offline matches", ref CustomMatch.Enabled)) CustomMatch.SavePrefs();

            // Every choice visible: click one.
            ImUi.Header("Map");
            int pick = ImUi.ChoiceGrid("map", CustomMatch.Maps.Length, 4, i => CustomMatch.MapName(CustomMatch.Maps[i]), CustomMatch.MapIndex);
            if (pick >= 0) { CustomMatch.MapIndex = pick; CustomMatch.SavePrefs(); }
            if (ImUi.Primary(GameRefs.InGameSession ? $"Start a match on {CustomMatch.MapName(CustomMatch.Map)}" : "Start a match (load a save first)", GameRefs.InGameSession))
                ModTasks.Run(CustomMatch.Start);

            ImUi.Header("Time limit");
            pick = ImUi.ChoiceGrid("time", CustomMatch.TimeChoices.Length, 5,
                i => i == 0 ? "Default" + GameDefaultHint() : CustomMatch.TimeName(CustomMatch.TimeChoices[i]), CustomMatch.TimeIndex);
            if (pick >= 0) { CustomMatch.TimeIndex = pick; CustomMatch.SavePrefs(); }

            ImUi.Header("CPU princes");
            int cpu = Array.IndexOf(Cpus, CustomMatch.Cpu);
            if (ImUi.Segments("cpu", CpuLabels, ref cpu)) { CustomMatch.Cpu = Cpus[cpu]; CustomMatch.SavePrefs(); }
            ImUi.Hint(CustomMatch.CpuName(CustomMatch.Cpu));

            ImUi.Header("Freebies");
            if (ImGui.Checkbox("Freebies (item boxes and King Box rewards)", ref CustomMatch.FreebiesOn)) CustomMatch.SavePrefs();
            string live = LiveMatchLine();
            if (live != null) ImUi.Ok(live);

            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted($"Freebies in play: {CustomMatch.OnCount} of {CustomMatch.Freebies.Length}");
            float bw = ImUi.Px(70f);
            ImUi.RightAlign(bw * 2f + ImGui.GetStyle().ItemSpacing.X);
            ImGui.BeginDisabled(!CustomMatch.FreebiesOn);
            if (ImGui.Button("All", new Vector2(bw, 0f))) CustomMatch.SetAll(true);
            ImGui.SameLine();
            if (ImGui.Button("None", new Vector2(bw, 0f))) CustomMatch.SetAll(false);

            var items = CustomMatch.Freebies;
            if (ImGui.BeginTable("freebies", 3, ImGuiTableFlags.SizingStretchSame))
            {
                foreach (var f in items)
                {
                    ImGui.TableNextColumn();
                    bool on = CustomMatch.IsOn(f);
                    if (ImGui.Checkbox(f.Name + (f.BoxItem == Il2CppApp.KatamariSin.eInstageItemType.NoItem ? " *" : "") + "##" + f.Name, ref on)) CustomMatch.Toggle(f);
                }
                ImGui.EndTable();
            }
            ImGui.EndDisabled();
            ImUi.Hint("* = King Box only; the rest also come from item boxes.");
        }

        private static string GameDefaultHint()
        {
            float t = CustomMatch.GameDefaultTime;
            return CustomMatch.TimeLimit == 0 && t > 0f ? $"  ({(int)t / 60}:{(int)t % 60:00})" : "";
        }

        private static string LiveMatchLine()
        {
            if (!CustomMatch.Applies) return null;
            var gm = GameRefs.Main;
            var mgm = CustomMatch.Mgm;
            if (gm == null || mgm == null) return null;
            float left = Mathf.Max(0f, gm._startTime - mgm.playTime);
            string time = left > 90000f ? "no time limit" : $"{(int)left / 60}:{(int)left % 60:00} left";
            return $"Custom match running on {CustomMatch.MapName(mgm.gimiSingleStage)}: {time}.";
        }
    }
}
