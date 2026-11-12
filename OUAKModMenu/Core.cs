using System;
using System.Linq;
using MelonLoader;
using OUAKModAPI;
using UnityEngine;

[assembly: MelonInfo(typeof(OUAKModMenu.Core), "OUAK Mod Menu", OUAKModMenu.Core.Version, "OUAKModMenu")]
[assembly: MelonGame("Bandai Namco Entertainment Inc.", "Once Upon A KATAMARI")]
[assembly: MelonAdditionalDependencies("OUAKModAPI")]

namespace OUAKModMenu
{
    /// <summary>
    /// The OUAK Mod Menu: cheats and tools (freebies, the stage clock, every map and mode, hotkeys, unlocks, DLC
    /// locking, save backups) as pages of the OUAK Modding API's mod window. Single-player only (speedrunning, TASing,
    /// messing around): nothing here changes KatamariBall or multiplayer.
    /// </summary>
    public sealed class Core : MelonMod
    {
        public const string Version = "1.5.0";
        public const string ModName = "OUAK Mod Menu";

        private readonly Menu _menu = new();

        /// <summary>The status line the window shows (kept for older code; it's the API's).</summary>
        public static string Status => ModTasks.Status;

        public override void OnInitializeMelon()
        {
            var cat = ApiCore.Category("OUAKModMenu", "OUAK Mod Menu");
            var keys = ApiCore.Category("OUAKModMenu_Keys", "OUAK Mod Menu key bindings");
            StageCheats.Init(cat, HarmonyInstance);
            BoxRules.Init(cat);
            // Shoes/Crown/Mushroom in story stages and the Random fix are the API's patches, always part of the API and
            // acting only in their own context (the user: "The Mod Menu should not manage what patches are 'on' or
            // 'off'"). The menu just gives the freebies. FreebieEffects.cs is its old copy (Moved/, not built).
            StoryFreebies.Applied += (item, what) => ModTasks.Toast($"{StageControl.Pretty(item)}: {what}");
            RegisterHotkeys(keys);
            DlcLock.Init(cat, HarmonyInstance);
            // The API asks the menu about DLC locks (wording of its messages, what backups record).
            SceneLoader.IsDlcLocked = DlcLock.IsLocked;
            BackupManager.ModVersion = Version;
            BackupManager.DlcState = () =>
            {
                var packs = DlcLock.Packs(refresh: true).Select(p => (p.DisplayName, p.Owned, p.Locked)).ToList();
                if (DlcLock.OtherGamesLocked) packs.Add((DlcLock.OtherGamesLabel, true, true));
                return packs;
            };
            _menu.Register();
            LoggerInstance.Msg($"Loaded. Press {Hotkeys.MenuKeyName} to open the menu.");
            SelfTest.CheckFlag();
            FreebieTest.CheckFlag();
            ItemTest.CheckFlag();
            DlcDump.CheckFlag();
            TextDump.CheckFlag();
            BattleFreebieProbe.CheckFlag();
        }

        private static void RegisterHotkeys(MelonPreferences_Category cat)
        {
            const string me = "OUAK Mod Menu";
            Hotkeys.Register(cat, "FreezeTimer", "Toggle: freeze stage timer", "None", StageCheats.ToggleFreezeTimer, me);
            Hotkeys.Register(cat, "FreezeFreebie", "Toggle: freeze freebie", "None", StageCheats.ToggleFreezeFreebie, me);
            Hotkeys.Register(cat, "AutoFreebie", "Toggle: spawn with the chosen freebie", "None", StageCheats.ToggleAutoFreebie, me);
            Hotkeys.Register(cat, "GiveAutoFreebie", "Give the auto freebie now", "None", StageCheats.GiveAutoItem, me);
            Hotkeys.Register(cat, "GiveRandomFreebie", "Give a random freebie", "None", () => StageCheats.GiveFreebie(Il2CppApp.KatamariSin.eInstageItemType.RandomItem), me);
            Hotkeys.Register(cat, "ClearFreebie", "Clear freebie", "None", StageCheats.ClearFreebie, me);
            Hotkeys.Register(cat, "CompleteTimer", "Complete stage timer (time's up)", "None", StageCheats.CompleteTimer, me);
            Hotkeys.Register(cat, "FinishStage", "Finish stage (instant clear)", "None", StageCheats.FinishStage, me);
            Hotkeys.Register(cat, "RestartStage", "Restart stage", "None", StageCheats.RestartStage, me);
            Hotkeys.Register(cat, "ReloadStage", "Reload stage (full)", "None", SceneLoader.ReloadCurrentStage, me);
            Hotkeys.Register(cat, "ReturnToPlaza", "Return to the Plaza", "None", StageCheats.ReturnToPlaza, me);
        }

        public override void OnUpdate()
        {
            if (SelfTest.Armed)
            {
                string tab = SelfTest.Tick();
                if (tab != null) ModMenu.CurrentTab = tab;
                if (SelfTest.Armed) ModMenu.Open();
            }

            try { StageCheats.Tick(); }
            catch (Exception e) { LoggerInstance.Warning($"Cheat tick failed: {e.Message}"); }

            DlcLock.Tick();
            DlcDump.Tick();
            TextDump.Tick();
            BattleFreebieProbe.Tick();
            FreebieTest.Tick();
            ItemTest.Tick();
        }

        /// <summary>The Plaza keeps its era map as the active scene, so look for the hub scene itself.</summary>
        public static void ReloadPlazaIfThere()
        {
            bool inPlaza = UnityEngine.SceneManagement.SceneManager.GetSceneByName("SelectHiroba").isLoaded;
            if (!inPlaza || GameRefs.Main != null) return;
            // Give the async save a moment to start before the scene swap.
            ModTasks.Run(() =>
            {
                Il2Cpp.SceneManager.LoadSceneAsync("SelectHiroba", UnityEngine.SceneManagement.LoadSceneMode.Single, true);
                return ModTasks.Status + " - reloading the Plaza";
            }, delay: 1.5f);
        }
    }
}
