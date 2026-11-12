using System;
using System.IO;
using System.Linq;
using Il2CppApp.KatamariSin;
using MelonLoader;
using MelonLoader.Utils;
using OUAKModAPI;
using UnityEngine;

namespace OUAKModMenu
{
    /// <summary>
    /// Spawn freebie test, armed by UserData/OUAKModMenu/freebietest.flag. Backs the saves up and loads the last-used
    /// slot (to the ship), turns "spawn with" on for Shoes (your own setting comes back afterwards, unsaved), enters
    /// As Large As Possible 1 and checks you're already holding Shoes the first frame the player exists,
    /// pictures the Freebies and Match tabs in the stage, restarts and checks again, then quits.
    /// </summary>
    internal static class FreebieTest
    {
        private static string FlagPath => Path.Combine(MelonEnvironment.UserDataDirectory, "OUAKModMenu", "freebietest.flag");
        private const eInstageItemType Item = eInstageItemType.Shoes;

        private static bool _armed;
        private static int _step;
        private static float _at, _deadline, _nextTrace;
        private static int _slot = -1;
        private static int _tipsClosed;
        private static float _nextTipClose;

        /// <summary>What a restart may be waiting on: the shutter (CorRestartStage waits for IsShutterOpen), a tip (it pauses time), pause.</summary>
        private static string RestartState(MainGameManager gm)
        {
            if (gm == null) return "no game manager";
            string shutter = "none";
            try
            {
                var sh = gm.MainUI != null ? gm.MainUI.ShutterTransition : null;
                if (sh != null) shutter = $"{sh.Status} (object active {sh.gameObject.activeInHierarchy}, component enabled {sh.isActiveAndEnabled})";
            }
            catch (System.Exception e) { shutter = "error " + e.Message; }
            bool tip = false;
            try { tip = gm.MainUI != null && gm.MainUI.IsTipsActive(); } catch { }
            return $"restart wait {gm._isInRestartWait}, shutter {shutter}, tip open {tip}, paused {gm._isInPause}, running {gm.IsGameRunning}, time scale {UnityEngine.Time.timeScale}, " +
                   $"focus {UnityEngine.Application.isFocused}, item {gm._nowInventoryItem}, game input locked {GameInput.Blocked} ({string.Join(",", GameInput.Holders)})";
        }
        private static bool _prevAuto, _spawnOk, _restartOk;
        private static eInstageItemType _prevItem;
        private static string _spawnNote = "", _restartNote = "";
        private static IntPtr _stage;

        public static void CheckFlag()
        {
            if (!File.Exists(FlagPath)) return;
            // "slot 3" in the flag file loads that slot instead of the last-used one.
            try
            {
                var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(FlagPath), @"slot\s*(\d)");
                _slot = m.Success ? int.Parse(m.Groups[1].Value) - 1 : -1;
            }
            catch { _slot = -1; }
            try { File.Delete(FlagPath); } catch { }
            _armed = true;
            MelonLogger.Msg($"[FreebieTest] armed (spawn hook {StageCheats.SpawnHookActive})");
        }

        public static void Tick()
        {
            if (!_armed || Time.unscaledTime < _at) return;
            try { Step(); }
            catch (Exception e) { Finish($"FAIL: step {_step}: {e.Message}"); }
        }

        private static void Step()
        {
            float now = Time.unscaledTime;
            var gm = GameRefs.Main;
            switch (_step)
            {
                case 0:
                {
                    var ctrl = GameRefs.SaveCtrl;
                    if (SceneLoader.ActiveScene != "SelectData" || GameRefs.Global == null || GameRefs.Save == null ||
                        ctrl == null || !ctrl.ActionWait() || ctrl.GameData?._SystemData == null) { _at = now + 1f; return; }
                    TestSlot.LoadLastUsedSlot(ctrl, "Freebie test", "[FreebieTest]", _slot);
                    _step = 1;
                    _deadline = now + 60f;
                    _at = now + 1f;
                    return;
                }
                case 1:
                {
                    if (SceneLoader.ActiveScene != "OujiShip" || !GameRefs.InGameSession)
                    {
                        if (now > _deadline) throw new InvalidOperationException("the save never reached the ship");
                        _at = now + 1f;
                        return;
                    }
                    _prevAuto = StageCheats.AutoFreebie;
                    _prevItem = StageCheats.AutoItem;
                    StageCheats.AutoFreebie = true;
                    StageCheats.AutoItem = Item;
                    var entry = StageCatalog.Entries.FirstOrDefault(e => e.Kind == MapKind.Stage && e.Internal == "Stage_Big1")
                                ?? throw new InvalidOperationException("Stage_Big1 isn't in the catalog");
                    MelonLogger.Msg("[FreebieTest] " + SceneLoader.LoadStage(entry));
                    _step = 2;
                    _deadline = now + 60f;
                    _at = now + 0.1f;
                    return;
                }
                case 2:
                    // The moment the player has spawned (the fly-over hasn't even started): the slot should hold Shoes already.
                    if (gm == null || !gm.PlayerInitiated)
                    {
                        if (now > _deadline) throw new InvalidOperationException("the stage never spawned the player");
                        _at = now + 0.05f;
                        return;
                    }
                    _stage = gm.Pointer;
                    // (The game already counts as running here, during the intro: the first frame the player exists is the check.)
                    _spawnOk = gm._nowInventoryItem == Item && StageCheats.SpawnGiven > 0;
                    _spawnNote = $"at spawn: holding {StageCheats.Pretty(gm._nowInventoryItem)}, start item {gm._startInventoryItem}, control yet {gm.IsGameRunning}, given at spawn {StageCheats.SpawnGiven}x";
                    MelonLogger.Msg($"[FreebieTest] {(_spawnOk ? "PASS" : "FAIL")}: {_spawnNote}");
                    _step = 3;
                    _deadline = now + 45f;
                    _at = now + 1f;
                    return;
                case 3:
                    if (gm == null || !gm.IsGameRunning)
                    {
                        if (now > _deadline) throw new InvalidOperationException("never got control in the stage");
                        _at = now + 0.5f;
                        return;
                    }
                    ModMenu.Open();
                    ModMenu.CurrentTab = "freebies";
                    _step = 4;
                    _at = now + 4f;   // the icons are looked up in the background
                    return;
                case 4:
                    FrameCapture.Request("freebies_in_stage");
                    _step = 5;
                    _at = now + 1f;
                    return;
                case 5:
                case 6:
                    // (Used to show the Match tab: KatamariBall left this single-player menu on 2026-11-06.)
                    _step = 7;
                    return;
                case 7:
                    ModMenu.Close();
                    // Use it up first, so a restart has to put it back.
                    StageCheats.ClearFreebie();
                    MelonLogger.Msg("[FreebieTest] " + StageCheats.RestartStage());
                    _step = 8;
                    _deadline = now + 30f;
                    _at = now + 0.5f;
                    return;
                case 8:
                    // Right after the restart puts the prince back (the game's ResetItem): Shoes again.
                    if (gm == null || gm._isInRestartWait || gm._nowInventoryItem == eInstageItemType.NoItem)
                    {
                        // A one-time game tip (e.g. CameraPattern on a save that hasn't seen it) can open during the
                        // restart: it pauses game time until it's closed, so close it the game's way, as a player would.
                        // CloseTips fades the tip out first: ask once, then give the fade time (asking again restarts it).
                        if (gm != null && gm.MainUI != null && gm.MainUI.IsTipsActive() && now > _nextTipClose)
                        {
                            MelonLogger.Msg("[FreebieTest] a game tip opened during the restart; closing it");
                            gm.MainUI.DeactivateTipsUI();
                            _tipsClosed++;
                            _nextTipClose = now + 4f;
                        }
                        if (now > _nextTrace) { _nextTrace = now + 5f; MelonLogger.Msg("[FreebieTest] waiting: " + RestartState(gm)); }
                        if (now > _deadline) { _restartNote = $"after the restart: holding {gm?._nowInventoryItem}; {RestartState(gm)}"; Finish("FAIL: " + _restartNote); return; }
                        _at = now + 0.1f;
                        return;
                    }
                    _restartOk = gm._nowInventoryItem == Item && gm.Pointer == _stage;
                    _restartNote = $"after the restart: holding {StageCheats.Pretty(gm._nowInventoryItem)}, control yet {gm.IsGameRunning}, game tips closed {_tipsClosed}";
                    Finish(_spawnOk && _restartOk
                        ? $"PASS: spawned holding the chosen freebie ({_spawnNote}); {_restartNote}"
                        : $"FAIL: spawn {_spawnOk} ({_spawnNote}); restart {_restartOk} ({_restartNote})");
                    return;
            }
        }

        private static void Finish(string verdict)
        {
            _armed = false;
            StageCheats.AutoFreebie = _prevAuto;
            StageCheats.AutoItem = _prevItem;
            MelonLogger.Msg("[FreebieTest] " + verdict);
            MelonLogger.Msg("[FreebieTest] DONE");
            ModTasks.Later(Application.Quit, 2f);
        }
    }
}
