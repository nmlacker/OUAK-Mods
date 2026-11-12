using System;
using System.Linq;
using Il2Cpp;
using Il2CppApp.KatamariSin;
using Il2CppIceMilkTea.Core;
using MelonLoader;
using UnityEngine;
using UnityEngine.SceneManagement;
using eStage = Il2CppApp.KatamariSin.MainGameManager.eStage;

namespace OUAKModAPI
{
    public enum ModeAction { Scene, Cosmos, MultiplayerRoom }

    /// <summary>
    /// A mod's say before a map loads through <see cref="SceneLoader.Load"/>: return true to take the load over
    /// (an online session holds it for a vote, for instance) and set <paramref name="message"/> for the status line.
    /// </summary>
    public delegate bool LoadInterceptor(MapEntry entry, out string message);

    public sealed class ModeEntry
    {
        public string Label;
        public string Scene;
        public bool NeedsAA;
        public ModeAction Action = ModeAction.Scene;
    }

    /// <summary>
    /// Loads stages, multiplayer maps, plazas and top-level scenes the way the game's own menus do (the same
    /// fields set, the same scene hand-offs), so the game behaves as if the player had walked there. Every load
    /// needs a save to be loaded first (the default save slot could otherwise be written over).
    /// </summary>
    public static class SceneLoader
    {
        private const string Ship = "OujiShip";

        /// <summary>Asked in order before <see cref="Load"/> loads a map; the first to return true takes it over.</summary>
        public static readonly System.Collections.Generic.List<LoadInterceptor> Interceptors = new();

        /// <summary>Set by a mod that can lock owned DLC (the DLC id is the game's DLC_PACKS number): only changes the wording of "you don't have it".</summary>
        public static Func<int, bool> IsDlcLocked;

        /// <summary>Runs <paramref name="load"/> without asking the interceptors (a load they already approved).</summary>
        public static string Approved(Func<string> load)
        {
            _approved++;
            try { return load(); }
            finally { _approved--; }
        }

        private static int _approved;

        private static bool Intercepted(MapEntry e, out string msg)
        {
            msg = null;
            if (_approved > 0) return false;
            foreach (var f in Interceptors.ToArray())
            {
                try { if (f(e, out msg)) return true; }
                catch (Exception ex) { MelonLogger.Warning($"Load interceptor {f.Method.DeclaringType?.Name}: {ex.Message}"); }
            }
            return false;
        }

        public static readonly ModeEntry[] Modes =
        {
            new() { Label = "Title Screen", Scene = "Title" },
            new() { Label = "Save Select", Scene = "SelectData" },
            new() { Label = "Plaza (hub)", Scene = "SelectHiroba" },
            new() { Label = "Prince's Ship", Scene = Ship },
            new() { Label = "Ending Game", Scene = "EndingGame" },
            new() { Label = "Multiplayer Room (Bugged: opens the ship's multiplayer menu)", Scene = "MultiRoom", Action = ModeAction.MultiplayerRoom },
            new() { Label = "Cosmos", Scene = "Cosmo", Action = ModeAction.Cosmos },
            new() { Label = "Collection", Scene = "SutekiCollection" },
            new() { Label = "Collection (AA)", Scene = "SutekiCollectionAA", NeedsAA = true },
            new() { Label = "Memorial", Scene = "Memorial" },
            new() { Label = "Achievements", Scene = "Achievements" },
            new() { Label = "Character Customize", Scene = "CharaCustom" },
            new() { Label = "Dev Stage Launcher", Scene = "StageLauncher" },
        };

        public static string ActiveScene => UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;

        /// <summary>Loads any map from <see cref="StageCatalog"/> (stage, multiplayer map or plaza).</summary>
        public static string Load(MapEntry e) => Intercepted(e, out var msg) ? msg : e.Kind switch
        {
            MapKind.Multiplayer => LoadMultiplayerMap(e),
            MapKind.Plaza => LoadPlaza(e),
            _ => LoadStage(e),
        };

        public static string LoadStage(MapEntry entry)
        {
            RequireSession();
            RequireAA(entry.NeedsAA, "stage");

            var g = GameRefs.Global;
            var save = GameRefs.Save;
            LeaveCurrentGame();

            // Mirrors SelectHirobaKingController.KingTalkSwich: MainGameManager.Start() picks the
            // stage from launcherStage + 1 when isFromLauncher is set.
            g.isFromLauncher = true;
            g.launcherStage = (int)entry.Stage - 1;
            g._nowCharacterID = save.GetSelectPlayerId;
            g.spaceSkipEnding = false;
            Il2Cpp.SceneManager.LoadSceneAsync("MainGame", LoadSceneMode.Single, true);
            SelectHirobaLoad._gameLastPlay = true;
            return $"Loading {entry.Name}...";
        }

        /// <summary>
        /// Multiplayer maps run in the game's offline training mode (the same call offline Sports Katamari makes:
        /// MultiplayGameManager.StartTraining). Its scene hand-off expects to start from the Prince's Ship, so a
        /// freshly loaded ship is used every time: no ship menu or event open, the prince free to move.
        /// Race and Target also need their own round rule, which TrainingRules swaps in.
        /// </summary>
        public static string LoadMultiplayerMap(MapEntry entry)
        {
            RequireSession();
            var gimi = (GI_MI)((int)entry.Stage - 1);
            var rule = TrainingRules.RuleFor(gimi);
            if (rule != null && !TrainingRules.PatchActive)
                throw new InvalidOperationException($"{entry.Name} needs the game's {rule} rule, but the mod couldn't hook the game to set it (see the MelonLoader log).");
            string what = rule == NetworkBattleRule.race ? $"a race on {entry.Name}" : $"training on {entry.Name}";
            TrainingTrace.Begin(what);
            GoToShipThen(() => PrepareTraining(gimi, what));
            return $"Going to the Prince's Ship, then starting {what}...";
        }

        private const int TrainingEvent = (int)MultiplayGameManager.StateEventId.Training;

        /// <summary>
        /// The ship's menu creates the multiplayer manager when you pick a mode, seconds before you choose a map,
        /// so by then it has finished loading and its state machine sits in a state that takes the Training event.
        /// StartTrainingMode ends with SendEvent(Training), and a state without that transition ignores it, which
        /// leaves the screen black. So: create the manager, wait until it would accept Training, then start. A
        /// manager left mid-round by a failed or abandoned launch is released the way the game leaves multiplayer,
        /// and a fresh one is made.
        /// </summary>
        private static string PrepareTraining(GI_MI gimi, string what)
        {
            if (ActiveScene != Ship) throw new InvalidOperationException($"Couldn't reach the Prince's Ship, so {what} wasn't started.");
            var old = SingletonMonoBehaviour<MultiplayGameManager>.instance;
            if (old != null && !old.isLoading && !TrainingReady(old))
            {
                TrainingTrace.Note($"releasing a multiplayer manager left in {TrainingTrace.StateName(old.stateMachine)}");
                old.Release(true);
            }
            // A moment later, so a released manager is gone before the new one is made.
            ModTasks.RunWhen(() => true, () =>
            {
                var m = SingletonMonoBehaviour<MultiplayGameManager>.Instance;
                ModTasks.RunWhen(() => TrainingReady(m), () =>
                {
                    if (!TrainingReady(m))
                        throw new InvalidOperationException($"The multiplayer mode didn't get ready ({TrainingTrace.StateName(m?.stateMachine)}), so {what} wasn't started. Restart the game and try again.");
                    m.StartTraining(gimi, false);
                    return $"Starting {what}...";
                }, timeout: 20f);
                return $"Preparing {what}...";
            }, timeout: 1f);
            return $"Preparing {what}...";
        }

        private static bool TrainingReady(MultiplayGameManager m)
        {
            if (m == null || m.isLoading || m.isStartTrainingCoroutine) return false;
            var sm = m.stateMachine;
            return sm != null && sm.nextState == null && AcceptsTraining(sm);
        }

        /// <summary>True when the state machine's current state (or its any-state table) has a Training transition.</summary>
        public static bool AcceptsTraining(ImtStateMachine<MultiplayGameManager> sm)
        {
            if (sm == null || !sm.Running) return false;
            if (sm.currentState?.transitionTable?.ContainsKey(TrainingEvent) == true) return true;
            var states = sm.stateList;
            if (states == null) return false;
            for (int i = 0; i < states.Count; i++)
            {
                var s = states[i];
                if (s?.TryCast<ImtStateMachine<MultiplayGameManager>.AnyState>() != null && s.transitionTable?.ContainsKey(TrainingEvent) == true)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Leaving a multiplayer round through the mod skips the game's own exit, which would leave
        /// GlobalManager.isMultiPlay set (the next stage would then run as a multiplayer one) and the manager
        /// mid-round. Release it the way the game does when it leaves multiplayer.
        /// </summary>
        private static void LeaveMultiplayer()
        {
            var g = GameRefs.Global;
            if (g == null || !g.isMultiPlay) return;
            var m = SingletonMonoBehaviour<MultiplayGameManager>.instance;
            TrainingTrace.Note("leaving a multiplayer round: releasing the multiplayer manager");
            try { m?.Release(true); }
            catch (Exception e) { MelonLogger.Warning($"Multiplayer release: {e.Message}"); }
            g.isMultiPlay = false;
        }

        public static string LoadPlaza(MapEntry entry)
        {
            RequireSession();
            RequireAA(entry.NeedsAA, "plaza");
            LeaveCurrentGame();
            // Same as UISelectMapJumpBehavior.SelectMapChange: pick the era, then reload the Plaza.
            GameRefs.Save._stageIndex = SelectHirobaManager.GetStageIndex(entry.PlazaKey);
            StopMusic();
            Il2Cpp.SceneManager.LoadSceneAsync("SelectHiroba", LoadSceneMode.Single, true);
            return $"Loading the {entry.Name} plaza...";
        }

        public static string ReloadCurrentStage()
        {
            var gm = GameRefs.Main;
            if (gm == null || !GameRefs.InStage) throw new InvalidOperationException("You need to be in a stage.");
            var stage = gm.TargetStage;
            var entry = StageCatalog.Entries.FirstOrDefault(s => s.Kind == MapKind.Stage && s.Stage == stage)
                        ?? new MapEntry { Kind = MapKind.Stage, Stage = stage, Name = stage.ToString() };
            return LoadStage(entry);
        }

        public static string LoadMode(ModeEntry mode)
        {
            bool menuScene = mode.Scene is "Title" or "SelectData";
            if (!menuScene) RequireSession();
            RequireAA(mode.NeedsAA, "mode");

            switch (mode.Action)
            {
                case ModeAction.Cosmos:
                    return EnterFromShip("Cosmo", OujiShipSponePointList.CosmoSpone, "Cosmos");
                case ModeAction.MultiplayerRoom:
                    // MultiplayWaitingRoom is only opened by the online states (create / join / random match),
                    // so the scene on its own has no room to show. The ship's terminal is the real way in.
                    if (GameRefs.Main == null && ActiveScene == Ship) return OpenShipMultiplayerMenu();
                    GoToShipThen(OpenShipMultiplayerMenu);
                    return "Going to the Prince's Ship, then opening its multiplayer menu...";
            }

            LeaveCurrentGame();
            Il2Cpp.SceneManager.LoadSceneAsync(mode.Scene, LoadSceneMode.Single, true);
            return $"Loading {mode.Label}...";
        }

        /// <summary>
        /// The ship's EventToSceneTrans path: fade to black, stop the music, remember where to put the prince on
        /// the way back (OujiShipSponePointList.s_SponeIndex), then load the scene.
        /// </summary>
        private static string EnterFromShip(string scene, int spawn, string label)
        {
            string Enter()
            {
                var fade = SingletonMonoBehaviour<UISceneFadeController>.Instance;
                if (fade != null) fade.FadeInScene(false);
                ModTasks.RunWhen(() => fade == null || fade.EndFade, () =>
                {
                    StopMusic();
                    OujiShipSponePointList.s_SponeIndex = spawn;
                    Il2Cpp.SceneManager.LoadSceneAsync(scene, LoadSceneMode.Single, true);
                    return $"Loading {label}...";
                }, timeout: 4f);
                return $"Going to {label}...";
            }
            if (GameRefs.Main == null && ActiveScene == Ship) return Enter();
            GoToShipThen(Enter);
            return $"Going to the Prince's Ship first, then {label}...";
        }

        /// <summary>
        /// Uses the ship's multiplayer terminal the way walking up to it does: its OrderToBehavior.OrderActiveEvent()
        /// locks the prince, hides the prompts and flags the paired EventToMultiMenuController, which opens the menu
        /// (and calls ReportEndEvent when you back out).
        /// </summary>
        private static string OpenShipMultiplayerMenu()
        {
            if (ActiveScene != Ship) throw new InvalidOperationException("Couldn't reach the Prince's Ship, so the multiplayer menu wasn't opened.");
            foreach (var order in UnityEngine.Object.FindObjectsOfType<OrderToBehavior>())
            {
                var ev = order?.m_EventToBehavior;
                if (ev == null || ev.TryCast<EventToMultiMenuController>() == null) continue;
                if (order.OrderEvent) return "The ship's multiplayer menu is already open.";
                order.OrderActiveEvent();
                return "Opened the ship's multiplayer menu (Create / Join / Random are online; Sports Katamari is offline).";
            }
            throw new InvalidOperationException("Couldn't find the ship's multiplayer terminal. Walk up to it and use it to open the menu.");
        }

        /// <summary>Loads the Prince's Ship and runs <paramref name="next"/> once it has been up for a few seconds.</summary>
        private static void GoToShipThen(Func<string> next)
        {
            LeaveCurrentGame();
            int before = UnityEngine.SceneManagement.SceneManager.GetActiveScene().m_Handle;
            Il2Cpp.SceneManager.LoadSceneAsync(Ship, LoadSceneMode.Single, true);
            float shipSince = -1f;
            ModTasks.RunWhen(() =>
            {
                // Only the newly loaded ship counts: when starting on the ship, the old one stays active until the reload lands.
                var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                if (active.name != Ship || active.m_Handle == before) { shipSince = -1f; return false; }
                if (shipSince < 0f) shipSince = Time.unscaledTime;
                return Time.unscaledTime - shipSince > 3f;
            }, next, timeout: 40f);
        }

        private static void RequireSession()
        {
            if (!GameRefs.InGameSession) throw new InvalidOperationException("Load a save file first.");
        }

        private static void RequireAA(bool needsAA, string what)
        {
            if (!needsAA || GameRefs.OwnsAA) return;
            throw new InvalidOperationException(IsDlcLocked?.Invoke(4) == true
                ? $"That {what} is part of DLC you've locked (DLC tab)."
                : $"That {what} is part of DLC you don't own.");
        }

        private static void StopMusic()
        {
            try { GameRefs.Sound?.StopBGM(0.5f); } catch (Exception e) { MelonLogger.Warning(e.Message); }
        }

        private static void LeaveCurrentGame()
        {
            if (GameRefs.Main != null) PrepareLeaveMainGame();
            LeaveMultiplayer();
        }

        /// <summary>The cleanup MainGameManager.ToStageSelectCor() performs before it changes scene.</summary>
        private static void PrepareLeaveMainGame()
        {
            var gm = GameRefs.Main;
            try { gm?.ResetDebugTimeSpeed(); } catch (Exception e) { MelonLogger.Warning(e.Message); }
            try
            {
                var snd = GameRefs.Sound;
                if (snd != null)
                {
                    snd.StopBGMInStageImmediate();
                    snd.StopCoreSE();
                    snd.StopSEAll();
                }
            }
            catch (Exception e) { MelonLogger.Warning(e.Message); }
            try { gm?.ResetSingleton(); } catch (Exception e) { MelonLogger.Warning(e.Message); }
            Cursor.lockState = CursorLockMode.None;
        }
    }
}
