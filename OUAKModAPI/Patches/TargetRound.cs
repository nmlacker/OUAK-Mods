using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using Il2CppApp.KatamariSin;

namespace OUAKModAPI
{
    /// <summary>
    /// The Target round (Stage_ONTarget1, the "collect" rule). Reading is always available; points need
    /// <see cref="GamePatches.TargetPoints"/> enabled (and the round itself <see cref="GamePatches.Target"/>).
    /// <para>The game's own target counter: what counts is the stage's target type
    /// (<c>MainGameManager.CheckMonoTargetType(MonoID)</c>), and how many targets an object is worth comes from
    /// <c>MainGameManager.GetMonoTargetCount(MonoID)</c>. Every roll-up in a match goes through
    /// <c>MainGameCore.MultiGetMono</c> -> <c>StageMonoManager.RegisterMakikomiMonoMulti</c>, which adds that to the
    /// prince's count (<c>currentTypeMono</c>; each prince's own value is kept in <c>listMultiCorrectNum[coreNo]</c>
    /// and swapped in by <c>ChangeMultiWork</c> / <c>RestoreMultiWork</c> while that prince is processed).</para>
    /// <para>Points: the game's collect rule never scores anything, so the round only had the results' end bonuses.
    /// With the patch on, every target rolled up gives <see cref="PointsPerTarget"/> through the game's own point path,
    /// <c>MainGameCore.AddHardPointNum</c>: <c>UserStatus.hardPointNum</c> is what the HUD's "pt" labels and the
    /// results' score both show, and your own katamari shows the "+points" effect. An object scores once per prince, when
    /// it really goes onto that prince's katamari; losing it again doesn't take the points back.</para>
    /// </summary>
    public static class TargetRound
    {
        /// <summary>Points for each target (default 10).</summary>
        public static int PointsPerTarget = 10;

        /// <summary>A prince scored: core, targets in the object, points given.</summary>
        public static event Action<MainGameCore, int, int> Scored;

        /// <summary>Points given this round, all princes together.</summary>
        public static int PointsGiven { get; private set; }

        private static readonly Dictionary<uint, HashSet<IntPtr>> _scored = new();
        private static IntPtr _round;

        /// <summary>The Target mode is running: the Target map under the collect rule.</summary>
        public static bool InRound
        {
            get
            {
                if (!GameRefs.IsMultiplay) return false;
                var m = GameRefs.Multiplay;
                return m != null && m.nowRule == NetworkBattleRule.collect && m.gimiSingleStage == GI_MI.GI_MI_ONLINE_TARGET1;
            }
        }

        /// <summary>The object is one of the stage's targets (the game's own check).</summary>
        public static bool IsTarget(MainGameMonoBase mono)
        {
            var gm = GameRefs.Main;
            return gm != null && mono != null && gm.CheckMonoTargetType(mono.MonoID);
        }

        /// <summary>How many targets the object counts as (0 when it isn't one), e.g. a bundle of cranes counts each crane.</summary>
        public static int TargetsIn(MainGameMonoBase mono)
        {
            var gm = GameRefs.Main;
            if (gm == null || mono == null || !gm.CheckMonoTargetType(mono.MonoID)) return 0;
            return Math.Max(1, gm.GetMonoTargetCount(mono.MonoID));
        }

        /// <summary>The game's target count for that prince (each prince's own value).</summary>
        public static int Count(MainGameCore core)
        {
            var mm = GameRefs.Main?.MonoManager;
            var list = mm?.listMultiCorrectNum;
            if (core == null || list == null) return 0;
            int i = (int)core.multiCoreNo;
            return i >= 0 && i < list.Count ? list[i] : 0;
        }

        /// <summary>The count being processed right now (StageMonoManager.currentTypeMono: whichever prince's work is swapped in).</summary>
        public static int CurrentCount => GameRefs.Main?.MonoManager?.currentTypeMono ?? 0;

        internal static bool Install()
        {
            var harmony = ApiCore.Instance?.HarmonyInstance ?? throw new InvalidOperationException("the API isn't initialised");
            harmony.Patch(AccessTools.Method(typeof(MainGameCore), nameof(MainGameCore.MultiGetMono)),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(TargetRound), nameof(AfterMultiGetMono))));
            return true;
        }

        // ------------------------------------------------------------------ CPU princes (GamePatches.TargetCpus)

        /// <summary>How close (in the CPU katamari's radius) an object's centre must be for the CPU to roll it up.</summary>
        public static float CpuReach = 1.4f;

        /// <summary>Objects CPU princes rolled up through the patch this round.</summary>
        public static int CpuPickups { get; private set; }

        private static readonly List<MainGameMonoBase> _monos = new();
        private static float _monosAt = -999f;
        private static readonly Dictionary<uint, float> _cpuNext = new();
        private static IntPtr _cpuRound;

        internal static bool InstallCpus()
        {
            var harmony = ApiCore.Instance?.HarmonyInstance ?? throw new InvalidOperationException("the API isn't initialised");
            harmony.Patch(AccessTools.Method(typeof(MainGameCore), "CoreMultiMove"),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(TargetRound), nameof(AfterCoreMultiMove))));
            return true;
        }

        // Runs inside each prince's own update (MainGameCore.UpdateMultiPlay calls CoreMultiMove between the
        // StageMonoManager.ChangeMultiWork that swaps that prince's counters in and the RestoreMultiWork that saves them),
        // so a roll-up here counts for that prince exactly like one from the game's own collision.
        private static void AfterCoreMultiMove(MainGameCore __instance)
        {
            if (__instance == null || !__instance.isMultiCpu || !InRound) return;
            try
            {
                var gm = GameRefs.Main;
                if (gm == null) return;
                float now = UnityEngine.Time.time;
                if (gm.Pointer != _cpuRound) { _cpuRound = gm.Pointer; _cpuNext.Clear(); _monos.Clear(); _monosAt = -999f; CpuPickups = 0; }
                uint no = __instance.multiCoreNo;
                if (_cpuNext.TryGetValue(no, out float next) && now < next) return;
                _cpuNext[no] = now + 0.1f;
                if (now - _monosAt > 2f) RefreshMonos();

                var pos = __instance.transform.position;
                float reach = __instance.KatamariRadius * CpuReach;
                int size = __instance.multiCoreSize;
                MainGameMonoBase best = null;
                float bestSq = reach * reach;
                foreach (var m in _monos)
                {
                    if (m == null || !m.gameObject.activeInHierarchy) continue;
                    var parent = m.transform.parent;
                    if (parent != null && parent.name.StartsWith("MonoAttach", StringComparison.Ordinal)) continue;
                    float sq = (m.transform.position - pos).sqrMagnitude;
                    if (sq > bestSq) continue;
                    int need = gm.GetMonoRequireSize(m.MonoID);
                    if (need <= 0 || need > size) continue;
                    bestSq = sq; best = m;
                }
                if (best == null) return;
                // MultiGetMono only lets a CPU take objects marked for syncing (none are, in the map data).
                best.isMultiplaySync = true;
                if (__instance.MultiGetMono(best)) CpuPickups++;
            }
            catch (Exception e) { ApiCore.Log.Warn($"Target CPU pickup: {e.Message}"); }
        }

        private static void RefreshMonos()
        {
            _monos.Clear();
            _monosAt = UnityEngine.Time.time;
            foreach (var m in UnityEngine.Resources.FindObjectsOfTypeAll<MainGameMonoBase>())
            {
                try
                {
                    if (m == null || !m.gameObject.scene.isLoaded) continue;
                    if (m.GetComponentsInChildren<MainGameMonoBase>(true).Length > 1) continue;   // groups: their parts are separate objects
                    _monos.Add(m);
                }
                catch { }
            }
        }

        private static void AfterMultiGetMono(MainGameCore __instance, MainGameMonoBase mono, bool __result)
        {
            if (!__result || __instance == null || mono == null || !InRound) return;
            try
            {
                // MultiGetMono also returns true when it turns a CPU away (object not marked for syncing): only score
                // what really went onto this prince's katamari.
                var attach = __instance.MonoAttachment;
                if (attach == null || !mono.transform.IsChildOf(attach.transform)) return;
                var gm = GameRefs.Main;
                if (gm == null) return;
                if (gm.Pointer != _round) { _round = gm.Pointer; _scored.Clear(); PointsGiven = 0; }
                int targets = TargetsIn(mono);
                if (targets <= 0) return;
                if (!_scored.TryGetValue(__instance.multiCoreNo, out var done)) _scored[__instance.multiCoreNo] = done = new HashSet<IntPtr>();
                if (!done.Add(mono.Pointer)) return;
                int points = targets * PointsPerTarget;
                if (points <= 0) return;
                __instance.AddHardPointNum(points);
                PointsGiven += points;
                try { Scored?.Invoke(__instance, targets, points); } catch (Exception e) { GameEvents.Report(Scored, e); }
            }
            catch (Exception e) { ApiCore.Log.Warn($"Target points: {e.Message}"); }
        }
    }
}
