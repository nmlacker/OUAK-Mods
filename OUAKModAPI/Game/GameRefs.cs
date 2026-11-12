using System;
using Il2Cpp;
using Il2CppApp.KatamariSin;
using MelonLoader;
using UnityEngine;

namespace OUAKModAPI
{
    /// <summary>Null-safe access to the game's long-lived managers.</summary>
    public static class GameRefs
    {
        private static GlobalManager _global;

        public static string SceneName { get; set; } = "";

        public static GlobalManager Global
        {
            get
            {
                if (_global != null)
                    return _global;
                _global = null;
                try { _global = SingletonMonoBehaviour<GlobalManager>.instance; } catch { }
                if (_global == null)
                {
                    try { _global = UnityEngine.Object.FindObjectOfType<GlobalManager>(); } catch { }
                }
                return _global;
            }
        }

        public static MainGameManager Main
        {
            get
            {
                var g = Global;
                if (g == null) return null;
                var gm = g.GameMan;
                return gm != null ? gm : null;
            }
        }

        public static GlobalSaveData Save
        {
            get
            {
                var g = Global;
                GlobalSaveData s = null;
                if (g != null) s = g.glbSave;
                if (s == null)
                {
                    try { s = SingletonMonoBehaviour<GlobalSaveData>.instance; } catch { }
                }
                return s != null ? s : null;
            }
        }

        public static SaveDataController SaveCtrl
        {
            get
            {
                SaveDataController c = null;
                try { c = SingletonMonoBehaviour<SaveDataController>.instance; } catch { }
                return c != null ? c : null;
            }
        }

        public static SoundManager Sound
        {
            get
            {
                SoundManager s = null;
                try { s = SingletonMonoBehaviour<SoundManager>.instance; } catch { }
                return s != null ? s : null;
            }
        }

        /// <summary>True once a save slot has been chosen and loaded into the game.</summary>
        public static bool SaveLoaded
        {
            get
            {
                var c = SaveCtrl;
                return c != null && Save != null && c.SelectedData && c.CurrentUseSlot >= 0;
            }
        }

        /// <summary>
        /// True once a slot is loaded and the player is past the title / save-select screens. The save
        /// controller keeps its "selected" flag after returning to the title, so the scene is checked too.
        /// </summary>
        public static bool InGameSession
        {
            get
            {
                if (!SaveLoaded) return false;
                string s = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                return s != "Title" && s != "SelectData" && s != "SceneLogo";
            }
        }

        /// <summary>True while a single-player stage is loaded and the player has spawned.</summary>
        public static bool InStage
        {
            get
            {
                var gm = Main;
                return gm != null && gm.PlayerInitiated && !IsMultiplay;
            }
        }

        /// <summary>The multiplayer / training match manager (MultiplayGameManager), or null outside matches.</summary>
        public static MultiplayGameManager Multiplay
        {
            get
            {
                MultiplayGameManager m = null;
                try { m = SingletonMonoBehaviour<MultiplayGameManager>.instance; } catch { }
                return m != null ? m : null;
            }
        }

        public static bool IsMultiplay
        {
            get
            {
                var g = Global;
                return g != null && g.isMultiPlay;
            }
        }

        public static bool OwnsDlcPack(DlcManager.DLC_PACKS pack)
        {
            try
            {
                var dlc = Global?.dlcManager;
                return dlc != null && dlc.IsAvailableDLCPack(pack);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"DLC check for {pack} failed: {e.Message}");
                return false;
            }
        }

        /// <summary>The "AA" expansion stages are gated behind DLC pack 4 by the game itself.</summary>
        public static bool OwnsAA => OwnsDlcPack((DlcManager.DLC_PACKS)4);

        public static bool OwnsAnyCollab =>
            OwnsDlcPack(DlcManager.DLC_PACKS.DLC_COLLABO1) ||
            OwnsDlcPack(DlcManager.DLC_PACKS.DLC_COLLABO2) ||
            OwnsDlcPack(DlcManager.DLC_PACKS.DLC_COLLABO3);
    }
}
