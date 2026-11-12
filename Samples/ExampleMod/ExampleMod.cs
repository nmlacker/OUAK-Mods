using System;
using ImGuiNET;
using Il2CppApp.KatamariSin;
using MelonLoader;
using OUAKModAPI;
using UnityEngine;

[assembly: MelonInfo(typeof(ExampleMod.ExampleMod), "OUAK Example Mod", "1.0.0", "you")]
[assembly: MelonGame("Bandai Namco Entertainment Inc.", "Once Upon A KATAMARI")]
[assembly: MelonAdditionalDependencies("OUAKModAPI")]

namespace ExampleMod
{
    /// <summary>
    /// A complete mod built on the OUAK Modding API: a page in the mod window and a window of its own (Dear ImGui), a hotkey, game events, the API's game patches, a hook on
    /// a game function, a game mode of its own, a message shared with other players, logging and screenshots.
    /// Copy this project to start yours.
    /// </summary>
    public sealed class ExampleMod : MelonMod
    {
        private static readonly ModLog Log = ModLog.For("Example Mod");
        private static readonly HookSet Hooks = new("you.examplemod");
        private int _restarts;
        private ModWindow _hud;

        public override void OnInitializeMelon()
        {
            // Settings and key bindings live in MelonPreferences (UserData/MelonPreferences.cfg).
            var cat = ApiCore.Category("ExampleMod", "OUAK Example Mod");

            // A page in the mod window (Right Shift).
            ModMenu.AddTab("example", "Example", DrawPage, order: 500, owner: "Example Mod");
            _hud = ModWindows.Add("example.hud", DrawHud, open: false, takesControl: false);

            // A rebindable hotkey (listed in the Keys tab).
            Hotkeys.Register(cat, "Shout", "Example: say how big the katamari is", "Ctrl+K",
                () => Katamari.Exists ? $"The katamari is {Katamari.SizeText}!" : "Roll into a stage first.", "Example Mod");

            // Game events.
            GameEvents.StagePlaying += gm => Log.Info($"Rolling! Time limit {Katamari.TimeLimit:0} s, size {Katamari.SizeText}.");
            GameEvents.StageEnded += (gm, cleared) => Log.Info(cleared ? "Stage cleared." : "Stage over.");

            // A hook on a game function: count restarts (keep hook bodies small and never let them throw).
            Hooks.Postfix(typeof(MainGameManager), nameof(MainGameManager.RestartStage), typeof(ExampleMod), nameof(AfterRestart));
            _instance = this;

            // A game mode of its own (listed in the Mods tab with a Start button).
            GameModes.Register(new HalfTimeMode());

            // Talking to the same mod on other players' games (needs OUAK Multiplayer and a session).
            Multiplayer.Received += (from, channel, data) =>
            {
                if (channel == "example") ModTasks.Toast($"Player {from + 1} says hi from the example mod!");
            };

            Log.Info("Example mod loaded.");
        }

        private static ExampleMod _instance;

        private static void AfterRestart()
        {
            try { if (_instance != null) _instance._restarts++; }
            catch { }
        }

        private void DrawPage()
        {
            // The page is plain Dear ImGui (ImGui.NET), plus the API's small ImUi helpers.
            ImUi.Title("OUAK Example Mod");
            ImUi.Hint("This page comes from Samples/ExampleMod: copy it to start your own mod.");
            ImGui.TextUnformatted(Katamari.Exists
                ? $"Katamari: {Katamari.SizeText}, {Katamari.Elapsed:0} s played, freebie {StageControl.Pretty(Katamari.Freebie)}"
                : "Not in a stage.");
            ImGui.TextUnformatted($"Restarts seen by the hook: {_restarts}");
            if (ImUi.Button("Give a random freebie", Katamari.Exists))
                ModTasks.Run(() => StageControl.GiveFreebie(eInstageItemType.RandomItem));
            if (ImUi.Button("Take a screenshot"))
                ModTasks.Run(Screenshots.Take);
            if (ImUi.Button(Multiplayer.InSession ? "Say hi to the other players" : "Say hi (join a session first)", Multiplayer.InSession))
                ModTasks.Run(() => Multiplayer.Send("example", new byte[] { 1 }) ? "Said hi." : "Not in a session.");
            ImGui.Checkbox("Show a big size readout while rolling (a window of its own)", ref _hud.Open);

            // The API's patches: game features it fixed, ready to use (README: "Patches for broken and unused features").
            ImUi.Header("Using the game patches");
            if (ImUi.Button("Story stage: give Shoes (the API makes them work: 10 s of unlimited dash)", GameRefs.InStage))
                ModTasks.Run(() => StageControl.GiveFreebie(eInstageItemType.Shoes));
            if (ImUi.Button("Battle: fire the match's next King Chance", KingChances.InOfflineMatch && !KingChances.Pending))
                ModTasks.Run(() => $"King Chance: {KingChances.FireNext()}");
            if (TargetRound.InRound && Katamari.Exists)
                ImGui.TextUnformatted($"Target mode: {TargetRound.Count(Katamari.Core)} crane(s), {TargetRound.PointsGiven} points given so far");
            if (ImUi.Button("Start the Target mode (cranes are worth points)", GameRefs.InGameSession))
                ModTasks.Run(() => GameModes.Start("target"));
        }

        // A window of its own that only shows information: no pointer, no locked controls, clicks pass through.
        private void DrawHud()
        {
            if (!Katamari.Playing) return;
            ImGui.SetNextWindowPos(new System.Numerics.Vector2(ImUi.Px(20f), ImGui.GetMainViewport().Size.Y - ImUi.Px(90f)));
            ImGui.SetNextWindowBgAlpha(0.5f);
            if (ImGui.Begin("##example.hud", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoNav))
            {
                ImGui.SetWindowFontScale(2f);
                ImUi.Title(Katamari.SizeText);
            }
            ImGui.End();
        }
    }

    /// <summary>A game mode: the first story stage you start gets half its time limit.</summary>
    public sealed class HalfTimeMode : GameMode
    {
        public override string Id => "example.halftime";
        public override string Name => "Half Time";
        public override string Description => "Pick any stage from a plaza after starting this: you get half the usual time.";
        public override string Author => "Example Mod";

        public override string Start() => "Half Time is on: start a stage within a minute.";

        // Only story stages (not multiplayer maps) count.
        public override bool Owns(MainGameManager gm) => !GameRefs.IsMultiplay;

        public override void OnPlaying(MainGameManager gm)
        {
            if (Katamari.ClockRule != 0) return;   // only stages with a time limit
            Katamari.TimeLimit = Mathf.Max(30f, Katamari.TimeLimit * 0.5f);
            ModTasks.Toast($"Half Time: {Katamari.TimeLimit:0} seconds!");
        }
    }
}
