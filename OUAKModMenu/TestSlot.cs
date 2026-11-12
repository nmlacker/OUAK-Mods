using System;
using Il2Cpp;
using Il2CppApp.KatamariSin;
using MelonLoader;
using UnityEngine.SceneManagement;
using OUAKModAPI;

namespace OUAKModMenu
{
    /// <summary>Test helper: loads a save slot from the title screen the way its "continue" does (after a backup).</summary>
    internal static class TestSlot
    {
        /// <summary>Backs the saves up, then loads the slot the title screen marks as last used, the way its "continue" does (to the ship).</summary>
        public static void LoadLastUsedSlot(SaveDataController ctrl, string backupReason, string tag, int forceSlot = -1)
        {
            var data = ctrl.GameData;
            int slot = forceSlot >= 0 ? forceSlot : data._SystemData._LastUser;
            var users = data._UserData?._UserDatas;
            if (users == null || slot < 0 || slot >= users.Length) throw new InvalidOperationException($"no last-used slot ({slot})");
            var progress = users[slot]?._PriorityData;
            if (progress == null || progress._GameProgression <= 0) throw new InvalidOperationException($"slot {slot + 1} has no game in progress");

            BackupManager.Create(backupReason, $"made before the {backupReason.ToLowerInvariant()} loaded slot {slot + 1}");
            MelonLogger.Msg($"{tag} loading slot {slot + 1}");
            ctrl.SelectLoadDataToGameData(slot);
            var save = GameRefs.Save;
            save.SetUseDataLogoMonoGetFlg();
            OujiShipSponePointList.s_SponeIndex = OujiShipSponePointList.ElevatorSpone;
            save.FrontSceneSelectData = true;
            SingletonMonoBehaviour<EncorsSaveDataChecker>.Instance.CheckEncorsContentsData();
            GameRefs.Sound?.StopBGM(0.5f);
            Il2Cpp.SceneManager.LoadSceneAsync("OujiShip", LoadSceneMode.Single, true);
            MelonLogger.Msg($"{tag} loaded: selected={ctrl.SelectedData} slot={ctrl.CurrentUseSlot + 1}");
        }
    }
}
