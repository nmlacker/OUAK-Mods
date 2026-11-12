using System;
using System.IO;
using System.Linq;
using ImGuiNET;
using MelonLoader;
using Vector2 = System.Numerics.Vector2;

namespace OUAKModAPI
{
    /// <summary>The API's own page in the mod window: installed mods, hot mods, asset replacement, game modes, tools.</summary>
    internal static class ApiTab
    {
        private static int _keepEdit;
        private static readonly float[] Scales = { 0f, 1f, 1.25f, 1.5f, 1.75f, 2f };
        private static readonly string[] ScaleNames = { "Automatic (by screen height)", "1x", "1.25x", "1.5x", "1.75x", "2x" };

        internal static void Register()
        {
            ModMenu.AddTab("api", "Mods", Draw, 1000, "OUAK API");
        }

        private static void Draw()
        {
            ImUi.Title($"OUAK Modding API {ApiCore.Version}");
            ImUi.Hint("The shared base every OUAK mod builds on: these windows (Dear ImGui), hotkeys, game events, stages, game modes, " +
                      "saves and backups, screenshots, logging, asset replacement and hot mods. Guide for modders: ModMenuSource/README.md.");

            // ---- installed mods
            ImUi.Header("Installed mods");
            if (ImGui.BeginTable("mods", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Mod", ImGuiTableColumnFlags.WidthStretch, 3f);
                ImGui.TableSetupColumn("Version", ImGuiTableColumnFlags.WidthStretch, 1f);
                ImGui.TableSetupColumn("Uses the API", ImGuiTableColumnFlags.WidthStretch, 1.2f);
                ImGui.TableHeadersRow();
                foreach (var m in MelonMod.RegisteredMelons)
                {
                    bool usesApi = false;
                    try { usesApi = m == ApiCore.Instance || m.MelonAssembly.Assembly.GetReferencedAssemblies().Any(a => a.Name == "OUAKModAPI"); } catch { }
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(m.Info.Name);
                    ImUi.Tip($"by {m.Info.Author}");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(m.Info.Version);
                    ImGui.TableNextColumn();
                    if (usesApi) ImGui.TextColored(ImTheme.Ok, "yes");
                    else ImGui.TextDisabled("no");
                }
                ImGui.EndTable();
            }

            DrawUpdates();

            // ---- hot mods
            ImUi.Header($"Hot mods ({HotMods.Running.Count()} running)");
            ImUi.Hint("DLLs in UserData/OUAKModAPI/HotMods load without restarting the game and reload by themselves when you rebuild them.");
            bool auto = HotMods.AutoReload;
            if (ImGui.Checkbox("Reload a hot mod as soon as its DLL changes", ref auto)) HotMods.AutoReload = auto;
            switch (ImUi.Buttons("Load / reload changed", "Open the folder"))
            {
                case 0: ModTasks.Run(HotMods.Sync); break;
                case 1: ModTasks.Run(() => Open(HotMods.Folder)); break;
            }
            if (HotMods.Entries.Count == 0) ImGui.TextDisabled("No hot mods in the folder.");
            else if (ImGui.BeginTable("hot", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("file", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("buttons", ImGuiTableColumnFlags.WidthFixed, ImUi.Px(190f));
                foreach (var e in HotMods.Entries.ToList())
                {
                    var entry = e;
                    ImGui.PushID(e.File);
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextUnformatted(Path.GetFileName(e.File));
                    ImGui.SameLine();
                    if (e.Error != null) ImGui.TextColored(ImTheme.Err, e.Error);
                    else ImGui.TextDisabled($"{string.Join(", ", e.Mods.Select(m => m.Name))} · loaded {e.Loads}x");
                    ImGui.TableNextColumn();
                    if (ImGui.Button("Reload")) ModTasks.Run(() => HotMods.Reload(entry));
                    ImGui.SameLine();
                    if (ImGui.Button("Unload")) ModTasks.Run(() => HotMods.Unload(entry));
                    ImGui.PopID();
                }
                ImGui.EndTable();
            }

            // ---- logs
            ImUi.Header("Logs");
            ImUi.Hint("Session logs (on unless you turn them off): every game session gets a folder in UserData/OUAKModAPI/Logs/Sessions with " +
                      "all.log (everything, in order), errors.log (warnings, errors, crashes) and one file per mod / source: every mod's lines, " +
                      "the game's errors, scene changes and game events. They can hold player names and SteamIDs from online play.");
            bool logs = SessionLog.Enabled;
            if (ImGui.Checkbox("Write session logs", ref logs)) SessionLog.Enabled = logs;
            if (_keepEdit < 1) _keepEdit = SessionLog.Keep;
            ImUi.Field("Sessions kept");
            ImGui.SliderInt("##keep", ref _keepEdit, 1, 200);
            if (ImGui.IsItemDeactivatedAfterEdit()) SessionLog.Keep = _keepEdit;   // saved once, when let go
            if (SessionLog.Running)
                ImGui.TextDisabled($"This session: {Path.GetFileName(SessionLog.Folder)} · {SessionLog.Lines} lines, {SessionLog.Problems} warnings / errors");
            if (SessionLog.PreviousAbnormal != null)
                ImGui.TextColored(ImTheme.Err, $"The previous session ({SessionLog.PreviousAbnormal}) did not end normally (crash or closed from outside).");
            switch (ImUi.Buttons("Open this session's logs", "Open the logs folder"))
            {
                case 0: ModTasks.Run(() => Open(SessionLog.Folder ?? SessionLog.Root)); break;
                case 1: ModTasks.Run(() => Open(ModLog.Folder)); break;
            }
            bool verbose = ModLog.Verbose;
            if (ImGui.Checkbox("Show mods' debug messages in the console", ref verbose)) ModLog.Verbose = verbose;

            // ---- assets
            ImUi.Header("Textures and sounds");
            ImUi.Hint($"Put Textures/<name>.png and Audio/<name>.wav in UserData/OUAKModAPI/Assets to replace the game's. {AssetReplacer.TextureFiles} texture(s), " +
                      $"{AssetReplacer.AudioFiles} sound(s) ready; {AssetReplacer.TexturesReplaced} texture(s) replaced, {AssetReplacer.SoundsSwapped} sound swap(s) so far. {AssetReplacer.LastReport}");
            bool live = AssetReplacer.LiveReload;
            if (ImGui.Checkbox("Swap a file in as soon as it changes (live reload)", ref live)) AssetReplacer.LiveReload = live;
            switch (ImUi.Buttons("Reload files", "List names", "Export textures", "Open the folder"))
            {
                case 0: ModTasks.Run(AssetReplacer.ReloadFiles); break;
                case 1: ModTasks.Run(AssetReplacer.DumpNames); break;
                case 2: ModTasks.Run(() => AssetReplacer.ExportTextures()); break;
                case 3: ModTasks.Run(() => Open(AssetReplacer.Folder)); break;
            }

            // ---- patches (always part of the API; each acts only in its own context)
            ImUi.Header("Game patches");
            ImUi.Hint("Fixes for broken or unfinished parts of the game. Each one only acts in its own context (the Target mode, a battle freebie " +
                      "used in a story stage...), which only exists through mods, so the game itself plays as before.");
            foreach (var p in GamePatches.All)
            {
                ImGui.PushID(p.Id);
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(p.Name);
                ImGui.SameLine();
                if (p.Ready) ImGui.TextColored(ImTheme.Ok, "ready");
                else ImGui.TextColored(ImTheme.Err, "unavailable: " + p.Error);
                ImUi.Hint(p.Description);
                ImGui.PopID();
            }

            // ---- game modes
            ImUi.Header(GameModes.Active != null ? $"Game modes (playing: {GameModes.Active.Name})" : "Game modes");
            foreach (var mode in GameModes.All.ToList())
            {
                string why = null;
                try { why = mode.Unavailable; } catch (Exception e) { why = e.Message; }
                var m = mode;
                ImGui.PushID(mode.Name);
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(mode.Name);
                if (mode.Author.Length > 0) { ImGui.SameLine(); ImGui.TextDisabled("· " + mode.Author); }
                ImUi.RightAlign(ImUi.Px(120f));
                if (ImUi.Button(m.Active ? "Playing" : "Start", why == null, ImUi.Px(120f))) ModTasks.Run(() => GameModes.Start(m), toast: true);
                ImUi.Hint(why != null ? $"{mode.Description} ({why})" : mode.Description);
                ImGui.PopID();
            }
            if (GameModes.Active != null && ImUi.Button("Stop the game mode (the stage carries on)")) ModTasks.Run(() => { GameModes.Stop(); return "Game mode stopped."; });

            // ---- tools and settings
            ImUi.Header("Screenshots, Steam and window");
            switch (ImUi.Buttons($"Take a screenshot ({KeyName("Screenshot")})", "Open the screenshots folder"))
            {
                case 0: ModTasks.Run(Screenshots.Take); break;
                case 1: ModTasks.Run(() => Open(Screenshots.Folder)); break;
            }
            if (Screenshots.LastPath != null) ImGui.TextDisabled("Last: " + Screenshots.LastPath);
            ImGui.TextDisabled(Steam.Available ? $"Steam: signed in as {Steam.MyName}." : "Steam: not available (SteamIDs and verification are off).");
            int si = Math.Max(0, Array.IndexOf(Scales, ModMenu.UiScaleSetting));
            ImUi.Field("Window scale");
            if (ImGui.Combo("##scale", ref si, ScaleNames, ScaleNames.Length)) ModMenu.UiScaleSetting = Scales[si];
            bool block = ModMenu.BlockGameInput;
            if (ImGui.Checkbox("Lock the game's controls while this window is open", ref block)) ModMenu.BlockGameInput = block;
            ImGui.TextDisabled($"Dear ImGui {ImGui.GetVersion()} · {ImGuiHost.ShaderName} · {ImGuiHost.LastVertices} vertices last frame");
        }

        // ---- updates: the OUAK Updater plugin runs before the mods load; this shows what it did at this launch.
        private static void DrawUpdates()
        {
            ImUi.Header("Updates");
            if (!Updates.Installed)
            {
                ImUi.Hint("Automatic updates need the OUAK Updater (Plugins/OUAKUpdater.dll), which comes with every OUAK release.");
                return;
            }
            ImUi.Hint("At every launch, before the mods load, the OUAK Updater installs the newest release of the OUAK mods you have " +
                      "installed (from GitHub" + (Updates.Repository != null ? $": {Updates.Repository}" : "") + "). A mod you built yourself " +
                      "with a newer version is never replaced. Old files are kept in UserData/OUAKUpdater/Backup.");
            bool on = Updates.Enabled;
            if (ImGui.Checkbox("Update automatically (from the next launch)", ref on)) Updates.Enabled = on;

            var s = Updates.Status;
            if (s == null)
            {
                ImGui.TextDisabled("The updater hasn't run yet (it runs at the next launch).");
                return;
            }
            string when = string.IsNullOrEmpty(s.CheckedAt) ? "" : $" ({s.CheckedAt})";
            switch (s.Result)
            {
                case "up-to-date":
                case "updated":
                    ImUi.Ok(s.Message + when);
                    break;
                case "error":
                case "partly-updated":
                    ImUi.Err(s.Message + when);
                    break;
                default:
                    ImGui.TextDisabled(s.Message + when);
                    break;
            }
            // TextUnformatted / ImUi.Wrapped: these strings come from GitHub and may hold '%'.
            foreach (var u in s.Updated) { ImGui.Bullet(); ImGui.SameLine(); ImGui.TextUnformatted(u); }
            foreach (var k in s.Skipped) { ImGui.Bullet(); ImGui.SameLine(); ImUi.Hint(k); }
            if (!string.IsNullOrWhiteSpace(s.ReleaseNotes) && ImGui.TreeNode("updnotes", $"What's new in {s.Release}".Replace("%", "%%")))
            {
                ImUi.Wrapped(s.ReleaseNotes, ImTheme.Dim);
                ImGui.TreePop();
            }
            switch (ImUi.Buttons("Open the release page", "Open the backups folder"))
            {
                case 0: ModTasks.Run(() => Updates.OpenUrl(s.ReleaseUrl)); break;
                case 1: ModTasks.Run(() => Open(Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "OUAKUpdater", "Backup"))); break;
            }
        }

        private static string KeyName(string id)
        {
            var a = Hotkeys.Actions.FirstOrDefault(h => h.Id == id);
            return a == null || a.Binding.IsNone ? "no key" : a.Binding.Display;
        }

        private static string Open(string folder)
        {
            Directory.CreateDirectory(folder);
            BackupManager.OpenFolder(folder);
            return "Opened " + folder;
        }
    }
}
