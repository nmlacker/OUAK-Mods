using System;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using Il2CppApp.KatamariSin;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using LongLostCousins.Dressing;
using LongLostCousins.Select;
using OUAKModAPI;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace LongLostCousins.Test
{
    /// <summary>
    /// Test only: the word <c>probe</c> in <c>cousintest.flag</c>. It runs the in-game probes of slice three's research
    /// (own_cousin, presents, animations, HUD framing) on top of the normal test and logs every answer on a
    /// <c>[CousinProbe]</c> line. Without the word none of this runs and the probe hooks are never made.
    /// <para>Every in-memory change a probe makes is listed in <see cref="Undo"/> and undone after it, and again when the
    /// test finishes; the test blocks every save write for the whole session, so none of it can reach the save. The probe
    /// changes: GlobalManager._nowCharacterID set to 0 for its stage loads; the saved-cousin getter reading 0 inside
    /// ModelLoader.SetUpPlayCharacterModel for one ship rebuild, and inside OujiPresent.Start on a Prince model during its
    /// stage loads; the worn HEAD present set to 0 for the last stage load; the hat objects switched off for two pictures;
    /// a few motion rows asked of the walking cousin and the stage player.</para>
    /// </summary>
    internal static partial class CousinTest
    {
        private static readonly ModLog ProbeLog = LongLostCousinsMod.Log.In("Probe");
        private static bool _probe;
        private static HookSet _probeSet;
        private static int _probeHooks;
        private const int ProbeHooksExpected = 4;

        /// <summary>A test run with the word probe (Dresser reports each dressed model then).</summary>
        public static bool Probing => _probe && _running;

        private static void P(string s) => ProbeLog.Info("[CousinProbe] " + s);

        /// <summary>One probe step: its exceptions are logged and the run goes on.</summary>
        private static void Safe(string what, Action a)
        {
            try { a(); }
            catch (Exception e) { P($"{what} failed: {e.GetType().Name}: {e.Message}"); }
        }

        /// <summary>A probe part whose exception is logged and ends only that part, never the test.</summary>
        private static IEnumerable<object> Guarded(string what, IEnumerable<object> steps)
        {
            using var e = steps.GetEnumerator();
            while (true)
            {
                bool more;
                try { more = e.MoveNext(); }
                catch (Exception ex) { P($"{what} failed: {ex.GetType().Name}: {ex.Message}"); yield break; }
                if (!more) yield break;
                yield return e.Current;
            }
        }

        // ---------------------------------------------------------------- undo

        private static readonly List<KeyValuePair<string, Action>> Undo = new();

        private static void OnUndo(string what, Action undo)
        {
            Undo.RemoveAll(u => u.Key == what);
            Undo.Add(new KeyValuePair<string, Action>(what, undo));
        }

        private static void RunUndo(string what)
        {
            for (int i = Undo.Count - 1; i >= 0; i--)
            {
                if (Undo[i].Key != what) continue;
                var u = Undo[i];
                Undo.RemoveAt(i);
                try { u.Value(); P("undone: " + u.Key); }
                catch (Exception e) { P($"undoing {u.Key} failed: {e.GetType().Name}: {e.Message}"); }
            }
        }

        private static void RunAllUndo()
        {
            while (Undo.Count > 0) RunUndo(Undo[Undo.Count - 1].Key);
        }

        // ---------------------------------------------------------------- the save, read from its fields

        private static int _startSaved = int.MinValue, _startCustom = int.MinValue, _start1D4 = int.MinValue;
        private static int[] _startWearing;

        private static int[] Wearing()
        {
            var w = GameRefs.Save?._wearingPresent;
            if (w == null) return new int[0];
            var r = new int[w.Length];
            for (int i = 0; i < r.Length; i++) r[i] = w[i];
            return r;
        }

        /// <summary>The save's cousin fields, GlobalManager's run-time id, the worn presents, and the getter (read suspended).</summary>
        private static string SaveFacts()
        {
            var save = GameRefs.Save;
            var g = GameRefs.Global;
            if (save == null) return "no save loaded";
            return $"_selectPlayerId {save._selectPlayerId}, _customPlayerIndex {save._customPlayerIndex}, GlobalManager._nowCharacterID {(g != null ? g._nowCharacterID.ToString() : "-")}, " +
                   $"_wearingPresent [{string.Join(", ", Wearing())}] (BODY, FACE, HEAD), the getter reads {Chosen.SavedCousin()}";
        }

        // ---------------------------------------------------------------- arming, start and end

        /// <summary>From CheckFlag, once the save guard is in: the probe-only hooks.</summary>
        private static void ArmProbe()
        {
            var set = new HookSet("LongLostCousins.Probe");
            set.Postfix(typeof(GlobalSaveData), "get_GetSelectPlayerId", typeof(CousinTest), nameof(ProbeAfterGetter), Type.EmptyTypes);
            set.Hook(typeof(ModelLoader), nameof(ModelLoader.SetUpPlayCharacterModel), typeof(CousinTest),
                     nameof(ProbeBeforeSetUp), null, nameof(ProbeFinallySetUp), new[] { typeof(Vector3) });
            set.Hook(typeof(OujiPresent), nameof(OujiPresent.Start), typeof(CousinTest),
                     nameof(ProbeBeforePresentStart), null, nameof(ProbeFinallyPresentStart), Type.EmptyTypes);
            set.Hook(typeof(MainGameManager._StartSceneLoad_d__229), nameof(MainGameManager._StartSceneLoad_d__229.MoveNext), typeof(CousinTest),
                     nameof(ProbeBeforeStartSceneLoad), null, nameof(ProbeFinallyStartSceneLoad), Type.EmptyTypes);
            _probeSet = set;
            _probeHooks = set.Count;
            P($"armed: {set.Count} of {ProbeHooksExpected} probe hooks in place (the saved-cousin getter, ModelLoader.SetUpPlayCharacterModel, OujiPresent.Start, " +
              "MainGameManager.StartSceneLoad's MoveNext); a probe whose hook is missing says so and is skipped");
            OnUndo("the probe hooks", () => { _probeSet?.UnpatchAll(); _probeSet = null; });
        }

        private static bool HookIn(string method) => _probeSet != null && _probeSet.Patched.Any(m => m.Name == method);

        /// <summary>On the ship, once the slot is loaded.</summary>
        private static void ProbeBegin()
        {
            var save = GameRefs.Save;
            if (save != null)
            {
                _startSaved = save._selectPlayerId;
                _startCustom = save._customPlayerIndex;
                _startWearing = Wearing();
            }
            _start1D4 = GameRefs.Global != null ? GameRefs.Global._nowCharacterID : int.MinValue;
            P("start of the run: " + SaveFacts());
            Safe("loading acOuji0000", StartControllerLoad);
        }

        /// <summary>From Finish, before the DONE line: undo everything, the save at the end, summaries.</summary>
        private static void ProbeEnd()
        {
            _zeroInSetUp = false;
            _zeroForPrince = false;
            _zeroStage = false;
            try { RunAllUndo(); } catch (Exception e) { P("undoing failed: " + e.Message); }
            try { ProbeEndChecks(); } catch (Exception e) { P($"the end-of-run checks failed: {e.GetType().Name}: {e.Message}"); }
        }

        private static void ProbeEndChecks()
        {
            P("end of the run: " + SaveFacts());
            var save = GameRefs.Save;
            var g = GameRefs.Global;
            // The select test stores the save's own cousin again, unless that cousin had no slot and it stored the Prince.
            if (_selectStoredPrince) P("the save at the end is not compared with the start: the select test stored the Prince itself");
            else if (save != null && _startSaved != int.MinValue)
            {
                Check("probe: the save's cousin and custom index are as at the start", save._selectPlayerId == _startSaved && save._customPlayerIndex == _startCustom,
                      $"start {_startSaved}/{_startCustom}, end {save._selectPlayerId}/{save._customPlayerIndex}");
                var w = Wearing();
                Check("probe: the worn presents are as at the start", _startWearing != null && w.SequenceEqual(_startWearing),
                      $"start [{string.Join(", ", _startWearing ?? new int[0])}], end [{string.Join(", ", w)}]");
                // While the mod holds the run-time id at 0 for her (a stage still loaded), 0 is the expected value too.
                if (g != null && _start1D4 != int.MinValue)
                    Check("probe: GlobalManager._nowCharacterID is as at the start (or the mod's 0 while a stage holds it)",
                          g._nowCharacterID == _start1D4 || (PrinceBase.Zeroed && g._nowCharacterID == 0),
                          $"start {_start1D4}, end {g._nowCharacterID}, the mod's 0 held {PrinceBase.Zeroed}");
            }
            Check("probe: neither the save nor GlobalManager holds 100", (save == null || save._selectPlayerId != SelectScreen.ModId) && (g == null || g._nowCharacterID != SelectScreen.ModId),
                  $"save {(save != null ? save._selectPlayerId : -1)}, _nowCharacterID {(g != null ? g._nowCharacterID : -1)}");
            Safe("animator summaries", AnimSummaries);
        }

        /// <summary>Each LateUpdate of the run, waits included: no game method is on the stack, so the scopes go back to zero.</summary>
        private static void ProbeTick(float now)
        {
            _setUpDepth = 0;
            _presentZeroDepth = 0;
            _sceneLoadDepth = 0;
            _presentMarks.Clear();
            Safe("watching the animators", () => WatchAnimators(now));
            Safe("watching the presents", () => WatchPresents(now));
            Safe("loading acOuji0000", () => PollControllerLoad(now));
            Safe("watching GlobalManager", WatchGlobal);
        }

        // ---------------------------------------------------------------- hooks (probe runs only)

        private static int _setUpDepth, _setUpCalls, _setUpReads, _setUpRewrites;
        private static bool _zeroInSetUp;
        private static int _presentZeroDepth, _presentZeroReads, _presentZeroRewrites;
        private static bool _zeroForPrince;
        private static readonly Stack<bool> _presentMarks = new();
        private static int _sceneLoadDepth, _sceneLines, _presentLines, _sceneSteps, _lastScene1D4 = int.MinValue;
        private static bool _zeroStage;
        private const int PresentLinesMax = 60, SceneLinesMax = 40;

        /// <summary>GlobalSaveData.GetSelectPlayerId, after it: 0 inside the probe's scopes only (never while the mod suspends it).</summary>
        public static void ProbeAfterGetter(ref int __result)
        {
            if (!_probe || SelectScreen.SuspendDepth != 0) return;
            if (_setUpDepth > 0)
            {
                _setUpReads++;
                if (_zeroInSetUp && __result != 0) { __result = 0; _setUpRewrites++; }
            }
            if (_presentZeroDepth > 0)
            {
                _presentZeroReads++;
                if (__result != 0) { __result = 0; _presentZeroRewrites++; }
            }
        }

        public static void ProbeBeforeSetUp()
        {
            _setUpDepth++;
            if (!_probe) return;
            _setUpCalls++;
            try { P($"ModelLoader.SetUpPlayCharacterModel called (call {_setUpCalls}, frame {Time.frameCount}); the getter {(_zeroInSetUp ? "reads 0 inside it (probe)" : "is the game's inside it")}"); }
            catch { }
        }

        public static Exception ProbeFinallySetUp(Exception __exception)
        {
            if (_setUpDepth > 0) _setUpDepth--;
            return __exception;
        }

        public static void ProbeBeforePresentStart(OujiPresent __instance)
        {
            bool mark = false;
            try
            {
                if (_probe && __instance != null)
                {
                    var save = GameRefs.Save;
                    int saved = save != null ? save._selectPlayerId : -1;
                    int id = __instance._oujiID;
                    mark = _zeroForPrince && id == 0 && saved != 0;
                    if (_presentLines++ < PresentLinesMax)
                        P($"OujiPresent.Start on {U.PathOf(__instance.transform)}: _oujiID {id}, _autoEquip {__instance._autoEquip}, enabled {__instance.enabled}, " +
                          $"_installing {__instance._installing}, _completed {__instance._completed}, saved cousin {saved}, custom index {(save != null ? save._customPlayerIndex : -1)}; " +
                          $"inside SetUpPlayCharacterModel {_setUpDepth > 0}, inside StartSceneLoad {_sceneLoadDepth > 0}, frame {Time.frameCount}" +
                          (mark ? "; the getter reads 0 for this call (probe)" : ""));
                }
            }
            catch { }
            _presentMarks.Push(mark);
            if (mark) _presentZeroDepth++;
        }

        public static Exception ProbeFinallyPresentStart(OujiPresent __instance, Exception __exception)
        {
            bool mark = _presentMarks.Count > 0 && _presentMarks.Pop();
            if (mark && _presentZeroDepth > 0) _presentZeroDepth--;
            try
            {
                if (_probe && __instance != null && _presentLines <= PresentLinesMax)
                    P($"  after OujiPresent.Start on {__instance.name}: _installing {__instance._installing}, _completed {__instance._completed}, _loadCount {__instance._loadCount}" +
                      (__exception != null ? $", it threw {__exception.GetType().Name}" : ""));
            }
            catch { }
            return __exception;
        }

        private static string _lastPrefab = "";

        public static void ProbeBeforeStartSceneLoad()
        {
            _sceneLoadDepth++;
            if (!_probe) return;
            _sceneSteps++;
            try
            {
                var g = GameRefs.Global;
                var save = GameRefs.Save;
                int now = g != null ? g._nowCharacterID : -1;
                string act = "";
                if (_zeroStage && g != null && now != 0)
                {
                    g._nowCharacterID = 0;
                    act = $"; it held {now}, set to 0 (probe)";
                }
                // The iterator's own fields (<>1__state, <>4__this) are left alone: the call audit can't vouch for them.
                bool changed = now != _lastScene1D4 || act.Length > 0;
                _lastScene1D4 = g != null ? g._nowCharacterID : -1;
                if (changed && _sceneLines++ < SceneLinesMax)
                    P($"StartSceneLoad step {_sceneSteps} (frame {Time.frameCount}): _nowCharacterID {now}, saved cousin {(save != null ? save._selectPlayerId : -1)}{act}");
            }
            catch { }
        }

        public static Exception ProbeFinallyStartSceneLoad(Exception __exception)
        {
            if (_sceneLoadDepth > 0) _sceneLoadDepth--;
            if (!_probe) return __exception;
            try
            {
                var gm = GameRefs.Main;
                var pf = gm != null ? gm._playerPrefab : null;
                string n = pf != null ? pf.name : "null";
                if (n != _lastPrefab)
                {
                    _lastPrefab = n;
                    P($"StartSceneLoad: MainGameManager._playerPrefab is now {n}{(pf != null ? $" (ModelSetup {pf.GetComponentInChildren<ModelSetup>(true) != null}, OujiPresent._oujiID {PresentId(pf)})" : "")}, " +
                      $"_nowCharacterID {GameRefs.Global?._nowCharacterID}");
                }
            }
            catch { }
            return __exception;
        }

        private static int _seen1D4 = int.MinValue;

        /// <summary>Every change of GlobalManager._nowCharacterID during the run, with the scene.</summary>
        private static void WatchGlobal()
        {
            var g = GameRefs.Global;
            if (g == null) return;
            int v = g._nowCharacterID;
            if (v == _seen1D4) return;
            if (_seen1D4 != int.MinValue) P($"GlobalManager._nowCharacterID changed {_seen1D4} -> {v} (scene {SceneLoader.ActiveScene}, frame {Time.frameCount})");
            _seen1D4 = v;
        }

        // ---------------------------------------------------------------- models and presents

        private static int PresentId(GameObject go)
        {
            var op = go != null ? go.GetComponentInChildren<OujiPresent>(true) : null;
            return op != null ? op._oujiID : -1;
        }

        /// <summary>What a cousin model is: its path (the prefab's clone name), OujiPresent's id, ModelSetup, the controller.</summary>
        private static string ModelFacts(GameObject go)
        {
            if (go == null) return "none";
            var op = go.GetComponentInChildren<OujiPresent>(true);
            var ms = go.GetComponentInChildren<ModelSetup>(true);
            var an = go.GetComponentInChildren<Animator>(true);
            var ctrl = an != null ? an.runtimeAnimatorController : null;
            return $"{U.PathOf(go.transform)}: OujiPresent._oujiID {(op != null ? op._oujiID.ToString() : "none")}, " +
                   $"ModelSetup {(ms == null ? "none" : $"custom mode {ms._customMode}, status {ms.status}")}, controller {(ctrl != null ? ctrl.name : "none")}";
        }

        /// <summary>What tells two built cousins apart: OujiPresent's id, ModelSetup or not, the controller, the model's own name.</summary>
        private static string ModelKey(GameObject go)
        {
            if (go == null) return null;
            var an = go.GetComponentInChildren<Animator>(true);
            var ctrl = an != null ? an.runtimeAnimatorController : null;
            return $"id {PresentId(go)}, ModelSetup {go.GetComponentInChildren<ModelSetup>(true) != null}, controller {(ctrl != null ? ctrl.name : "none")}, {go.name}";
        }

        private static readonly string[] Types = { "BODY", "FACE", "HEAD" };
        private static readonly HashSet<int> ListedIds = new();

        private static bool Under(Transform t, Transform root)
        {
            if (t == null || root == null) return false;
            IntPtr r = root.Pointer;
            for (int i = 0; t != null && i < 64; t = t.parent, i++) if (t.Pointer == r) return true;
            return false;
        }

        private static PresentData Entry(OujiPresent op, int type, int id)
        {
            var l = op._datas;
            if (l == null) return null;
            PresentData any = null;
            for (int i = 0; i < l.Count; i++)
            {
                var pd = l[i];
                if (pd == null || pd._preID != id) continue;
                if ((int)pd._type == type) return pd;
                any ??= pd;
            }
            return any;
        }

        /// <summary>A present object: where it hangs, whether it is on, its renderers (skinned or not) and layer.</summary>
        private static string ObjFacts(GameObject go)
        {
            if (go == null) return "no object";
            var rs = go.GetComponentsInChildren<Renderer>(true);
            int on = 0, skinned = 0;
            foreach (var r in rs) { if (r.enabled) on++; if (r.TryCast<SkinnedMeshRenderer>() != null) skinned++; }
            return $"{go.name} under {(go.transform.parent != null ? U.PathOf(go.transform.parent) : "nothing")}, active {go.activeSelf}/{go.activeInHierarchy}, layer {go.layer}, " +
                   $"{rs.Length} renderer(s), {on} on, {skinned} skinned";
        }

        private static string EntryLine(PresentData pd, Transform root)
        {
            var bone = pd._boneTransform;
            var obj = pd._presentGameObject;
            string where = bone == null ? "no bone" : Under(bone, root) ? "bone in this model" : "bone OUTSIDE this model";
            bool skinned = obj != null && obj.GetComponentInChildren<SkinnedMeshRenderer>(true) != null;
            return $"{Types.ElementAtOrDefault((int)pd._type) ?? ((int)pd._type).ToString()} {pd._preID} {pd._aseetName}: bone {(bone != null ? bone.name : "none")} ({where}), " +
                   $"object {(obj != null ? $"{obj.name}, active {obj.activeSelf}, skinned {skinned}" : "none")}";
        }

        /// <summary>OujiPresent on a model: the lists, the worn entries, and (once per cousin id) every entry.</summary>
        private static void DumpPresents(OujiPresent op, string label)
        {
            var root = op.transform;
            var datas = op._datas;
            var stage = op._stagePresentDatas;
            int n = datas != null ? datas.Count : -1, ns = stage != null ? stage.Count : -1;
            int inModel = 0, outside = 0, noBone = 0;
            if (datas != null)
                for (int i = 0; i < datas.Count; i++)
                {
                    var b = datas[i]?._boneTransform;
                    if (b == null) noBone++; else if (Under(b, root)) inModel++; else outside++;
                }
            var ant = op._antennaObject;
            var body = op._bodyRoot;
            P($"presents on the {label} ({U.PathOf(root)}): _oujiID {op._oujiID}, _datas {n}, _stagePresentDatas {ns}, bones in this model {inModel}, outside it {outside}, none {noBone}; " +
              $"_completed {op._completed}, _installing {op._installing}, _autoEquip {op._autoEquip}, _loadCount {op._loadCount}, " +
              $"antenna {(ant != null ? "active " + ant.activeSelf : "none")}, body_root {(body != null ? "active " + body.activeSelf : "none")}");
            var w = Wearing();
            for (int t = 0; t < w.Length && t < 3; t++)
            {
                if (w[t] <= 0) { P($"  worn {Types[t]}: nothing"); continue; }
                var pd = Entry(op, t, w[t]);
                P($"  worn {Types[t]} {w[t]}: {(pd == null ? "no entry in _datas" : EntryLine(pd, root) + "; " + ObjFacts(pd._presentGameObject))}");
            }
            if (stage != null)
                for (int i = 0; i < stage.Count; i++) if (stage[i] != null) P($"  stage list [{i}] {EntryLine(stage[i], root)}");
            if (datas != null && ListedIds.Add(op._oujiID))
            {
                P($"  every entry of cousin {op._oujiID}'s _datas (listed once per cousin id):");
                for (int i = 0; i < datas.Count; i++) if (datas[i] != null) P($"  [{i}] {EntryLine(datas[i], root)}");
            }
        }

        private sealed class PresentWatch
        {
            public DressedModel Dm;
            public OujiPresent Op;
            public float At;
            public readonly bool[] Had = new bool[3];
            public bool Completed;
        }

        private static readonly List<PresentWatch> PresentWatches = new();

        /// <summary>Dresser, after a model is dressed or re-bound (probe runs only): what the presents are at that moment.</summary>
        public static void ProbeDressed(DressedModel dm)
        {
            if (!_probe || dm == null) return;
            Safe("the dressed model", () =>
            {
                P($"dressed for the {dm.Context} at {U.F(Time.unscaledTime)} s (frame {Time.frameCount}): {ModelFacts(dm.Root)}");
                var op = dm.Root.GetComponentInChildren<OujiPresent>(true);
                if (op == null) { P("  it has no OujiPresent"); return; }
                DumpPresents(op, dm.Context + " at dress time");
                var pw = new PresentWatch { Dm = dm, Op = op, At = Time.unscaledTime, Completed = op._completed };
                var w = Wearing();
                for (int t = 0; t < 3 && t < w.Length; t++)
                {
                    var pd = w[t] > 0 ? Entry(op, t, w[t]) : null;
                    pw.Had[t] = w[t] <= 0 || pd == null || pd._presentGameObject != null;
                }
                PresentWatches.Add(pw);
            });
        }

        /// <summary>For 30 s after each dressing: when each worn present's object appears, and when OujiPresent completes.</summary>
        private static void WatchPresents(float now)
        {
            for (int i = PresentWatches.Count - 1; i >= 0; i--)
            {
                var pw = PresentWatches[i];
                if (pw.Op == null || pw.Dm.Root == null) { PresentWatches.RemoveAt(i); continue; }
                var w = Wearing();
                for (int t = 0; t < 3 && t < w.Length; t++)
                {
                    if (pw.Had[t]) continue;
                    var pd = Entry(pw.Op, t, w[t]);
                    var obj = pd != null ? pd._presentGameObject : null;
                    if (obj == null) continue;
                    pw.Had[t] = true;
                    P($"present {Types[t]} {w[t]} arrived on the {pw.Dm.Context} {U.F(now - pw.At)} s after dressing (frame {Time.frameCount}): {ObjFacts(obj)}");
                }
                if (!pw.Completed && pw.Op._completed)
                {
                    pw.Completed = true;
                    P($"OujiPresent on the {pw.Dm.Context} completed {U.F(now - pw.At)} s after dressing (_installing {pw.Op._installing}, _loadCount {pw.Op._loadCount})");
                }
                if (now - pw.At > 30f)
                {
                    var missing = Enumerable.Range(0, 3).Where(t => !pw.Had[t]).Select(t => Types[t]).ToList();
                    P($"presents on the {pw.Dm.Context}, 30 s after dressing: {(missing.Count == 0 ? "every worn present has its object" : "no object yet for " + string.Join(", ", missing))}, completed {pw.Op._completed}");
                    PresentWatches.RemoveAt(i);
                }
            }
        }

        // ---------------------------------------------------------------- animators

        private static Dictionary<int, string> _stateNames;

        /// <summary>Names for state hashes: this game's states are 4-digit numbers (some with _select, _select_1), a few short numbers, Idle and PataPata.</summary>
        private static void BuildStateNames()
        {
            var d = new Dictionary<int, string>();
            void Add(string s) { int h = Animator.StringToHash(s); if (!d.ContainsKey(h)) d[h] = s; }
            for (int i = 0; i < 2000; i++) Add(i.ToString("0000"));
            for (int i = 0; i < 200; i++) { Add(i.ToString()); Add(i.ToString("0000") + "_select"); Add(i.ToString("0000") + "_select_1"); }
            Add("Idle");
            Add("PataPata");
            _stateNames = d;
            P($"state names: {d.Count} candidate names hashed (StringToHash)");
        }

        private static string StateName(int h) => _stateNames != null && _stateNames.TryGetValue(h, out var s) ? s : "#" + h.ToString("X8");

        private sealed class AnimWatch
        {
            public string Label;
            public Animator Anim;
            public int Hash = int.MinValue;
            public float Nt, Speed = float.NaN, Since;
            public int Lines, Changes;
            public bool Gone;
            public readonly Dictionary<int, int> Seen = new();
        }

        private static readonly Dictionary<DressedModel, AnimWatch> Watches = new();
        private const int AnimLinesMax = 80;

        private static string AnimSetup(Animator a)
        {
            var c = a.runtimeAnimatorController;
            var clips = c != null ? c.animationClips : null;
            return $"{U.PathOf(a.transform)}: controller {(c != null ? c.name : "none")} ({(clips != null ? clips.Length : 0)} clips), updateMode {a.updateMode}, " +
                   $"cullingMode {a.cullingMode}, speed {U.F(a.speed)}, enabled {a.enabled}, active {a.gameObject.activeInHierarchy}";
        }

        /// <summary>Every dressed model's game Animator (the player base): each state change and speed change.</summary>
        private static void WatchAnimators(float now)
        {
            if (Dresser.Models.Count == 0) return;
            if (_stateNames == null) BuildStateNames();
            foreach (var dm in Dresser.Models)
            {
                if (dm.Root == null) continue;
                if (!Watches.TryGetValue(dm, out var w))
                {
                    var a = dm.Root.GetComponentInChildren<Animator>(true);
                    w = new AnimWatch { Label = dm.Context, Anim = a, Since = now };
                    Watches[dm] = w;
                    P($"anim {w.Label}: watching {(a != null ? AnimSetup(a) : "no Animator")}");
                }
                if (w.Gone) continue;
                if (w.Anim == null) { w.Gone = true; P($"anim {w.Label}: the Animator is gone after {w.Changes} state change(s)"); continue; }
                var st = w.Anim.GetCurrentAnimatorStateInfo(0);
                int h = st.m_Name;
                float sp = w.Anim.speed;
                if (h != w.Hash)
                {
                    bool trans = w.Anim.IsInTransition(0);
                    string next = "";
                    if (trans) { var nx = w.Anim.GetNextAnimatorStateInfo(0); next = $", next {StateName(nx.m_Name)} nt {U.F(nx.m_NormalizedTime)}"; }
                    w.Seen[h] = w.Seen.TryGetValue(h, out int c) ? c + 1 : 1;
                    w.Changes++;
                    if (w.Lines++ < AnimLinesMax)
                        P($"anim {w.Label}: {(w.Hash == int.MinValue ? "first state" : $"{StateName(w.Hash)} (left at nt {U.F(w.Nt)} after {U.F(now - w.Since)} s)")} -> {StateName(h)} " +
                          $"nt {U.F(st.m_NormalizedTime)}, length {U.F(st.m_Length)} s, loop {st.m_Loop}, state speed {U.F(st.m_Speed)} x {U.F(st.m_SpeedMultiplier)}, " +
                          $"animator speed {U.F(sp)}, updateMode {w.Anim.updateMode}, in transition {trans}{next}, frame {Time.frameCount}");
                    w.Hash = h;
                    w.Since = now;
                }
                else if (!float.IsNaN(w.Speed) && Math.Abs(sp - w.Speed) > 0.001f && w.Lines++ < AnimLinesMax)
                    P($"anim {w.Label}: animator speed {U.F(w.Speed)} -> {U.F(sp)} in {StateName(h)} at nt {U.F(st.m_NormalizedTime)}");
                w.Nt = st.m_NormalizedTime;
                w.Speed = sp;
            }
        }

        private static void AnimSummaries()
        {
            foreach (var w in Watches.Values)
                P($"anim summary {w.Label}: {w.Changes} state change(s); states seen: " +
                  (w.Seen.Count == 0 ? "none" : string.Join(", ", w.Seen.OrderByDescending(kv => kv.Value).Select(kv => $"{StateName(kv.Key)} x{kv.Value}"))));
        }

        // ---------------------------------------------------------------- acOuji0000 by Addressables

        private static readonly string[] ControllerKeys = { "Ouji/Anim/acOuji0000.controller", "Ouji/Anim/acOuji0000", "acOuji0000.controller", "acOuji0000" };
        private static AsyncOperationHandle<Il2CppSystem.Collections.Generic.IList<IResourceLocation>> _acLocate;
        private static AsyncOperationHandle<UnityEngine.Object> _acHandle;
        // 0 not started, 1 looking up key _acKey, 2 loading, 3 done
        private static int _acPhase, _acKey;
        private static float _acAt;
        private static RuntimeAnimatorController _acController;

        /// <summary>
        /// Looks each candidate key up (LoadResourceLocationsAsync: an unknown key gives no locations and no error), then
        /// loads the first key that has a location. Only a found key is asked for.
        /// </summary>
        private static void StartControllerLoad()
        {
            _acKey = 0;
            _acPhase = 1;
            _acAt = Time.unscaledTime;
            _acLocate = Addressables.LoadResourceLocationsAsync((Il2CppSystem.String)ControllerKeys[0], null);
        }

        private static void PollControllerLoad(float now)
        {
            if (_acPhase == 0 || _acPhase == 3) return;
            if (now - _acAt > 30f) { P($"Addressables: no answer within 30 s ({(_acPhase == 1 ? "looking up " + ControllerKeys[_acKey] : "loading")})"); _acPhase = 3; return; }
            if (_acPhase == 1)
            {
                if (!_acLocate.IsDone) return;
                int count = -1;
                if (_acLocate.Status == AsyncOperationStatus.Succeeded)
                {
                    var list = _acLocate.Result?.TryCast<Il2CppSystem.Collections.Generic.List<IResourceLocation>>();
                    count = list != null ? list.Count : -2;
                }
                string key = ControllerKeys[_acKey];
                P($"Addressables: key {key}: {(count > 0 ? count + " location(s)" : count == 0 ? "no location" : count == -2 ? "a result of another list type" : "the look-up " + _acLocate.Status)}");
                if (count > 0)
                {
                    _acPhase = 2;
                    _acAt = now;
                    _acHandle = Addressables.LoadAssetAsync<UnityEngine.Object>((Il2CppSystem.String)key);
                    P("Addressables: loading " + key);
                    return;
                }
                if (++_acKey >= ControllerKeys.Length) { _acPhase = 3; P("Addressables: no acOuji0000 key has a location; not loading it"); return; }
                _acLocate = Addressables.LoadResourceLocationsAsync((Il2CppSystem.String)ControllerKeys[_acKey], null);
                return;
            }
            if (!_acHandle.IsDone) return;
            _acPhase = 3;
            var obj = _acHandle.Status == AsyncOperationStatus.Succeeded ? _acHandle.Result : null;
            _acController = obj != null ? obj.TryCast<RuntimeAnimatorController>() : null;
            var clips = _acController != null ? _acController.animationClips : null;
            P($"Addressables: acOuji0000 load {_acHandle.Status} after {U.F(now - _acAt)} s: {(obj != null ? obj.name : "nothing")}, " +
              $"a RuntimeAnimatorController {_acController != null}, {(clips != null ? clips.Length : 0)} clips" +
              (clips != null && clips.Length > 0 ? ", first " + string.Join(", ", Enumerable.Range(0, Math.Min(5, clips.Length)).Select(i => clips[i] != null ? clips[i].name : "null")) : ""));
        }

        /// <summary>The loaded acOuji0000 against a model's own controller: the same object, the same clips.</summary>
        private static void CompareController(string label, GameObject go)
        {
            var an = go != null ? go.GetComponentInChildren<Animator>(true) : null;
            var mine = an != null ? an.runtimeAnimatorController : null;
            if (_acController == null) { P($"controller compare ({label}): acOuji0000 was not loaded; the model's is {(mine != null ? mine.name : "none")}"); return; }
            if (mine == null) { P($"controller compare ({label}): the model has no controller"); return; }
            var a = _acController.animationClips;
            var b = mine.animationClips;
            var na = new HashSet<string>(Enumerable.Range(0, a != null ? a.Length : 0).Select(i => a[i] != null ? a[i].name : ""));
            var nb = new HashSet<string>(Enumerable.Range(0, b != null ? b.Length : 0).Select(i => b[i] != null ? b[i].name : ""));
            P($"controller compare ({label}): the model's {mine.name} ({(b != null ? b.Length : 0)} clips) against the loaded {_acController.name} ({(a != null ? a.Length : 0)} clips): " +
              $"the same object {mine.Pointer == _acController.Pointer}, the same clip names {na.SetEquals(nb)} ({na.Intersect(nb).Count()} shared)");
        }

        // ---------------------------------------------------------------- motion rows asked of a driver

        /// <summary>The walking cousin asked for a few rows through its own request call (the test gives no input, so it only idles otherwise).</summary>
        private static IEnumerable<object> AskRows(string label, Action<int> request, int[] rows)
        {
            foreach (int r in rows)
            {
                bool ok = true;
                try { request(r); }
                catch (Exception e) { ok = false; P($"asking the {label} for row {r} failed: {e.Message}"); }
                if (ok) P($"asked the {label} for motion row {r}");
                yield return 3f;
            }
        }

        // ---------------------------------------------------------------- the ship

        private static string _firstWalker;

        private static IEnumerable<object> ShipProbes()
        {
            P("ship probes: " + SaveFacts());
            var walker = Contexts.PlazaOwnerModel();
            _firstWalker = ModelKey(walker);
            P("ship: the walking cousin is " + ModelFacts(walker));
            Safe("the ship's UI copies", LogUiCopies);
            Safe("the walking cousin's controller", () => CompareController("ship walking cousin", walker));

            // Motion: idle on its own, then a few rows asked through CharacterAnimation.ReqestCharacterAnime.
            yield return 8f;
            var ca = walker != null ? walker.GetComponent<CharacterAnimation>() : null;
            if (ca != null)
            {
                foreach (var x in AskRows("walking cousin", r => ca.ReqestCharacterAnime(r), new[] { 41, 48, 0 })) yield return x;
            }
            else P("ship: the walking cousin has no CharacterAnimation on its root; no rows asked");

            // A ship rebuild with the getter reading 0 inside SetUpPlayCharacterModel, then one with the game's getter.
            if (!HookIn("SetUpPlayCharacterModel") || !HookIn("get_GetSelectPlayerId"))
            {
                P("ship rebuilds left out: the probe hooks on SetUpPlayCharacterModel and the getter are not both in");
                yield break;
            }
            bool rebuilt = false;
            foreach (var x in ShipRebuild(true, ok => rebuilt = ok)) yield return x;
            if (!rebuilt) yield break;
            yield return Until(() => Dresser.FindFor(Contexts.PlazaOwnerModel()) != null, 20f, "the rebuilt walking cousin to be dressed (probe)", false);
            yield return 2f;
            var prince = Contexts.PlazaOwnerModel();
            P("ship, after the rebuild with the getter at 0: the walking cousin is " + ModelFacts(prince));
            Safe("the ship's UI copies", LogUiCopies);
            Safe("the walking cousin's controller", () => CompareController("ship walking cousin, rebuilt with the getter at 0", prince));
            yield return 8f;
            var pca = prince != null ? prince.GetComponent<CharacterAnimation>() : null;
            if (pca != null) foreach (var x in AskRows("rebuilt walking cousin", r => pca.ReqestCharacterAnime(r), new[] { 41, 0 })) yield return x;

            foreach (var x in ShipRebuild(false, ok => rebuilt = ok)) yield return x;
            if (!rebuilt) yield break;
            yield return Until(() => Dresser.FindFor(Contexts.PlazaOwnerModel()) != null, 20f, "the rebuilt walking cousin to be dressed (probe)", false);
            yield return 2f;
            var back = Contexts.PlazaOwnerModel();
            string backName = ModelKey(back);
            P("ship, after the rebuild with the game's getter: the walking cousin is " + ModelFacts(back));
            Check("probe: the ship rebuilt with the game's getter builds the first walker's model again (the Prince while she is worn)", backName != null && backName == _firstWalker,
                  $"first {_firstWalker ?? "none"}, now {backName ?? "none"}");
            Safe("the ship's UI copies", LogUiCopies);
        }

        private static IEnumerable<object> ShipRebuild(bool zero, Action<bool> done)
        {
            IntPtr before = WalkerPtr();
            int calls = _setUpCalls, reads = _setUpReads, rewrites = _setUpRewrites;
            var etm = UnityEngine.Object.FindObjectOfType<EventToMenuController>();
            var och = etm != null ? etm.m_OrderChangeChara : null;
            if (och == null) och = UnityEngine.Object.FindObjectOfType<OrderToChangeCharacter>();
            if (och == null) { P("ship rebuild left out: no OrderToChangeCharacter in the scene"); done(false); yield break; }
            _zeroInSetUp = zero;
            if (zero) OnUndo("the getter reading 0 inside SetUpPlayCharacterModel", () => _zeroInSetUp = false);
            P($"[direct] ship rebuild {(zero ? "with the getter reading 0 inside SetUpPlayCharacterModel" : "with the game's getter")}: OrderToChangeCharacter.OrderActiveEvent(); {SaveFacts()}");
            och.OrderActiveEvent();
            yield return Until(() => WalkerPtr() != IntPtr.Zero && WalkerPtr() != before, 60f, "the ship to rebuild the walking cousin (probe)", false);
            if (zero) RunUndo("the getter reading 0 inside SetUpPlayCharacterModel");
            bool ok = WalkerPtr() != IntPtr.Zero && WalkerPtr() != before;
            P($"ship rebuild {(ok ? "done" : "not done")}: SetUpPlayCharacterModel called {_setUpCalls - calls}x, the getter read {_setUpReads - reads}x inside it, " +
              $"{_setUpRewrites - rewrites} read(s) turned to 0; {SaveFacts()}");
            done(ok);
        }

        /// <summary>Every UIOuji in the scene and the model it shows.</summary>
        private static void LogUiCopies()
        {
            var all = UnityEngine.Object.FindObjectsOfType<UIOuji>();
            if (all.Length == 0) { P("UI copies: no UIOuji in the scene"); return; }
            foreach (var u in all)
            {
                var ca = u._characterAnimation;
                P($"UI copy {U.PathOf(u.transform)}: shows {(ca != null ? ModelFacts(ca.gameObject) : "no model")}; dressed {(ca != null && Dresser.FindFor(ca.gameObject) != null)}");
            }
        }

        // ---------------------------------------------------------------- the stage

        private static int _stage1D4Before = int.MinValue;

        /// <summary>Right after SceneLoader.LoadStage (which sets _nowCharacterID from the save): 0 for this load, put back after the stage probes.</summary>
        private static void ProbeAfterLoadStage(string which)
        {
            var g = GameRefs.Global;
            if (g == null) { P("stage: no GlobalManager; _nowCharacterID left alone"); return; }
            int before = g._nowCharacterID;
            if (_stage1D4Before == int.MinValue) _stage1D4Before = before;
            int restore = _stage1D4Before;
            g._nowCharacterID = 0;
            _zeroStage = HookIn("MoveNext");
            _zeroForPrince = HookIn("Start") && HookIn("get_GetSelectPlayerId");
            _sceneLines = 0;
            _lastScene1D4 = int.MinValue;
            _sceneSteps = 0;
            OnUndo("_nowCharacterID set to 0 for the stage", () =>
            {
                _zeroStage = false;
                _zeroForPrince = false;
                var gg = GameRefs.Global;
                if (gg != null) gg._nowCharacterID = restore;
            });
            P($"{which} stage load: GlobalManager._nowCharacterID was {before} after SceneLoader.LoadStage; set to 0 in memory for this load (put back to {restore} after the stage probes)" +
              $"{(_zeroStage ? "; StartSceneLoad's steps set it to 0 again if something changed it" : "; no StartSceneLoad hook, so a later change is only logged")}" +
              $"{(_zeroForPrince ? "; OujiPresent.Start on a Prince model reads the saved cousin as 0" : "")}");
        }

        private static int HudLayer(DressedModel hd, UIMainGameOuji hud)
        {
            if (hd != null && hd.LayerSource != null) return hd.LayerSource.layer;
            return hud != null ? hud.gameObject.layer : LayerMask.NameToLayer("UIOuji");
        }

        private static IEnumerable<object> StageProbes()
        {
            var main = GameRefs.Main;
            if (main == null) { P("stage probes left out: no stage manager"); yield break; }
            _zeroStage = false;
            _zeroForPrince = false;
            Safe("the stage's models", () => StageModels(main));
            Safe("the stage's presents", () => StagePresents(main));
            Safe("the purple object", () => PurpleSearch(main));
            Safe("the HUD camera", () => HudFraming(main, "idle"));
            Safe("the controller", () => CompareController("stage player", main.Ouji != null ? main.Ouji.gameObject : null));

            // Pictures: the frame and the HUD's own render texture; then the hats under the HUD copy off, then every hat off.
            var cams = HudCameras(main);
            Shot("probe_hud_" + _style);
            yield return PictureTaken();
            SaveHud(cams, "probe_hudrt_" + _style);
            var hats = Hats();
            var hudRoot = main.UIOuji != null ? main.UIOuji.transform : null;
            var copyHats = hats.Where(h => Under(h.transform, hudRoot)).ToList();
            P($"purple object pictures: {hats.Count} hat object(s) named KD4_pre_01_01 in the scene, {copyHats.Count} under the HUD copy");
            foreach (var x in HatsOff(copyHats, "under the HUD copy", "probe_hud_nohat_copy_" + _style, cams)) yield return x;
            foreach (var x in HatsOff(hats, "anywhere", "probe_hud_nohat_all_" + _style, cams)) yield return x;

            // Motion on the stage: idle, then rows with a state speed of their own (31 x2, 60 x3), then idle again.
            yield return 6f;
            var ouji = main.Ouji;
            if (ouji != null) foreach (var x in AskRows("stage player", r => ouji.RequestOujiAnime(r, false), new[] { 31, 60, 0 })) yield return x;
            yield return 4f;
            Safe("the HUD camera", () => HudFraming(main, "after the rows"));
            RunUndo("_nowCharacterID set to 0 for the stage");
            P("after the stage probes: " + SaveFacts());
        }

        private static void StageModels(MainGameManager main)
        {
            var g = GameRefs.Global;
            var pf = main._playerPrefab;
            P($"stage {main._stageIdx}: MainGameManager._playerPrefab {(pf != null ? pf.name : "null")} (ModelSetup {(pf != null && pf.GetComponentInChildren<ModelSetup>(true) != null)}); " +
              $"_beforeOujiIdx {main._beforeOujiIdx}, _beforeOujiIdx_Select {main._beforeOujiIdx_Select}; {SaveFacts()}");
            P($"OujiPresent.Start on a Prince model with the getter at 0 (probe): the getter was read {_presentZeroReads}x inside those calls, {_presentZeroRewrites} read(s) turned to 0");
            P("stage player: " + ModelFacts(main.Ouji != null ? main.Ouji.gameObject : null));
            P("stage HUD copy: " + ModelFacts(main.UIOuji != null ? main.UIOuji.gameObject : null));
            var uic = UnityEngine.Object.FindObjectOfType<UIOujiController>();
            if (uic != null)
                P($"UIOujiController {U.PathOf(uic.transform)}: _waitCustomOuji {uic._waitCustomOuji}, CustomOujiSetup {(uic.CustomOujiSetup != null ? "set" : "none")}, " +
                  $"OujiChaseCam {(uic.OujiChaseCam != null ? U.PathOf(uic.OujiChaseCam) : "none")}, _camDefaultPos {U.F(uic._camDefaultPos)}, UISCALE {U.F(UIOujiController.UISCALE)}");
            var rd = g != null ? g.LastStageResultData : null;
            if (rd != null)
            {
                var l = rd.ItokoIDList;
                var ids = new List<int>();
                if (l != null) for (int i = 0; i < l.Count && i < 64; i++) ids.Add(l[i]);
                P($"results data (GlobalManager.LastStageResultData, which PerformLoadResultMono reads): ItokoID {rd.ItokoID}, ItokoIDList [{string.Join(", ", ids)}], PresentID {rd.PresentID}");
            }
            else P("results data: GlobalManager.LastStageResultData is null");
        }

        private static void StagePresents(MainGameManager main)
        {
            var ouji = main.Ouji;
            var op = ouji != null ? ouji.GetComponentInChildren<OujiPresent>(true) : null;
            if (op != null) DumpPresents(op, "stage player, after the start");
            var hud = main.UIOuji;
            var hop = hud != null ? hud.GetComponentInChildren<OujiPresent>(true) : null;
            if (hop != null) DumpPresents(hop, "stage HUD copy, after the start");

            // The fade list: InitializeOuji builds rendList once; presents that come later are not in it.
            if (ouji != null && op != null)
            {
                var inList = new HashSet<IntPtr>();
                var list = ouji.rendList;
                if (list != null) for (int i = 0; i < list.Count; i++) { var r = list[i]; if (r != null) inList.Add(r.Pointer); }
                var w = Wearing();
                for (int t = 0; t < 3 && t < w.Length; t++)
                {
                    if (w[t] <= 0) continue;
                    var obj = Entry(op, t, w[t])?._presentGameObject;
                    if (obj == null) { P($"fade list: worn {Types[t]} {w[t]} has no object"); continue; }
                    var rs = obj.GetComponentsInChildren<Renderer>(true);
                    P($"fade list: worn {Types[t]} {w[t]} ({obj.name}): {rs.Count(r => inList.Contains(r.Pointer))} of {rs.Length} renderer(s) in rendList ({inList.Count} in the list)");
                }
                var dm = Dresser.FindFor(ouji.gameObject);
                int body = dm != null && dm.LayerSource != null ? dm.LayerSource.layer : -1;
                P($"layers: the game's body_m {body}, her marker {(dm != null && dm.Marker != null ? dm.Marker.gameObject.layer : -1)}, the model root {ouji.gameObject.layer}");
            }
        }

        /// <summary>Every object named KD4_pre_01_01* (the hat this save wears) that holds renderers, active ones only.</summary>
        private static List<GameObject> Hats()
        {
            var found = new Dictionary<IntPtr, GameObject>();
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                Transform top = null;
                for (var t = r.transform; t != null; t = t.parent)
                    if (t.name.StartsWith("KD4_pre_01_01", StringComparison.Ordinal)) top = t;
                if (top != null) found[top.gameObject.Pointer] = top.gameObject;
            }
            return found.Values.ToList();
        }

        private static string Seers(int layer)
        {
            var names = new List<string>();
            foreach (var c in UnityEngine.Object.FindObjectsOfType<Camera>())
                if (c.enabled && c.gameObject.activeInHierarchy && (c.cullingMask & (1 << layer)) != 0) names.Add(c.name);
            return names.Count == 0 ? "no camera" : string.Join(", ", names);
        }

        private static bool OwnedByHer(Renderer r)
        {
            foreach (var dm in Dresser.Models) if (dm.Own.Contains(r.Pointer) || dm.PresentRenderers.Contains(r.Pointer)) return true;
            return false;
        }

        /// <summary>The purple object: every KD4_pre_01_01* renderer in the scene and who sees it, and what else is drawn on the HUD layer.</summary>
        private static void PurpleSearch(MainGameManager main)
        {
            var hd = main.UIOuji != null ? Dresser.FindFor(main.UIOuji.gameObject) : null;
            int layer = HudLayer(hd, main.UIOuji);
            P($"the HUD copy's layer is {layer} ({LayerMask.LayerToName(layer)}); cameras that draw it: {Seers(layer)}");
            int n = 0;
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                bool hat = false;
                for (var t = r.transform; t != null && !hat; t = t.parent) hat = t.name.StartsWith("KD4_pre_01_01", StringComparison.Ordinal);
                if (!hat) continue;
                n++;
                int l = r.gameObject.layer;
                P($"hat renderer {U.PathOf(r.transform)}: enabled {r.enabled}, active {r.gameObject.activeInHierarchy}, drawn {r.isVisible}, layer {l} ({LayerMask.LayerToName(l)}), seen by {Seers(l)}");
            }
            if (n == 0) P("no renderer named KD4_pre_01_01* in the scene");
            var others = new List<string>();
            int count = 0;
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (r.gameObject.layer != layer || !r.enabled || OwnedByHer(r)) continue;
                count++;
                if (others.Count < 40) others.Add($"{U.PathOf(r.transform)} (drawn {r.isVisible})");
            }
            P($"other renderers on in layer {layer} (not hers): {count}{(count > 0 ? ": " + string.Join("; ", others) : "")}");
        }

        private static IEnumerable<object> HatsOff(List<GameObject> hats, string which, string shot, List<Camera> cams)
        {
            if (hats.Count == 0) { P($"no hat object {which}: picture {shot} left out"); yield break; }
            var was = hats.Select(h => h != null && h.activeSelf).ToList();
            string key = "hat objects switched off " + which;
            OnUndo(key, () => { for (int i = 0; i < hats.Count; i++) if (hats[i] != null && was[i]) hats[i].SetActive(true); });
            foreach (var h in hats) if (h != null) h.SetActive(false);
            P($"switched off {hats.Count} hat object(s) {which}: {string.Join(", ", hats.Where(h => h != null).Select(h => U.PathOf(h.transform)))}");
            yield return 0.3f;
            Shot(shot);
            yield return PictureTaken();
            SaveHud(cams, shot.Replace("probe_hud_", "probe_hudrt_"));
            RunUndo(key);
            yield return 0.2f;
        }

        // ---------------------------------------------------------------- HUD framing

        private static List<Camera> HudCameras(MainGameManager main)
        {
            var hd = main.UIOuji != null ? Dresser.FindFor(main.UIOuji.gameObject) : null;
            int layer = HudLayer(hd, main.UIOuji);
            return UnityEngine.Object.FindObjectsOfType<Camera>().Where(c => c.enabled && c.gameObject.activeInHierarchy && (c.cullingMask & (1 << layer)) != 0).ToList();
        }

        private static void SaveHud(List<Camera> cams, string name)
        {
            int i = 0;
            foreach (var c in cams)
            {
                var rt = c.targetTexture;
                if (rt == null) continue;
                P("HUD render texture picture: " + FrameCapture.SaveTexture(rt, cams.Count > 1 ? $"{name}_{i++}" : name, Shots));
            }
        }

        private struct VBox { public float X0, Y0, X1, Y1, Z0, Z1; public int N; }

        private static void AddPoint(ref VBox b, Camera cam, Vector3 w)
        {
            var v = cam.WorldToViewportPoint(w);
            if (b.N == 0) { b.X0 = b.X1 = v.x; b.Y0 = b.Y1 = v.y; b.Z0 = b.Z1 = v.z; }
            else
            {
                b.X0 = Math.Min(b.X0, v.x); b.X1 = Math.Max(b.X1, v.x);
                b.Y0 = Math.Min(b.Y0, v.y); b.Y1 = Math.Max(b.Y1, v.y);
                b.Z0 = Math.Min(b.Z0, v.z); b.Z1 = Math.Max(b.Z1, v.z);
            }
            b.N++;
        }

        private static void AddBounds(ref VBox b, Camera cam, Bounds bd)
        {
            var c = bd.m_Center;
            var e = bd.m_Extents;
            for (int i = 0; i < 8; i++)
                AddPoint(ref b, cam, U.V3(c.x + ((i & 1) != 0 ? e.x : -e.x), c.y + ((i & 2) != 0 ? e.y : -e.y), c.z + ((i & 4) != 0 ? e.z : -e.z)));
        }

        private static string BoxText(VBox b, Camera cam)
        {
            if (b.N == 0) return "nothing";
            int pw = cam.pixelWidth, ph = cam.pixelHeight;
            return $"viewport x {U.F(b.X0)}..{U.F(b.X1)}, y {U.F(b.Y0)}..{U.F(b.Y1)} (pixels x {b.X0 * pw:0}..{b.X1 * pw:0}, y {b.Y0 * ph:0}..{b.Y1 * ph:0} of {pw}x{ph}), depth {U.F(b.Z0)}..{U.F(b.Z1)}";
        }

        /// <summary>A box in the local space of <paramref name="root"/> (the model's own units) around the points.</summary>
        private static string LocalBox(Transform root, IEnumerable<Vector3> world)
        {
            float x0 = float.MaxValue, y0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue, z1 = float.MinValue;
            int n = 0;
            foreach (var w in world)
            {
                var p = root.InverseTransformPoint(w);
                x0 = Math.Min(x0, p.x); y0 = Math.Min(y0, p.y); z0 = Math.Min(z0, p.z);
                x1 = Math.Max(x1, p.x); y1 = Math.Max(y1, p.y); z1 = Math.Max(z1, p.z);
                n++;
            }
            return n == 0 ? "nothing" : $"x {U.F(x0)}..{U.F(x1)}, y {U.F(y0)}..{U.F(y1)}, z {U.F(z0)}..{U.F(z1)}";
        }

        private static IEnumerable<Vector3> Corners(Bounds bd)
        {
            var c = bd.m_Center;
            var e = bd.m_Extents;
            for (int i = 0; i < 8; i++)
                yield return U.V3(c.x + ((i & 1) != 0 ? e.x : -e.x), c.y + ((i & 2) != 0 ? e.y : -e.y), c.z + ((i & 4) != 0 ? e.z : -e.z));
        }

        private static string Q(Quaternion q) => $"({U.F(q.x)}, {U.F(q.y)}, {U.F(q.z)}, {U.F(q.w)})";

        /// <summary>The HUD copy, the cameras that draw it, the panel that shows their picture, and her against the base in each camera.</summary>
        private static void HudFraming(MainGameManager main, string pose)
        {
            var hud = main.UIOuji;
            if (hud == null) { P("HUD framing: MainGameManager.UIOuji is null"); return; }
            var hd = Dresser.FindFor(hud.gameObject);
            var root = hud.transform;
            P($"HUD copy ({pose}) {U.PathOf(root)}: local position {U.F(root.localPosition)}, local rotation {Q(root.localRotation)}, local scale {U.F(root.localScale)}, " +
              $"lossy scale {U.F(root.lossyScale)}, world position {U.F(root.position)}, layer {root.gameObject.layer}; dressed {hd != null}");
            var uic = UnityEngine.Object.FindObjectOfType<UIOujiController>();
            if (uic != null)
            {
                var t = uic.transform;
                P($"UIOujiController (3DModel) {U.PathOf(t)}: world position {U.F(t.position)}, rotation {Q(t.rotation)}, lossy scale {U.F(t.lossyScale)}; " +
                  $"OujiChaseCam {(uic.OujiChaseCam != null ? U.F(uic.OujiChaseCam.position) : "none")}");
            }
            int layer = HudLayer(hd, hud);
            foreach (var c in UnityEngine.Object.FindObjectsOfType<Camera>())
            {
                var rt = c.targetTexture;
                var r = c.rect;
                bool sees = (c.cullingMask & (1 << layer)) != 0;
                P($"camera {U.PathOf(c.transform)}: enabled {c.enabled && c.gameObject.activeInHierarchy}, draws the HUD layer {sees}, cullingMask 0x{c.cullingMask:X8}, " +
                  $"{(c.orthographic ? $"orthographic size {U.F(c.orthographicSize)}" : $"field of view {U.F(c.fieldOfView)}")}, near {U.F(c.nearClipPlane)}, depth {U.F(c.depth)}, " +
                  $"rect ({U.F(r.m_XMin)}, {U.F(r.m_YMin)}, {U.F(r.m_Width)}, {U.F(r.m_Height)}), pixels {c.pixelWidth}x{c.pixelHeight}, " +
                  $"render texture {(rt != null ? $"{rt.name} {rt.width}x{rt.height}" : "none (the screen)")}, " +
                  $"position {U.F(c.transform.position)}, in the HUD copy's space {U.F(root.InverseTransformPoint(c.transform.position))}");
                if (!sees || !c.enabled || !c.gameObject.activeInHierarchy) continue;
                if (hd != null)
                {
                    VBox her = default, herJ = default, baseJ = default, baseR = default;
                    var herBounds = new List<Vector3>();
                    foreach (var p in hd.Pieces)
                        if (p.Renderer.enabled && p.Go.activeInHierarchy) { var bd = p.Renderer.bounds; AddBounds(ref her, c, bd); herBounds.AddRange(Corners(bd)); }
                    foreach (var j in hd.HerJoints) if (j != null) AddPoint(ref herJ, c, j.position);
                    foreach (var j in hd.GameJoints) if (j != null) AddPoint(ref baseJ, c, j.position);
                    foreach (var gr in hd.Root.GetComponentsInChildren<Renderer>(true))
                        if (gr != null && !hd.Own.Contains(gr.Pointer) && !Presents.IsWorn(hd, gr) && gr.gameObject.activeInHierarchy) AddBounds(ref baseR, c, gr.bounds);
                    P($"  her renderers' bounds: {BoxText(her, c)}");
                    P($"  her joints: {BoxText(herJ, c)}");
                    P($"  the base's joints: {BoxText(baseJ, c)}");
                    P($"  the base's renderers' bounds (switched off; Unity may keep old bounds for them): {BoxText(baseR, c)}");
                    P($"  in the HUD copy's own space: her renderers {LocalBox(root, herBounds)}; her joints {LocalBox(root, hd.HerJoints.Where(j => j != null).Select(j => j.position))}; " +
                      $"the base's joints {LocalBox(root, hd.GameJoints.Where(j => j != null).Select(j => j.position))}");
                    var names = hd.Data.Joints;
                    var pts = new List<string>();
                    for (int i = 0; i < names.Length; i++)
                    {
                        if (hd.HerJoints[i] == null) continue;
                        var v = c.WorldToViewportPoint(hd.HerJoints[i].position);
                        string g = "";
                        if (hd.GameJoints[i] != null) { var gv = c.WorldToViewportPoint(hd.GameJoints[i].position); g = $" / base ({U.F(gv.x)}, {U.F(gv.y)})"; }
                        pts.Add($"{names[i].Name} ({U.F(v.x)}, {U.F(v.y)}){g}");
                    }
                    P("  joints in the viewport, hers / the base's: " + string.Join("; ", pts));
                }
                if (rt != null) Panels(rt);
            }
        }

        /// <summary>The RawImages that show <paramref name="rt"/>: where they sit on the screen, their canvas, and any mask above them.</summary>
        private static void Panels(RenderTexture rt)
        {
            int n = 0;
            foreach (var img in UnityEngine.Object.FindObjectsOfType<UnityEngine.UI.RawImage>())
            {
                var tex = img.texture;
                if (tex == null || tex.Pointer != rt.Pointer) continue;
                n++;
                var rtf = img.rectTransform;
                var corners = new Il2CppStructArray<Vector3>(4);
                rtf.GetWorldCorners(corners);
                var rr = rtf.rect;
                var masks = new List<string>();
                string canvas = "none";
                for (var t = img.transform; t != null; t = t.parent)
                {
                    if (t.GetComponent<UnityEngine.UI.Mask>() != null || t.GetComponent<UnityEngine.UI.RectMask2D>() != null) masks.Add(t.name);
                    var cv = t.GetComponent<Canvas>();
                    if (cv != null && canvas == "none") canvas = $"{cv.name} ({cv.renderMode}, scale factor {U.F(cv.scaleFactor)})";
                }
                var uv = img.uvRect;
                P($"  panel {U.PathOf(img.transform)}: active {img.gameObject.activeInHierarchy}, enabled {img.enabled}, rect {U.F(rr.width)}x{U.F(rr.height)}, " +
                  $"world corners {U.F(corners[0])} {U.F(corners[1])} {U.F(corners[2])} {U.F(corners[3])}, lossy scale {U.F(img.transform.lossyScale)}, " +
                  $"uvRect ({U.F(uv.m_XMin)}, {U.F(uv.m_YMin)}, {U.F(uv.m_Width)}, {U.F(uv.m_Height)}), canvas {canvas}, masks above {(masks.Count == 0 ? "none" : string.Join(", ", masks))}");
            }
            if (n == 0) P($"  no RawImage shows {rt.name}");
        }

        // ---------------------------------------------------------------- the last stage load: the HEAD present at 0

        /// <summary>The stage again with the worn HEAD present at 0 in memory, for the HUD picture without the hat; then put back.</summary>
        private static IEnumerable<object> HeadlessStage(MapEntry entry)
        {
            var save = GameRefs.Save;
            var w = save != null ? save._wearingPresent : null;
            if (w == null || w.Length < 3) { P("HEAD at 0: no worn present array; left out"); yield break; }
            int head = w[2];
            if (head <= 0) { P("HEAD at 0: this save wears no HEAD present; left out"); yield break; }
            OnUndo("the worn HEAD present set to 0", () => { var ww = GameRefs.Save?._wearingPresent; if (ww != null && ww.Length > 2) ww[2] = head; });
            w[2] = 0;
            P($"HEAD at 0: the worn HEAD present {head} set to 0 in memory; loading the stage again for the HUD picture");
            Msg("load stage (probe, HEAD at 0): " + SceneLoader.LoadStage(entry));
            ProbeAfterLoadStage("second");
            bool playing = false;
            foreach (var x in UntilPlaying(ok => playing = ok)) yield return x;
            if (!playing) { RunUndo("the worn HEAD present set to 0"); yield break; }
            var main = GameRefs.Main;
            if (main != null)
            {
                _zeroStage = false;
                _zeroForPrince = false;
                Safe("the stage's models", () => StageModels(main));
                Safe("the purple object", () => PurpleSearch(main));
                var cams = HudCameras(main);
                Shot("probe_hud_nohead_" + _style);
                yield return PictureTaken();
                SaveHud(cams, "probe_hudrt_nohead_" + _style);
            }
            RunUndo("the worn HEAD present set to 0");
            RunUndo("_nowCharacterID set to 0 for the stage");
            P("after the HEAD-at-0 stage: " + SaveFacts());
        }
    }
}
