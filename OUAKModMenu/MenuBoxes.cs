using System;
using System.Linq;
using ImGuiNET;
using Il2CppApp.KatamariSin;
using UnityEngine;
using OUAKModAPI;
using Vector2 = System.Numerics.Vector2;

namespace OUAKModMenu
{
    /// <summary>The Item boxes tab: what story stages' freebie boxes give (BoxRules over the API's ItemBoxes).</summary>
    internal sealed partial class Menu
    {
        private void DrawBoxes()
        {
            ImUi.Hint("Freebie boxes in story stages hold Drink, Magnet, Pickel, Sonar or Timer. Switch one off (a box left with nothing " +
                      "disappears until you allow it again) or swap it for any freebie: the box gives the new one and shows its model. " +
                      "These rules are saved and apply in every story stage.");

            float icon = ImUi.Px(26f);
            var all = StageCheats.Freebies;
            if (ImGui.BeginTable("boxrules", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Freebie", ImGuiTableColumnFlags.WidthStretch, 1.4f);
                ImGui.TableSetupColumn("In boxes", ImGuiTableColumnFlags.WidthFixed, ImUi.Px(90f));
                ImGui.TableSetupColumn("Boxes give instead", ImGuiTableColumnFlags.WidthStretch, 2f);
                ImGui.TableHeadersRow();
                foreach (var item in BoxRules.BoxItems)
                {
                    ImGui.PushID((int)item);
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    var tex = GameSprites.Freebie(item);
                    if (tex != null) { ImUi.Image(tex, icon, icon); ImGui.SameLine(); }
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextUnformatted(StageCheats.Pretty(item));
                    ImGui.TableNextColumn();
                    bool on = ItemBoxes.IsEnabled(item);
                    if (ImGui.Checkbox("##on", ref on)) BoxRules.SetEnabled(item, on);
                    ImGui.TableNextColumn();
                    var to = ItemBoxes.SwapOf(item);
                    ImGui.SetNextItemWidth(-1f);
                    ImGui.BeginDisabled(!on);
                    if (ImGui.BeginCombo("##swap", to == item ? "(itself)" : SwapLabel(to)))
                    {
                        if (ImGui.Selectable("(itself)", to == item)) BoxRules.SetSwap(item, item);
                        foreach (var f in all)
                        {
                            if (f == item) continue;
                            if (ImGui.Selectable(SwapLabel(f), f == to)) BoxRules.SetSwap(item, f);
                            ImUi.Tip(FreebieTip(f));
                        }
                        ImGui.EndCombo();
                    }
                    ImGui.EndDisabled();
                    ImGui.PopID();
                }
                ImGui.EndTable();
            }
            ImUi.Hint("* does nothing in story stages: it's used up with just its look.");
            if (ImUi.Button("Back to the game's boxes", ItemBoxes.Active)) BoxRules.Reset();

            ImUi.Header("Boxes in this stage");
            if (!GameRefs.InStage) { ImGui.TextDisabled("Enter a story stage to see its boxes."); return; }
            var boxes = ItemBoxes.All().OrderBy(b => b.name).ToList();
            if (boxes.Count == 0) { ImGui.TextDisabled("This stage has no freebie boxes."); return; }
            ImUi.Hint("One box can give something else for this visit only (not saved): pick it here.");
            ImGui.BeginChild("boxes", new Vector2(0f, 0f), ImGuiChildFlags.Borders);
            for (int k = 0; k < boxes.Count; k++)
            {
                var box = boxes[k];
                ImGui.PushID(k);
                ImGui.AlignTextToFramePadding();
                bool changed = ItemBoxes.Changes(box);
                string line = ItemBoxes.Describe(box);
                if (changed) ImGui.TextColored(ImTheme.Accent, line); else ImGui.TextUnformatted(line);
                ImGui.SameLine();
                ImUi.RightAlign(ImUi.Px(220f));
                ImGui.SetNextItemWidth(ImUi.Px(220f));
                if (ImGui.BeginCombo("##box", "this box gives..."))
                {
                    if (ImGui.Selectable("(the rules above)")) ItemBoxes.SetBox(box, null);
                    foreach (var f in all)
                        if (ImGui.Selectable(SwapLabel(f))) ItemBoxes.SetBox(box, new[] { f });
                    ImGui.EndCombo();
                }
                ImGui.PopID();
            }
            ImGui.EndChild();
        }

        private static string SwapLabel(eInstageItemType f) => StageCheats.Pretty(f) + (StageCheats.IsCosmetic(f) ? " *" : "");
    }
}
