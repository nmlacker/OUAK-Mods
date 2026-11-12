using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using MelonLoader;
using MelonLoader.Utils;

namespace OUAKModAPI
{
    /// <summary>
    /// Automatic updates (the user's choice, 2026-11-12: "Auto-install silently"). At every launch, in the background, the
    /// API asks GitHub for the newest release of <see cref="Repo"/>, downloads its <see cref="AssetName"/> and replaces
    /// every mod file in it that differs from the installed one. Windows keeps loaded mods locked, so a file is swapped
    /// by renaming the old one to .old (allowed while loaded) and the update takes effect at the next launch; the .old
    /// files are removed at the launch after. Only DLLs under Mods/ and UserLibs/ are taken from the zip, nothing else.
    /// <para>On a development machine (the game folder holds ModMenuSource) nothing is replaced, unless
    /// [OUAKModAPI] AutoUpdateOnDevMachine = true: the sessions' newer local builds must not be swapped for an older
    /// release. It only logs what a release would change. Off entirely with [OUAKModAPI] AutoUpdate = false.</para>
    /// Every step goes to the session logs (OUAK API/Update).
    /// <para>Once an update is installed, players must restart (the user, 2026-11-12: "prompt that there is an update, and
    /// force players to either restart their game or quit"): a prompt with only Restart now and Quit, which can't be
    /// dismissed (it comes back the next frame, also after another prompt replaced it).</para>
    /// </summary>
    public static class Updater
    {
        public const string Repo = "nmlacker/OUAK-Mods";   // public since 2026-11-12: source + releases; the website is nmlacker/OUAK-hub
        public const string AssetName = "OUAK_Mods.zip";

        /// <summary>What the last check did, for the mod window and the logs.</summary>
        public static string Status { get; private set; } = "not checked yet";
        /// <summary>Files replaced this launch (they take effect at the next one).</summary>
        public static IReadOnlyList<string> Replaced => _replaced;
        /// <summary>The release tag last installed (or seen, on a development machine).</summary>
        public static string LastRelease => _lastPref?.Value ?? "";

        private static readonly List<string> _replaced = new();
        private static MelonPreferences_Entry<bool> _onPref, _devPref;
        private static MelonPreferences_Entry<string> _lastPref;
        private static readonly ModLog Log = ApiCore.Log.In("Update");

        private static string Game => MelonEnvironment.GameRootDirectory;

        internal static void Init(MelonPreferences_Category prefs)
        {
            _onPref = prefs.GetEntry<bool>("AutoUpdate") ?? prefs.CreateEntry("AutoUpdate", true, "Automatic updates",
                $"At launch, download the newest OUAK mods release from github.com/{Repo} and install it silently (takes effect at the next launch).");
            _devPref = prefs.GetEntry<bool>("AutoUpdateOnDevMachine") ?? prefs.CreateEntry("AutoUpdateOnDevMachine", false, "Update a development install",
                "Also install updates when the game folder holds the mods' source (ModMenuSource). Off: only report.");
            _lastPref = prefs.GetEntry<string>("AutoUpdateLastRelease") ?? prefs.CreateEntry("AutoUpdateLastRelease", "", "Last release installed", "Set by the updater.");
            CleanOld();
            if (!_hooked) { _hooked = true; GameEvents.Update += Tick; }
            if (!_onPref.Value) { Status = "automatic updates are off"; Log.Info(Status); return; }
            Task.Run(CheckAsync);
        }

        // The .old copies the last update left behind (no longer loaded now).
        private static void CleanOld()
        {
            foreach (var dir in new[] { "Mods", "UserLibs" })
            {
                try
                {
                    var d = Path.Combine(Game, dir);
                    if (!Directory.Exists(d)) continue;
                    foreach (var f in Directory.GetFiles(d, "*.dll.ouak-old")) { try { File.Delete(f); } catch { } }
                }
                catch { }
            }
        }

        private static async Task CheckAsync()
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("OUAK-Modding-API/" + ApiCore.Version);
                http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                var json = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string tag = root.GetProperty("tag_name").GetString() ?? "";
                string url = null;
                foreach (var a in root.GetProperty("assets").EnumerateArray())
                    if (string.Equals(a.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase))
                        url = a.GetProperty("browser_download_url").GetString();
                if (url == null) { Done($"release {tag} has no {AssetName}"); return; }
                if (!url.StartsWith($"https://github.com/{Repo}/releases/download/", StringComparison.OrdinalIgnoreCase))
                {
                    Done($"release {tag}: unexpected download address, skipped"); return;
                }

                byte[] zip = await http.GetByteArrayAsync(url);
                var changes = new List<(string target, byte[] data)>();
                using (var ms = new MemoryStream(zip))
                using (var archive = new ZipArchive(ms, ZipArchiveMode.Read))
                {
                    foreach (var e in archive.Entries)
                    {
                        string name = e.FullName.Replace('\\', '/');
                        string[] parts = name.Split('/');
                        // Only Mods/<file>.dll and UserLibs/<file>.dll, one level deep: nothing else in the zip is written.
                        if (parts.Length != 2 || !(parts[0] == "Mods" || parts[0] == "UserLibs") || !parts[1].EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                            || parts[1].IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || parts[1].StartsWith(".")) continue;
                        using var es = e.Open();
                        using var buf = new MemoryStream();
                        es.CopyTo(buf);
                        var data = buf.ToArray();
                        string target = Path.Combine(Game, parts[0], parts[1]);
                        if (File.Exists(target) && Same(File.ReadAllBytes(target), data)) continue;
                        changes.Add((target, data));
                    }
                }
                if (changes.Count == 0) { _lastPref.Value = tag; Done($"up to date with release {tag}"); return; }

                bool dev = Directory.Exists(Path.Combine(Game, "ModMenuSource"));
                if (dev && !_devPref.Value)
                {
                    Done($"release {tag} differs in {changes.Count} file(s) ({string.Join(", ", changes.Select(c => Path.GetFileName(c.target)))}); " +
                         "development install (ModMenuSource present): nothing replaced");
                    return;
                }
                foreach (var (target, data) in changes)
                {
                    try
                    {
                        string temp = target + ".ouak-new";
                        File.WriteAllBytes(temp, data);
                        if (File.Exists(target))
                        {
                            string old = target + ".ouak-old";
                            if (File.Exists(old)) File.Delete(old);
                            File.Move(target, old);   // a loaded DLL can be renamed, not overwritten
                        }
                        File.Move(temp, target);
                        lock (_replaced) _replaced.Add(Path.GetFileName(target));
                    }
                    catch (Exception e) { Log.Warn($"couldn't replace {Path.GetFileName(target)}: {e.Message}"); }
                }
                _lastPref.Value = tag;
                Done($"installed release {tag}: {string.Join(", ", _replaced)} (takes effect at the next launch)");
                if (_replaced.Count > 0) { _installedTag = tag; RestartRequired = true; }
            }
            catch (HttpRequestException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { Done("no release published yet"); }
            catch (Exception e) { Done($"check failed: {e.Message}"); }
        }

        /// <summary>An update was installed this launch: the restart prompt is up until the player restarts or quits.</summary>
        public static bool RestartRequired { get; private set; }
        /// <summary>Restart prompts shown (it comes back whenever it's closed).</summary>
        public static int PromptsShown { get; private set; }
        private static string _installedTag = "";
        private static bool _hooked;

        // Main thread, every frame (GameEvents.Update): keeps the restart prompt on screen.
        private static void Tick()
        {
            if (!RestartRequired || Prompt.Active) return;
            PromptsShown++;
            Prompt.Show("OUAK mods updated",
                $"Version {_installedTag} of the OUAK mods is installed. It starts with the next launch: restart the game now, or quit.",
                new[]
                {
                    new PromptOption("Restart now", Restart) { Detail = "Closes the game and starts it again with the new version." },
                    new PromptOption("Quit", () => UnityEngine.Application.Quit()) { Detail = "Closes the game. The new version runs next time." },
                });
        }

        /// <summary>Closes the game and starts it again (a hidden helper waits for this process to end, then launches the game).</summary>
        public static void Restart()
        {
            try
            {
                int pid = Environment.ProcessId;
                string exe = Environment.ProcessPath ?? Path.Combine(Game, "OnceUponaKATAMARI.exe");
                string cmd = $"Wait-Process -Id {pid} -ErrorAction SilentlyContinue; Start-Sleep -Seconds 2; Start-Process -FilePath '{exe.Replace("'", "''")}' -WorkingDirectory '{Game.Replace("'", "''")}'";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe",
                    $"-NoProfile -WindowStyle Hidden -Command \"{cmd}\"") { UseShellExecute = false, CreateNoWindow = true });
                Log.Info("restarting for the update");
            }
            catch (Exception e) { Log.Warn($"couldn't start the relaunch helper ({e.Message}); quitting only"); }
            UnityEngine.Application.Quit();
        }

        /// <summary>Test only: acts as if an update had just been installed (shows the restart prompt).</summary>
        internal static void TestRequireRestart(string tag) { _installedTag = tag; RestartRequired = true; }

        /// <summary>Test only: takes the requirement back (the prompt stops coming back).</summary>
        internal static void TestClear() { RestartRequired = false; if (Prompt.Active && Prompt.CurrentTitle == "OUAK mods updated") Prompt.Close(); }

        private static void Done(string status)
        {
            Status = status;
            Log.Info(status);
            ModTasks.Later(() => { try { MelonPreferences.Save(); } catch { } }, 0f);
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            using var sha = SHA256.Create();
            return sha.ComputeHash(a).SequenceEqual(sha.ComputeHash(b));
        }
    }
}
