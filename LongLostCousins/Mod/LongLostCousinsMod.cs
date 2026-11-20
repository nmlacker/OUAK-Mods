using System;
using LongLostCousins.Dressing;
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
    /// from the mod's own data. Slice one has Burda, in the new OUAK style or the old Katamari Forever style.
    /// Off until a cousin is picked in the mod window's Long Lost Cousins tab: while off, nothing of the game is hooked
    /// and nothing runs each frame.
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
                if (Settings.Active) Hooks.Install();
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
            if (!_ready || (!Settings.Active && Dresser.Models.Count == 0 && !CousinTest.Running)) return;
            try { Contexts.LateTick(); }
            catch (Exception e) { Log.Debug("contexts: " + e.Message); }
            try { Dresser.LateTick(); }
            catch (Exception e) { Log.Debug("dressed models: " + e.Message); }
            CousinTest.Tick();
        }
    }
}
