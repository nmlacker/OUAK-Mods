using System;
using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using Il2CppApp.KatamariSin;
using UnityEngine;
using OUAKModAPI;
using Vector2 = System.Numerics.Vector2;

namespace OUAKModMenu
{
    /// <summary>The mod menu's pages in the shared mod window, drawn with Dear ImGui (through the OUAK API).</summary>
    internal sealed partial class Menu
    {
        private string _selectedBackup;
        private readonly TextField _backupNote = new(200, "optional note, e.g. \"before the Big 3 stages\"");
        private readonly TextField _levelFilter = new(40, "search maps (name or internal name)");

        /// <summary>The mod menu's pages, as tabs of the shared mod window (in the order they always had).</summary>
        public static readonly (string id, string title)[] Pages =
        {
            ("freebies", "Freebies"), ("boxes", "Item boxes"), ("stage", "Stage"), ("levels", "Levels"), ("modes", "Modes"), ("keys", "Keys"),
            ("unlocks", "Unlocks"), ("dlc", "DLC"), ("backups", "Backups"),
        };

        public void Register()
        {
            ModMenu.Title = "Once Upon a Katamari  ·  Mod Menu";
            Action[] draw = { DrawFreebies, DrawBoxes, DrawStage, DrawLevels, DrawModes, DrawKeys, DrawUnlocks, DrawDlc, DrawBackups };
            for (int i = 0; i < Pages.Length; i++)
                ModMenu.AddTab(Pages[i].id, Pages[i].title, draw[i], 10 + i * 10, "OUAK Mod Menu");
        }

        // ---------------------------------------------------------------- Freebies

        private void DrawFreebies()
        {
            var gm = GameRefs.Main;
            // A story stage, or (since 2026-11-12) a Katamari Ball match: freebies are given the battle way there.
            bool inStage = StageCheats.CanUseFreebies;
            bool battle = StageCheats.InBattle;

            // What you hold now, with Clear.
            var heldItem = inStage ? StageCheats.Held : eInstageItemType.NoItem;
            var heldIcon = inStage && heldItem != eInstageItemType.NoItem ? GameSprites.Freebie(heldItem) : null;
            float icon = ImUi.Px(30f);
            if (heldIcon != null) { ImUi.Image(heldIcon, icon, icon); ImGui.SameLine(); }
            ImGui.AlignTextToFramePadding();
            string held = !inStage ? "Enter a stage or a Katamari Ball match to give or clear freebies."
                : heldItem == eInstageItemType.NoItem ? (gm._itemEffectTimer >= 0f ? $"Holding: nothing  ({StageCheats.Pretty(gm._nowInventoryItem)} in use)" : "Holding: nothing")
                : $"Holding: {StageCheats.Pretty(heldItem)}{(gm._itemEffectTimer >= 0f ? "  (in use)" : "")}";
            if (inStage) ImGui.TextUnformatted(held);
            else ImGui.TextDisabled(held);
            ImUi.RightAlign(ImUi.Px(124f));
            if (ImUi.Button("Clear it", inStage, ImUi.Px(124f))) ModTasks.Run(StageCheats.ClearFreebie);

            // Every freebie at once: click picks it, double-click gives it right away. The ones that do something in
            // story stages first, then the cosmetic-only ones (used up with just their visual effect here).
            var all = StageCheats.Freebies;
            ImUi.Header("Pick a freebie");
            if (inStage) ImUi.Hint("Click to pick · double-click to give it now · hover to see what it does");
            FreebieGrid("freebies", all.Where(f => !StageCheats.IsCosmetic(f)).ToArray(), inStage);
            ImUi.Header(battle ? "Does nothing in battles" : "Cosmetic only in story stages");
            ImUi.Hint(battle
                ? "These have no battle effect (story freebies the battle code doesn't use): given anyway, they're used up with just their visual effect."
                : "These do nothing here (battle freebies that act on other princes, and ones the game never finished): " +
                  "given anyway, they're used up with just their visual effect.");
            if (battle && inStage) ImUi.Hint("In a Katamari Ball match a freebie is given the game's battle way: the roulette spins, then it lands in your slot.");
            FreebieGrid("cosmetic", all.Where(StageCheats.IsCosmetic).ToArray(), inStage);
            ImGui.Spacing();

            ImUi.Hint($"{StageCheats.Pretty(StageCheats.AutoItem)}: {Effect(StageCheats.AutoItem)}" +
                      (StageCheats.IsCosmetic(StageCheats.AutoItem) ? " (cosmetic only here)" : ""));
            { string running = StoryFreebies.Describe(); if (running.Length > 0) ImUi.Ok("Running: " + running); }
            ImGui.Spacing();

            // What to do with the picked one.
            string name = StageCheats.Pretty(StageCheats.AutoItem);
            if (ImUi.Primary($"Give {name} now", inStage)) ModTasks.Run(StageCheats.GiveAutoItem);
            if (ImGui.Checkbox($"Spawn with {name} on every story stage start and restart (like carrying it in)", ref StageCheats.AutoFreebie))
                StageCheats.SavePrefs();
            ImGui.Checkbox("Freeze freebie: it comes back after you use it", ref StageCheats.FreezeFreebie);
            ImGui.TextDisabled(StageCheats.FrozenItem == eInstageItemType.NoItem ? "Frozen freebie: none yet (give one, or pick one up)"
                : $"Frozen freebie: {StageCheats.Pretty(StageCheats.FrozenItem)}");
            ImGui.Spacing();
            if (!battle) ImUi.Hint("Tarai, Spider, Bubble, Hand and Smoke act on other princes, so they only work in battles (Sports Katamari); " +
                      "Hoop, Dice and Sunglasses were never finished by the game. " +
                      "Shoes, Crown and Mushroom get their battle effect in story stages from the API's patch (Mushroom adds 2.5x a tenth of the stage's start size, the share battles use).");
        }

        /// <summary>Self-test only: shows this cell's hover text (of the first grid) without the mouse (the test never moves the pointer).</summary>
        internal static int ForceFreebieTip = -1;

        /// <summary>One grid of freebies: click picks it (the auto freebie), double-click gives it, hover explains it.</summary>
        private static void FreebieGrid(string id, eInstageItemType[] items, bool inStage)
        {
            if (items.Length == 0) return;
            int picked = Array.IndexOf(items, StageCheats.AutoItem);
            var origin = ImGui.GetCursorScreenPos();
            bool forced = ForceFreebieTip >= 0 && ForceFreebieTip < items.Length && id == "freebies";
            // Hover text per cell (the API's ChoiceGrid tip).
            int clicked = ImUi.ChoiceGrid(id, items.Length, 4, i => StageCheats.Pretty(items[i]), picked,
                i => GameSprites.Freebie(items[i]) ?? GameSprites.Freebie(BaseFreebie(items[i])), 36f, null,
                forced ? null : i => FreebieTip(items[i]));
            if (forced)
            {
                // Self-test: shown without the mouse, under the first row (the test never moves the pointer).
                ImGui.SetNextWindowPos(origin + new Vector2(0f, ImUi.Px(36f) + ImGui.GetStyle().ItemSpacing.Y));
                ShowTip(FreebieTip(items[ForceFreebieTip]));
            }
            if (clicked >= 0)
            {
                StageCheats.SetAutoItem(items[clicked]);
                if (ImUi.LastClickDouble && inStage) { var give = items[clicked]; ModTasks.Run(() => StageCheats.GiveFreebie(give)); }
            }
        }

        /// <summary>A tooltip at the mouse (same look as ImUi.Tip).</summary>
        private static void ShowTip(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImUi.Px(420f));
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }

        /// <summary>Every freebie's hover text, one line each (self-test log).</summary>
        internal static string AllFreebieTips() =>
            string.Join(" || ", StageCheats.Freebies.Select(f => FreebieTip(f).Replace("\n", " / ")));

        /// <summary>A Special freebie's normal version (for its icon: the game has no separate Special icons by that name).</summary>
        private static eInstageItemType BaseFreebie(eInstageItemType t) => (int)t switch
        {
            >= 16 and <= 24 => (eInstageItemType)((int)t - 15),
            25 => eInstageItemType.Crown,
            26 => eInstageItemType.Dice,
            _ => t,
        };

        // The game's own explanation of each story freebie: its one-time tips (MainGameManager.CallItemTips), title
        // TIPS_058..062 and text TIPS_063..067 (read from the game 2026-11-06: Drink is "Rocket", Pickel "Ice axe").
        private static readonly Dictionary<eInstageItemType, (string title, string text)> GameTips = new()
        {
            [eInstageItemType.Drink] = ("TIPS_058", "TIPS_063"),
            [eInstageItemType.Magnet] = ("TIPS_059", "TIPS_064"),
            [eInstageItemType.Pickel] = ("TIPS_060", "TIPS_065"),
            [eInstageItemType.Sonar] = ("TIPS_061", "TIPS_066"),
            [eInstageItemType.Timer] = ("TIPS_062", "TIPS_067"),
        };

        private static readonly Dictionary<string, string> TipCache = new();

        /// <summary>
        /// The hover text of a freebie on the Freebies tab: the game's own words for it (its tip, in the game's language)
        /// and what it does in story stages (Effect). Shoes, Crown and Mushroom have no story text in the game (their
        /// battle texts don't match what they do: Shoes says "unlimited dash", measured as an instant dash recharge),
        /// so they show the effect this mod gives them. Cached once the game's tables are up.
        /// </summary>
        private static string FreebieTip(eInstageItemType item)
        {
            string cacheKey = (StageCheats.InBattle ? "b:" : "s:") + item;
            if (TipCache.TryGetValue(cacheKey, out var cached)) return cached;
            var g = GameRefs.Global;
            if (g == null) return StageCheats.Pretty(item) + "\n" + Effect(item);
            string Text(Il2CppApp.KatamariSin.TextCommonTable table, string id)
            {
                try { string t = table?.GetText(id); return string.IsNullOrEmpty(t) || t == id ? null : t.Replace("\r", "").Replace("\n", " ").Trim(); }
                catch { return null; }
            }
            var b = BaseFreebie(item);
            bool special = b != item;
            var sb = new System.Text.StringBuilder();
            sb.Append(StageCheats.Pretty(item));
            string gameSays = null;
            if (GameTips.TryGetValue(b, out var tip))
            {
                // The tip's title is "Freebie: Rocket": keep the name after the last colon, and say it only when the
                // game's name differs from the menu's (Drink = Rocket, Pickel = Ice axe).
                string title = Text(g.TipsTable, tip.title);
                gameSays = Text(g.TipsTable, tip.text);
                if (title != null)
                {
                    int k = Math.Max(title.LastIndexOf(": ", StringComparison.Ordinal), title.LastIndexOf('：'));
                    string gameName = (k >= 0 ? title.Substring(k + (title[k] == '：' ? 1 : 2)) : title).Trim();
                    if (!string.Equals(gameName, StageCheats.Pretty(b), StringComparison.OrdinalIgnoreCase))
                        sb.Append($"   (the game calls it {gameName})");
                }
            }
            sb.Append('\n');
            if (gameSays != null) sb.Append('"').Append(gameSays).Append("\"\n");
            if (special && GameTips.ContainsKey(b)) sb.Append("Special version: lasts twice as long.\n");
            if (StageCheats.InBattle)
            {
                // A Katamari Ball match (battle rules): what it does in battles.
                string added = FreebieRules.AddedEffect(item, battle: true);
                if (StageCheats.IsCosmetic(item)) sb.Append("Does nothing in battles: given anyway, it's used up with just its visual effect.");
                else sb.Append("In battles: ").Append(added.Length > 0 ? added + " (added by the OUAK API)" : "the game's own battle effect");
            }
            else if (StageCheats.IsCosmetic(item))
                sb.Append("Cosmetic only in story stages: given anyway, it's used up with just its visual effect.\n").Append(Effect(item));
            else sb.Append("In story stages: ").Append(Effect(item));
            string result = sb.ToString();
            if (gameSays != null || !GameTips.ContainsKey(b)) TipCache[cacheKey] = result; // retry until the game's text is up
            return result;
        }

        /// <summary>What a freebie does in story stages (the game's numbers, measured in game).</summary>
        private static string Effect(eInstageItemType item)
        {
            string added = FreebieRules.AddedEffect(item);
            if (added.Length > 0) return added + " (effect added by the OUAK API: the game gives it none in story stages)";
            return item switch
            {
                eInstageItemType.Drink => "Roll 2x faster for 10 s",
                eInstageItemType.Magnet => "Pull nearby things in (range x2.2) for 10 s",
                eInstageItemType.Pickel => "Climb 5x faster for 10 s",
                eInstageItemType.Sonar => "Sonar for 30 s",
                eInstageItemType.Timer => "Timer for 10 s",
                eInstageItemType.SP_Drink => "Roll 2x faster for 20 s (twice as long as Drink)",
                eInstageItemType.SP_Magnet => "Pull things in from further (range x3) for 20 s",
                eInstageItemType.SP_Pickel => "Climb 5x faster for 20 s (twice as long as Pickel)",
                eInstageItemType.SP_Sonar => "Sonar for 60 s (twice as long)",
                eInstageItemType.SP_Timer => "Timer for 20 s (twice as long)",
                eInstageItemType.RandomItem => "One of Drink, Magnet, Pickel, Sonar or Timer, or a Special one, at random",
                _ => FreebieRules.WhyNot(item),
            };
        }

        // ---------------------------------------------------------------- Stage

        private void DrawStage()
        {
            var gm = GameRefs.Main;
            bool inStage = GameRefs.InStage;
            if (inStage)
            {
                float t = Mathf.Max(0f, gm._nowTime);
                ImUi.Title(gm.TargetStage.ToString().Replace("Stage_", ""));
                ImGui.TextUnformatted($"Time left: {(int)t / 60}:{(int)t % 60:00}{(StageCheats.FreezeTimer ? "  (frozen)" : "")}     Cleared: {(gm._hasCleared ? "yes" : "no")}");
            }
            else ImUi.Hint("Not in a stage. The timer freeze is remembered for the next one.");

            ImUi.Header("Time");
            ImGui.Checkbox("Freeze stage timer", ref StageCheats.FreezeTimer);
            if (ImUi.Button("Complete stage timer (time's up now)", inStage)) ModTasks.Run(StageCheats.CompleteTimer);

            ImUi.Header("Stage");
            if (ImUi.Button("Finish stage (instant clear)", inStage)) ModTasks.Run(StageCheats.FinishStage);
            if (ImUi.Button("Restart stage (instant retry)", inStage)) ModTasks.Run(StageCheats.RestartStage);
            if (ImUi.Button("Reload stage (full scene reload)", inStage)) ModTasks.Run(SceneLoader.ReloadCurrentStage);
            if (ImUi.Button("Return to the Plaza", inStage)) ModTasks.Run(StageCheats.ReturnToPlaza);
        }

        // ---------------------------------------------------------------- Levels

        private void DrawLevels()
        {
            bool ready = GameRefs.InGameSession;
            bool ownsAA = ready && GameRefs.OwnsAA;
            if (ready) ImUi.Hint("Click a map to load it. Plaza maps jump to that era's hub. (Single-player only: the multiplayer maps are OUAK Multiplayer's.)");
            else ImUi.Err("Load a save file first.");
            ImUi.Input("##filter", _levelFilter);

            string filter = _levelFilter.Text.Trim();
            ImGui.BeginChild("maps", Vector2.Zero, ImGuiChildFlags.Borders);
            string last = null;
            bool any = false;
            float h = ImUi.Px(30f);
            foreach (var e in StageCatalog.Entries)
            {
                if (e.Kind == MapKind.Multiplayer) continue; // KatamariBall maps: not this single-player menu's
                if (filter.Length > 0 && e.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0
                    && e.Internal.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (last != e.Group)
                {
                    ImUi.Header(e.Group + (e.NeedsAA && !ownsAA ? AADlcSuffix() : ""));
                    last = e.Group;
                }
                any = true;
                var entry = e;
                bool locked = e.NeedsAA && !ownsAA;
                ImGui.BeginDisabled(!ready || locked);
                if (ImGui.Selectable(e.Name + "##" + e.Internal, false, ImGuiSelectableFlags.None, new Vector2(0f, h)))
                    ModTasks.Run(() => SceneLoader.Load(entry));
                ImGui.EndDisabled();
                // The internal name, greyed, at the right.
                var size = ImGui.CalcTextSize(e.Internal);
                var max = ImGui.GetItemRectMax();
                ImGui.GetWindowDrawList().AddText(new Vector2(max.X - size.X - ImUi.Px(8f), max.Y - (h + size.Y) / 2f),
                    ImGui.GetColorU32(ImGuiCol.TextDisabled), e.Internal);
            }
            if (!any) ImGui.TextDisabled("No map matches.");
            ImGui.EndChild();
        }

        // ---------------------------------------------------------------- Keys

        private void DrawKeys()
        {
            ImUi.Hint("Click Set, then press a key (hold Ctrl / Alt / Shift for a combination). Esc cancels. Hotkeys work with the menu closed, and a short message confirms what happened. " +
                      "They're ignored while you type in a text box. Every mod's hotkeys are listed here.");
            var cap = Hotkeys.Capturing;
            if (cap != null) ImGui.TextColored(ImTheme.Accent, $"Press a key for \"{cap.Label}\"...  (Esc to cancel)");

            if (!ImGui.BeginTable("keys", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp))
                return;
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthStretch, 3f);
            ImGui.TableSetupColumn("Key", ImGuiTableColumnFlags.WidthStretch, 1.6f);
            ImGui.TableSetupColumn("##buttons", ImGuiTableColumnFlags.WidthFixed, ImUi.Px(140f));
            ImGui.TableHeadersRow();
            foreach (var a in Hotkeys.Actions)
            {
                ImGui.PushID(a.Id);
                ImGui.TableNextRow();
                var conflicts = Hotkeys.Conflicts(a).Select(c => c.Label).ToList();
                string owner = string.IsNullOrEmpty(a.Owner) || a.Owner == "OUAK Mod Menu" ? "" : $"  ({a.Owner})";
                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                if (conflicts.Count > 0) ImGui.TextColored(ImTheme.Err, a.Label + owner);
                else ImGui.TextUnformatted(a.Label + owner);
                if (conflicts.Count > 0) ImUi.Tip("Same key as: " + string.Join(", ", conflicts));
                ImGui.TableNextColumn();
                if (cap == a) ImGui.TextColored(ImTheme.Accent, "press a key...");
                else ImGui.TextUnformatted(a.Binding.Display + (conflicts.Count > 0 ? "  (clash)" : ""));
                ImGui.TableNextColumn();
                ImGui.BeginDisabled(cap != null);
                if (ImGui.Button("Set")) Hotkeys.BeginCapture(a);
                ImGui.SameLine();
                ImGui.BeginDisabled(a.Binding.IsNone);
                if (ImGui.Button("Clear")) Hotkeys.Clear(a);
                ImGui.EndDisabled();
                ImGui.EndDisabled();
                ImGui.PopID();
            }
            ImGui.EndTable();
        }

        // ---------------------------------------------------------------- Modes

        private void DrawModes()
        {
            bool ready = GameRefs.InGameSession;
            bool ownsAA = ready && GameRefs.OwnsAA;
            ImUi.Hint("Jump straight to a game mode / scene. Some modes expect to be entered from the Plaza; if one gets stuck, load the Plaza from here.");
            ImGui.Spacing();
            float h = ImUi.Px(30f);
            foreach (var m in SceneLoader.Modes)
            {
                if (m.Action == ModeAction.MultiplayerRoom) continue; // multiplayer: not this single-player menu's
                var mode = m;
                bool menuScene = m.Scene is "Title" or "SelectData";
                bool locked = m.NeedsAA && !ownsAA;
                ImGui.BeginDisabled(!(ready || menuScene) || locked);
                if (ImGui.Button(m.Label + (locked ? AADlcSuffix() : "") + "##" + m.Scene, new Vector2(-1f, h))) ModTasks.Run(() => SceneLoader.LoadMode(mode));
                ImGui.EndDisabled();
            }
        }

        // ---------------------------------------------------------------- Unlocks

        private void DrawUnlocks()
        {
            bool ready = GameRefs.InGameSession;
            bool inStage = GameRefs.Main != null;
            int slot = Unlocker.CurrentSlot;
            var pre = ready ? Unlocker.PreUnlockBackup(slot) : null;

            if (!ready) ImUi.Hint("Load a save file first.");
            else
            {
                ImUi.Title($"Save slot {slot + 1}");
                ImUi.Hint(pre != null
                    ? $"Relock restores this slot to the backup from {pre.Created:yyyy-MM-dd HH:mm}{(string.IsNullOrEmpty(pre.Note) ? "" : $" (\"{pre.Note}\")")}."
                    : "Not unlocked by this mod yet, so there is nothing to relock.");
            }
            if (inStage) ImUi.Err("Leave the stage to unlock or relock.");
            ImGui.Spacing();

            if (ImUi.Primary("Unlock everything on this slot...", ready && !inStage))
            {
                ConfirmDialog.Ask(new ConfirmRequest
                {
                    Title = $"Unlock everything on slot {slot + 1}",
                    Body = "Clears every stage at top rank and gives all crowns, cousins, presents, collection objects, music, emotes, rooms, movies, tips and skins on this save slot, then saves. " +
                           "DLC you've locked or don't own is skipped. Relock can put the slot back afterwards.",
                    ConfirmLabel = "Unlock everything",
                    Action = Unlocker.UnlockAll,
                    Then = Core.ReloadPlazaIfThere,
                });
            }
            if (ImUi.Danger("Relock (restore pre-unlock progress)...", ready && !inStage && pre != null))
            {
                ConfirmDialog.Ask(new ConfirmRequest
                {
                    Title = $"Relock slot {slot + 1}",
                    Body = $"Puts this slot's progress back to how it was before the first unlock ({pre?.Created:yyyy-MM-dd HH:mm}), then saves. " +
                           "Anything earned since then on this slot is replaced; your options are kept.",
                    ConfirmLabel = "Relock",
                    Action = Unlocker.Relock,
                    Then = Core.ReloadPlazaIfThere,
                });
            }
            ImGui.Spacing();
            ImUi.Hint("Unlocking edits the loaded save, so it asks you to type \"I understand\" first and always makes a backup. " +
                      "To hide DLC instead (for example to start a new game without it), use the DLC tab.");
        }

        // ---------------------------------------------------------------- DLC

        private void DrawDlc()
        {
            ImUi.Hint("Lock hides DLC you own: the game treats it as not installed, so a new game starts with no DLC prompts, stages, music or items. " +
                      "Unlock brings it back; on a save that hasn't seen it yet the game then offers it like newly bought DLC, so you can keep playing or start a new game with it. " +
                      "Locks stay on between launches. Change them from the Title screen or the Plaza, then restart the game. This can't unlock DLC you haven't bought.");
            if (!DlcLock.PatchesActive)
                ImUi.Err("Warning: the DLC hook didn't install (see MelonLoader log); locks are applied every frame instead.");

            bool canChange = GameRefs.Main == null;
            var packs = DlcLock.Packs();
            float half = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2f;
            if (ImUi.Button("Lock all owned DLC...", canChange && packs.Any(p => p.Owned && !p.Locked), half))
                AskDlc("Lock all owned DLC", "Hides every DLC pack Steam says you own.", "Lock DLC", "Before locking all DLC", _ => DlcLock.LockAllOwned());
            ImGui.SameLine();
            if (ImUi.Button("Unlock all DLC...", canChange && (packs.Any(p => p.Locked) || DlcLock.OtherGamesLocked), half))
                AskDlc("Unlock all DLC", "Makes every DLC pack you own visible to the game again (and the other-KATAMARI bonus).", "Unlock DLC", "Before unlocking all DLC",
                    _ => { string m = DlcLock.UnlockAll(); if (DlcLock.OtherGamesLocked) DlcLock.SetOtherGamesLocked(false); return m; });
            if (!canChange) ImUi.Err("Leave the stage to change DLC locks.");
            ImGui.Spacing();

            if (packs.Count == 0) ImGui.TextDisabled("DLC info isn't ready yet - wait for the title screen.");
            if (!ImGui.BeginTable("dlc", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp)) return;
            ImGui.TableSetupColumn("pack", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("button", ImGuiTableColumnFlags.WidthFixed, ImUi.Px(110f));
            foreach (var p in packs)
            {
                var pack = p;
                ImGui.PushID(p.Id);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(p.DisplayName);
                string state = p.Locked ? "LOCKED (hidden from the game)" : p.Owned ? "enabled" : "not owned";
                ImGui.TextDisabled($"{p.Label}  ·  owned: {(p.Owned ? "yes" : "no")}  ·  {state}  ·  in game now: {(p.AvailableNow ? "yes" : "no")}");
                ImGui.TableNextColumn();
                if (p.Locked)
                {
                    if (ImUi.Primary("Unlock...", canChange))
                        AskDlc($"Unlock {pack.DisplayName}", "The game will see this DLC again (if Steam says you own it).", "Unlock DLC", $"Before unlocking {pack.Label}", _ => DlcLock.Unlock(pack.Id));
                }
                else if (ImUi.Button("Lock...", canChange && p.Owned))
                    AskDlc($"Lock {pack.DisplayName}", "The game will treat this DLC as not installed until you unlock it.", "Lock DLC", $"Before locking {pack.Label}", _ => DlcLock.Lock(pack.Id));
                ImGui.PopID();
            }
            // The other-KATAMARI bonus (the King).
            bool locked = DlcLock.OtherGamesLocked;
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(DlcLock.OtherGamesLabel);
            ImGui.TextDisabled($"Detected: {DlcLock.OtherGamesDetected()}  ·  {(locked ? "LOCKED (hidden from the game)" : "enabled")}");
            ImGui.TableNextColumn();
            if (locked ? ImUi.Primary("Unlock...##other", canChange) : ImUi.Button("Lock...##other", canChange))
            {
                AskDlc($"{(locked ? "Unlock" : "Lock")}: {DlcLock.OtherGamesLabel}",
                    locked ? "Saves can receive the King again." : "The game will act as if you don't own Katamari Damacy REROLL / We Love Katamari REROLL+, so new saves don't get the King or his prompt. Saves that already have him keep him.",
                    locked ? "Unlock" : "Lock", $"Before {(locked ? "unlocking" : "locking")} the other-KATAMARI bonus", _ => DlcLock.SetOtherGamesLocked(!locked));
            }
            ImGui.EndTable();
        }

        private void AskDlc(string title, string what, string confirm, string reason, Func<string, string> apply)
        {
            ConfirmDialog.Ask(new ConfirmRequest
            {
                Title = title,
                Body = what + " The game may change the loaded save to match (for example taking off DLC presents). Restart the game afterwards.",
                ConfirmLabel = confirm,
                Action = note =>
                {
                    var backup = BackupManager.Create(reason, note);
                    return apply(note) + $" Backup: {backup.Id}";
                },
            });
        }

        // ---------------------------------------------------------------- Backups

        private void DrawBackups()
        {
            ImUi.Hint("Each backup is a timestamped copy of all your save files in UserData\\OUAKModMenu\\Backup, with a description of every slot and your note. " +
                      "One is made automatically before anything that edits, restores or changes DLC.");
            ImGui.TextUnformatted("Note for a new backup (optional):");
            bool enter = ImUi.Input("##note", _backupNote);

            bool atTitle = BackupManager.AtTitleScreens;
            float third = (ImGui.GetContentRegionAvail().X - 2 * ImGui.GetStyle().ItemSpacing.X) / 3f;
            if (ImUi.Primary("Create backup now", true, third) || enter)
            {
                string note = _backupNote.Text;
                _backupNote.Text = "";
                ModTasks.Run(() =>
                {
                    var b = BackupManager.Create("Manual backup", note);
                    if (GameRefs.InGameSession && GameRefs.Main == null) BackupManager.WriteLoadedSlotSnapshot(b);
                    _selectedBackup = b.Id;
                    return $"Backup created: {b.Id}";
                });
            }
            ImGui.SameLine();
            if (ImUi.Button("Open Backup folder", true, third))
                ModTasks.Run(() => { BackupManager.OpenFolder(BackupManager.BackupRoot); return "Opened the Backup folder."; });
            ImGui.SameLine();
            if (ImUi.Button("Go to Title (to restore)", !atTitle, third))
                ModTasks.Run(() => SceneLoader.LoadMode(SceneLoader.Modes[0]));
            ImGui.Spacing();

            var backups = BackupManager.List();
            var selected = backups.FirstOrDefault(b => b.Id == _selectedBackup) ?? backups.FirstOrDefault();
            float detailsH = ImUi.Px(160f);
            ImGui.BeginChild("backups", new Vector2(0f, -detailsH - ImGui.GetStyle().ItemSpacing.Y), ImGuiChildFlags.Borders);
            if (backups.Count == 0) ImGui.TextDisabled("No backups yet.");
            float bw = ImUi.Px(130f), rowH = ImUi.Px(48f);
            foreach (var b in backups)
            {
                var target = b;
                ImGui.PushID(b.Id);
                bool isSel = selected != null && b.Id == selected.Id;
                var start = ImGui.GetCursorScreenPos();
                float w = ImGui.GetContentRegionAvail().X - bw - ImGui.GetStyle().ItemSpacing.X;
                if (ImGui.Selectable("##row", isSel, ImGuiSelectableFlags.None, new Vector2(w, rowH))) _selectedBackup = b.Id;
                var dl = ImGui.GetWindowDrawList();
                float line = ImGui.GetTextLineHeight();
                dl.AddText(start + new Vector2(ImUi.Px(8f), ImUi.Px(3f)), ImGui.GetColorU32(ImGuiCol.Text), b.Title);
                dl.AddText(start + new Vector2(ImUi.Px(8f), ImUi.Px(5f) + line), ImGui.GetColorU32(ImGuiCol.TextDisabled),
                    string.IsNullOrEmpty(b.Note) ? "(no note)" : "Note: " + b.Note);
                ImGui.SameLine();
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (rowH - ImUi.Px(30f)) / 2f);
                if (ImUi.Danger(atTitle ? "Restore..." : "Restore (Title)", atTitle, bw))
                {
                    ConfirmDialog.Ask(new ConfirmRequest
                    {
                        Title = $"Restore the backup from {target.Created:yyyy-MM-dd HH:mm:ss}",
                        Body = "Replaces your current save files (all slots and settings) with the ones in this backup, then the game re-reads them and shows the save list again. " +
                               "Slots that didn't exist in the backup are left as they are now.",
                        ConfirmLabel = "Restore backup",
                        Action = note => BackupManager.RestoreFiles(target, note),
                        Then = () => BackupManager.List(refresh: true),
                    });
                }
                ImGui.PopID();
            }
            ImGui.EndChild();

            ImGui.BeginChild("details", Vector2.Zero, ImGuiChildFlags.Borders);
            if (selected != null)
            {
                ImGui.TextUnformatted(selected.Title);
                ImGui.TextDisabled($"Game {selected.GameVersion ?? "?"} · taken in {selected.Scene ?? "?"} · loaded slot: {(selected.LoadedSlot >= 0 ? (selected.LoadedSlot + 1).ToString() : "none")}" +
                                   $"{(selected.HasSlotSnapshot ? " (with live snapshot)" : "")}");
                foreach (var s in selected.Slots) ImGui.TextUnformatted(BackupManager.SlotLine(s));
                if (selected.DlcLocked.Count > 0) ImGui.TextDisabled("DLC locked: " + string.Join(", ", selected.DlcLocked));
            }
            ImGui.EndChild();
        }

        private static string AADlcSuffix() => DlcLock.IsLocked(4) ? "  (DLC locked)" : "  (DLC not owned)";
    }
}
