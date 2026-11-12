using ImGuiNET;
using OUAKModAPI;
using UnityEngine;

namespace ExampleHotMod
{
    /// <summary>
    /// A hot mod: build this project while the game runs and the API swaps the new build in by itself (the old
    /// one's tab, hotkey and hooks are removed first). Change the text below, build, and look at the "Hot mod" tab.
    /// </summary>
    public sealed class ExampleHotMod : HotMod
    {
        private int _clicks;
        private float _loadedAt;

        public override void OnLoad()
        {
            _loadedAt = Time.unscaledTime;
            AddTab("Hot mod", Draw);
            AddHotkey("Hello", "Hot mod example: say hello", "None", () => "Hello from the hot mod!");
            GameEvents.StagePlaying += OnPlaying;
            Track(() => GameEvents.StagePlaying -= OnPlaying);
            Log.Info("Example hot mod loaded.");
        }

        public override void OnUnload() => Log.Info($"Example hot mod unloaded after {_clicks} click(s).");

        private void OnPlaying(Il2CppApp.KatamariSin.MainGameManager gm) => Log.Info("A stage started (seen by the hot mod).");

        private void Draw()
        {
            ImUi.Title("Example hot mod");
            ImUi.Hint("Edit Samples/ExampleHotMod/ExampleHotMod.cs and build it while the game runs: this page reloads by itself.");
            ImGui.TextUnformatted($"Loaded {Time.unscaledTime - _loadedAt:0} s ago.");
            if (ImGui.Button($"Clicked {_clicks} time(s)")) _clicks++;
        }
    }
}
