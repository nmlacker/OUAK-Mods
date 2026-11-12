using System;
using System.IO;
using System.Linq;
using Il2Cpp;
using Il2CppApp.KatamariSin;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using OUAKModAPI;

namespace OUAKModMenu
{
    /// <summary>
    /// Smoke test, armed by creating UserData/OUAKModMenu/selftest.flag before launch. Logs what the mod can
    /// see, makes one backup (a copy of the save files; the saves themselves are never written), opens the
    /// menu on every tab and shows the confirmation dialog without confirming it.
    /// </summary>
    internal static class SelfTest
    {
        private static string FlagPath => Path.Combine(MelonEnvironment.UserDataDirectory, "OUAKModMenu", "selftest.flag");

        public static bool Armed { get; private set; }
        private static float _nextStep;
        private static int _step = -1;

        public static void CheckFlag()
        {
            if (!File.Exists(FlagPath)) return;
            Armed = true;
            try { File.Delete(FlagPath); } catch { }
            MelonLogger.Msg("[SelfTest] armed");
        }

        /// <summary>Returns the id of the tab to show, or null when the window should stay as-is.</summary>
        public static string Tick()
        {
            if (!Armed || Time.unscaledTime < _nextStep) return null;
            if (_step < 0)
            {
                // Wait for the title screen so the save data and Steam DLC check are ready. With existing
                // saves the logo goes straight to "SelectData", which is also the title screen.
                string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                if (GameRefs.Global == null || (scene != "Title" && scene != "SelectData"))
                {
                    _nextStep = Time.unscaledTime + 1f;
                    return null;
                }
                Report();
                ModMenu.Open();
            }
            _step++;
            _nextStep = Time.unscaledTime + 2.5f;

            // Every tab in the window: this menu's and those other mods added (the API's, Online...).
            var tabs = ModMenu.Tabs;
            if (_step < tabs.Count)
            {
                MelonLogger.Msg($"[SelfTest] showing tab {_step} ({tabs[_step].Title}, from {tabs[_step].Owner}); last picture: {FrameCapture.Last}");
                // A picture of the game's frame with the tab drawn (UserData/OUAKModMenu/TestShots).
                string id = tabs[_step].Id;
                // Freebies: one hover text shown without the mouse (the test never moves the pointer), all of them logged.
                Menu.ForceFreebieTip = id == "freebies" ? 0 : -1;
                if (id == "freebies") MelonLogger.Msg("[SelfTest] freebie hover texts: " + Menu.AllFreebieTips());
                ModTasks.Later(() => FrameCapture.Request("tab_" + id), 1.2f);
                return id;
            }
            if (_step == tabs.Count)
            {
                ConfirmDialog.Ask(new ConfirmRequest
                {
                    Title = "Self-test dialog (never confirmed)",
                    Body = "This is how the confirmation looks before anything edits, moves or restores saves.",
                    Action = _ => "self-test",
                });
                ConfirmDialog.PhraseField.Text = "I understand";
                MelonLogger.Msg($"[SelfTest] showing tab {_step} (confirm dialog, phrase accepted: {ConfirmDialog.PhraseAccepted})");
                return null;
            }
            ConfirmDialog.Close();
            Menu.ForceFreebieTip = -1;
            Armed = false;
            MelonLogger.Msg("[SelfTest] DONE");
            return tabs.Count > 0 ? tabs[0].Id : null;
        }

        private static void Report()
        {
            void Check(string name, Func<object> f)
            {
                try { MelonLogger.Msg($"[SelfTest] {name}: {f()}"); }
                catch (Exception e) { MelonLogger.Error($"[SelfTest] {name}: FAILED {e.GetType().Name}: {e.Message}"); }
            }

            Check("scene", () => UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
            Check("managers", () => $"global={GameRefs.Global != null} save={GameRefs.Save != null} ctrl={GameRefs.SaveCtrl != null} sound={GameRefs.Sound != null}");
            Check("session", () => $"SaveLoaded={GameRefs.SaveLoaded} InGameSession={GameRefs.InGameSession} AtTitleScreens={BackupManager.AtTitleScreens}");
            Check("DLC hook installed", () => DlcLock.PatchesActive);
            Check("Steam initialized", () => SteamManager.Initialized);
            Check("DLC packs", () => string.Join(" | ", DlcLock.Packs(refresh: true).Select(p =>
                $"{p.Id}:{p.DisplayName} owned={p.Owned} locked={p.Locked} available={p.AvailableNow} entries=[{string.Join(", ", p.Entries.Select(e => $"{e.Name}#{e.AppId}"))}]")));
            Check("other KATAMARI games", DlcLock.OtherGamesDetected);
            Check("maps", () =>
            {
                var all = StageCatalog.Entries;
                return $"{all.Count} entries\n" + string.Join("\n", all.GroupBy(e => e.Group).Select(grp =>
                    $"  [{grp.Key}] " + string.Join(" | ", grp.Select(e => $"{e.Name} <{e.Internal}>"))));
            });
            Check("hotkeys", () => string.Join(", ", Hotkeys.Actions.Select(a => $"{a.Id}={a.Binding}")));
            Check("window tabs", () => string.Join(", ", ModMenu.Tabs.Select(t => $"{t.Title} ({t.Owner})")));
            Check("pause menu hooks", () => PauseMenu.HooksActive);
            Check("Steam", () => Steam.Available ? $"signed in, persona name {Steam.MyName.Length} characters" : "not available");
            Check("hot mods", () => string.Join(", ", HotMods.Entries.Select(e => $"{System.IO.Path.GetFileName(e.File)}: {e.Error ?? string.Join("+", e.Mods.Select(m => m.Name))}")));
            Check("auto freebie", () => $"{StageCheats.AutoFreebie} / {StageCheats.AutoItem}");
            Check("existing backups", () => BackupManager.List(refresh: true).Count);
        }
    }
}
