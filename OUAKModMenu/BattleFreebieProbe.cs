using System;
using System.IO;
using Il2CppApp.KatamariSin;
using MelonLoader;
using MelonLoader.Utils;
using OUAKModAPI;
using UnityEngine;

namespace OUAKModMenu
{
    /// <summary>
    /// Test-only (UserData/OUAKModMenu/battlefreebie.flag, together with OUAK Multiplayer's trainingtest.flag, which
    /// starts the match): what the menu's freebie tools see in a Katamari Ball match, and whether a freebie can be
    /// given and used there the game's battle way (SetInventoryItem(item, isRandom: true) + ActivateItemEffect).
    /// </summary>
    internal static class BattleFreebieProbe
    {
        private static string FlagPath => Path.Combine(MelonEnvironment.UserDataDirectory, "OUAKModMenu", "battlefreebie.flag");
        private static bool _armed;
        private static int _step;
        private static float _at = -1f;

        private static void Log(string s) => MelonLogger.Msg("[BattleFreebie] " + s);

        public static void CheckFlag()
        {
            if (!File.Exists(FlagPath)) return;
            try { File.Delete(FlagPath); } catch { }
            _armed = true;
            Log("armed");
        }

        public static void Tick()
        {
            if (!_armed) return;
            var gm = GameRefs.Main;
            var mgr = GameRefs.Multiplay;
            if (_at < 0f)
            {
                // Wait for a running match with the player spawned.
                if (gm == null || mgr == null || !GameRefs.IsMultiplay || !gm.PlayerInitiated || !gm.IsGameRunning) return;
                if (TrainingTrace.StateName(mgr.stateMachine) != "GameState") return;   // the round itself (items work)
                _at = Time.unscaledTime + 1f;
                return;
            }
            if (Time.unscaledTime < _at) return;
            try { Step(gm, mgr); }
            catch (Exception e) { Log($"step {_step}: {e.GetType().Name}: {e.Message}"); _armed = false; }
        }

        private static string State(MainGameManager gm, MultiplayGameManager mgr)
        {
            var core = gm?.GetCore();
            var us = core != null ? mgr?.GetUserStatusFromCoreNo(core.multiCoreNo) : null;
            return $"IsMultiplay {GameRefs.IsMultiplay}, InStage {GameRefs.InStage}, InGameSession {GameRefs.InGameSession}, rule {mgr?.nowRule}, " +
                   $"slot {gm?._nowInventoryItem}, battle item {core?.multiItemType}, effect timer {gm?._itemEffectTimer:0.0}, crown power {us?.timeCrownPower:0.0}, " +
                   $"menu auto freebie {StageCheats.AutoFreebie}/{StageCheats.AutoItem}";
        }

        private static void Step(MainGameManager gm, MultiplayGameManager mgr)
        {
            float now = Time.unscaledTime;
            switch (_step++)
            {
                case 0:
                    Log("in the round: " + State(gm, mgr) + $", can use freebies {StageCheats.CanUseFreebies}, in match {StageCheats.InMatch}");
                    Log($"menu give Crown: {Try(() => StageCheats.GiveFreebie(eInstageItemType.Crown))}");
                    ModMenu.CurrentTab = "freebies";
                    ModMenu.Open();
                    _at = now + 3f;   // the roulette
                    return;
                case 1:
                    Log("3 s later: " + State(gm, mgr));
                    FrameCapture.Request("kb_freebies_tab");
                    _at = now + 1f;
                    return;
                case 2:
                    ModMenu.Close();
                    gm.ActivateItemEffect();   // what the use button does
                    Log("Crown used: " + State(gm, mgr));
                    _at = now + 2f;
                    return;
                case 3:
                    Log("2 s later: " + State(gm, mgr));
                    // Freeze: Shoes given, used, and back once its effect ends.
                    StageCheats.FreezeFreebie = true;
                    if (gm._itemEffectTimer >= 0f) gm.EndItemEffect(true);
                    Log($"freeze on; menu give Shoes: {Try(() => StageCheats.GiveFreebie(eInstageItemType.Shoes))}");
                    _at = now + 3f;
                    return;
                case 4:
                    Log("Shoes in the slot: " + State(gm, mgr));
                    gm.ActivateItemEffect();
                    Log("Shoes used: " + State(gm, mgr));
                    _at = now + 9f;   // 5 s effect + the roulette of the re-give
                    return;
                case 5:
                    Log($"after the Shoes ended (frozen {StageCheats.FrozenItem}): " + State(gm, mgr));
                    StageCheats.FreezeFreebie = false;
                    Log($"menu clear: {Try(StageCheats.ClearFreebie)}; " + State(gm, mgr));
                    Log("DONE");
                    _armed = false;
                    return;
            }
        }

        private static string Try(Func<string> f)
        {
            try { return f(); } catch (Exception e) { return "refused: " + e.Message; }
        }
    }
}
