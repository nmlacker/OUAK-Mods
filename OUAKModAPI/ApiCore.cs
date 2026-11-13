using System;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(OUAKModAPI.ApiCore), "OUAK Modding API", OUAKModAPI.ApiCore.Version, "OUAK modders")]
[assembly: MelonGame("Bandai Namco Entertainment Inc.", "Once Upon A KATAMARI")]
// Loads before every mod that uses it.
[assembly: MelonPriority(-1000)]

namespace OUAKModAPI
{
    /// <summary>
    /// The OUAK Modding API's own MelonLoader mod. It runs everything the API offers on the main thread and in a
    /// fixed order: input and hotkeys, queued actions (<see cref="ModTasks"/>), game events, game modes, hot mods
    /// and asset replacement during Update; overlays, the mod window, prompts and frame captures during OnGUI.
    /// Mods never need to call into this class; they use the static API classes.
    /// </summary>
    public sealed class ApiCore : MelonMod
    {
        public const string Version = "3.0.0";

        internal static ApiCore Instance { get; private set; }
        internal static readonly ModLog Log = ModLog.For("OUAK API");

        /// <summary>The API's own MelonPreferences category.</summary>
        internal static MelonPreferences_Category Prefs { get; private set; }

        public override void OnInitializeMelon()
        {
            Instance = this;
            Prefs = Category("OUAKModAPI", "OUAK Modding API");
            // First, so everything after it is in the session logs (on by default; the user can turn them off).
            Safe("session logs", () => SessionLog.Init(Prefs));
            // Silent automatic updates from the GitHub releases (background; take effect at the next launch).
            Safe("updates", () => Updater.Init(Prefs));
            // The window's settings and keys keep the categories they had when the menu was one mod.
            var legacy = Category("OUAKModMenu", "OUAK Mod Menu");
            var keys = Category("OUAKModMenu_Keys", "OUAK Mod Menu key bindings");
            ModMenu.Init(legacy);
            ImGuiHost.Init();
            Hotkeys.Init(keys, ModMenu.LegacyToggleKey);
            Screenshots.Init(keys);
            Safe("training rules", () => TrainingRules.Init(HarmonyInstance));
            Safe("pause menu hooks", () => PauseMenu.Init(HarmonyInstance));
            Safe("cursor control", () => CursorControl.Init(HarmonyInstance, Prefs));
            Safe("controller bindings", () => PadBindings.Init(HarmonyInstance, Category("OUAKModAPI_Pad", "OUAK controller bindings")));
            Safe("game modes", GameModes.Init);
            // Patches of broken/unused game features: all off until a mod enables them (GamePatches).
            Safe("patches", GamePatches.Init);
            Safe("asset replacement", () => AssetReplacer.Init(Prefs));
            Safe("hot mods", () => HotMods.Init(Prefs));
            ApiTab.Register();
            ApiSelfTest.CheckFlag();
            PatchTest.CheckFlag();
            InputProbe.CheckFlag();
            InputTest.CheckFlag();
            Log.Info($"OUAK Modding API {Version} ready. Press {Hotkeys.MenuKeyName} for the mod window.");
        }

        /// <summary>Every mod has been loaded and initialized (the session log lists them).</summary>
        internal static bool ModsListed { get; private set; }

        public override void OnLateInitializeMelon()
        {
            ModsListed = true;
            SessionLog.WriteMods();
        }

        /// <summary>The category with that identifier, created if needed (a category can be shared between mods).</summary>
        public static MelonPreferences_Category Category(string id, string display)
        {
            var cat = MelonPreferences.GetCategory(id);
            return cat ?? MelonPreferences.CreateCategory(id, display);
        }

        private static void Safe(string what, Action a)
        {
            try { a(); }
            catch (Exception e) { Log.Error($"Couldn't start {what}: {e}"); }
        }

        public override void OnUpdate()
        {
            Hotkeys.Update();
            ModMenu.Update();
            Prompt.Update();
            ModWindows.Update();
            ImGuiHost.HoldCursor(ModMenu.IsOpen || Prompt.Active || ModWindows.AnyTakesControl);
            ModTasks.Tick();
            Tick("training trace", TrainingTrace.Tick);
            Tick("game events", GameEvents.Tick);
            Tick("game modes", GameModes.Tick);
            Tick("hot mods", HotMods.Tick);
            Tick("assets", AssetReplacer.Tick);
            Tick("bundles", Bundles.Tick);
            Tick("game sprites", GameSprites.Tick);
            Tick("stage clock", StageControl.ApplyClock);
            GameEvents.RaiseUpdate();
            ApiSelfTest.Tick();
            PatchTest.Tick();
        }

        public override void OnLateUpdate()
        {
            GameEvents.RaiseLateUpdate();
            Tick("game input", GameInput.Apply);
            Tick("cursor", CursorControl.LateTick);
        }

        private static readonly System.Collections.Generic.HashSet<string> _failedTicks = new();

        private static void Tick(string what, Action a)
        {
            try { a(); }
            catch (Exception e)
            {
                // Once per kind, so a broken feature can't flood the log every frame.
                if (_failedTicks.Add(what)) Log.Warn($"{what} failed: {e.Message}");
            }
        }

        public override void OnGUI()
        {
            var oldMatrix = GUI.matrix;
            var ev = Event.current;
            // Clicks and scrolling on a Dear ImGui window don't reach the IMGUI overlays under it.
            if (ev != null && ImGuiHost.WantsMouse && ev.type is EventType.MouseDown or EventType.MouseUp or EventType.MouseDrag or EventType.ScrollWheel)
                ev.Use();
            try
            {
                ModGui.DrawOverlays();
                HotMods.OnGUI();
                ImGuiPass(ev);
            }
            catch (Exception e)
            {
                ModTasks.SetStatus($"Window error: {e.Message}", true);
                Log.Error(e.ToString());
                ModMenu.Close();
            }
            finally
            {
                GUI.matrix = oldMatrix;
                GUI.enabled = true;
            }
            // Pictures of the game's frame are read last, with everything drawn.
            FrameCapture.AfterGui();
        }

        // The Dear ImGui windows: mods' own, the mod window (or its toast) and the prompt, one frame per Repaint,
        // and nothing at all while none of them is up.
        private static void ImGuiPass(Event e)
        {
            if (!ImGuiHost.Ready || e == null) return;
            ImGuiHost.OnGuiEvent(e);
            if (e.type != EventType.Repaint) return;
            bool any = ModMenu.IsOpen || Prompt.Active || ModWindows.AnyOpen || ModTasks.CurrentToast(out _) != null;
            if (!any) { ImGuiHost.Idle(); return; }
            // A controller moves through the windows only while they have the game's controls.
            bool pad = !Prompt.Active && ((ModMenu.IsOpen && ModMenu.BlockGameInput) || ModWindows.AnyTakesControl);
            ImGuiHost.Frame(DrawWindows, pad);
        }

        private static void DrawWindows()
        {
            ModWindows.DrawAll();
            ModMenu.Draw();
            Prompt.Draw();
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            SessionLog.Scene("loaded", sceneName, buildIndex);
            GameRefs.SceneName = sceneName;
            GameEvents.RaiseSceneLoaded(sceneName);
            Tick("assets", () => AssetReplacer.OnSceneLoaded(sceneName));
            GameSprites.OnSceneLoaded();
        }

        public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
        {
            if (!string.IsNullOrEmpty(sceneName)) SessionLog.Scene("unloaded", sceneName, buildIndex);
        }

        public override void OnApplicationQuit()
        {
            GameEvents.RaiseQuit();
            Log.Info(CursorControl.Describe());
            HotMods.UnloadAll();
            ModLog.CloseAll();
            SessionLog.Quit();
        }
    }
}
