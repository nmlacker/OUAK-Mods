using System;
using HarmonyLib;
using Il2Cpp;
using Il2CppApp.KatamariSin;
using MelonLoader;
using UnityEngine;

namespace OUAKModAPI
{
    /// <summary>
    /// Offline training (MultiplayGameManager.StartTrainingMode) always writes the Sports Katamari rule (bigBattle)
    /// into its one-round room, and StartGame() then loads the chosen map under that rule. The Race and Target maps
    /// belong to rounds with their own rules (race / collect), so under bigBattle the Race map gets none of its race
    /// setup and the game bogs down until it hangs. A prefix on StartGame puts the map's own rule into the room
    /// before the game reads it: StartGame, LoadMainGame (InitRace), TrainingState (SetupRace) and the cores
    /// (MainGameCore.InitAI adds the CPU racers) then all take the race path they take online.
    /// Retries go through StartGame again, so they keep the rule.
    ///
    /// Offline, the stage spawns the katamaris (MainGameManager.InitMultiPlay) before LoadMainGame reaches
    /// InitRace, so the CPU racers copy a race-point list that isn't loaded yet. The course is loaded just before
    /// the stage's multiplayer init instead, and LoadMainGame's own InitRace then keeps it.
    ///
    /// SpawnMultiPlayer also reads respawn spots from a "ReStartPoint" group next to the stage's StartPoint group.
    /// The Race map has none, so offline maps without one get copies of their start points.
    ///
    /// The guide arrow (NextArrow) points at the active Big Battle hard point (MainGameManager.GetActiveHardPoint),
    /// which race and collect rounds don't have, so it throws every frame there. It's hidden in those rounds.
    /// The round logic (MultiplayGameManager.GameState.Update) also moves the hard point whenever playTime reaches
    /// MultiplayBigBattle.nextHardPointTime. Only SetupBigBattle sets that time, so in race and collect rounds it
    /// stays 0 and the hard-point list is null: it threw every frame, before the timer and round-end checks ran.
    /// Those rounds get a time that never comes.
    ///
    /// These hooks are part of the API (<see cref="GamePatches.Race"/>, <see cref="GamePatches.Target"/>) and only act
    /// when the Race or Target map runs offline, which only happens through mods. They also put the Big Battle rule
    /// back for any other offline map that would otherwise inherit a race / collect rule left in the room.
    /// </summary>
    public static class TrainingRules
    {
        /// <summary>The hooks are in place.</summary>
        public static bool PatchActive { get; private set; }

        /// <summary>The rule a training map needs instead of bigBattle, or null when bigBattle is right.</summary>
        public static NetworkBattleRule? RuleFor(GI_MI map) => map switch
        {
            GI_MI.GI_MI_ONLINE_RACE1 => NetworkBattleRule.race,
            GI_MI.GI_MI_ONLINE_TARGET1 => NetworkBattleRule.collect,
            _ => null,
        };

        private static HarmonyLib.Harmony _harmony;
        private static bool _tried;

        internal static void Init(HarmonyLib.Harmony harmony) => _harmony = harmony;

        /// <summary>Installs the hooks once (when the API starts).</summary>
        internal static bool Install()
        {
            if (_tried) return PatchActive;
            _tried = true;
            var harmony = _harmony ?? throw new InvalidOperationException("the API isn't initialised");
            int ok = 0;
            ok += TryPatch(harmony, typeof(MultiplayGameManager), nameof(MultiplayGameManager.StartGame), nameof(BeforeStartGame));
            ok += TryPatch(harmony, typeof(MainGameManager), nameof(MainGameManager.InitMultiPlay), nameof(BeforeStageMultiplayInit));
            ok += TryPatch(harmony, typeof(MultiplayGameManager), nameof(MultiplayGameManager.InitRace), nameof(BeforeInitRace));
            ok += TryPatch(harmony, typeof(MainGameManager), nameof(MainGameManager.SpawnMultiPlayer), nameof(BeforeSpawnMultiPlayer));
            ok += TryPatch(harmony, typeof(NextArrow), nameof(NextArrow.NormalArrowUpdate), nameof(BeforeNextArrowUpdate));
            try { HudSafety.Install(harmony); } catch (Exception e) { ApiCore.Log.Warn($"HUD safety: {e.Message}"); }
            PatchActive = ok == 5;
            return PatchActive;
        }

        private static int TryPatch(HarmonyLib.Harmony harmony, Type type, string method, string prefix)
        {
            try
            {
                harmony.Patch(AccessTools.Method(type, method), prefix: new HarmonyMethod(AccessTools.Method(typeof(TrainingRules), prefix)));
                return 1;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"Could not patch {type.Name}.{method}: {e.Message}");
                return 0;
            }
        }

        private static bool OfflineRace(MultiplayGameManager m) =>
            m != null && m.isSinglePlay && m.nowRule == NetworkBattleRule.race;

        private static void BeforeStartGame(MultiplayGameManager __instance)
        {
            try
            {
                if (__instance == null || !__instance.isSinglePlay) return;
                var rule = RuleFor(__instance.gimiSingleStage);
                var rules = __instance.roomInfo?.rule;
                int round = __instance.roundNo;
                if (rules == null || round < 0 || round >= rules.Length) return;
                if (rule == null)
                {
                    // Any other map runs the Big Battle rule offline; never let a race / collect rule from an earlier
                    // Race or Target round carry over (crane points showed up in a Pyramid match).
                    if (rules[round] == NetworkBattleRule.race || rules[round] == NetworkBattleRule.collect)
                    {
                        MelonLogger.Msg($"Training on {__instance.gimiSingleStage}: back to the Big Battle rule (was {rules[round]}).");
                        rules[round] = NetworkBattleRule.bigBattle;
                    }
                    return;
                }
                if (rules[round] != rule.Value)
                {
                    rules[round] = rule.Value;
                    MelonLogger.Msg($"Training on {__instance.gimiSingleStage}: using the {rule.Value} rule.");
                }
                // Each round loads its own course (see BeforeStageMultiplayInit).
                if (rule == NetworkBattleRule.race) __instance.listRacePointData = null;
                var bigBattle = __instance.multiplayBigBattle;
                if (bigBattle != null) bigBattle.nextHardPointTime = int.MaxValue / 2;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"Training rule: {e.Message}");
            }
        }

        private static void BeforeStageMultiplayInit(MainGameManager __instance)
        {
            MapTables.Fill();   // the game's per-map tables miss the Race map: InitMultiPlay would throw halfway
            try
            {
                var m = __instance?.multiGameMan;
                if (!OfflineRace(m) || m.listRacePointData != null) return;
                m.InitRace();
                TrainingTrace.Note($"race course loaded before the racers spawn ({m.listRacePointData?.Count ?? 0} points)");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"Race course: {e.Message}");
            }
        }

        private static void BeforeSpawnMultiPlayer(MainGameManager __instance)
        {
            try
            {
                var mgm = __instance?.multiGameMan;
                if (mgm?.isSinglePlay != true || RuleFor(mgm.gimiSingleStage) == null) return;
                var starts = __instance._inStageData?.SpawnPointParent;
                var stage = starts?.parent;
                if (stage == null || stage.Find("ReStartPoint") != null) return;
                // Mirror the StartPoint hierarchy so SpawnMultiPlayer reads the same positions and angles.
                var group = new GameObject("ReStartPoint").transform;
                group.SetParent(stage, false);
                group.localPosition = starts.localPosition;
                group.localRotation = starts.localRotation;
                group.localScale = starts.localScale;
                for (int i = 1; i <= starts.childCount; i++)
                {
                    var start = starts.Find($"StartPoint{i}");
                    if (start == null) break;
                    var point = new GameObject($"ReStartPoint{i}").transform;
                    point.SetParent(group, false);
                    point.localPosition = start.localPosition;
                    point.localRotation = start.localRotation;
                }
                TrainingTrace.Note($"added {group.childCount} restart points (copies of the start points) to a map that has none");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"Restart points: {e.Message}");
            }
        }

        // false skips the arrow's update for the round.
        private static bool BeforeNextArrowUpdate(NextArrow __instance)
        {
            try
            {
                var m = SingletonMonoBehaviour<MultiplayGameManager>.instance;
                if (!GameRefs.IsMultiplay || m == null || !m.isSinglePlay || m.nowRule == NetworkBattleRule.bigBattle
                    || RuleFor(m.gimiSingleStage) == null) return true;
                __instance.SetArrowVisible(false);
                return false;
            }
            catch { return true; }
        }

        // false skips the game's InitRace: the course the racers were set up with stays.
        private static bool BeforeInitRace(MultiplayGameManager __instance)
        {
            try { return !(OfflineRace(__instance) && __instance.listRacePointData != null); }
            catch { return true; }
        }
    }
}
