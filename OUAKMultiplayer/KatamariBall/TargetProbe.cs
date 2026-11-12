using System;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using Il2CppApp.KatamariSin;
using MelonLoader;
using OUAKModAPI;
using UnityEngine;
using UserStatus = Il2CppApp.KatamariSin.MultiplayController.UserStatus;

namespace OUAKMultiplayer
{
    /// <summary>
    /// Test-only (trainingtest.flag with Stage_ONTarget1): looks at the Target (collect) round. Logs the map's
    /// objects grouped by name, every player's counters (UserStatus: hardPointNum = the points the HUD and the
    /// results show, collectNum, point, totalMonoGetNum), the HUD's point labels, and rolls objects up with the
    /// game's own pickup (MainGameCore.MultiGetMono / GetTargetMono).
    /// </summary>
    internal static class TargetProbe
    {
        private static void Log(string s) => MelonLogger.Msg("[TargetProbe] " + s);

        private static HarmonyLib.Harmony _h;
        private static int _logged;
        public static readonly Dictionary<string, int> PickedByName = new();
        public static int Picked;

        /// <summary>Logs every pickup in the round (MainGameCore.MultiGetMono, which every katamari's roll-ups go through).</summary>
        public static void Install()
        {
            if (_h != null) return;
            _h = new HarmonyLib.Harmony("ouak.multiplayer.targetprobe");
            try
            {
                _h.Patch(HarmonyLib.AccessTools.Method(typeof(MainGameCore), nameof(MainGameCore.MultiGetMono)),
                    postfix: new HarmonyLib.HarmonyMethod(HarmonyLib.AccessTools.Method(typeof(TargetProbe), nameof(AfterMultiGetMono))));
            }
            catch (Exception e) { Log("hook: " + e.Message); }
        }

        private static void AfterMultiGetMono(MainGameCore __instance, MainGameMonoBase mono, bool __result)
        {
            try
            {
                Picked++;
                string name = mono == null ? "?" : mono.gameObject.name;
                PickedByName.TryGetValue(name, out int n);
                PickedByName[name] = n + 1;
                if (_logged++ < 30)
                {
                    string who = __instance.Pointer == (GameRefs.Main?.GetCore()?.Pointer ?? IntPtr.Zero) ? "you" : $"cpu{__instance.multiCoreNo}";
                    Log($"pickup by {who}: {name} (MonoID {mono?.MonoID}, ok {__result})");
                }
            }
            catch { }
        }

        /// <summary>Your katamari rolls up the nearest object it can take (the game's own match pickup, MultiGetMono).</summary>
        public static string RollUpNearest(Func<MainGameMonoBase, bool> filter = null)
        {
            var gm = GameRefs.Main;
            var core = gm?.GetCore();
            if (core == null) return "no katamari";
            var c = core.transform.position;
            int size = gm.KatamariSize;
            MainGameMonoBase best = null;
            float bestSq = float.MaxValue;
            foreach (var m in Resources.FindObjectsOfTypeAll<MainGameMonoBase>())
            {
                try
                {
                    if (m == null || !m.gameObject.activeInHierarchy || !m.gameObject.scene.isLoaded) continue;
                    if (m.transform.IsChildOf(core.transform)) continue;
                    if (filter != null && !filter(m)) continue;
                    int need = gm.GetMonoRequireSize(m.MonoID);
                    if (need <= 0 || need > size) continue;
                    if (m.GetComponentsInChildren<MainGameMonoBase>(true).Length > 1) continue;
                    float sq = (m.transform.position - c).sqrMagnitude;
                    if (sq < bestSq) { bestSq = sq; best = m; }
                }
                catch { }
            }
            if (best == null) return "nothing to roll up";
            string name = best.gameObject.name;
            best.transform.position = c + Vector3.up * core.KatamariRadius;
            bool ok = core.MultiGetMono(best);
            return $"rolled up {name} (MonoID {best.MonoID}, {Mathf.Sqrt(bestSq):0.0} away): {ok}";
        }

        public static string PickupSummary() =>
            $"{Picked} pickups: " + string.Join(", ", PickedByName.OrderByDescending(k => k.Value).Take(25).Select(k => $"{k.Key} x{k.Value}"));

        /// <summary>The map's objects grouped by name (count, MonoID, size needed), the biggest groups first.</summary>
        public static void Survey()
        {
            try
            {
                var gm = GameRefs.Main;
                var groups = new Dictionary<string, (int n, int id, int need, int active)>();
                int total = 0;
                foreach (var m in Resources.FindObjectsOfTypeAll<MainGameMonoBase>())
                {
                    try
                    {
                        if (m == null || !m.gameObject.scene.isLoaded) continue;
                        total++;
                        string name = m.gameObject.name;
                        int cut = name.IndexOf(" (", StringComparison.Ordinal);
                        if (cut > 0) name = name.Substring(0, cut);
                        int need = gm != null ? gm.GetMonoRequireSize(m.MonoID) : -1;
                        groups.TryGetValue(name, out var g);
                        groups[name] = (g.n + 1, m.MonoID, need, g.active + (m.gameObject.activeInHierarchy ? 1 : 0));
                    }
                    catch { }
                }
                Log($"{total} objects in {groups.Count} names; katamari {gm?.KatamariSize}");
                // Every group with the name the game shows for it (MainGameManager.GetMonoName -> DATA_NM_ table).
                foreach (var kv in groups.OrderByDescending(k => k.Value.n))
                {
                    string shown = "?";
                    try { shown = gm?.GetMonoName(kv.Value.id) ?? "?"; } catch { }
                    Log($"  {kv.Key} '{shown}': x{kv.Value.n} (active {kv.Value.active}), MonoID {kv.Value.id}, needs {kv.Value.need}");
                }
                var stage = gm?.MonoManager;
                if (stage != null)
                {
                    var special = stage.GetSpecialTargetMono();
                    Log($"special target mono: {(special == null ? "none" : special.gameObject.name + " MonoID " + special.MonoID)}");
                }
                RootNames();
            }
            catch (Exception e) { Log("survey: " + e.Message); }
        }

        private static void RootNames()
        {
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var sc = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (!sc.isLoaded) continue;
                var roots = sc.GetRootGameObjects();
                Log($"scene {sc.name}: " + string.Join(", ", roots.Take(40).Select(r => r.name + "[" + r.transform.childCount + "]")));
            }
        }

        /// <summary>Every player's counters.</summary>
        public static string Players()
        {
            try
            {
                var mgr = GameRefs.Multiplay;
                var cores = mgr?.dicPlayerCore;
                if (cores == null) return "no players";
                var parts = new List<string>();
                var it = cores.GetEnumerator();
                while (it.MoveNext())
                {
                    var core = it.Current.Value;
                    if (core == null) continue;
                    UserStatus us = mgr.GetUserStatusFromCoreNo(core.multiCoreNo);
                    string who = core.Pointer == (GameRefs.Main?.GetCore()?.Pointer ?? IntPtr.Zero) ? "you" : $"cpu{core.multiCoreNo}";
                    string ai = "";
                    try
                    {
                        var p = core.transform.position;
                        ai = $" at ({p.x:0},{p.y:0},{p.z:0})";
                        var cc = core.multiplayCPU?.TryCast<MultiplayCPUCollect>();
                        if (cc != null) ai += $" ai {cc.stateMachine?.CurrentStateName} wait {cc.waitAITime:0.0} target {(cc.targetMono == null ? "-" : cc.targetMono.gameObject.name)}";
                    }
                    catch (Exception e) { ai += " ai?" + e.Message; }
                    parts.Add(us == null ? $"{who}: no status" :
                        $"{who}: points {us.hardPointNum}, targets {TargetRound.Count(core)}, collect {us.collectNum}, point {us.point}, objects {us.totalMonoGetNum}, size {us.coreSize}, rank {us.rank}{ai}");
                }
                return string.Join(" | ", parts);
            }
            catch (Exception e) { return "players: " + e.Message; }
        }

        /// <summary>The HUD's per-player point labels (MultiplayGameManager.listTextMeshPoint).</summary>
        public static string HudPoints()
        {
            try
            {
                var list = GameRefs.Multiplay?.listTextMeshPoint;
                if (list == null) return "no point labels";
                var parts = new List<string>();
                for (int i = 0; i < list.Count; i++)
                {
                    var t = list[i];
                    parts.Add(t == null ? "null" : $"'{t.text}'{(t.gameObject.activeInHierarchy ? "" : " (hidden)")}");
                }
                return "HUD point labels: " + string.Join(", ", parts);
            }
            catch (Exception e) { return "HUD: " + e.Message; }
        }
    }
}
