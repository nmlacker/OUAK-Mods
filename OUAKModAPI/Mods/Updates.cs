using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MelonLoader;
using MelonLoader.Utils;

namespace OUAKModAPI
{
    /// <summary>
    /// The OUAK mods' auto-updater, as seen from the API. The updater itself is a MelonLoader plugin
    /// (<c>Plugins/OUAKUpdater.dll</c>), because it has to replace the mods' DLLs before they're loaded: at every launch,
    /// before any mod loads, it installs the latest GitHub release of the installed OUAK mods. This class reads what it
    /// did (<c>UserData/OUAKUpdater/status.json</c>) and its settings (<c>[OUAKUpdater]</c>) for the Mods tab.
    /// </summary>
    public static class Updates
    {
        /// <summary>The updater plugin's file.</summary>
        public static string PluginPath => Path.Combine(MelonEnvironment.UserDataDirectory, "..", "Plugins", "OUAKUpdater.dll");

        /// <summary>What the updater wrote at this launch.</summary>
        public static string StatusPath => Path.Combine(MelonEnvironment.UserDataDirectory, "OUAKUpdater", "status.json");

        /// <summary>The updater plugin is installed.</summary>
        public static bool Installed => File.Exists(PluginPath);

        /// <summary>Automatic updates on (<c>[OUAKUpdater] Enabled</c>). Takes effect at the next launch.</summary>
        public static bool Enabled
        {
            get => Entry<bool>("Enabled")?.Value ?? false;
            set
            {
                var e = Entry<bool>("Enabled");
                if (e == null || e.Value == value) return;
                e.Value = value;
                MelonPreferences.Save();
            }
        }

        /// <summary>owner/name of the GitHub repository the releases come from.</summary>
        public static string Repository => Entry<string>("Repository")?.Value;

        // The plugin creates the category before any mod loads.
        private static MelonPreferences_Entry<T> Entry<T>(string id) => MelonPreferences.GetCategory("OUAKUpdater")?.GetEntry<T>(id);

        /// <summary>The result of this launch's check (null when the updater didn't run).</summary>
        public static UpdateStatus Status
        {
            get
            {
                // Read once per few seconds at most: the tab draws every frame.
                if (_status == null || (DateTime.UtcNow - _readAt).TotalSeconds > 5) Reload();
                return _status;
            }
        }

        private static UpdateStatus _status;
        private static DateTime _readAt;

        private static void Reload()
        {
            _readAt = DateTime.UtcNow;
            try
            {
                if (!File.Exists(StatusPath)) { _status = null; return; }
                var s = JsonSerializer.Deserialize<UpdateStatus>(File.ReadAllText(StatusPath));
                _status = s;
            }
            catch (Exception e)
            {
                _status = new UpdateStatus { Result = "error", Message = $"Couldn't read {StatusPath}: {e.Message}" };
            }
        }

        /// <summary>Opens a web page in the default browser.</summary>
        public static string OpenUrl(string url)
        {
            if (string.IsNullOrEmpty(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("No web page to open.");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
            return "Opened " + url;
        }
    }

    /// <summary>One check by the updater (the JSON it writes).</summary>
    public sealed class UpdateStatus
    {
        public string CheckedAt { get; set; }
        public string Repository { get; set; }
        /// <summary>disabled, up-to-date, updated, partly-updated, no-release, offline or error.</summary>
        public string Result { get; set; }
        public string Message { get; set; }
        public string Release { get; set; }
        public string ReleaseUrl { get; set; }
        public string ReleaseNotes { get; set; }
        public List<string> Updated { get; set; } = new();
        public List<string> Skipped { get; set; } = new();
        public string UpdaterVersion { get; set; }
    }
}
