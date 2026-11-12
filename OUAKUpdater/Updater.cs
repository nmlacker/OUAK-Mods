using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using MelonLoader;
using MelonLoader.Utils;

namespace OUAKUpdater
{
    /// <summary>
    /// One update pass. A release holds two assets, made by tools/make_release.ps1:
    /// <c>manifest.json</c> (every file with its path in the game folder, its MelonInfo version and SHA-256) and
    /// <c>OUAK_Mods.zip</c> (the files at those paths). Only the manifest is downloaded when nothing needs updating.
    /// Rules:
    /// - A mod is updated only if it's installed and the release's version is <b>newer</b> than the installed one (so a
    ///   newer local build is never replaced by an older release). Entries marked "always" (the API, this updater) are
    ///   also installed when missing.
    /// - Libraries (no version: UserLibs/ImGui) are replaced when missing or different.
    /// - Mods are swapped all together or not at all (rolled back on any error); this plugin and UserLibs are loaded
    ///   already, so they are swapped best-effort (a renamed loaded file keeps running; a refused one waits for the next
    ///   release).
    /// - Every file is checked against the manifest's SHA-256 before anything is replaced. Old files go to
    ///   UserData/OUAKUpdater/Backup (the last few kept).
    /// </summary>
    internal sealed class Updater
    {
        private const string ManifestAsset = "manifest.json";
        private const string ZipAsset = "OUAK_Mods.zip";
        private const string NewSuffix = ".ouaknew";
        private const int KeepBackups = 3;
        private const long MaxZipBytes = 200L * 1024 * 1024;
        private static readonly string[] AllowedFolders = { "Mods/", "Plugins/", "UserLibs/" };

        private readonly MelonLogger.Instance _log;
        private readonly string _repo;
        private readonly bool _enabled;
        private readonly string _root;
        private readonly string _dataDir;
        private readonly Status _status = new();

        internal Updater(MelonLogger.Instance log, string repo, bool enabled)
        {
            _log = log;
            _repo = (repo ?? "").Trim();
            _enabled = enabled;
            _dataDir = Path.Combine(MelonEnvironment.UserDataDirectory, "OUAKUpdater");
            // UserData sits in the game folder.
            _root = Path.GetFullPath(Path.Combine(MelonEnvironment.UserDataDirectory, ".."));
        }

        internal static string StatusPath => Path.Combine(MelonEnvironment.UserDataDirectory, "OUAKUpdater", "status.json");

        internal void Run()
        {
            Directory.CreateDirectory(_dataDir);
            _status.Repository = _repo;
            _status.CheckedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            try
            {
                RunInner();
            }
            catch (HttpRequestException e) { Finish("offline", $"Couldn't reach GitHub ({e.Message}). Starting with the installed mods."); }
            catch (OperationCanceledException) { Finish("offline", "GitHub didn't answer in time. Starting with the installed mods."); }
            catch (Exception e)
            {
                Finish("error", e.Message);
                _log.Error(e.ToString());
            }
        }

        private void RunInner()
        {
            if (!_enabled) { Finish("disabled", "Automatic updates are off ([OUAKUpdater] Enabled = false)."); return; }
            if (!Regex.IsMatch(_repo, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$"))
            {
                Finish("error", $"\"{_repo}\" isn't a GitHub repository (owner/name): check [OUAKUpdater] Repository.");
                return;
            }
            CleanLeftovers();

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("OUAKUpdater/" + UpdaterPlugin.Version);

            // ---- the latest release
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{_repo}/releases/latest");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = http.Send(req);
            if ((int)resp.StatusCode == 404)
            {
                // Also what GitHub answers for a private repository.
                Finish("no-release", $"{_repo} has no public release yet (or the repository is private).");
                return;
            }
            if (!resp.IsSuccessStatusCode) { Finish("error", $"GitHub answered {(int)resp.StatusCode} {resp.ReasonPhrase}."); return; }

            string manifestUrl = null, zipUrl = null;
            long zipSize = 0;
            using (var doc = JsonDocument.Parse(ReadString(resp)))
            {
                var r = doc.RootElement;
                _status.Release = Str(r, "tag_name");
                _status.ReleaseUrl = Str(r, "html_url");
                _status.ReleaseNotes = Str(r, "body");
                if (r.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        var name = Str(a, "name");
                        if (name == ManifestAsset) manifestUrl = Str(a, "browser_download_url");
                        else if (name == ZipAsset)
                        {
                            zipUrl = Str(a, "browser_download_url");
                            if (a.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number) zipSize = s.GetInt64();
                        }
                    }
                }
            }
            if (manifestUrl == null || zipUrl == null)
            {
                Finish("error", $"Release {_status.Release} has no {ManifestAsset} / {ZipAsset} (make releases with tools/make_release.ps1).");
                return;
            }

            // ---- what needs updating
            var manifest = Manifest.Parse(GetString(http, manifestUrl));
            var plan = new List<Planned>();
            foreach (var e in manifest)
            {
                if (!SafePath(e.Path, out var target)) { _status.Skipped.Add($"{e.Path}: not a mod, plugin or library path, ignored"); continue; }
                bool exists = File.Exists(target);
                if (e.Version != null)
                {
                    if (!exists)
                    {
                        if (e.Always) plan.Add(new Planned(e, target, null));
                        continue;   // a mod the user didn't install stays uninstalled
                    }
                    var installed = ReadMelonVersion(target);
                    if (installed == null) { _status.Skipped.Add($"{e.Label}: installed version unreadable, left alone"); continue; }
                    int cmp = CompareVersions(e.Version, installed);
                    if (cmp > 0) plan.Add(new Planned(e, target, installed));
                    else if (cmp < 0) _status.Skipped.Add($"{e.Label}: installed {installed} is newer than the release's {e.Version}, left alone");
                }
                else if (!exists || !string.Equals(Sha256(target), e.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    plan.Add(new Planned(e, target, exists ? "different file" : null));
                }
            }
            if (plan.Count == 0)
            {
                Finish("up-to-date", $"Release {_status.Release}: every installed OUAK mod is up to date.");
                return;
            }

            // ---- download and check everything before touching the installed files
            if (zipSize > MaxZipBytes) { Finish("error", $"{ZipAsset} is {zipSize / 1048576} MB, more than expected; not downloaded."); return; }
            _log.Msg($"Release {_status.Release}: updating {string.Join(", ", plan.Select(p => p.Entry.Label))}");
            string zipPath = Path.Combine(_dataDir, "download.zip");
            Download(http, zipUrl, zipPath);
            try
            {
                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    foreach (var p in plan)
                    {
                        var ze = zip.Entries.FirstOrDefault(z => z.FullName.Replace('\\', '/') == p.Entry.Path);
                        if (ze == null) throw new InvalidDataException($"{p.Entry.Path} is in the manifest but not in {ZipAsset}.");
                        p.NewFile = p.Target + NewSuffix;
                        Directory.CreateDirectory(Path.GetDirectoryName(p.Target));
                        ze.ExtractToFile(p.NewFile, overwrite: true);
                        string hash = Sha256(p.NewFile);
                        if (!string.Equals(hash, p.Entry.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException($"{p.Entry.Path} doesn't match the release's SHA-256: nothing was installed.");
                    }
                }
            }
            catch
            {
                foreach (var p in plan) TryDelete(p.NewFile);
                throw;
            }
            finally
            {
                TryDelete(zipPath);
            }

            // ---- swap: mods all together, then the already-loaded files best-effort
            string backupDir = Path.Combine(_dataDir, "Backup", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + "_" + Clean(_status.Release));
            var critical = plan.Where(p => !p.BestEffort).ToList();
            var done = new List<Planned>();
            foreach (var p in critical)
            {
                try
                {
                    Swap(p, backupDir);
                    done.Add(p);
                }
                catch (Exception ex)
                {
                    for (int i = done.Count - 1; i >= 0; i--) Undo(done[i]);
                    foreach (var q in plan) TryDelete(q.NewFile);
                    Finish("error", $"Couldn't replace {p.Entry.Path} ({ex.Message}); every mod was put back as it was.");
                    return;
                }
            }
            foreach (var p in critical) _status.Updated.Add(p.Describe());

            int refused = 0;
            foreach (var p in plan.Where(p => p.BestEffort))
            {
                try
                {
                    Swap(p, backupDir);
                    _status.Updated.Add(p.Describe() + " (active from the next launch)");
                }
                catch (Exception ex)
                {
                    refused++;
                    TryDelete(p.NewFile);
                    _status.Skipped.Add($"{p.Entry.Label}: in use, not replaced ({ex.Message})");
                }
            }
            PruneBackups();
            Finish(refused == 0 ? "updated" : "partly-updated",
                $"Installed release {_status.Release}: {_status.Updated.Count} file(s) updated" + (refused > 0 ? $", {refused} in use" : "") + ".");
        }

        // ------------------------------------------------------------------------------------------------ files

        private void Swap(Planned p, string backupDir)
        {
            if (File.Exists(p.Target))
            {
                p.Backup = Path.Combine(backupDir, p.Entry.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(p.Backup));
                File.Move(p.Target, p.Backup);   // a rename: works on a loaded DLL where overwriting it wouldn't
            }
            try
            {
                File.Move(p.NewFile, p.Target);
            }
            catch
            {
                if (p.Backup != null) { File.Move(p.Backup, p.Target); p.Backup = null; }
                throw;
            }
        }

        private void Undo(Planned p)
        {
            try
            {
                if (File.Exists(p.Target)) File.Delete(p.Target);
                if (p.Backup != null) File.Move(p.Backup, p.Target);
            }
            catch (Exception e)
            {
                _log.Error($"Couldn't put {p.Entry.Path} back: {e.Message}. Its old copy is in {p.Backup}");
            }
        }

        /// <summary>Only Mods/, Plugins/ and UserLibs/ DLLs inside the game folder.</summary>
        private bool SafePath(string rel, out string full)
        {
            full = null;
            if (string.IsNullOrEmpty(rel) || rel.Contains("..") || rel.Contains(':') || rel.StartsWith("/")) return false;
            if (!AllowedFolders.Any(f => rel.StartsWith(f, StringComparison.Ordinal))) return false;
            if (!rel.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return false;
            full = Path.GetFullPath(Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar)));
            return full.StartsWith(_root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Half-written files from a run that was cut off.</summary>
        private void CleanLeftovers()
        {
            foreach (var folder in AllowedFolders)
            {
                var dir = Path.Combine(_root, folder.TrimEnd('/'));
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir, "*" + NewSuffix)) TryDelete(f);
            }
        }

        private void PruneBackups()
        {
            try
            {
                var dir = Path.Combine(_dataDir, "Backup");
                if (!Directory.Exists(dir)) return;
                foreach (var old in Directory.GetDirectories(dir).OrderByDescending(d => d, StringComparer.Ordinal).Skip(KeepBackups))
                    Directory.Delete(old, true);
            }
            catch (Exception e) { _log.Warning($"Couldn't remove old backups: {e.Message}"); }
        }

        private static void TryDelete(string path)
        {
            try { if (path != null && File.Exists(path)) File.Delete(path); } catch { }
        }

        private static string Clean(string s) => Regex.Replace(s ?? "release", @"[^A-Za-z0-9_.-]", "_");

        internal static string Sha256(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(fs));
        }

        /// <summary>
        /// The version in a DLL's [assembly: MelonInfo(type, name, version, author)], read from its metadata without
        /// loading it. Null when there's none or it isn't the string form.
        /// </summary>
        internal static string ReadMelonVersion(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var pe = new PEReader(fs);
                if (!pe.HasMetadata) return null;
                var md = pe.GetMetadataReader();
                foreach (var h in md.GetAssemblyDefinition().GetCustomAttributes())
                {
                    var ca = md.GetCustomAttribute(h);
                    if (ca.Constructor.Kind != HandleKind.MemberReference) continue;
                    var mr = md.GetMemberReference((MemberReferenceHandle)ca.Constructor);
                    if (mr.Parent.Kind != HandleKind.TypeReference) continue;
                    if (md.GetString(md.GetTypeReference((TypeReferenceHandle)mr.Parent).Name) != "MelonInfoAttribute") continue;
                    var blob = md.GetBlobReader(ca.Value);
                    if (blob.ReadUInt16() != 1) return null;   // custom attribute prolog
                    blob.ReadSerializedString();                // the melon's type
                    blob.ReadSerializedString();                // name
                    var version = blob.ReadSerializedString();
                    return version != null && Regex.IsMatch(version, @"^\d+(\.\d+){0,3}([-+].*)?$") ? version : null;
                }
            }
            catch { }
            return null;
        }

        /// <summary>Numeric comparison ("1.10.0" &gt; "1.9.0"); 0 when either can't be read.</summary>
        internal static int CompareVersions(string a, string b)
        {
            var va = ParseVersion(a);
            var vb = ParseVersion(b);
            if (va == null || vb == null) return 0;
            return va.CompareTo(vb);
        }

        private static Version ParseVersion(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim().TrimStart('v', 'V').Split('-', '+')[0];
            if (!s.Contains('.')) s += ".0";
            if (!Version.TryParse(s, out var v)) return null;
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
        }

        // ------------------------------------------------------------------------------------------------ network

        private static string ReadString(HttpResponseMessage resp)
        {
            using var s = resp.Content.ReadAsStream();
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }

        private static string GetString(HttpClient http, string url)
        {
            using var resp = http.Send(new HttpRequestMessage(HttpMethod.Get, url));
            resp.EnsureSuccessStatusCode();
            return ReadString(resp);
        }

        private static void Download(HttpClient http, string url, string file)
        {
            using var resp = http.Send(new HttpRequestMessage(HttpMethod.Get, url), HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            using var src = resp.Content.ReadAsStream();
            using var dst = File.Create(file);
            var buf = new byte[81920];
            long total = 0;
            int n;
            while ((n = src.Read(buf, 0, buf.Length)) > 0)
            {
                total += n;
                if (total > MaxZipBytes) throw new InvalidDataException($"{ZipAsset} is bigger than expected; download stopped.");
                dst.Write(buf, 0, n);
            }
        }

        private static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        // ------------------------------------------------------------------------------------------------ status

        private void Finish(string result, string message)
        {
            _status.Result = result;
            _status.Message = message;
            if (result is "error") _log.Error(message);
            else if (result is "offline" or "no-release" or "partly-updated") _log.Warning(message);
            else _log.Msg(message);
            foreach (var u in _status.Updated) _log.Msg("  updated: " + u);
            foreach (var s in _status.Skipped) _log.Msg("  " + s);
            try
            {
                Directory.CreateDirectory(_dataDir);
                File.WriteAllText(StatusPath, JsonSerializer.Serialize(_status, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception e) { _log.Warning($"Couldn't write {StatusPath}: {e.Message}"); }
        }

        // ------------------------------------------------------------------------------------------------ types

        private sealed class Planned
        {
            internal readonly Manifest.Entry Entry;
            internal readonly string Target;
            internal readonly string From;     // installed version, "different file", or null (new install)
            internal string NewFile;
            internal string Backup;

            internal Planned(Manifest.Entry e, string target, string from) { Entry = e; Target = target; From = from; }

            /// <summary>Loaded before this plugin runs (this plugin itself, UserLibs), so replacing it may be refused.</summary>
            internal bool BestEffort => Entry.Path.StartsWith("Plugins/", StringComparison.Ordinal) || Entry.Path.StartsWith("UserLibs/", StringComparison.Ordinal);

            internal string Describe() =>
                Entry.Version == null ? $"{Entry.Label}" + (From == null ? " (installed)" : " (replaced)")
                : From == null ? $"{Entry.Label} {Entry.Version} (installed)"
                : $"{Entry.Label} {From} -> {Entry.Version}";
        }

        public sealed class Status
        {
            public string CheckedAt { get; set; }
            public string Repository { get; set; }
            /// <summary>disabled, up-to-date, updated, partly-updated, no-release, offline, error.</summary>
            public string Result { get; set; }
            public string Message { get; set; }
            public string Release { get; set; }
            public string ReleaseUrl { get; set; }
            public string ReleaseNotes { get; set; }
            public List<string> Updated { get; set; } = new();
            public List<string> Skipped { get; set; } = new();
            public string UpdaterVersion { get; set; } = UpdaterPlugin.Version;
        }
    }
}
