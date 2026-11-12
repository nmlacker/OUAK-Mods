using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Il2Cpp;
using Il2CppApp.KatamariSin;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using OUAKModAPI;

namespace OUAKModMenu
{
    /// <summary>
    /// Unlocks all progression on the loaded save slot and restores it again.
    /// The first unlock on a slot makes a backup that includes a snapshot of the slot; that backup is
    /// remembered as the slot's "pre-unlock" state and Relock writes it back through the game's converter.
    /// Content from DLC that is locked or not owned is skipped.
    /// </summary>
    internal static class Unlocker
    {
        private const int LastBaseStar = (int)StarIDEnum.STAR_ETERNAL_3;
        private const int FirstAAStar = (int)StarIDEnum.STAR_AA_BIG2_1;
        private const int LastAAStar = (int)StarIDEnum.STAR_AA_LIVESTAGE_1;
        private const int FirstCollabStar = (int)StarIDEnum.STAR_DLC_11;
        private const int LastCollabStar = (int)StarIDEnum.STAR_DLC_30;

        private static string StatePath => Path.Combine(BackupManager.ModDataDir, "unlock_state.json");

        public static int CurrentSlot => GameRefs.SaveCtrl?.CurrentUseSlot ?? -1;

        /// <summary>The backup Relock will restore for the loaded slot, if there is one.</summary>
        public static BackupInfo PreUnlockBackup(int slot)
        {
            if (slot < 0) return null;
            return LoadState().TryGetValue(slot, out string id) ? BackupManager.Find(id) : null;
        }

        public static string UnlockAll(string note)
        {
            var save = RequireSaveOutsideStage();
            int slot = CurrentSlot;

            var backup = BackupManager.Create($"Before unlocking everything (slot {slot + 1})", note);
            BackupManager.WriteLoadedSlotSnapshot(backup);
            var state = LoadState();
            bool firstUnlock = !state.ContainsKey(slot) || BackupManager.Find(state[slot]) == null;
            if (firstUnlock)
            {
                state[slot] = backup.Id;
                SaveState(state);
            }

            bool ownsAA = GameRefs.OwnsAA;
            bool ownsCollab = GameRefs.OwnsAnyCollab;
            var failures = new List<string>();
            void Step(string name, Action a)
            {
                try { a(); }
                catch (Exception e) { failures.Add(name); MelonLogger.Warning($"Unlock step '{name}' failed: {e.Message}"); }
            }

            // The developers' own progress helpers (OutGameDebug) set story/event/movie flags consistently.
            Step("story", OutGameDebug.AllClearSaveData);
            Step("post-game", OutGameDebug.PlusBig5SaveData);
            Step("post-game Big4", OutGameDebug.PlusReleaseStageBig4);
            Step("post-game Big5", OutGameDebug.PlusReleaseStageBig5);
            if (ownsAA) Step("AA story", OutGameDebug.AASaveData);

            // Clear every stage at top rank with its crowns, cousins and present.
            for (int star = 1; star < (int)StarIDEnum.STAR_MAX; star++)
            {
                if (!StarAllowed(star, ownsAA, ownsCollab)) continue;
                var id = (StarIDEnum)star;
                Step($"stage {id}", () => OutGameDebug.SingleResultDataEdit(id));
                Step($"rank {id}", () => save.SetStageEvaluation(star, 4));
            }

            Step("presents", () => UnlockPresents(save));
            Step("cousins", () => UnlockCousins(save, ownsAA));
            Step("collection", () => UnlockCollection(save, ownsAA));
            Step("music", () => FillAll(save._bgmReleasables));
            Step("emotes", () => FillAll(save._emoteFlag));
            Step("rooms", () => FillAll(save.m_UnlockRoomFlgs));
            Step("movies", () => FillAll(save._palyMovie));
            Step("tips", () => FillAll(save.tips_get));
            Step("faces", () => FillAll(save._faceSkin));
            Step("skins", () => FillAll(save._rewardSkin));
            Step("treasures", () => FillAll(save._treasureFlag));
            Step("sports ages", () => { FillAll(save.m_IsClearSportsAges); FillAll(save.m_CanPlayedSportsAges); });
            Step("save logos", () => FillAll(save.m_SaveLogoGetArray));

            GameRefs.SaveCtrl.SetSaveAction(slot, false);
            StageCatalog.Invalidate();

            string msg = $"Unlocked everything on slot {slot + 1}. Backup: {backup.Id}";
            if (!ownsAA || !ownsCollab) msg += " (locked / unowned DLC skipped)";
            if (failures.Count > 0) msg += $"; {failures.Count} step(s) failed, see MelonLoader log";
            return msg;
        }

        public static string Relock(string note)
        {
            RequireSaveOutsideStage();
            int slot = CurrentSlot;
            var target = PreUnlockBackup(slot);
            if (target == null) throw new InvalidOperationException($"No pre-unlock backup is recorded for slot {slot + 1}.");

            var safety = BackupManager.Create($"Before relocking (slot {slot + 1})", note);
            BackupManager.WriteLoadedSlotSnapshot(safety);
            BackupManager.RestoreLoadedSlotSnapshot(target);
            StageCatalog.Invalidate();

            var state = LoadState();
            state.Remove(slot);
            SaveState(state);
            return $"Relocked slot {slot + 1} to its progress from {target.Created:yyyy-MM-dd HH:mm}. Backup of the unlocked state: {safety.Id}";
        }

        private static bool StarAllowed(int star, bool ownsAA, bool ownsCollab)
        {
            if (star <= LastBaseStar) return true;
            if (star >= FirstAAStar && star <= LastAAStar) return ownsAA;
            if (star >= FirstCollabStar && star <= LastCollabStar) return ownsCollab;
            return false;
        }

        private static void UnlockPresents(GlobalSaveData save)
        {
            var flags = save.PresentFlag;
            var skip = new HashSet<int>();
            // Game.CheckDLCPresent pairs pack N with SelectHirobaConstants.PresentIDDLC0(N+1).
            Il2CppStructArray<int>[] tables =
            {
                SelectHirobaConstants.PresentIDDLC01, SelectHirobaConstants.PresentIDDLC02,
                SelectHirobaConstants.PresentIDDLC03, SelectHirobaConstants.PresentIDDLC04,
                SelectHirobaConstants.PresentIDDLC05,
            };
            for (int pack = 0; pack < tables.Length; pack++)
            {
                if (tables[pack] == null || GameRefs.OwnsDlcPack((DlcManager.DLC_PACKS)pack)) continue;
                foreach (int id in tables[pack]) skip.Add(PresentIndex(id));
            }
            for (int i = 0; i < flags.Length; i++)
                if (!skip.Contains(i)) flags[i] = true;
        }

        // Mirrors GlobalSaveData.SetPresentFlag: IDs 0-40 map directly, IDs 100+ are stored at ID - 60.
        private static int PresentIndex(int id) => id >= 100 ? id - 60 : id;

        private static void UnlockCousins(GlobalSaveData save, bool ownsAA)
        {
            var flags = save.OujiItokoGet;
            var skip = new HashSet<int>();
            if (!ownsAA)
            {
                foreach (var name in Game.AAItokoGroup) skip.Add((int)name);
            }
            // The King is the bonus for owning Katamari Damacy REROLL or We Love Katamari REROLL+
            // (TableCustomData.OldGameDataSetModel); only grant him when the game detected that and it isn't locked.
            bool ownsOtherGame = save.is_exist_katamaridamacy != 0 || save.is_exist_welovekatamari != 0;
            if (!ownsOtherGame || DlcLock.OtherGamesLocked) skip.Add((int)ItokoSpawnData.ITOKONAME.KING);
            for (int i = 0; i < flags.Length; i++)
                if (!skip.Contains(i)) flags[i] = true;
        }

        private static void UnlockCollection(GlobalSaveData save, bool ownsAA)
        {
            var dict = save.MonoGetFlagDic;
            if (dict == null) return;
            var ids = new List<int>();
            foreach (int id in dict.Keys) ids.Add(id);
            foreach (int id in ids)
            {
                // GetListIndexFromMonoID puts the AA expansion's objects in the 20000+ range.
                if (!ownsAA && id >= 20000) continue;
                save.SetMonoGetFlagFromMonoID(id, true);
            }
        }

        private static void FillAll(Il2CppStructArray<bool> arr)
        {
            if (arr == null) return;
            for (int i = 0; i < arr.Length; i++) arr[i] = true;
        }

        private static void FillAll(Il2CppStructArray<byte> arr)
        {
            if (arr == null) return;
            for (int i = 0; i < arr.Length; i++) arr[i] = 1;
        }

        private static GlobalSaveData RequireSaveOutsideStage()
        {
            if (!GameRefs.InGameSession) throw new InvalidOperationException("Load a save file first.");
            if (GameRefs.Main != null) throw new InvalidOperationException("Leave the stage first (use it from the Plaza).");
            return GameRefs.Save;
        }

        private static Dictionary<int, string> _state;

        // Slot -> id of the backup taken before that slot's first unlock. Read once, then kept in memory.
        private static Dictionary<int, string> LoadState()
        {
            if (_state != null) return new Dictionary<int, string>(_state);
            _state = new Dictionary<int, string>();
            try
            {
                if (File.Exists(StatePath))
                    _state = JsonSerializer.Deserialize<Dictionary<int, string>>(File.ReadAllText(StatePath)) ?? new();
            }
            catch (Exception e) { MelonLogger.Warning($"unlock_state.json unreadable: {e.Message}"); }
            return new Dictionary<int, string>(_state);
        }

        private static void SaveState(Dictionary<int, string> state)
        {
            _state = new Dictionary<int, string>(state);
            Directory.CreateDirectory(BackupManager.ModDataDir);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
