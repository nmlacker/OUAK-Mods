using System;
using System.Linq;
using ImGuiNET;
using UnityEngine;
using OUAKModAPI;
using Vector2 = System.Numerics.Vector2;

namespace OUAKMultiplayer
{
    /// <summary>
    /// The Katamari Ball tab (the game's Sports Katamari, offline against three CPU princes): start a match on any
    /// multiplayer map (Race and Target included, through the API's fixes), the Multiplayer Room, King Chances and Target
    /// points (MatchExtras), and the custom match rules (CustomMatch). Moved here from the Mod Menu, which is
    /// single-player only.
    /// </summary>
    internal sealed class KatamariBallPage
    {
        private static readonly CpuMode[] Cpus = (CpuMode[])Enum.GetValues(typeof(CpuMode));
        private static readonly string[] CpuLabels = Cpus.Select(c => c.ToString()).ToArray();

        public void Register() => ModMenu.AddTab("katamariball", "Katamari Ball", Draw, 90, MatchExtras.ModName);

        private void Draw()
        {
            ImUi.Hint("Katamari Ball (the game's Sports Katamari) offline against three CPU princes, the game's training mode. " +
                      "These settings never change online play with other people.");
            bool save = GameRefs.InGameSession;

            ImUi.Header("Sports Katamari maps");
            int pick = ImUi.ChoiceGrid("map", CustomMatch.Maps.Length, 4, i => CustomMatch.MapName(CustomMatch.Maps[i]), CustomMatch.MapIndex);
            if (pick >= 0) { CustomMatch.MapIndex = pick; CustomMatch.SavePrefs(); }
            if (ImUi.Primary(save ? $"Start a match on {CustomMatch.MapName(CustomMatch.Map)}" : "Start a match (load a save first)", save))
                ModTasks.Run(CustomMatch.Start);

            ImUi.Header("Other rounds");
            ImUi.Hint("The game's unfinished online rounds, offline against CPU princes, with the API's fixes for them: " +
                      "Race (a course with checkpoints) and Target (roll up the origami cranes).");
            var race = MapEntryFor("Stage_ONRace1");
            var target = MapEntryFor("Stage_ONTarget1");
            int b = ImUi.Buttons(save ? "Start Race" : "Race (load a save first)", save ? "Start Target" : "Target (load a save first)",
                                 "Multiplayer Room");
            if (b == 0 && save && race != null) ModTasks.Run(() => MatchExtras.Launch(race), toast: true);
            if (b == 1 && save && target != null) ModTasks.Run(() => MatchExtras.Launch(target), toast: true);
            if (b == 2) ModTasks.Run(() => SceneLoader.LoadMode(SceneLoader.Modes.First(m => m.Action == ModeAction.MultiplayerRoom)), toast: true);
            ImUi.Hint("Multiplayer Room is the game's online lobby: on its own it has no room to show, so it opens the ship's multiplayer menu.");

            ImUi.Header("King Chances");
            if (ImGui.Checkbox("King Chances in offline matches", ref MatchExtras.KingChancesOn)) MatchExtras.Save();
            ImUi.Hint("The game picks 4 different King Chances for every match but never starts them. With this on, the King starts " +
                      "the match's 4 on time: one in each quarter of the time limit, evenly spaced (at 1/8, 3/8, 5/8 and 7/8 of it).");
            string kc = MatchExtras.Describe();
            if (kc != null) ImUi.Ok(kc);
            if (!GamePatches.KingChances.Ready && GamePatches.KingChances.Error != null) ImUi.Hint("Unavailable: " + GamePatches.KingChances.Error);

            ImUi.Header("Target round");
            if (ImGui.Checkbox($"{TargetRound.PointsPerTarget} points for every crane rolled up", ref MatchExtras.TargetPointsOn)) MatchExtras.Save();
            ImUi.Hint("Counted by the game's own crane counter, for you and the CPU princes; the HUD's points and the results show them.");
            if (TargetRound.InRound) ImUi.Ok($"Target round running: {TargetRound.PointsGiven} points given so far.");

            ImUi.Header("Custom rules");
            ImUi.Hint("While custom rules are on, they apply to every offline Sports Katamari match, including ones started from the ship's terminal.");
            if (ImGui.Checkbox("Custom rules for offline matches", ref CustomMatch.Enabled)) CustomMatch.SavePrefs();

            ImGui.BeginDisabled(!CustomMatch.Enabled);
            ImUi.Header("Time limit");
            pick = ImUi.ChoiceGrid("time", CustomMatch.TimeChoices.Length, 5,
                i => i == 0 ? "Default" + GameDefaultHint() : CustomMatch.TimeName(CustomMatch.TimeChoices[i]), CustomMatch.TimeIndex);
            if (pick >= 0) { CustomMatch.TimeIndex = pick; CustomMatch.SavePrefs(); }

            ImUi.Header("CPU princes");
            int cpu = Array.IndexOf(Cpus, CustomMatch.Cpu);
            if (ImUi.Segments("cpu", CpuLabels, ref cpu)) { CustomMatch.Cpu = Cpus[cpu]; CustomMatch.SavePrefs(); }
            ImUi.Hint(CustomMatch.CpuName(CustomMatch.Cpu));

            ImUi.Header("Freebies");
            if (ImGui.Checkbox("Freebies (King Boxes and item boxes give something)", ref CustomMatch.FreebiesOn)) CustomMatch.SavePrefs();
            string live = LiveMatchLine();
            if (live != null) ImUi.Ok(live);
            ImGui.BeginDisabled(!CustomMatch.FreebiesOn);

            // King Boxes (the King's chests): any reward the battle code has (the API's BattleItemPools).
            PoolHeader("King Boxes (the King's chests)", CustomMatch.KingBoxCount, CustomMatch.KingBoxTotal, "kb",
                CustomMatch.SetKingBoxPool);
            if (ImGui.BeginTable("kingbox", 3, ImGuiTableFlags.SizingStretchSame))
            {
                foreach (var r in BattleItemPools.KingBoxRewards)
                {
                    ImGui.TableNextColumn();
                    bool on = CustomMatch.InKingBoxPool(r);
                    if (ImGui.Checkbox(CustomMatch.RewardName(r) + "##kb" + r, ref on)) CustomMatch.ToggleKingBox(r);
                }
                // The freebies with no King Box reward of their own (the API hands them to your slot).
                foreach (var t in BattleItemPools.KingBoxFreebies)
                {
                    ImGui.TableNextColumn();
                    bool on = CustomMatch.InKingBoxFreebies(t);
                    if (ImGui.Checkbox(CustomMatch.ItemName(t) + "##kbf" + (int)t, ref on)) CustomMatch.ToggleKingBoxFreebie(t);
                }
                ImGui.EndTable();
            }
            ImUi.Hint("The game's own King Boxes give a Buddy, Washtub, Mushroom, Magnet, Rocket or a Special one. Tick others " +
                      "(Hand, Spiderweb, Bubble, Shoes, Crown, Smoke, Hoop, Dice, Special Dice) and they can come from King Boxes too. " +
                      "Smoke, Hoop and both Dice have no King Box reward of their own: they go straight to your freebie slot with the roulette, " +
                      "and a CPU prince gets one of the ticked rewards instead (or nothing). None ticked: King Boxes give nothing.");

            // Item boxes: any freebie the battle code has.
            PoolHeader("Item boxes", CustomMatch.ItemBoxPool.Count, BattleItemPools.ItemBoxItems.Length, "ib", CustomMatch.SetItemBoxPool);
            if (ImGui.BeginTable("itembox", 3, ImGuiTableFlags.SizingStretchSame))
            {
                foreach (var t in BattleItemPools.ItemBoxItems)
                {
                    ImGui.TableNextColumn();
                    bool on = CustomMatch.InItemBoxPool(t);
                    if (ImGui.Checkbox(CustomMatch.ItemName(t) + "##ib" + t, ref on)) CustomMatch.ToggleItemBox(t);
                }
                ImGui.EndTable();
            }
            ImUi.Hint("The game's own item boxes give a Washtub, Spiderweb, Bubble, Mushroom, Hand, Shoes, Crown or a Special one. " +
                      "Magnet and Rocket can come from item boxes too if ticked. None ticked: item boxes give nothing. " +
                      "Smoke, Hoop and both Dice can come from item boxes too. Sunglasses has no battle code, so it can't be in either; " +
                      "Ice axe, Sonar and Timer do nothing in battles.");
            ImGui.EndDisabled();
            ImGui.EndDisabled();
        }

        /// <summary>A pool's title with its count, and Game's own / All / None buttons on the right.</summary>
        private static void PoolHeader(string title, int count, int total, string id, Action<int> set)
        {
            ImGui.Spacing();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted($"{title}: {count} of {total}");
            float bw = ImUi.Px(86f);
            ImUi.RightAlign(bw * 3f + ImGui.GetStyle().ItemSpacing.X * 2f);
            if (ImGui.Button("Game's own##" + id, new Vector2(bw, 0f))) set(0);
            ImGui.SameLine();
            if (ImGui.Button("All##" + id, new Vector2(bw, 0f))) set(1);
            ImGui.SameLine();
            if (ImGui.Button("None##" + id, new Vector2(bw, 0f))) set(2);
        }

        private static MapEntry MapEntryFor(string internalName) =>
            StageCatalog.Entries.FirstOrDefault(e => e.Kind == MapKind.Multiplayer && e.Internal == internalName);

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
