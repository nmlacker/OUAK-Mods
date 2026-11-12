using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace OUAKUpdater
{
    /// <summary>
    /// A release's manifest.json, written by tools/make_release.ps1:
    /// <code>
    /// { "release": "v2026.11.11",
    ///   "files": [ { "path": "Mods/OUAKModAPI.dll", "name": "OUAK Modding API", "version": "3.0.0",
    ///                "sha256": "...", "always": true }, ... ] }
    /// </code>
    /// "version" is the DLL's MelonInfo version (absent for libraries); "always" installs the file even where it's missing.
    /// </summary>
    internal static class Manifest
    {
        internal sealed class Entry
        {
            internal string Path;
            internal string Name;
            internal string Version;
            internal string Sha256;
            internal bool Always;
            internal string Label => string.IsNullOrEmpty(Name) ? Path : Name;
        }

        internal static List<Entry> Parse(string json)
        {
            var list = new List<Entry>();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("manifest.json has no \"files\" list.");
            foreach (var f in files.EnumerateArray())
            {
                var e = new Entry
                {
                    Path = Str(f, "path")?.Replace('\\', '/'),
                    Name = Str(f, "name"),
                    Version = Str(f, "version"),
                    Sha256 = Str(f, "sha256"),
                    Always = f.TryGetProperty("always", out var a) && a.ValueKind == JsonValueKind.True,
                };
                if (string.IsNullOrEmpty(e.Path) || string.IsNullOrEmpty(e.Sha256) || e.Sha256.Length != 64)
                    throw new InvalidDataException($"manifest.json has an entry without a path or SHA-256 ({e.Path}).");
                if (string.IsNullOrWhiteSpace(e.Version)) e.Version = null;
                list.Add(e);
            }
            return list;
        }

        private static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
