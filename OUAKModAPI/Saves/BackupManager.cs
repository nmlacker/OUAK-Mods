using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Il2CppApp.KatamariSin;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OUAKModAPI
{
    public sealed class SlotSummary
    {
        public int Slot;
        public bool InUse;
        public string LastSaved;
        public int Progression;
        public int StagesCleared;
        public int ClearCount;
        public int Crowns;
        public int Cousins;
        public int Presents;
        public int CollectionObjects;
        public int Stardust;
        public int MaxKatamariSize;
        public uint KingMedals;
        public bool StoryCleared;
        public bool AllCleared;
    }

    public sealed class BackupFile
    {
        public string Path;
        public long Size;
        public DateTime Modified;
    }

    public sealed class BackupInfo
    {
        public int FormatVersion = 1;
        public string Id;
        public DateTime Created;
        public string Reason;
        public string Note;
        public string GameVersion;
        public string ModVersion;
        public string Scene;
        public int LoadedSlot = -1;
        public int LastUsedSlot = -1;
        public bool HasSlotSnapshot;
        public List<SlotSummary> Slots = new();
        public List<BackupFile> Files = new();
        public List<string> DlcOwned = new();
        public List<string> DlcLocked = new();

        [JsonIgnore] public string Folder => System.IO.Path.Combine(BackupManager.BackupRoot, Id);
        [JsonIgnore] public string Title => $"{Created:yyyy-MM-dd HH:mm:ss}  ·  {Reason}";
    }

    /// <summary>
    /// Timestamped save backups in UserData/OUAKModMenu/Backup/&lt;yyyy-MM-dd_HH-mm-ss&gt;_&lt;reason&gt;/:
    /// the raw (encrypted) save files, a snapshot of the loaded slot, backup.json and a readable info.txt.
    /// </summary>
    public static class BackupManager
    {
        public const string SnapshotFile = "LoadedSlot.bin";

        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };
        private static List<BackupInfo> _cache;

        /// <summary>Where backups and the mod menu's data live (UserData/OUAKModMenu, as before the API split).</summary>
        public static string ModDataDir => Path.Combine(MelonEnvironment.UserDataDirectory, "OUAKModMenu");

        /// <summary>Recorded in each backup as the version that made it.</summary>
        public static string ModVersion = "API " + ApiCore.Version;

        /// <summary>
        /// The DLC state recorded in a backup: (name, owned, locked) per pack. A mod that can lock DLC (the mod
        /// menu) sets it; without it the backup records owned packs only.
        /// </summary>
        public static Func<System.Collections.Generic.IEnumerable<(string name, bool owned, bool locked)>> DlcState;
        public static string BackupRoot => Path.Combine(ModDataDir, "Backup");

        public static string SaveRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Bandai Namco Entertainment", "Once Upon A KATAMARI", "Saved", "SaveGames");

        /// <summary>True on the title / save-select screens, where no save slot is in use.</summary>
        public static bool AtTitleScreens
        {
            get
            {
                if (GameRefs.Main != null) return false;
                string s = SceneManager.GetActiveScene().name;
                return s is "Title" or "SelectData" or "SceneLogo";
            }
        }

        public static List<BackupInfo> List(bool refresh = false)
        {
            if (_cache != null && !refresh) return _cache;
            var list = new List<BackupInfo>();
            if (Directory.Exists(BackupRoot))
            {
                foreach (string dir in Directory.GetDirectories(BackupRoot))
                {
                    string id = Path.GetFileName(dir);
                    BackupInfo info = null;
                    try
                    {
                        string jsonPath = Path.Combine(dir, "backup.json");
                        if (File.Exists(jsonPath)) info = JsonSerializer.Deserialize<BackupInfo>(File.ReadAllText(jsonPath), Json);
                    }
                    catch (Exception e) { MelonLogger.Warning($"Backup {id}: unreadable backup.json ({e.Message})"); }
                    info ??= new BackupInfo { Reason = "(no backup.json)", Created = Directory.GetCreationTime(dir) };
                    info.Id = id;
                    list.Add(info);
                }
            }
            _cache = list.OrderByDescending(b => b.Created).ToList();
            return _cache;
        }

        public static BackupInfo Find(string id) => List().FirstOrDefault(b => b.Id == id);

        /// <summary>Copies every save file and records what was in it. Never modifies the saves.</summary>
        public static BackupInfo Create(string reason, string note)
        {
            var now = DateTime.Now;
            string id = UniqueId(now, reason);
            string folder = Path.Combine(BackupRoot, id);
            Directory.CreateDirectory(folder);

            var info = new BackupInfo
            {
                Id = id,
                Created = now,
                Reason = reason,
                Note = string.IsNullOrWhiteSpace(note) ? "" : note.Trim(),
                GameVersion = Application.version,
                ModVersion = ModVersion,
                Scene = SceneManager.GetActiveScene().name,
            };

            if (Directory.Exists(SaveRoot))
            {
                foreach (string file in Directory.GetFiles(SaveRoot, "*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(SaveRoot, file);
                    string target = Path.Combine(folder, "SaveGames", rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Copy(file, target, true);
                    var fi = new FileInfo(file);
                    info.Files.Add(new BackupFile { Path = rel, Size = fi.Length, Modified = fi.LastWriteTime });
                }
            }
            if (info.Files.Count == 0) MelonLogger.Warning($"No save files found under {SaveRoot}");

            try { FillSlotInfo(info); }
            catch (Exception e) { MelonLogger.Warning($"Could not read slot details: {e.Message}"); }

            try
            {
                if (DlcState != null)
                {
                    foreach (var (name, owned, locked) in DlcState())
                    {
                        if (owned) info.DlcOwned.Add(name);
                        if (locked) info.DlcLocked.Add(name);
                    }
                }
                else
                {
                    foreach (DlcManager.DLC_PACKS pack in Enum.GetValues(typeof(DlcManager.DLC_PACKS)))
                        if (GameRefs.OwnsDlcPack(pack)) info.DlcOwned.Add(pack.ToString());
                }
            }
            catch (Exception e) { MelonLogger.Warning($"Could not read DLC state: {e.Message}"); }

            File.WriteAllText(Path.Combine(folder, "backup.json"), JsonSerializer.Serialize(info, Json));
            File.WriteAllText(Path.Combine(folder, "info.txt"), Describe(info));
            _cache = null;
            MelonLogger.Msg($"Backup created: {folder}");
            return info;
        }

        /// <summary>
        /// Writes a backup's save files back over the current ones, then has the game re-read them.
        /// Only allowed on the title / save-select screens so no loaded slot can overwrite the result.
        /// </summary>
        public static string RestoreFiles(BackupInfo backup, string note)
        {
            if (!AtTitleScreens) throw new InvalidOperationException("Go to the Title screen first (Modes tab), then restore.");
            string src = Path.Combine(backup.Folder, "SaveGames");
            if (!Directory.Exists(src)) throw new InvalidOperationException("That backup has no save files.");
            var files = Directory.GetFiles(src, "*.bin", SearchOption.AllDirectories);
            if (files.Length == 0) throw new InvalidOperationException("That backup has no save files.");

            var safety = Create($"Before restoring {backup.Created:yyyy-MM-dd HH-mm-ss}", note);
            foreach (string file in files)
            {
                string target = Path.Combine(SaveRoot, Path.GetRelativePath(src, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, true);
            }

            string done = $"Restored {files.Length} save file(s) from {backup.Created:yyyy-MM-dd HH:mm:ss}. Your previous saves were backed up first ({safety.Id}).";

            // Same sequence as boot (SaveDataController.Start + SceneLogo): re-read every file, wait for the
            // load to finish, push the system data (options etc.) into the game, then show the save list again.
            var ctrl = GameRefs.SaveCtrl;
            if (ctrl == null) return done + " Restart the game to load them.";
            ctrl.SetLoadAction();
            ModTasks.RunWhen(() => ctrl.ActionWait() || ctrl.ActionError(), () =>
            {
                if (!ctrl.ActionWait())
                    return done + " The game couldn't re-read them yet - restart the game before playing.";
                ctrl.SystemDataToGameData();
                Il2Cpp.SceneManager.LoadSceneAsync("SelectData", LoadSceneMode.Single, true);
                return done + " Saves reloaded.";
            }, timeout: 20f);
            return done + " Reloading saves...";
        }

        /// <summary>Serialized progress of the loaded slot, taken through the game's own converter.</summary>
        public static void WriteLoadedSlotSnapshot(BackupInfo info)
        {
            var ctrl = GameRefs.SaveCtrl;
            ctrl.m_DataToConvert.GameToSaveData(false);
            var user = ctrl.CurrentUserData();

            var buffer = new Il2CppStructArray<byte>(SaveInfoUserData.Size + 4096);
            int length = 0;
            user.GetByteData(ref buffer, ref length);
            if (length <= 0) throw new InvalidOperationException("The game returned an empty save buffer.");

            var bytes = new byte[length];
            for (int i = 0; i < length; i++) bytes[i] = buffer[i];
            File.WriteAllBytes(Path.Combine(info.Folder, SnapshotFile), bytes);

            info.HasSlotSnapshot = true;
            info.LoadedSlot = ctrl.CurrentUseSlot;
            File.WriteAllText(Path.Combine(info.Folder, "backup.json"), JsonSerializer.Serialize(info, Json));
            File.WriteAllText(Path.Combine(info.Folder, "info.txt"), Describe(info));
        }

        /// <summary>Puts a slot snapshot back into the loaded slot, keeping the player's current options.</summary>
        public static void RestoreLoadedSlotSnapshot(BackupInfo backup)
        {
            string path = Path.Combine(backup.Folder, SnapshotFile);
            if (!File.Exists(path)) throw new InvalidOperationException($"Backup {backup.Id} has no slot snapshot.");

            var ctrl = GameRefs.SaveCtrl;
            var convert = ctrl.m_DataToConvert;
            convert.GameToSaveData(false);
            var user = ctrl.CurrentUserData();

            var options = new SaveInfoOptionData();
            options.CopyFrom(user._OptionData);

            byte[] bytes = File.ReadAllBytes(path);
            var data = new Il2CppStructArray<byte>(bytes.Length);
            for (int i = 0; i < bytes.Length; i++) data[i] = bytes[i];
            int index = 0;
            user.SetByteData(data, ref index);
            user._OptionData.CopyFrom(options);

            convert.SaveToGameData();
            ctrl.SetSaveAction(ctrl.CurrentUseSlot, false);
        }

        public static void OpenFolder(string path)
        {
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true });
        }

        // ------------------------------------------------------------------ slot details

        private static void FillSlotInfo(BackupInfo info)
        {
            var ctrl = GameRefs.SaveCtrl;
            if (ctrl == null) return;
            if (GameRefs.InGameSession) info.LoadedSlot = ctrl.CurrentUseSlot;
            var data = ctrl.GameData;
            if (data == null) return;
            if (data._SystemData != null) info.LastUsedSlot = data._SystemData._LastUser;
            var users = data._UserData?._UserDatas;
            if (users == null) return;
            for (int i = 0; i < users.Length; i++)
            {
                var s = Summarize(i, users[i]);
                if (s != null) info.Slots.Add(s);
            }
        }

        private static SlotSummary Summarize(int slot, SaveInfoUserData u)
        {
            if (u == null) return null;
            var s = new SlotSummary { Slot = slot };
            var pr = u._PriorityData;
            if (pr != null)
            {
                var dt = pr._SaveDateTime;
                if (dt != null && dt.Length >= 5 && dt[0] > 2000)
                    s.LastSaved = $"{dt[0]:0000}-{dt[1]:00}-{dt[2]:00} {dt[3]:00}:{dt[4]:00}";
                s.Progression = pr._GameProgression;
                s.Crowns = pr._CrownCount;
                s.MaxKatamariSize = pr._MaxCoreSize;
                s.KingMedals = pr._CurrentKingMedal;
                s.StoryCleared = pr._GameNormalClear;
                s.AllCleared = pr._GameAllClear;
            }
            var gd = u._GameData;
            if (gd != null)
            {
                var results = gd._ResultSaveData;
                if (results != null)
                    for (int i = 0; i < results.Length; i++)
                        if (results[i]?._UtilitySaveInfo != null && results[i]._UtilitySaveInfo._IsClear != 0) s.StagesCleared++;
                if (gd._GameUserData != null)
                {
                    s.Stardust = gd._GameUserData._StarDustCount;
                    s.ClearCount = gd._GameUserData._ClearStageCount;
                }
                var mono = gd._MonoData;
                if (mono != null)
                {
                    s.Cousins = Count(mono._Itoko);
                    s.Presents = Count(mono._Present);
                    s.CollectionObjects = mono._MonoGetCount;
                }
            }
            s.InUse = s.LastSaved != null || s.StagesCleared > 0 || s.Progression > 0;
            return s;
        }

        private static int Count(Il2CppStructArray<bool> arr)
        {
            if (arr == null) return 0;
            int n = 0;
            for (int i = 0; i < arr.Length; i++) if (arr[i]) n++;
            return n;
        }

        public static string SlotLine(SlotSummary s)
        {
            if (!s.InUse) return $"Slot {s.Slot + 1}: empty";
            return $"Slot {s.Slot + 1}: last saved {s.LastSaved ?? "?"} · {N(s.StagesCleared, "stage")} cleared · " +
                   $"{N(s.Crowns, "crown")} · {N(s.Cousins, "cousin")} · {N(s.Presents, "present")} · {N(s.CollectionObjects, "object")} · " +
                   $"{s.Stardust} stardust{(s.AllCleared ? " · 100% clear" : s.StoryCleared ? " · story cleared" : "")}";
        }

        private static string N(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

        public static string Describe(BackupInfo b)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Once Upon a KATAMARI save backup (OUAK Mod Menu)");
            sb.AppendLine("================================================");
            sb.AppendLine($"Created:      {b.Created:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Reason:       {b.Reason}");
            sb.AppendLine($"Note:         {(string.IsNullOrEmpty(b.Note) ? "(none)" : b.Note)}");
            sb.AppendLine($"Game version: {b.GameVersion}   Mod version: {b.ModVersion}");
            sb.AppendLine($"Taken in:     {b.Scene}");
            sb.AppendLine($"Loaded slot:  {(b.LoadedSlot >= 0 ? (b.LoadedSlot + 1).ToString() : "none")}" +
                          $"{(b.HasSlotSnapshot ? " (live snapshot included: " + SnapshotFile + ")" : "")}");
            if (b.LastUsedSlot >= 0) sb.AppendLine($"Last used:    slot {b.LastUsedSlot + 1}");
            sb.AppendLine();
            sb.AppendLine("Slots (as last saved):");
            if (b.Slots.Count == 0) sb.AppendLine("  (slot details were not available)");
            foreach (var s in b.Slots) sb.AppendLine("  " + SlotLine(s));
            sb.AppendLine();
            sb.AppendLine($"DLC owned:    {(b.DlcOwned.Count == 0 ? "none detected" : string.Join(", ", b.DlcOwned))}");
            sb.AppendLine($"DLC locked:   {(b.DlcLocked.Count == 0 ? "none" : string.Join(", ", b.DlcLocked))}");
            sb.AppendLine();
            sb.AppendLine("Files (copied into SaveGames\\):");
            foreach (var f in b.Files) sb.AppendLine($"  {f.Path}  ({f.Size:N0} bytes, modified {f.Modified:yyyy-MM-dd HH:mm:ss})");
            sb.AppendLine();
            sb.AppendLine("To restore by hand: quit the game and copy the SaveGames folder contents back to");
            sb.AppendLine("  " + SaveRoot);
            return sb.ToString();
        }

        private static string UniqueId(DateTime now, string reason)
        {
            string slug = new string(reason.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
            while (slug.Contains("--")) slug = slug.Replace("--", "-");
            if (slug.Length > 40) slug = slug.Substring(0, 40).TrimEnd('-');
            string id = $"{now:yyyy-MM-dd_HH-mm-ss}_{slug}";
            string candidate = id;
            for (int n = 2; Directory.Exists(Path.Combine(BackupRoot, candidate)); n++) candidate = $"{id}_{n}";
            return candidate;
        }
    }
}
