using System;
using LongLostCousins.Dressing;
using LongLostCousins.Select;
using LongLostCousins.Test;
using MelonLoader;
using OUAKModAPI;

[assembly: MelonInfo(typeof(LongLostCousins.LongLostCousinsMod), "Long Lost Cousins", "0.1.0", "OUAK mods")]
[assembly: MelonGame("Bandai Namco Entertainment Inc.", "Once Upon A KATAMARI")]
[assembly: MelonAdditionalDependencies("OUAKModAPI")]

namespace LongLostCousins
{
    /// <summary>
    /// Long Lost Cousins: dresses the player's own cousin model as a cousin the game never shipped, built at run time
    /// from the mod's own data. It has Burda, in the new OUAK style or the old Katamari Forever style. She is one more
    /// cousin on the game's own select screen (page 4), and can also be picked in the mod window's Long Lost Cousins tab.
    /// The select screen's slot hooks are always in place (they add her slot and keep her id out of the save); its marker
    /// hooks exist only while the settings name her, and the dressing hooks and the per-frame work only while she is worn.
    /// </summary>
    public sealed class LongLostCousinsMod : MelonMod
    {
        internal static readonly ModLog Log = ModLog.For("Long Lost Cousins");
        private static bool _ready;

        public override void OnInitializeMelon()
        {
            try
            {
                Settings.Load();
                bool testing = CousinTest.CheckFlag();
                CousinsTab.Init();
                SelectScreen.Install();
                Chosen.Refresh();
                _ready = true;
                Log.Info($"Long Lost Cousins loaded: {Settings.Describe()}{(testing ? " (cousin test)" : "")}");
            }
            catch (Exception e)
            {
                Log.Error($"could not start: {e.GetType().Name}: {e.Message}");
                // A test run still has to report and quit: its checks will show what is missing.
                if (CousinTest.Running) _ready = true;
            }
        }

        public override void OnLateUpdate()
        {
            // No game method is on the stack here: the select screen's counters go back to zero and its marker hooks follow the settings.
            SelectScreen.LateTick();
            if (!_ready || (!Settings.Active && !Hooks.Installed && Dresser.Models.Count == 0 && !CousinTest.Running)) return;
            Chosen.Sync();
            try { Contexts.LateTick(); }
            catch (Exception e) { Log.Debug("contexts: " + e.Message); }
            try { Dresser.LateTick(); }
            catch (Exception e) { Log.Debug("dressed models: " + e.Message); }
            CousinTest.Tick();
        }
    }
}
