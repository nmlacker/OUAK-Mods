using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Il2Cpp;
using Il2CppApp.KatamariSin;
using Il2CppSteamworks;
using MelonLoader;
using UnityEngine;
using OUAKModAPI;

namespace OUAKModMenu
{
    internal sealed class DlcEntry
    {
        public int TableIndex;
        public string Name;
        public uint AppId;
    }

    internal sealed class DlcPack
    {
        public int Id;
        public string Label;
        public string SteamName;
        public readonly List<DlcEntry> Entries = new();
        public bool Owned;
        public bool Locked;
        public bool AvailableNow;

        /// <summary>Steam's DLC name without the game-title prefix, or our own label if Steam has none.</summary>
        public string DisplayName => string.IsNullOrEmpty(SteamName) ? Label : SteamName;
    }

    /// <summary>
    /// Hides DLC the player owns so the game treats it as not installed (no DLC prompts or items, e.g. for
    /// a fresh new game), and shows it again later. Never makes DLC available that Steam doesn't report as owned.
    ///
    /// The game keeps availability in DlcBase.dictDlcAvailable (name -> entry, null = unavailable), filled by
    /// DlcSteam.GetDownLoadContent() from SteamApps.BIsDlcInstalled(). Locks are applied right after that call
    /// and re-checked every frame; unlocking repeats the game's own Steam check for that pack's entries.
    /// </summary>
    internal static class DlcLock
    {
        // Owning Katamari Damacy REROLL or We Love Katamari REROLL+ unlocks the King (cousin #58) as a playable character.
        public const string OtherGamesLabel = "Bonus for owning other KATAMARI games (the King)";

        private static MelonPreferences_Entry<string> _lockedPref;
        private static MelonPreferences_Entry<bool> _otherGamesPref;
        private static readonly HashSet<int> Locked = new();
        private static Dictionary<uint, string> _steamNames;
        private static List<DlcPack> _packs;
        private static float _packsAt;

        public static bool PatchesActive { get; private set; }
        public static bool OtherGamesLocked => _otherGamesPref?.Value ?? false;
        public static bool AnyLocked => Locked.Count > 0 || OtherGamesLocked;

        public static bool IsLocked(int pack) => Locked.Contains(pack);

        public static void Init(MelonPreferences_Category cat, HarmonyLib.Harmony harmony)
        {
            _lockedPref = cat.CreateEntry("LockedDlcPacks", "", "Locked DLC packs",
                "DLC pack ids hidden from the game (comma separated). Managed from the menu's DLC tab.");
            _otherGamesPref = cat.CreateEntry("LockOtherKatamariBonus", false, "Lock other-KATAMARI bonus",
                "Hide the bonus for owning Katamari Damacy REROLL / We Love Katamari REROLL+.");
            foreach (string part in (_lockedPref.Value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(part.Trim(), out int id)) Locked.Add(id);

            int ok = 0;
            ok += TryPatch(harmony, typeof(DlcSteam), nameof(DlcSteam.GetDownLoadContent), nameof(AfterSteamCheck));
            ok += TryPatch(harmony, typeof(GlobalSaveData), nameof(GlobalSaveData.GetExistKatamariDamacyFlag), nameof(AfterExistFlag));
            ok += TryPatch(harmony, typeof(GlobalSaveData), nameof(GlobalSaveData.GetExistWeLoveKatamariFlag), nameof(AfterExistFlag));
            PatchesActive = ok == 3;
            if (AnyLocked) MelonLogger.Msg($"DLC locks active: packs [{string.Join(",", Locked)}]{(OtherGamesLocked ? " + other-KATAMARI bonus" : "")}");
        }

        private static int TryPatch(HarmonyLib.Harmony harmony, Type type, string method, string postfix)
        {
            try
            {
                harmony.Patch(AccessTools.Method(type, method),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(DlcLock), postfix)));
                return 1;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"Could not patch {type.Name}.{method}: {e.Message}");
                return 0;
            }
        }

        // Runs inside the game's Steam DLC check, before anything can read the fresh results.
        private static void AfterSteamCheck()
        {
            try { Enforce(); } catch (Exception e) { MelonLogger.Warning($"DLC lock: {e.Message}"); }
        }

        private static void AfterExistFlag(ref bool __result)
        {
            if (OtherGamesLocked) __result = false;
        }

        /// <summary>Called every frame; cheap when nothing is locked.</summary>
        public static void Tick()
        {
            if (Locked.Count == 0) return;
            try { Enforce(); } catch { }
        }

        private static void Enforce()
        {
            var mgr = GameRefs.Global?.dlcManager;
            var datas = mgr?.dlc?.dlcTable?.datas;
            if (datas == null) return;
            for (int i = 0; i < datas.Count; i++)
            {
                var d = datas[i];
                if (d == null || !Locked.Contains(d.runtimePathIndex)) continue;
                // The game's own setter: clears the entry and drops it from the DLC asset list.
                if (mgr.IsAvailable(i)) mgr.SetAvailable(i, false);
            }
        }

        public static string Lock(int pack)
        {
            RequireOutsideStage();
            Locked.Add(pack);
            SavePrefs();
            Enforce();
            _packs = null;
            return $"Locked {Describe(pack)}. Restart the game so menus and DLC music fully refresh.";
        }

        public static string Unlock(int pack)
        {
            RequireOutsideStage();
            Locked.Remove(pack);
            SavePrefs();
            int restored = MakeOwnedEntriesAvailable(pack);
            _packs = null;
            return restored > 0
                ? $"Unlocked {Describe(pack)}. Restart the game so its music and menus load again."
                : $"Unlocked {Describe(pack)}, but Steam doesn't report it as owned, so it stays unavailable.";
        }

        public static string SetOtherGamesLocked(bool locked)
        {
            RequireOutsideStage();
            _otherGamesPref.Value = locked;
            MelonPreferences.Save();
            return locked
                ? "Locked the other-KATAMARI bonus. New games won't get the King until you unlock it."
                : "Unlocked the other-KATAMARI bonus (the King).";
        }

        public static string LockAllOwned()
        {
            RequireOutsideStage();
            var owned = Packs(refresh: true).Where(p => p.Owned).Select(p => p.Id).ToList();
            foreach (int id in owned) Locked.Add(id);
            SavePrefs();
            Enforce();
            _packs = null;
            return owned.Count == 0
                ? "Steam doesn't report any owned DLC, so there was nothing to lock."
                : $"Locked {owned.Count} owned DLC pack(s). Restart the game, then start your new game.";
        }

        public static string UnlockAll()
        {
            RequireOutsideStage();
            var ids = Locked.ToList();
            Locked.Clear();
            SavePrefs();
            int restored = ids.Sum(MakeOwnedEntriesAvailable);
            _packs = null;
            return $"Unlocked all DLC ({restored} owned item(s) back in the game). Restart the game so DLC music and menus load again.";
        }

        /// <summary>Same per-entry logic as DlcSteam.GetDownLoadContent, limited to one pack.</summary>
        private static int MakeOwnedEntriesAvailable(int pack)
        {
            var dlc = GameRefs.Global?.dlcManager?.dlc;
            var datas = dlc?.dlcTable?.datas;
            var dict = dlc?.dictDlcAvailable;
            if (datas == null || dict == null) return 0;
            int n = 0;
            for (int i = 0; i < datas.Count; i++)
            {
                var d = datas[i];
                if (d == null || d.runtimePathIndex != pack || !SteamOwns(d.steamAppID)) continue;
                dict[d.nameOrAddress] = d;
                string name = d.nameOrAddress ?? "";
                var assets = DlcBase.dlc_asset_names;
                if (assets != null && (name.Contains(".acb") || name.Contains(".awb")) && !assets.Contains(name)) assets.Add(name);
                n++;
            }
            return n;
        }

        public static List<DlcPack> Packs(bool refresh = false)
        {
            if (!refresh && _packs != null && Time.unscaledTime - _packsAt < 1.5f) return _packs;
            var result = new Dictionary<int, DlcPack>();
            var mgr = GameRefs.Global?.dlcManager;
            var datas = mgr?.dlc?.dlcTable?.datas;
            if (datas != null)
            {
                var names = SteamDlcNames();
                for (int i = 0; i < datas.Count; i++)
                {
                    var d = datas[i];
                    if (d == null) continue;
                    if (!result.TryGetValue(d.runtimePathIndex, out var pack))
                        result[d.runtimePathIndex] = pack = new DlcPack { Id = d.runtimePathIndex, Label = PackLabel(d.runtimePathIndex) };
                    pack.Entries.Add(new DlcEntry { TableIndex = i, Name = d.nameOrAddress, AppId = d.steamAppID });
                    if (d.steamAppID != 0 && names.TryGetValue(d.steamAppID, out string steamName) && string.IsNullOrEmpty(pack.SteamName))
                        pack.SteamName = ShortName(steamName);
                    if (SteamOwns(d.steamAppID)) pack.Owned = true;
                    if (mgr.IsAvailable(i)) pack.AvailableNow = true;
                }
            }
            foreach (var p in result.Values) p.Locked = Locked.Contains(p.Id);
            _packs = result.Values.OrderBy(p => p.Id == 4 ? -10 : p.Id).ToList();
            _packsAt = Time.unscaledTime;
            return _packs;
        }

        /// <summary>Whether Steam reports the game has detected ownership of another KATAMARI game.</summary>
        public static string OtherGamesDetected()
        {
            var save = GameRefs.Save;
            if (save == null) return "unknown";
            var found = new List<string>();
            if (save.is_exist_katamaridamacy != 0) found.Add("Katamari Damacy REROLL");
            if (save.is_exist_welovekatamari != 0) found.Add("We Love Katamari REROLL+");
            return found.Count == 0 ? "none detected" : string.Join(" + ", found);
        }

        /// <summary>Name for a pack, preferring Steam's (e.g. "Rolling Live Highlights" for the AA stages).</summary>
        public static string PackName(int id) =>
            Packs().FirstOrDefault(p => p.Id == id)?.DisplayName ?? PackLabel(id);

        private static string ShortName(string steamName)
        {
            const string prefix = "Once Upon A KATAMARI";
            string s = steamName.Trim();
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                s = s.Substring(prefix.Length).TrimStart(' ', '-', '–', '—', ':');
            return s.Length > 0 ? s : steamName;
        }

        public static string PackLabel(int id) => id switch
        {
            4 => "AA stages + music",
            >= 0 and <= 3 => $"Music pack {id + 1}",
            -1 => "Bonus presents",
            -2 => "Collab pack 1",
            -3 => "Collab pack 2",
            -4 => "Collab pack 3",
            _ => $"DLC pack {id}",
        };

        private static string Describe(int pack) => PackName(pack);

        private static bool SteamOwns(uint appId)
        {
            if (appId == 0) return false;
            try { return SteamManager.Initialized && SteamApps.BIsDlcInstalled(new AppId_t { m_AppId = appId }); }
            catch { return false; }
        }

        private static Dictionary<uint, string> SteamDlcNames()
        {
            if (_steamNames != null) return _steamNames;
            var map = new Dictionary<uint, string>();
            try
            {
                if (!SteamManager.Initialized) return map;
                int count = SteamApps.GetDLCCount();
                for (int i = 0; i < count; i++)
                {
                    if (SteamApps.BGetDLCDataByIndex(i, out AppId_t appId, out bool _, out string name, 256) && !string.IsNullOrEmpty(name))
                        map[appId.m_AppId] = name;
                }
                _steamNames = map;
            }
            catch (Exception e) { MelonLogger.Warning($"Could not read DLC names from Steam: {e.Message}"); }
            return map;
        }

        private static void SavePrefs()
        {
            _lockedPref.Value = string.Join(",", Locked.OrderBy(x => x));
            MelonPreferences.Save();
        }

        private static void RequireOutsideStage()
        {
            if (GameRefs.Main != null) throw new InvalidOperationException("Leave the stage first; change DLC locks from the Title screen or the Plaza.");
        }
    }
}
