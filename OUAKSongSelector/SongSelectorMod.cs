using System;
using System.IO;
using MelonLoader;
using MelonLoader.Utils;
using OUAKModAPI;

[assembly: MelonInfo(typeof(OUAKSongSelector.SongSelectorMod), "OUAK Song Selector", "1.0.0", "OUAK mods")]
[assembly: MelonGame("Bandai Namco Entertainment Inc.", "Once Upon A KATAMARI")]
[assembly: MelonAdditionalDependencies("OUAKModAPI")]

namespace OUAKSongSelector
{
    /// <summary>
    /// OUAK Song Selector: named playlists of the game's own music and your own WAV files, for story stages and the
    /// Plaza, managed from the "Songs" tab of the mod window (Right Shift).
    /// Files: UserData/OUAKSongSelector/playlists.json (playlists and where they play), UserData/OUAKSongSelector/Music (your WAVs).
    /// </summary>
    public sealed class SongSelectorMod : MelonMod
    {
        internal static readonly ModLog Log = ModLog.For("Song Selector");
        internal static string Dir => Path.Combine(MelonEnvironment.UserDataDirectory, "OUAKSongSelector");

        public override void OnInitializeMelon()
        {
            Directory.CreateDirectory(Dir);
            Store.Load();
            Log.Info(Catalog.RescanMusic());
            Player.Init();

            var cat = ApiCore.Category("OUAKSongSelector", "OUAK Song Selector");
            Hotkeys.Register(cat, "NextSong", "Next song in the playlist", "None", Player.Next, "OUAK Song Selector");
            Hotkeys.Register(cat, "PreviousSong", "Previous song in the playlist", "None", Player.Previous, "OUAK Song Selector");
            Hotkeys.Register(cat, "PlayPause", "Play / pause the playlist", "None", Player.TogglePlayPause, "OUAK Song Selector");
            Hotkeys.Register(cat, "LoopPlaylist", "Loop the playlist on / off", "None", Player.ToggleLoop, "OUAK Song Selector");
            Hotkeys.Register(cat, "LoopSong", "Loop this song on / off", "None", Player.ToggleLoopOne, "OUAK Song Selector");
            Hotkeys.Register(cat, "Shuffle", "Shuffle on / off", "None", Player.ToggleShuffle, "OUAK Song Selector");

            ModMenu.AddTab("songs", "Songs", SongsTab.Draw, order: 650, owner: "OUAK Song Selector");
            GameEvents.Update += Player.Tick;
            GameEvents.Update += SongTest.Tick;
            GameEvents.Quit += Library.Stop;
            SongTest.CheckFlag();
            Log.Info("Song Selector loaded" + (Store.LastError != "" ? ": " + Store.LastError : "."));
        }
    }
}
