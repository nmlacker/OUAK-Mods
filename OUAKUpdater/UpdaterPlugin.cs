using System;
using MelonLoader;

[assembly: MelonInfo(typeof(OUAKUpdater.UpdaterPlugin), "OUAK Updater", OUAKUpdater.UpdaterPlugin.Version, "OUAK mods")]
[assembly: MelonGame("Bandai Namco Entertainment Inc.", "Once Upon A KATAMARI")]

namespace OUAKUpdater
{
    /// <summary>
    /// Keeps the OUAK mods up to date. Runs once per launch, before MelonLoader loads anything from Mods/, so the game
    /// always starts on the newest release: it reads the latest GitHub release of the OUAK mods, and every installed
    /// OUAK mod with a newer version there is replaced (checked against the release's SHA-256, old DLLs backed up).
    /// Settings: [OUAKUpdater] in UserData/MelonPreferences.cfg. What it did: UserData/OUAKUpdater/status.json, shown on the
    /// API's Mods tab.
    /// </summary>
    public sealed class UpdaterPlugin : MelonPlugin
    {
        public const string Version = "1.0.0";

        /// <summary>Where releases come from until the user changes it (owner/repository on GitHub).</summary>
        public const string DefaultRepository = "nmlacker/OUAK-Mods";

        public override void OnPreModsLoaded()
        {
            MelonPreferences_Entry<bool> enabled;
            MelonPreferences_Entry<string> repo;
            try
            {
                var cat = MelonPreferences.GetCategory("OUAKUpdater") ?? MelonPreferences.CreateCategory("OUAKUpdater", "OUAK Updater");
                enabled = cat.GetEntry<bool>("Enabled") ?? cat.CreateEntry("Enabled", true, "Update automatically",
                    "Before the mods load, download and install newer releases of the installed OUAK mods.");
                repo = cat.GetEntry<string>("Repository") ?? cat.CreateEntry("Repository", DefaultRepository, "GitHub repository",
                    "owner/name of the GitHub repository whose releases hold the OUAK mods.");
                MelonPreferences.Save();
            }
            catch (Exception e)
            {
                LoggerInstance.Error($"Couldn't read the settings: {e.Message}");
                return;
            }

            try
            {
                new Updater(LoggerInstance, repo.Value, enabled.Value).Run();
            }
            catch (Exception e)
            {
                // Never stop the game from starting: the mods already installed just load as they are.
                LoggerInstance.Error($"Update failed, starting with the installed mods: {e}");
            }
        }
    }
}
