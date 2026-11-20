using System;
using LongLostCousins.Dressing;
using LongLostCousins.Select;
using OUAKModAPI;
using UnityEngine;

namespace LongLostCousins
{
    /// <summary>
    /// Whether the player wears a Long Lost Cousin now, and the dressing hooks that follow it.
    /// <para>Picked in the tab: she is worn whatever cousin the save holds (the mod dresses that cousin, as in 0.1.0).
    /// Picked on the game's select screen: the game's save holds the Prince (id 0) in her place, and she is worn only
    /// while it still does. Anything else that changes the saved cousin turns her off, so the mod never dresses a cousin
    /// the player picked afterwards.</para>
    /// </summary>
    internal static class Chosen
    {
        private static readonly ModLog Log = LongLostCousinsMod.Log.In("Chosen");
        private static bool _on;
        private static float _nextSync;

        /// <summary>The result of the last <see cref="Refresh"/> (cheap: for per-frame checks).</summary>
        public static bool On => _on;

        /// <summary>The cousin the save holds now (GlobalSaveData.GetSelectPlayerId, outside the select screen's scope), or -1.</summary>
        public static int SavedCousin()
        {
            var save = GameRefs.Save;
            if (save == null) return -1;
            using (SelectScreen.Suspended()) return save.GetSelectPlayerId;
        }

        /// <summary>Worn now, read live: the settings name a cousin, and one picked on the select screen still has the Prince in the save.</summary>
        public static bool Now
        {
            get
            {
                if (!Settings.Active) return false;
                if (!Settings.OnScreen) return true;
                try { return SavedCousin() == 0; }
                catch { return false; }
            }
        }

        /// <summary>Re-reads <see cref="Now"/> and installs or removes the dressing hooks when it changed. Never throws.</summary>
        public static void Refresh()
        {
            try
            {
                bool now = Now;
                if (now && !Hooks.Installed) Hooks.Install();
                else if (!now && Hooks.Installed) Hooks.Uninstall();
                if (now != _on) Log.Info(now ? "worn now: " + Settings.Describe() : "not worn now" + (Settings.OnScreen ? " (the save holds another cousin than the Prince)" : ""));
                _on = now;
            }
            catch (Exception e) { Log.Warn("refresh: " + e.Message); }
        }

        /// <summary>MelonMod.OnLateUpdate: twice a second while a cousin is in the settings or the hooks are in, so a save change is seen.</summary>
        public static void Sync()
        {
            if (!Settings.Active && !Hooks.Installed) return;
            float now = Time.unscaledTime;
            if (now < _nextSync) return;
            _nextSync = now + 0.5f;
            Refresh();
        }
    }
}
