using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppApp.KatamariSin;
using OUAKModAPI;
using UnityEngine;

namespace LongLostCousins.Dressing
{
    /// <summary>
    /// Where the local player's cousin model is made, and which models to dress:
    /// the story stage player, its HUD copy and the ending (from the hooks), the walking cousin in the Plaza and on the
    /// Prince's Ship (watched each LateUpdate), its UI copy (hook), and the model on the stage results (new cousin
    /// models queued by OujiPresent.Start and matched against the results' holder).
    /// </summary>
    internal static class Contexts
    {
        private static readonly ModLog Log = LongLostCousinsMod.Log.In("Contexts");

        /// <summary>The stage index where the game always plays the Prince (MainGameManager._stageIdx); the mod leaves it to him.</summary>
        public const int PrinceOnlyStage = 20;
        private static bool _warnedNoManager;

        // ---------------------------------------------------------------- stage, HUD, ending

        /// <summary>Why the stage contexts are skipped now, or null. Read from the managers: InitializeOuji has not set the model's own fields yet.</summary>
        private static string StageSkip()
        {
            if (GameRefs.IsMultiplay) return "an online battle";
            var gm = GameRefs.Main;
            if (gm == null) return "no stage manager";
            if (gm._stageIdx == PrinceOnlyStage) return "the stage where the game plays the Prince";
            return null;
        }

        /// <summary>MainGameOuji.InitializeOuji, before it: the single-player stage's player model.</summary>
        public static void StagePlayer(MainGameOuji ouji)
        {
            if (!Chosen.Now || ouji == null) return;
            string skip = StageSkip();
            if (skip != null)
            {
                // No manager at all would be a surprise (the stage's own setup makes this call): say so once, visibly.
                if (GameRefs.Main == null && !_warnedNoManager) { _warnedNoManager = true; Log.Warn("stage player not dressed: GlobalManager.GameMan was not set yet"); }
                else Log.Debug("stage player not dressed: " + skip);
                return;
            }
            var own = GameRefs.Main.Ouji;
            if (own == null || own.Pointer != ouji.Pointer) { Log.Debug("InitializeOuji on a model that is not the stage's player"); return; }
            Dresser.Dress(ouji.gameObject, "stage");
        }

        /// <summary>UIMainGameOuji.Initialize, before it: the stage HUD's copy of the player.</summary>
        public static void StageHud(UIMainGameOuji ui)
        {
            if (!Chosen.Now || ui == null) return;
            string skip = StageSkip();
            if (skip != null) { Log.Debug("stage HUD copy not dressed: " + skip); return; }
            Dresser.Dress(ui.gameObject, "stage HUD");
        }

        /// <summary>MainGameOuji.InitializeOujiByEndingCore, before it: the ending's player model.</summary>
        public static void Ending(MainGameOuji ouji)
        {
            if (!Chosen.Now || ouji == null || GameRefs.IsMultiplay) return;
            // Only the ending manager calls this, on its own player model; a different model there is left alone.
            var end = GameRefs.Global?.EndGameMan;
            var own = end != null ? end.Ouji : null;
            if (own != null && own.Pointer != ouji.Pointer) { Log.Debug("InitializeOujiByEndingCore on a model that is not the ending's player"); return; }
            Dresser.Dress(ouji.gameObject, "ending");
        }

        // ---------------------------------------------------------------- Plaza and ship

        /// <summary>UIOuji.Initialize, before it: the Plaza's and ship's UI copy of the walking cousin.</summary>
        public static void PlazaUi(UIOuji ui)
        {
            if (!Chosen.Now || ui == null) return;
            Dresser.Dress(ui.gameObject, "Plaza UI");
        }

        private static IntPtr _owner, _leaveAlone;
        private static int _ownerTries;
        private static float _ownerRetryAt;

        /// <summary>When the player turns a cousin on, the walking cousin already shown stays as it is (the change shows at the next load).</summary>
        public static void LeaveCurrentOwnerAlone()
        {
            _leaveAlone = OwnerAnimation()?.Pointer ?? IntPtr.Zero;
            _owner = _leaveAlone;
        }

        private static CharacterAnimation OwnerAnimation()
        {
            CharacterManager cm = null;
            try { cm = SingletonMonoBehaviour<CharacterManager>.instance; } catch { }
            var ca = AnimationOf(cm);
            return ca ?? ShipAnimation();
        }

        private static CharacterAnimation AnimationOf(CharacterManager cm)
        {
            if (cm == null) return null;
            var oc = cm._ownerCharacter;
            if (oc == null) return null;
            var ca = oc._characterAnimation;
            return ca != null ? ca : null;
        }

        // On the Prince's Ship the shared CharacterManager stays unset (measured 2026-11-20: 60 s): the ship's
        // PlayCharacterAdominist sets up the walking character, with its own CharacterManager or the player object it
        // made. Looked up again only when the scene changes, or twice a second while nothing is found.
        private static PlayCharacterAdominist _admin;
        private static int _adminScene = -1;
        private static float _adminNextFind;

        private static CharacterAnimation ShipAnimation()
        {
            int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            if (scene != _adminScene) { _adminScene = scene; _admin = null; _adminNextFind = 0f; }
            if (_admin == null)
            {
                if (Time.unscaledTime < _adminNextFind) return null;
                _adminNextFind = Time.unscaledTime + 0.5f;
                _admin = UnityEngine.Object.FindObjectOfType<PlayCharacterAdominist>();
                if (_admin == null) return null;
            }
            var ca = AnimationOf(_admin.m_CharacterManager);
            if (ca != null) return ca;
            var sel = _admin.m_Selectplayer;
            if (sel == null) return null;
            var c = sel.GetComponentInChildren<OujiCharacterController>(true);
            if (c == null) return null;
            ca = c._characterAnimation;
            return ca != null ? ca : null;
        }

        /// <summary>The walking cousin's model in the Plaza or on the ship, or null.</summary>
        public static GameObject PlazaOwnerModel() => OwnerAnimation()?.gameObject;

        /// <summary>Each LateUpdate: a new walking cousin (scene load, cousin change) gets dressed; cheap pointer reads otherwise.</summary>
        private static void WatchOwner(float now)
        {
            var ca = OwnerAnimation();
            IntPtr p = ca != null ? ca.Pointer : IntPtr.Zero;
            if (p != _owner)
            {
                _owner = p; _ownerTries = 0; _ownerRetryAt = now;
                if (p != _leaveAlone) _leaveAlone = IntPtr.Zero;   // that model is gone; its address may come back for a new one
            }
            if (p == IntPtr.Zero || p == _leaveAlone || _ownerTries < 0 || now < _ownerRetryAt) return;
            var go = ca.gameObject;
            if (Dresser.Find(go) != null) { _ownerTries = -1; return; }
            if (Dresser.Dress(go, "Plaza") != null) { _ownerTries = -1; return; }
            // A designed cousin builds its model frames later: try again for a while.
            _ownerRetryAt = now + 1f;
            if (++_ownerTries >= 15) _ownerTries = -1;
        }

        // ---------------------------------------------------------------- results (and copies of dressed models)

        private struct Queued { public GameObject Go; public IntPtr Ptr; public float At; }
        private static readonly List<Queued> Pending = new();
        private static float _nextSort;

        /// <summary>OujiPresent.Start, after it: a cousin model's first frame. Queued to be sorted in LateUpdate.</summary>
        public static void PresentStarted(OujiPresent p)
        {
            if (!Chosen.Now || p == null) return;
            var go = p.gameObject;
            if (Pending.Count >= 64) Pending.RemoveAt(0);
            Pending.Add(new Queued { Go = go, Ptr = go.Pointer, At = Time.unscaledTime });
        }

        /// <summary>The results' holders (ResultMonoSetter / ResultAAMonoSetter.m_PlayCharacter), looked up once per sorting round.</summary>
        private static List<IntPtr> ResultHolders()
        {
            var list = new List<IntPtr>();
            foreach (var s in UnityEngine.Object.FindObjectsOfType<ResultMonoSetter>())
                if (s != null && s.m_PlayCharacter != null) list.Add(s.m_PlayCharacter.Pointer);
            foreach (var s in UnityEngine.Object.FindObjectsOfType<ResultAAMonoSetter>())
                if (s != null && s.m_PlayCharacter != null) list.Add(s.m_PlayCharacter.Pointer);
            return list;
        }

        /// <summary>
        /// The object directly under a results holder that holds <paramref name="t"/> (the instance the results made), or
        /// null. Looks a few levels up, in case OujiPresent sits below the instance's root.
        /// </summary>
        private static Transform HeldBy(Transform t, List<IntPtr> holders)
        {
            for (int depth = 0; t != null && depth < 4; depth++, t = t.parent)
            {
                var p = t.parent;
                if (p != null && holders.Contains(p.Pointer)) return t;
            }
            return null;
        }

        private static void SortPending(float now)
        {
            if (Pending.Count == 0 || now < _nextSort) return;
            _nextSort = now + 0.25f;
            List<IntPtr> holders = null;
            for (int i = Pending.Count - 1; i >= 0; i--)
            {
                var q = Pending[i];
                if (q.Go == null || Dresser.IsDressed(q.Ptr)) { Pending.RemoveAt(i); continue; }
                // The results model first: it is often a copy of the dressed stage player, and is still the results model.
                holders ??= ResultHolders();
                var held = holders.Count > 0 ? HeldBy(q.Go.transform, holders) : null;
                if (held != null)
                {
                    DressResultModel(held.gameObject);
                    Pending.RemoveAt(i);
                    continue;
                }
                if (Dresser.CarriesMarker(q.Go))
                {
                    Dresser.Dress(q.Go, "copy");
                    Pending.RemoveAt(i);
                    continue;
                }
                if (now - q.At > 5f) Pending.RemoveAt(i);
            }
        }

        private static void DressResultModel(GameObject go)
        {
            if (GameRefs.Main != null && GameRefs.Main._stageIdx == PrinceOnlyStage)
            {
                Log.Debug("results model not dressed: the stage where the game plays the Prince");
                return;
            }
            Dresser.Dress(go, "results");
        }

        // ResultMonoSetter / ResultAAMonoSetter.SetPlayCharacter, after it: the results' holder now has (or soon gets) the
        // player's model. Watched for 10 s, because a designed cousin builds its model frames later.
        private struct Holder { public Transform T; public float Until; public string Who; }
        private static readonly List<Holder> Holders = new();
        private static float _nextHolderLook;

        /// <summary>How many times the results set their player character this session (for the test).</summary>
        public static int ResultSets { get; private set; }

        public static void ResultCharacterSet(Transform holder, string who)
        {
            if (!Chosen.Now) return;
            ResultSets++;
            if (holder == null) { Log.Info($"{who}.SetPlayCharacter ran with no holder"); return; }
            Log.Info($"{who}.SetPlayCharacter ran: {holder.childCount} object(s) under {holder.name}");
            Holders.Add(new Holder { T = holder, Until = Time.unscaledTime + 10f, Who = who });
            _nextHolderLook = 0f;
        }

        private static void LookAtHolders(float now)
        {
            if (Holders.Count == 0 || now < _nextHolderLook) return;
            _nextHolderLook = now + 0.25f;
            for (int i = Holders.Count - 1; i >= 0; i--)
            {
                var h = Holders[i];
                if (h.T == null || now > h.Until) { Holders.RemoveAt(i); continue; }
                int n = h.T.childCount;
                for (int c = 0; c < n; c++)
                {
                    var go = h.T.GetChild(c).gameObject;
                    if (go.GetComponentInChildren<Animator>(true) == null) continue;
                    if (Dresser.FindFor(go) != null) { Holders.RemoveAt(i); break; }
                    DressResultModel(go);
                    if (Dresser.FindFor(go) != null) { Holders.RemoveAt(i); break; }
                }
            }
        }

        public static void ClearPending() { Pending.Clear(); Holders.Clear(); }

        /// <summary>MelonMod.OnLateUpdate, before the dressed models are kept in step. Never throws.</summary>
        public static void LateTick()
        {
            if (!Chosen.On) return;
            float now = Time.unscaledTime;
            try { WatchOwner(now); }
            catch (Exception e) { Log.Debug("watching the walking cousin: " + e.Message); _ownerTries = -1; }
            try { SortPending(now); }
            catch (Exception e) { Log.Debug("sorting new cousin models: " + e.Message); Pending.Clear(); }
            try { LookAtHolders(now); }
            catch (Exception e) { Log.Debug("looking at the results' holder: " + e.Message); Holders.Clear(); }
        }
    }
}
