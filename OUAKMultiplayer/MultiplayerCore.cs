using System;
using MelonLoader;
using OUAKModAPI;

[assembly: MelonInfo(typeof(OUAKMultiplayer.MultiplayerCore), "OUAK Multiplayer", OUAKMultiplayer.MultiplayerCore.Version, "OUAKModMenu")]
[assembly: MelonGame("Bandai Namco Entertainment Inc.", "Once Upon A KATAMARI")]
[assembly: MelonAdditionalDependencies("OUAKModAPI")]

namespace OUAKMultiplayer
{
    /// <summary>
    /// OUAK Multiplayer: play together over the mod's own connection (never the game's online service). Ghost
    /// katamaris and cousins, shared pickups, stages started and raced together, votes, chat, voice, and the
    /// host's authority over restarts and returns to the plaza. Its page is the Online tab of the mod window. It also has
    /// the Katamari Ball tab: the game's Sports Katamari offline against CPU princes, with custom rules, King Chances and
    /// Target points (through the API's patches).
    /// </summary>
    public sealed class MultiplayerCore : MelonMod
    {
        /// <summary>Sent to the host when joining (a different version still joins, with a note).</summary>
        public const string Version = "2.2.0";

        private readonly OnlinePage _page = new();
        private readonly KatamariBallPage _ballPage = new();

        public override void OnInitializeMelon()
        {
            NetSession.Init(ApiCore.Category("OUAKModMenu_Online", "OUAK Mod Menu online prototype"));
            Ghosts.Init(HarmonyInstance);
            SceneSwitchGuard.Init(HarmonyInstance);
            HostAuthority.Init();
            VersusGate.Init(HarmonyInstance);
            SceneLoader.Interceptors.Add(StageGate.Intercept);
            ModGui.AddOverlay(Ghosts.OnGUI, 0, "OUAK Multiplayer");
            HudWindows.Init();
            Multiplayer.Current = new Bridge();
            _page.Register();
            // Katamari Ball offline (moved here from the Mod Menu): custom match rules, King Chances, Target points, the tab, its test.
            var match = ApiCore.Category("OUAKModMenu_Match", "OUAK Multiplayer: Katamari Ball");
            CustomMatch.Init(match, HarmonyInstance);
            MatchExtras.Init(match);
            _ballPage.Register();
            TrainingTest.CheckFlag();
            LoggerInstance.Msg($"Loaded (protocol v{Proto.Version}). Host or join from the Online tab ({Hotkeys.MenuKeyName}).");
        }

        public override void OnUpdate()
        {
            try { NetSession.Tick(); Ghosts.Tick(); HostAuthority.Tick(); HudWindows.Tick(); }
            catch (Exception e) { LoggerInstance.Warning($"Online tick failed: {e.Message}"); }
            try { CustomMatch.Tick(); MatchExtras.Tick(); }
            catch (Exception e) { LoggerInstance.Warning($"Katamari Ball tick failed: {e.Message}"); }
            TrainingTest.Tick();
        }

        public override void OnApplicationQuit() => NetSession.Leave(null);

        /// <summary>What other mods see of the session (OUAKModAPI.Multiplayer).</summary>
        private sealed class Bridge : IMultiplayer
        {
            public bool InSession => NetSession.Connected;
            public bool IsHost => NetSession.Mode == NetSession.Role.Host;
            public byte MyId => NetSession.MyId;
            public System.Collections.Generic.IReadOnlyList<SessionPlayer> Players => NetSession.SessionPlayers();
            public void SendModMessage(string channel, byte[] data, byte to = 255) => NetSession.SendModData(channel, data, to);
        }
    }
}
