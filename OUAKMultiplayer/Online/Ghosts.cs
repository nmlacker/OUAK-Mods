using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using Il2CppApp.KatamariSin;
using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering;
using OUAKModAPI;

namespace OUAKMultiplayer
{
    /// <summary>
    /// How the other players appear in your game: purely visual "invincible-looking" katamaris, never a second
    /// working katamari, so it works in any stage (As Big As Possible, As Fast As Possible, matches...).
    /// - Ball: a translucent sphere at their katamari's size, turning with their ball.
    /// - Stuck objects: when they roll something up, the original is hidden here and a translucent copy of its
    ///   meshes is stuck to their ball where it sits on theirs.
    /// - Cousin: their own character, built by the game's multiplayer model loader from their look (Looks),
    ///   standing where their prince stands and mirroring their prince's animation.
    /// - Name tag above the ball.
    /// Pickups: a local roll-up (MainGameCore.GetTargetMono in stages, MultiGetMono in matches, own core only) is
    /// sent once the object is attached to the ball, with its pose in the ball's attach frame. Object keys are
    /// MainGameMonoBase.hierarchyID, mixed with the start position where two objects share one.
    /// Map keys: 1000 + eStage for single-player stages, 0 for anything else (multiplayer maps are never shared: the
    /// mod's sessions are for the single-player stages; Katamari Ball, Race and Target use the game's own networking).
    /// </summary>
    internal static class Ghosts
    {
        private const int MaxStuckPerPlayer = 250;

        private sealed class View
        {
            public GameObject Root;
            /// <summary>Turns with their CoreMesh rotation (the core model hangs here).</summary>
            public Transform Spin;
            /// <summary>Their MonoAttacher: follows the core, turns and scales like theirs; their rolled-up objects hang here.</summary>
            public Transform Attach;
            public GameObject Ball;
            public GameObject CoreModel;
            public int CoreMap;
            public CousinPuppet Cousin;
            public byte[] CousinLook;
            public readonly Queue<GameObject> Stuck = new();
            public Vector3 Shown, PrinceOffset;
            public Quaternion Rot = Quaternion.identity, AttachRot = Quaternion.identity;
            public float Radius, Yaw, CoreScale = 1f, AttachScale = 1f;
            /// <summary>Their prince's Animator as last shown on the cousin (PoseSync).</summary>
            public readonly PoseTrack Pose = new();
            public bool Placed;
            /// <summary>Drawn this frame (GhostSettings); NearHidden / FarHidden remember the last call for a little slack.</summary>
            public bool Visible = true, NearHidden, FarHidden;
            /// <summary>Gap from your katamari's surface to theirs, in your diameters.</summary>
            public float Gap;
        }

        private sealed class PendingPickup
        {
            public MainGameMonoBase Mono;
            public uint Key;
            public float At;
        }

        public static readonly Color[] Colors =
        {
            new(1f, 0.85f, 0.3f, 0.3f), new(0.35f, 0.65f, 1f, 0.3f), new(1f, 0.4f, 0.55f, 0.3f), new(0.45f, 1f, 0.5f, 0.3f),
            new(0.75f, 0.5f, 1f, 0.3f), new(1f, 0.6f, 0.25f, 0.3f), new(0.3f, 0.95f, 0.95f, 0.3f), new(0.95f, 0.95f, 0.95f, 0.3f),
        };

        /// <summary>The cousin portrait for a remote player, once their model has loaded.</summary>

        private static readonly Dictionary<byte, View> Views = new();
        private static Shader _shader;
        private static bool _shaderSearched, _ballsBroken, _tagsBroken, _cousinsBroken, _chatBroken;
        private static GUIStyle _label, _shadow;
        private static Camera _cam;

        private static int _indexMap = int.MinValue;
        private static bool _indexBuilt;
        private static readonly Dictionary<uint, MainGameMonoBase> ByKey = new();
        private static readonly Dictionary<IntPtr, uint> KeyByPtr = new();
        private static readonly HashSet<uint> Gone = new();
        private static readonly List<PendingPickup> Pending = new();

        public static int CurrentMapKey { get; private set; }
        public static int IndexedObjects => ByKey.Count;
        public static int ShownGhosts => Views.Values.Count(v => v.Root != null && v.Root.activeSelf);
        public static int PlacedGhosts => Views.Values.Count(v => v.Placed);
        public static int CousinsShown => Views.Values.Count(v => v.Cousin != null && v.Cousin.Ready && v.Cousin.Model.activeSelf);
        public static int StuckCopies => Views.Values.Sum(v => v.Stuck.Count);
        public static int SentPickups { get; private set; }
        public static int HiddenByOthers { get; private set; }
        public static bool PatchActive { get; private set; }

        public static void Init(HarmonyLib.Harmony harmony)
        {
            try
            {
                harmony.Patch(AccessTools.Method(typeof(MainGameCore), nameof(MainGameCore.GetTargetMono)),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(Ghosts), nameof(AfterGetTargetMono))));
                harmony.Patch(AccessTools.Method(typeof(MainGameCore), nameof(MainGameCore.MultiGetMono)),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(Ghosts), nameof(AfterMultiGetMono))));
                PatchActive = true;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[Online] Pickup sync patches failed: {e.Message}");
            }
            if (StageStartTrace.Enabled) StageStartTrace.Init(harmony);
            IntroSync.Init(harmony);
            RunStats.Init(harmony);
            StageGate.Init(harmony);
            PlazaFlow.Init(harmony);
            Lobby.Patch(harmony);
        }

        // ---------------------------------------------------------------- maps and local state

        public static int KeyFor(MapEntry e) => e.Kind switch
        {
            MapKind.Stage => 1000 + (int)e.Stage,
            MapKind.Multiplayer => 2000 + (int)e.Stage - 1,
            _ => 0,
        };

        /// <summary>
        /// The single-player stage you're on (0 elsewhere). Multiplayer maps (Katamari Ball, Race, Target, and Philosopher
        /// Hunt, which runs as a multiplayer stage) are 0: the mod's own sessions never share, sync or show ghosts there;
        /// those modes use the game's own networking.
        /// </summary>
        private static int ComputeMapKey()
        {
            var gm = GameRefs.Main;
            if (gm == null || !gm.PlayerInitiated || GameRefs.IsMultiplay) return 0;
            return 1000 + (int)gm.TargetStage;
        }

        /// <summary>The map you're on as a Levels-list entry (what the host sends), or null.</summary>
        public static MapEntry CurrentEntry()
        {
            int key = CurrentMapKey;
            if (key == 0) return null;
            return StageCatalog.Entries.FirstOrDefault(e => (e.Kind == MapKind.Stage || e.Kind == MapKind.Multiplayer) && KeyFor(e) == key);
        }

        private static float _converge, _clockValue = float.NaN, _clockMovedAt;
        private static readonly PoseReader StagePose = new();

        /// <summary>Your katamari and prince right now. False when you're not in a stage or match.</summary>
        public static bool TryLocalState(out KatamariState s)
        {
            s = new KatamariState { Map = CurrentMapKey, CoreRot = Quaternion.identity, PrinceScale = 1f, AnimSpeed = 1f,
                                    ClearIgt = -1f, ClearRta = -1f, ClearStage = -1f, HudRank = 255,
                                    StageRate = -1, Score = -1 };
            if (s.Map == 0) return false;
            try
            {
                var gm = GameRefs.Main;
                var core = gm?.GetCore();
                if (core == null) return false;
                var ct = core.transform;
                var ouji = gm.GetOuji();
                s.Size = gm.KatamariSize;
                // The game's own Katamari Ball packet for this katamari, plus the decoded values for local use.
                s.CoreInfo = CoreInfoCodec.Encode(core, ouji, s.Size);
                s.CorePos = ct.position;
                s.CoreRot = core.CoreMesh != null ? core.CoreMesh.rotation : ct.rotation;
                s.Radius = core.KatamariRadius;
                s.PrincePos = s.CorePos;
                // Katamari Ball's display values: the core's scale (ApplyCoreScale) and its MonoAttacher, where
                // rolled-up objects hang (GetTargetMono parents them to it), and how fast they sink in.
                s.CoreScale = core.KatamariScale > 0f ? core.KatamariScale : ct.lossyScale.x;
                s.AttachRot = s.CoreRot;
                s.AttachScale = 1f;
                var att = core.MonoAttachment;
                if (att != null)
                {
                    var at = att.transform;
                    s.AttachRot = at.rotation;
                    s.AttachScale = at.lossyScale.x;
                    float delta = att.ConvergeDelta, dt = Time.deltaTime;
                    if (delta > 0f && delta < 1f && dt > 1e-4f) _converge = Mathf.Clamp(-Mathf.Log(delta) / dt, 0f, 50f);
                }
                s.Converge = _converge;
                s.TimerRule = (sbyte)Mathf.Clamp(gm.CheckIsTimerRule(), -1, 1);
                s.TimeLeft = gm._nowTime;
                s.Elapsed = gm._playTime;
                s.Igt = RunClocks.Igt;
                s.ClearIgt = RunClocks.ClearIgt;
                s.ClearRta = RunClocks.ClearRta;
                s.ClearStage = RunClocks.ClearStage;
                s.HudRank = RunClocks.HudRankNow;
                s.StageRate = RunClocks.RateNow;
                s.ScoreKind = (byte)RunClocks.ScoreKindNow;
                s.Score = RunClocks.ScoreNow;
                // Paused, the King talking, the end demo...: the clock isn't moving, so others shouldn't run it on.
                float clock = s.TimerRule == 1 ? s.Elapsed : s.TimeLeft, nowT = Time.unscaledTime;
                if (clock != _clockValue) { _clockValue = clock; _clockMovedAt = nowT; }
                if (nowT - _clockMovedAt > 0.15f) s.Flags |= KatamariState.FlagClockStopped;
                if (ouji != null)
                {
                    var ot = ouji.transform;
                    s.PrincePos = ot.position;
                    s.PrinceYaw = ot.eulerAngles.y;
                    s.PrinceScale = ot.lossyScale.x;
                    s.AnimNo = ouji.NowAnime;
                    s.AnimSpeed = ouji.AnimeSpeed;
                    s.Pose = StagePose.Read(ouji._animator, ouji.oujiFace);
                }
                return true;
            }
            catch { return false; }
        }

        // ---------------------------------------------------------------- per frame

        public static void Tick()
        {
            if (StageStartTrace.Enabled) try { StageStartTrace.Tick(); } catch { }
            try { SceneSwitchGuard.Tick(); }
            catch (Exception e) { MelonLogger.Warning($"[Online] Scene change guard: {e.Message}"); }
            try { CurrentMapKey = ComputeMapKey(); }
            catch { CurrentMapKey = 0; }
            try { IntroSync.Tick(); }
            catch (Exception e) { MelonLogger.Warning($"[Online] Start sync: {e.Message}"); }
            try { RankIcons.Tick(); }
            catch (Exception e) { MelonLogger.Warning($"[Online] Place icons: {e.Message}"); }
            try { HubPresence.Tick(); }
            catch (Exception e) { MelonLogger.Warning($"[Online] Free roam: {e.Message}"); }
            try { RunClocks.Tick(); }
            catch (Exception e) { MelonLogger.Warning($"[Online] Run timers: {e.Message}"); }
            try { Teleport.Tick(); }
            catch (Exception e) { MelonLogger.Warning($"[Online] Teleport: {e.Message}"); }
            try { StageGate.Tick(); }
            catch (Exception e) { MelonLogger.Warning($"[Online] Stage pick: {e.Message}"); }
            try { PlazaFlow.Tick(); }
            catch (Exception e) { MelonLogger.Warning($"[Online] Plaza: {e.Message}"); }
            try { if (NetSession.Connected) PortraitStudio.Tick(); }
            catch (Exception e) { MelonLogger.Warning($"[Online] Portraits: {e.Message}"); }
            try { Lobby.Tick(); }
            catch (Exception e) { MelonLogger.Warning($"[Online] Lobby rules: {e.Message}"); }
            try { Chat.Tick(); }
            catch (Exception e) { MelonLogger.Warning($"[Online] Chat: {e.Message}"); }
            try { QuickChat.Tick(); }
            catch (Exception e) { MelonLogger.Warning($"[Online] Quick chat: {e.Message}"); }
            // Voice chat: the microphone while you're in a session, and everyone's sound (turns itself off if it fails).
            Voice.Tick();

            if (!NetSession.Connected)
            {
                if (Views.Count > 0) Clear();
                if (_indexMap != int.MinValue) ResetIndex(int.MinValue);
                return;
            }
            if (CurrentMapKey != _indexMap)
            {
                ResetIndex(CurrentMapKey);
                foreach (var v in Views.Values) ClearStuck(v);
                GhostLook.ForgetMaterials();
            }
            if (!_indexBuilt && CurrentMapKey != 0 && NetSession.PickupsShared) BuildIndex();
            FlushPending();
            UpdateViews();
        }

        private static readonly List<View> _ranked = new();

        /// <summary>
        /// Which ghosts to draw (GhostSettings), from where they were last frame: none when ghosts are off; not
        /// those within / beyond the near and far distances (with a little slack so they don't flicker at the
        /// edge), not one between the camera and your katamari, and at most the N nearest.
        /// </summary>
        private static void DecideVisibility()
        {
            Vector3 me = Vector3.zero;
            float myR = 0f;
            try
            {
                var core = GameRefs.Main?.GetCore();
                if (core != null) { me = core.transform.position; myR = core.KatamariRadius; }
            }
            catch { myR = 0f; }
            bool haveMe = myR > 0f;
            float diameter = Mathf.Max(1e-4f, myR * 2f);
            Camera cam = null;
            var camPos = Vector3.zero;
            if (GhostSettings.HideBlocking && haveMe)
            {
                try { cam = FindCamera(); if (cam != null) camPos = cam.transform.position; }
                catch { cam = null; }
            }
            _ranked.Clear();
            foreach (var v in Views.Values)
            {
                bool vis = GhostSettings.Shown;
                if (vis && v.Placed && haveMe)
                {
                    v.Gap = ((v.Shown - me).magnitude - myR - v.Radius) / diameter;
                    float near = GhostSettings.NearSteps[GhostSettings.Near];
                    if (near < 0f) v.NearHidden = false;
                    else v.NearHidden = v.NearHidden ? v.Gap < near + 0.5f : v.Gap < near;
                    float far = GhostSettings.FarSteps[GhostSettings.Far];
                    if (far <= 0f) v.FarHidden = false;
                    else v.FarHidden = v.FarHidden ? v.Gap > far * 0.95f : v.Gap > far;
                    bool blocking = cam != null && Blocks(camPos, me, myR, v.Shown, v.Radius);
                    vis = !v.NearHidden && !v.FarHidden && !blocking;
                    if (vis) _ranked.Add(v);
                }
                v.Visible = vis;
            }
            int max = GhostSettings.MaxSteps[GhostSettings.Max];
            if (max > 0 && _ranked.Count > max)
            {
                _ranked.Sort((a, b) => a.Gap.CompareTo(b.Gap));
                for (int i = max; i < _ranked.Count; i++) _ranked[i].Visible = false;
            }
        }

        /// <summary>The ghost sits between the camera and your katamari, or the camera is inside it.</summary>
        private static bool Blocks(Vector3 cam, Vector3 me, float myR, Vector3 ghost, float r)
        {
            if ((ghost - cam).sqrMagnitude < r * r * 1.44f) return true;
            var line = me - cam;
            float len = line.magnitude;
            if (len < 1e-5f) return false;
            var dir = line / len;
            float t = Vector3.Dot(ghost - cam, dir);
            if (t <= 0f || t >= len - myR) return false;
            return (ghost - (cam + dir * t)).magnitude < r + myR * 0.5f;
        }

        private static void HideParts(View v)
        {
            try { if (v.Root != null && v.Root.activeSelf) v.Root.SetActive(false); } catch { }
            HideCousin(v);
        }

        private static void HideCousin(View v)
        {
            try
            {
                if (v.Cousin?.Model != null && v.Cousin.Model.activeSelf)
                {
                    v.Cousin.Model.SetActive(false);
                    v.Pose.Reset();   // show their move again when it shows
                }
            }
            catch { }
        }

        /// <summary>Every ghost's katamari and cousin switched off at once (a scene change: SceneSwitchGuard).</summary>
        public static void HideAllNow()
        {
            foreach (var v in Views.Values) Hide(v);
        }

        private static void UpdateViews()
        {
            // A scene is changing: nothing of other players may be around while it sets itself up.
            if (SceneSwitchGuard.Holding)
            {
                foreach (var v in Views.Values) if (v.Placed || (v.Root != null && v.Root.activeSelf)) Hide(v);
                return;
            }
            float now = Time.unscaledTime, dt = Time.unscaledDeltaTime;
            float k = 1f - Mathf.Exp(-12f * dt);
            DecideVisibility();
            foreach (var p in NetSession.Peers.Values)
            {
                Views.TryGetValue(p.Id, out var v);
                if (!IsHere(p, now))
                {
                    if (v != null) Hide(v);
                    continue;
                }
                if (v == null) Views[p.Id] = v = new View();
                var s = p.State;

                // Their look: (re)build the cousin when it arrives or changes.
                if (p.Look != null && !_cousinsBroken && !Looks.SameLook(p.Look, v.CousinLook))
                {
                    v.Cousin?.Destroy();
                    v.CousinLook = p.Look;
                    v.Pose.Reset();
                    try
                    {
                        v.Cousin = Looks.Load(p.Look);
                        MelonLogger.Msg($"[Online] Loading {p.Name}'s cousin ({Looks.Describe(p.Look)}).");
                    }
                    catch (Exception e)
                    {
                        _cousinsBroken = true;
                        v.Cousin = null;
                        MelonLogger.Warning($"[Online] Can't build cousin models ({e.Message}); showing balls only.");
                    }
                }
                v.Cousin?.Poll();

                // What their game draws, the way Katamari Ball draws a remote core (MainGameCore.MultiSetPosition,
                // ApplyCoreScale, MonoAttacher.MonoFollow): core scale, CoreMesh spin, attacher pose, prince offset.
                float coreScale = s.CoreScale > 0f ? s.CoreScale : s.Radius;
                // In story stages the game's core mesh is much smaller than the katamari (KatamariScale 0.087 at a radius
                // of 0.156, measured): its rolled-up objects make up the rest. A ghost only has those it was sent (shared
                // pickups that line up), so drawn at its own scale it looked too small and floated at its centre's height
                // (the user's report, 2026-11-08). The core is drawn at least as big as the katamari really is.
                if (_coreRadiusPerUnit > 0f && s.Radius > 0f) coreScale = Mathf.Max(coreScale, s.Radius / _coreRadiusPerUnit);
                var attachRot = s.AttachScale > 0f ? s.AttachRot : s.CoreRot;
                float attachScale = s.AttachScale > 0f ? s.AttachScale : 1f;
                var princeOffset = s.PrincePos - s.CorePos;
                float snap = Mathf.Max(10f, s.Radius * 20f);
                if (!v.Placed || (v.Shown - s.CorePos).sqrMagnitude > snap * snap)
                {
                    v.Shown = s.CorePos;
                    v.PrinceOffset = princeOffset;
                    v.Radius = s.Radius;
                    v.CoreScale = coreScale;
                    v.Rot = s.CoreRot;
                    v.AttachRot = attachRot;
                    v.AttachScale = attachScale;
                    v.Yaw = s.PrinceYaw;
                    v.Placed = true;
                }
                else
                {
                    v.Shown = Vector3.Lerp(v.Shown, s.CorePos, k);
                    v.PrinceOffset = Vector3.Lerp(v.PrinceOffset, princeOffset, k);
                    v.Radius = Mathf.Lerp(v.Radius, s.Radius, k * 0.5f);
                    v.CoreScale = Mathf.Lerp(v.CoreScale, coreScale, k * 0.5f);
                    v.Rot = Quaternion.Slerp(v.Rot, s.CoreRot, k);
                    v.AttachRot = Quaternion.Slerp(v.AttachRot, attachRot, k);
                    v.AttachScale = Mathf.Lerp(v.AttachScale, attachScale, k * 0.5f);
                    v.Yaw = Mathf.LerpAngle(v.Yaw, s.PrinceYaw, k);
                }

                if (v.Root == null && !_ballsBroken) CreateKatamari(p.Id, v);
                SinkStuck(v, s.Converge);
                // Your ghost settings: not drawn at all (it keeps moving underneath, so it shows up in the right place).
                if (!v.Visible)
                {
                    HideParts(v);
                    continue;
                }
                if (v.Root != null)
                {
                    if (!v.Root.activeSelf) v.Root.SetActive(true);
                    EnsureCoreModel(v);
                    v.Root.transform.position = v.Shown;
                    v.Spin.rotation = v.Rot;
                    if (v.CoreModel != null) v.CoreModel.transform.localScale = Vector3.one * v.CoreScale;
                    if (v.Attach != null)
                    {
                        if (v.Attach.gameObject.activeSelf != GhostSettings.Stuck) v.Attach.gameObject.SetActive(GhostSettings.Stuck);
                        v.Attach.rotation = v.AttachRot;
                        v.Attach.localScale = Vector3.one * v.AttachScale;
                    }
                    if (v.Ball != null)
                    {
                        // The plain sphere only stands in when there's no katamari model to copy.
                        if (v.Ball.activeSelf == (v.CoreModel != null)) v.Ball.SetActive(v.CoreModel == null);
                        v.Ball.transform.localScale = Vector3.one * (v.Radius * 2f);
                    }
                }
                if (GhostSettings.Cousins) UpdateCousin(v, s, Time.unscaledTime - p.StateAt);
                else HideCousin(v);
            }

            List<byte> left = null;
            foreach (byte id in Views.Keys)
                if (!NetSession.Peers.ContainsKey(id)) (left ??= new List<byte>()).Add(id);
            if (left != null)
                foreach (byte id in left) { DestroyView(Views[id]); Views.Remove(id); }
        }

        /// <summary>
        /// Their rolled-up objects sink into the ball the way MonoAttacher.UpdateFunc sinks yours: each frame their
        /// local positions shrink by ConvergeDelta (sent as a rate per second, so frame rates don't matter).
        /// The game's UpdateFunc isn't run on ghosts: it also ages the stage's real objects (DecreaseMonoConvLife).
        /// </summary>
        private static void SinkStuck(View v, float rate)
        {
            if (rate <= 0f || v.Stuck.Count == 0) return;
            float f = Mathf.Exp(-rate * Time.deltaTime);
            if (f >= 1f) return;
            foreach (var go in v.Stuck)
            {
                try { if (go != null) go.transform.localPosition *= f; } catch { }
            }
        }

        private static void UpdateCousin(View v, KatamariState s, float age)
        {
            var c = v.Cousin;
            if (c == null || !c.Ready) return;
            try
            {
                var model = c.Model;
                if (!model.activeSelf) model.SetActive(true);
                var t = model.transform;
                // Katamari Ball's way: the prince is a MainGameOuji whose scale InitializeOuji sets once (the same for
                // every prince on a map; it doesn't grow with the ball), standing where CalculatePosition puts it
                // (behind the ball, by its radius). So the cousin's MainGameOuji gets the world scale of their prince
                // and stands where their prince stands, relative to the ball as drawn here.
                var facing = Quaternion.Euler(0f, v.Yaw, 0f);
                t.localScale = Vector3.one;
                t.rotation = facing;
                var ouji = c.OujiTransform != null ? c.OujiTransform : t;
                float princeScale = s.PrinceScale > 0f ? s.PrinceScale : LocalPrinceScale();
                if (princeScale > 0f) ouji.localScale = Vector3.one * (princeScale / Mathf.Max(1e-6f, t.lossyScale.x));
                var target = v.Shown + v.PrinceOffset;
                t.position = target - (ouji.position - t.position);

                // Their prince's Animator as it is (state, time, speed, face); Katamari Ball's move number when it can't be.
                PosePlayer.Apply(c, v.Pose, s.Pose, age, s.AnimNo, s.AnimSpeed);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[Online] Cousin update failed ({e.Message}); hiding cousins.");
                _cousinsBroken = true;
                c.Destroy();
                v.Cousin = null;
            }
        }

        /// <summary>Self-test: a ghost's katamari as drawn (the core model's reach at its drawn scale) and its real radius.</summary>
        public static bool DrawnSize(byte id, out float drawn, out float radius)
        {
            drawn = radius = 0f;
            if (!Views.TryGetValue(id, out var v) || !v.Placed) return false;
            radius = v.Radius;
            drawn = v.CoreModel != null ? v.CoreScale * _coreRadiusPerUnit : v.Radius;
            return true;
        }

        /// <summary>Self-test: the first remote cousin shown in this stage, or null.</summary>
        public static GameObject FirstCousinModel()
        {
            foreach (var v in Views.Values)
            {
                try { if (v.Cousin != null && v.Cousin.Ready && v.Cousin.Model.activeSelf) return v.Cousin.Model; }
                catch { }
            }
            return null;
        }

        private static bool IsHere(Peer p, float now) =>
            CurrentMapKey != 0 && p.HasState && p.MapKey == CurrentMapKey && p.Radius > 0f && now - p.StateAt < 3f;

        private static void Hide(View v)
        {
            try
            {
                if (v.Root != null && v.Root.activeSelf) v.Root.SetActive(false);
                if (v.Cousin?.Model != null && v.Cousin.Model.activeSelf) v.Cousin.Model.SetActive(false);
            }
            catch { }
            v.Placed = false;
        }

        public static void Clear()
        {
            foreach (var v in Views.Values) DestroyView(v);
            Views.Clear();
        }

        private static void DestroyView(View v)
        {
            ClearStuck(v);
            v.Cousin?.Destroy();
            v.Cousin = null;
            try { if (v.Root != null) UnityEngine.Object.Destroy(v.Root); } catch { }
            v.Root = null;
        }

        private static void ClearStuck(View v)
        {
            while (v.Stuck.Count > 0)
            {
                var go = v.Stuck.Dequeue();
                try { if (go != null) UnityEngine.Object.Destroy(go); } catch { }
            }
        }

        private sealed class CorePart
        {
            public Mesh Mesh;
            public Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Material> Materials;
            public Vector3 Pos, Scale;
            public Quaternion Rot;
            public string Name;
        }

        private static List<CorePart> _coreTemplate;
        private static int _coreTemplateMap = int.MinValue;
        /// <summary>How far the core model reaches from its centre per unit of core scale (its meshes' bounds), 0 = unknown.</summary>
        private static float _coreRadiusPerUnit;
        private static bool _coreLogged;

        /// <summary>
        /// The meshes of your own katamari's core model, MainGameCore.CoreMesh (not stuck objects, not the
        /// prince), stored in the CoreMesh frame per unit of core scale. The game scales the whole core by
        /// KatamariScale (MainGameCore.ApplyCoreScale), so a remote katamari is drawn with the real model at the
        /// scale its owner sends, spun by its CoreMesh rotation. Taken once per map.
        /// </summary>
        private static List<CorePart> CoreTemplate()
        {
            if (_coreTemplateMap == CurrentMapKey) return _coreTemplate;
            var core = GameRefs.Main?.GetCore();
            if (core == null) return null;
            float unit = core.KatamariScale > 0f ? core.KatamariScale : core.transform.lossyScale.x;
            if (unit <= 0f) return null;
            _coreTemplateMap = CurrentMapKey;
            _coreTemplate = new List<CorePart>();
            try
            {
                var ct = core.transform;
                var mesh0 = core.CoreMesh;
                var inv = Quaternion.Inverse(mesh0 != null ? mesh0.rotation : ct.rotation);
                var source = mesh0 != null ? mesh0.GetComponentsInChildren<Renderer>() : core.GetComponentsInChildren<Renderer>();
                foreach (var r in source)
                {
                    if (_coreTemplate.Count >= 8) break;
                    if (!r.enabled || r.GetComponentInParent<MainGameMonoBase>() != null || r.GetComponentInParent<MainGameOuji>() != null) continue;
                    Mesh mesh = null;
                    var smr = r.TryCast<SkinnedMeshRenderer>();
                    if (smr != null)
                    {
                        if (smr.sharedMesh == null) continue;
                        mesh = new Mesh();
                        smr.BakeMesh(mesh);
                    }
                    else if (r.TryCast<MeshRenderer>() != null)
                    {
                        var mf = r.GetComponent<MeshFilter>();
                        if (mf != null) mesh = mf.sharedMesh;
                    }
                    if (mesh == null) continue;
                    var t = r.transform;
                    _coreTemplate.Add(new CorePart
                    {
                        Mesh = mesh,
                        Materials = GhostLook.TranslucentCopies(r.sharedMaterials),
                        Pos = inv * (t.position - ct.position) / unit,
                        Rot = inv * t.rotation,
                        Scale = t.lossyScale / unit,
                        Name = r.gameObject.name,
                    });
                }
                _coreRadiusPerUnit = 0f;
                foreach (var part in _coreTemplate)
                {
                    var b = part.Mesh.bounds;
                    var c = part.Pos + part.Rot * Vector3.Scale(b.center, part.Scale);
                    float reach = c.magnitude + Vector3.Scale(b.extents, part.Scale).magnitude * 0.8f;
                    _coreRadiusPerUnit = Mathf.Max(_coreRadiusPerUnit, reach);
                }
                if (!_coreLogged)
                {
                    _coreLogged = true;
                    MelonLogger.Msg($"[Online] Katamari model reach: {_coreRadiusPerUnit:0.###} per unit of core scale (your core: scale {unit:0.###}, " +
                                    $"model radius {_coreRadiusPerUnit * unit:0.###}, katamari radius {core.KatamariRadius:0.###}).");
                    MelonLogger.Msg(_coreTemplate.Count > 0
                        ? $"[Online] Katamari model: {_coreTemplate.Count} part(s) from your core ({string.Join(", ", _coreTemplate.Select(c => c.Name))})."
                        : "[Online] Katamari model: no core meshes found, using a plain ball. Core: " + DescribeHierarchy(ct, 2));
                }
            }
            catch (Exception e)
            {
                _coreTemplate.Clear();
                MelonLogger.Warning($"[Online] Couldn't copy the katamari model: {e.Message}");
            }
            return _coreTemplate;
        }

        private static void EnsureCoreModel(View v)
        {
            if (v.CoreModel != null && v.CoreMap == CurrentMapKey) return;
            if (v.CoreModel != null) { UnityEngine.Object.Destroy(v.CoreModel); v.CoreModel = null; }
            var parts = CoreTemplate();
            v.CoreMap = CurrentMapKey;
            if (parts == null || parts.Count == 0) return;
            var root = new GameObject("core");
            root.transform.SetParent(v.Spin, false);
            foreach (var part in parts)
            {
                var go = new GameObject(part.Name);
                var t = go.transform;
                t.SetParent(root.transform, false);
                t.localPosition = part.Pos;
                t.localRotation = part.Rot;
                t.localScale = part.Scale;
                go.AddComponent<MeshFilter>().sharedMesh = part.Mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterials = part.Materials;
                mr.shadowCastingMode = ShadowCastingMode.Off;
            }
            v.CoreModel = root;
        }

        private static void CreateKatamari(byte id, View v)
        {
            var root = new GameObject($"OUAK online katamari {id}");
            UnityEngine.Object.DontDestroyOnLoad(root);
            v.Root = root;
            v.Spin = new GameObject("spin").transform;
            v.Spin.SetParent(root.transform, false);
            v.Attach = new GameObject("attach").transform;
            v.Attach.SetParent(root.transform, false);
            v.Ball = CreateBall(id);
            if (v.Ball != null) v.Ball.transform.SetParent(root.transform, false);
        }

        private static GameObject CreateBall(byte id)
        {
            try
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = "ball";
                var col = go.GetComponent<Collider>();
                if (col != null) UnityEngine.Object.Destroy(col);

                var r = go.GetComponent<Renderer>();
                var shader = GhostShader();
                if (shader != null) r.material = new Material(shader);
                var mat = r.material;
                var color = Colors[id % Colors.Length];
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
                if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
                return go;
            }
            catch (Exception e)
            {
                _ballsBroken = true;
                MelonLogger.Warning($"[Online] Couldn't create ghost balls ({e.Message}); showing name tags only.");
                return null;
            }
        }

        /// <summary>Self-test: makes one ghost ball and destroys it again, reporting what it was drawn with.</summary>
        public static string ProbeVisuals()
        {
            var go = CreateBall(0);
            if (go == null) return "ghost balls unavailable (name tags only)";
            string shader = "?";
            try { shader = go.GetComponent<Renderer>().sharedMaterial.shader.name; } catch { }
            UnityEngine.Object.Destroy(go);
            return $"ghost ball OK (shader {shader})";
        }

        /// <summary>A transparent shader that renders under URP; legacy ones without a LightMode tag still draw there.</summary>
        private static Shader GhostShader()
        {
            if (_shaderSearched) return _shader;
            _shaderSearched = true;
            foreach (var name in new[] { "Sprites/Default", "UI/Default", "Universal Render Pipeline/Unlit", "Unlit/Color" })
            {
                try
                {
                    var s = Shader.Find(name);
                    if (s == null) continue;
                    _shader = s;
                    MelonLogger.Msg($"[Online] Ghost shader: {name}");
                    break;
                }
                catch { }
            }
            return _shader;
        }

        // ---------------------------------------------------------------- name tags

        /// <summary>
        /// Called from Core.OnGUI before the menu. Name tags over remote katamaris, and a Katamari Ball-style
        /// player list (icon, name, size, time). Every value is what that player's own game reported.
        /// Turns itself off if it ever fails.
        /// </summary>
        public static void OnGUI()
        {
            // The chat box takes key events, so it sees every OnGUI call; everything else draws on Repaint.
            if (!_chatBroken)
            {
                try { Chat.OnGUI(); }
                catch (Exception e)
                {
                    _chatBroken = true;
                    MelonLogger.Warning($"[Online] Chat turned off: {e.Message}");
                }
            }
            if (_tagsBroken || !NetSession.Connected || Event.current.type != EventType.Repaint) return;
            try
            {
                EnsureStyles();
                DrawTags();
                HubPresence.OnGUI();
                Chat.DrawBubbles();
                float scale = Votes.GuiScale();
                var old = GUI.matrix;
                GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
                try
                {
                    float barTop = PlayerBar.Draw();
                    HudWindows.BarTopPx = barTop * scale;   // the vote popup (Dear ImGui) sits above it
                    StageGate.OnGUI(barTop);
                    PlazaFlow.OnGUI();
                    QuickChat.OnGUI(Screen.height / scale);
                }
                finally { GUI.matrix = old; }
                SyncRun.OnGUI();
            }
            catch (Exception e)
            {
                _tagsBroken = true;
                MelonLogger.Warning($"[Online] Player list and name tags turned off: {e.Message}");
            }
        }

        private static void DrawTags()
        {
            if (CurrentMapKey == 0 || !GhostSettings.Shown) return;
            bool tags = GhostSettings.Tags, bubbles = GhostSettings.Bubbles;
            if (!tags && !bubbles) return;
            var cam = FindCamera();
            if (cam == null) return;
            float now = Time.unscaledTime;
            foreach (var p in NetSession.Peers.Values)
            {
                if (!IsHere(p, now)) continue;
                Views.TryGetValue(p.Id, out var v);
                if (v != null && !v.Visible) continue;
                Vector3 at = v != null && v.Placed ? v.Shown : p.Position;
                float radius = v != null && v.Placed ? v.Radius : p.Radius;
                // Just above their ball, in its own size: stages are tiny (a 10 cm katamari is ~0.07 units across).
                var sp = cam.WorldToScreenPoint(at + Vector3.up * (radius * 1.6f));
                if (sp.z <= 0f) continue;
                float y = Screen.height - sp.y;
                if (tags)
                {
                    Shadowed(new Rect(sp.x - 120f, y - 40f, 240f, 22f), p.Name, _label, _shadow);
                    Voice.TagMark(p.Id, sp.x, y - 40f, 22f, p.Name, _label);
                    string size = KatamariState.SizeText(p.State.Size);
                    if (size.Length > 0) Shadowed(new Rect(sp.x - 120f, y - 20f, 240f, 18f), size, _small, _smallShadow);
                }
                if (bubbles) Chat.QueueBubble(p.Id, sp.x, tags ? y - 42f : y - 4f, sp.z);
            }
        }

        private static void Shadowed(Rect r, string text, GUIStyle style, GUIStyle shadow)
        {
            GUI.Label(new Rect(r.x + 1.5f, r.y + 1.5f, r.width, r.height), text, shadow);
            GUI.Label(r, text, style);
        }

        private static Camera FindCamera()
        {
            if (_cam != null && _cam.isActiveAndEnabled) return _cam;
            _cam = null;
            try
            {
                var main = Camera.main;
                if (main != null && main.isActiveAndEnabled) return _cam = main;
                foreach (var c in Camera.allCameras)
                    if (c != null && c.isActiveAndEnabled && !c.orthographic && (_cam == null || c.depth > _cam.depth)) _cam = c;
            }
            catch { }
            return _cam;
        }

        private static GUIStyle _small, _smallShadow;

        private static void EnsureStyles()
        {
            if (_label != null && _small != null) return;
            _label = Style(16, Color.white, TextAnchor.MiddleCenter, FontStyle.Bold);
            _shadow = Style(16, new Color(0f, 0f, 0f, 0.85f), TextAnchor.MiddleCenter, FontStyle.Bold);
            _small = Style(13, new Color(1f, 0.92f, 0.55f), TextAnchor.MiddleCenter, FontStyle.Bold);
            _smallShadow = Style(13, new Color(0f, 0f, 0f, 0.85f), TextAnchor.MiddleCenter, FontStyle.Bold);
        }

        private static GUIStyle Style(int size, Color color, TextAnchor align, FontStyle font)
        {
            var s = new GUIStyle();
            s.fontSize = size;
            s.fontStyle = font;
            s.alignment = align;
            s.normal.textColor = color;
            s.clipping = TextClipping.Clip;
            return s;
        }

        private static Texture2D Tex(Color c)
        {
            var t = new Texture2D(1, 1);
            t.hideFlags = HideFlags.HideAndDontSave;
            t.SetPixels(new[] { c });
            t.Apply();
            return t;
        }

        // ---------------------------------------------------------------- shared objects

        /// <summary>Objects hidden here because someone else rolled them up (put back if sharing goes off).</summary>
        private static readonly List<MainGameMonoBase> HiddenMonos = new();
        public static int HiddenNow => HiddenMonos.Count;
        private static bool _rejectLogged, _holderLogged;

        private static void ResetIndex(int map)
        {
            ByKey.Clear();
            KeyByPtr.Clear();
            Gone.Clear();
            Pending.Clear();
            HiddenMonos.Clear();
            HiddenByOthers = 0;
            _indexMap = map;
            _indexBuilt = false;
        }

        /// <summary>
        /// Sharing went off (or the session ended): every object hidden because someone else rolled it up comes
        /// back where it was, and the copies on their katamaris go. Returns how many came back.
        /// </summary>
        public static int RestoreHidden()
        {
            int n = 0;
            foreach (var mono in HiddenMonos)
            {
                try
                {
                    if (mono == null) continue;
                    var go = mono.gameObject;
                    if (go != null && !go.activeSelf) { go.SetActive(true); n++; }
                }
                catch { }
            }
            HiddenMonos.Clear();
            Gone.Clear();
            Pending.Clear();
            HiddenByOthers = 0;
            foreach (var v in Views.Values) ClearStuck(v);
            if (n > 0) MelonLogger.Msg($"[Online] Put back {n} object(s) other players had rolled up.");
            return n;
        }

        /// <summary>The game's minimum katamari size to roll this object up (MainGameManager.GetMonoRequireSize, tenths of a mm like KatamariSize), or -1.</summary>
        public static int RequireSize(MainGameMonoBase mono)
        {
            try
            {
                var gm = GameRefs.Main;
                return gm != null && mono != null ? gm.GetMonoRequireSize(mono.MonoID) : -1;
            }
            catch { return -1; }
        }

        /// <summary>Other objects hang under this one (things on a table, a building's parts): hiding it would hide them too.</summary>
        private static bool HoldsOtherObjects(MainGameMonoBase mono)
        {
            try { return mono.GetComponentsInChildren<MainGameMonoBase>(true).Length > 1; }
            catch { return true; }
        }

        private static bool OnMyKatamari(MainGameMonoBase mono)
        {
            try
            {
                var core = GameRefs.Main?.GetCore();
                if (core == null) return false;
                var t = mono.transform;
                var att = core.MonoAttachment;
                return t.IsChildOf(core.transform) || (att != null && t.IsChildOf(att.transform));
            }
            catch { return false; }
        }

        private static float LocalPrinceScale()
        {
            try
            {
                var ouji = GameRefs.Main?.GetOuji();
                return ouji != null ? ouji.transform.lossyScale.x : 0f;
            }
            catch { return 0f; }
        }

        private static void BuildIndex()
        {
            _indexBuilt = true;
            float t0 = Time.realtimeSinceStartup;
            try
            {
                var all = UnityEngine.Object.FindObjectsOfType<MainGameMonoBase>();
                var groups = new Dictionary<uint, List<MainGameMonoBase>>();
                for (int i = 0; i < all.Length; i++)
                {
                    var mono = all[i];
                    if (mono == null) continue;
                    uint id = mono.hierarchyID;
                    if (id == 0) id = Fnv(mono.name);
                    if (!groups.TryGetValue(id, out var list)) groups[id] = list = new List<MainGameMonoBase>(1);
                    list.Add(mono);
                }
                foreach (var g in groups)
                {
                    foreach (var mono in g.Value)
                    {
                        // Objects sharing an id are told apart by where the stage placed them (MainGameMonoBase._startPos),
                        // which is the same on every PC even after they've moved.
                        uint key = g.Value.Count == 1 ? g.Key : Mix(g.Key, PositionHash(StartPos(mono)));
                        if (key == 0) key = 1;
                        if (ByKey.ContainsKey(key)) continue;
                        ByKey[key] = mono;
                        KeyByPtr[mono.Pointer] = key;
                    }
                }
                MelonLogger.Msg($"[Online] Indexed {ByKey.Count} objects ({groups.Count} ids) in {(Time.realtimeSinceStartup - t0) * 1000f:0} ms.");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[Online] Object index failed: {e.Message}");
            }
        }

        private static void AfterGetTargetMono(MainGameCore __instance, MainGameMonoBase __0) => OnLocalPickup(__instance, __0);

        private static void AfterMultiGetMono(MainGameCore __instance, MainGameMonoBase __0, bool __result)
        {
            if (__result) OnLocalPickup(__instance, __0);
        }

        private static void OnLocalPickup(MainGameCore core, MainGameMonoBase mono)
        {
            try
            {
                if (!NetSession.Connected || CurrentMapKey == 0 || mono == null || core == null) return;
                var mine = GameRefs.Main?.GetCore();
                if (mine == null || core.Pointer != mine.Pointer) return;
                RunStats.OnLocalPickup();
                if (!NetSession.PickupsShared) return;
                if (!_indexBuilt) BuildIndex();
                if (!KeyByPtr.TryGetValue(mono.Pointer, out uint key) || !Gone.Add(key)) return;
                // Katamari Ball sends a pickup the moment it's caught (NetworkMonoGetInfo.AddDragInMono): the
                // object's local pose in the katamari's MonoAttacher, which GetTargetMono has just parented it to.
                var att = mine.MonoAttachment;
                var t = mono.transform;
                if (att != null && t.parent == att.transform)
                {
                    NetSession.SendPickup(CurrentMapKey, key, t.localPosition, t.localRotation);
                    SentPickups++;
                    return;
                }
                Pending.Add(new PendingPickup { Mono = mono, Key = key, At = Time.unscaledTime });
            }
            catch (Exception e) { MelonLogger.Warning($"[Online] Pickup sync: {e.Message}"); }
        }

        /// <summary>
        /// Pickups that weren't on the attacher yet (match pickups go through MultiGetMono): sent once the object
        /// hangs on the ball (or after 1 s anyway), with its pose in the attacher's frame, as above.
        /// </summary>
        private static void FlushPending()
        {
            if (Pending.Count == 0) return;
            try
            {
                var core = GameRefs.Main?.GetCore();
                if (core == null) { Pending.Clear(); return; }
                var ct = core.transform;
                var at = core.MonoAttachment != null ? core.MonoAttachment.transform : core.CoreMesh != null ? core.CoreMesh : ct;
                float now = Time.unscaledTime;
                for (int i = Pending.Count - 1; i >= 0; i--)
                {
                    var p = Pending[i];
                    Transform t = null;
                    try { if (p.Mono != null) t = p.Mono.transform; } catch { }
                    if (t == null) { Pending.RemoveAt(i); continue; }
                    var parent = t.parent;
                    bool attached = parent != null && (parent.IsChildOf(ct) || parent.IsChildOf(at));
                    if (!attached && now - p.At < 1f) continue;
                    NetSession.SendPickup(CurrentMapKey, p.Key, at.InverseTransformPoint(t.position), Quaternion.Inverse(at.rotation) * t.rotation);
                    SentPickups++;
                    Pending.RemoveAt(i);
                }
            }
            catch (Exception e)
            {
                Pending.Clear();
                MelonLogger.Warning($"[Online] Sending pickups failed: {e.Message}");
            }
        }

        /// <summary>
        /// Someone else rolled this object up (Katamari Ball's GetTargetMonoMulti): stick a copy of it to their
        /// katamari's attacher at the pose they sent, and hide the original here. Not when: sharing is off, it's
        /// on our own katamari, it holds other objects, or it's too big for the size they're at (the game's
        /// GetMonoRequireSize) - a mix-up of objects between PCs must never take part of the stage away.
        /// </summary>
        public static void ApplyRemotePickup(byte player, int map, uint key, Vector3 localPos, Quaternion localRot)
        {
            if (!NetSession.PickupsShared || map == 0 || map != CurrentMapKey) return;
            if (!_indexBuilt) BuildIndex();
            if (Gone.Contains(key) || !ByKey.TryGetValue(key, out var mono)) return;
            try
            {
                if (mono == null) return;
                var go = mono.gameObject;
                if (go == null || !go.activeSelf) return;
                if (OnMyKatamari(mono)) { Gone.Add(key); return; }
                int need = RequireSize(mono), pickerSize = PickerSize(player);
                if (need < 0 || (pickerSize > 0 && need > pickerSize * 1.1f + 50))
                {
                    if (!_rejectLogged) { _rejectLogged = true; MelonLogger.Warning($"[Online] Ignored a pickup of {mono.name} (needs {KatamariState.SizeText(need)}, the player is {KatamariState.SizeText(pickerSize)}): objects don't line up between your games."); }
                    return;
                }
                if (HoldsOtherObjects(mono))
                {
                    if (!_holderLogged) { _holderLogged = true; MelonLogger.Msg($"[Online] Not hiding {mono.name}: other objects hang under it."); }
                    return;
                }
                Gone.Add(key);
                StickCopy(player, mono, localPos, localRot);
                go.SetActive(false);
                HiddenMonos.Add(mono);
                HiddenByOthers++;
            }
            catch (Exception e) { MelonLogger.Warning($"[Online] Hiding an object failed: {e.Message}"); }
        }

        private static int PickerSize(byte player) =>
            NetSession.Peers.TryGetValue(player, out var p) && p.HasState ? p.State.Size : 0;

        private static void StickCopy(byte player, MainGameMonoBase mono, Vector3 localPos, Quaternion localRot)
        {
            if (!Views.TryGetValue(player, out var v) || v.Attach == null) return;
            var copy = VisualCopy(mono);
            if (copy == null) return;
            var t = copy.transform;
            // GetTargetMonoMulti: SetParent(MonoAttacher), then the sender's localPosition / localRotation. The
            // object keeps its own size: the attacher's scale is divided back out.
            var size = mono.transform.lossyScale;
            float a = Mathf.Max(1e-6f, v.AttachScale);
            t.SetParent(v.Attach, false);
            t.localPosition = localPos;
            t.localRotation = localRot;
            t.localScale = size / a;
            v.Stuck.Enqueue(copy);
            while (v.Stuck.Count > MaxStuckPerPlayer)
            {
                var old = v.Stuck.Dequeue();
                try { if (old != null) UnityEngine.Object.Destroy(old); } catch { }
            }
        }

        /// <summary>A script-free, collider-free copy of an object's meshes (skinned ones baked), translucent.</summary>
        private static GameObject VisualCopy(MainGameMonoBase mono)
        {
            var src = mono.transform;
            var root = new GameObject("stuck");
            root.transform.localScale = src.lossyScale;
            int parts = 0;
            try
            {
                foreach (var mr in mono.GetComponentsInChildren<MeshRenderer>())
                {
                    if (!mr.enabled) continue;
                    var mf = mr.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) continue;
                    AddPart(root, src, mr.transform, mf.sharedMesh, mr.sharedMaterials);
                    parts++;
                }
                foreach (var smr in mono.GetComponentsInChildren<SkinnedMeshRenderer>())
                {
                    if (!smr.enabled || smr.sharedMesh == null) continue;
                    var mesh = new Mesh();
                    smr.BakeMesh(mesh);
                    AddPart(root, src, smr.transform, mesh, smr.sharedMaterials);
                    parts++;
                }
            }
            catch (Exception e) { MelonLogger.Warning($"[Online] Copying an object failed: {e.Message}"); }
            if (parts > 0) return root;
            UnityEngine.Object.Destroy(root);
            return null;
        }

        private static void AddPart(GameObject root, Transform src, Transform part, Mesh mesh, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Material> mats)
        {
            var go = new GameObject("part");
            var t = go.transform;
            t.SetParent(root.transform, false);
            t.localPosition = src.InverseTransformPoint(part.position);
            t.localRotation = Quaternion.Inverse(src.rotation) * part.rotation;
            Vector3 ps = part.lossyScale, ss = src.lossyScale;
            t.localScale = new Vector3(ps.x / NonZero(ss.x), ps.y / NonZero(ss.y), ps.z / NonZero(ss.z));
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterials = GhostLook.TranslucentCopies(mats);
            r.shadowCastingMode = ShadowCastingMode.Off;
        }

        private static float NonZero(float f) => Mathf.Abs(f) < 1e-5f ? 1f : f;

        /// <summary>
        /// Loopback bot: the closest still-present object within <paramref name="range"/> that a katamari of
        /// <paramref name="size"/> mm could roll up (the game's GetMonoRequireSize), or 0.
        /// </summary>
        public static uint FindObjectNear(Vector3 pos, float range, int size)
        {
            if (!_indexBuilt) return 0;
            uint best = 0;
            float bestSq = range * range;
            foreach (var kv in ByKey)
            {
                if (Gone.Contains(kv.Key)) continue;
                var mono = kv.Value;
                try
                {
                    if (mono == null) continue;
                    float sq = (mono.transform.position - pos).sqrMagnitude;
                    if (sq >= bestSq || !mono.gameObject.activeInHierarchy) continue;
                    int need = RequireSize(mono);
                    if (need < 0 || need > size || OnMyKatamari(mono) || HoldsOtherObjects(mono)) continue;
                    best = kv.Key;
                    bestSq = sq;
                }
                catch { }
            }
            return best;
        }

        /// <summary>Where the stage first placed this object (MainGameMonoBase._startPos), else where it is now.</summary>
        private static Vector3 StartPos(MainGameMonoBase mono)
        {
            try
            {
                var p = mono._startPos;
                if (p.sqrMagnitude > 1e-8f && float.IsFinite(p.x) && float.IsFinite(p.y) && float.IsFinite(p.z)) return p;
            }
            catch { }
            return mono.transform.position;
        }

        public static bool TryGetObject(uint key, out MainGameMonoBase mono) => ByKey.TryGetValue(key, out mono) && mono != null;

        /// <summary>Self-test: the first ghost's gap to you, in your diameters.</summary>
        public static float FirstGhostGap()
        {
            foreach (var v in Views.Values) if (v.Placed) return v.Gap;
            return -1f;
        }

        /// <summary>Self-test: where the first shown ghost katamari is drawn, and its radius.</summary>
        public static bool TryFirstGhost(out Vector3 center, out float radius)
        {
            foreach (var v in Views.Values)
                if (v.Root != null && v.Root.activeSelf && v.Placed)
                {
                    center = v.Shown;
                    radius = Mathf.Max(0.001f, v.Radius);
                    return true;
                }
            center = Vector3.zero;
            radius = 0f;
            return false;
        }

        public static List<MainGameMonoBase> HiddenSnapshot() => new(HiddenMonos);

        /// <summary>World height of everything a model draws (for comparing a cousin with your prince).</summary>
        private static float DrawnHeight(GameObject go)
        {
            try
            {
                bool any = false;
                var b = new Bounds();
                foreach (var r in go.GetComponentsInChildren<Renderer>())
                {
                    if (!r.enabled) continue;
                    if (!any) { b = r.bounds; any = true; }
                    else b.Encapsulate(r.bounds);
                }
                return any ? b.size.y : 0f;
            }
            catch { return 0f; }
        }

        /// <summary>For the self-test: what the ghosts look like next to your own katamari and prince.</summary>
        public static string DescribeGhosts()
        {
            var sb = new StringBuilder();
            try
            {
                var core = GameRefs.Main?.GetCore();
                if (core != null)
                {
                    var att = core.MonoAttachment;
                    var ouji = GameRefs.Main.GetOuji();
                    sb.Append($"you: radius {core.KatamariRadius:0.###} scale {core.KatamariScale:0.###} prince {LocalPrinceScale():0.####} " +
                              $"(drawn {(ouji != null ? DrawnHeight(ouji.gameObject) : 0f):0.####} tall)");
                    if (att != null)
                        sb.Append($" attacher {att.transform.lossyScale.x:0.###} (parent {(att.transform.parent != null ? att.transform.parent.name : "none")}, " +
                                  $"{att.transform.childCount} objects, sink {_converge:0.###}/s, ConvergeDelta {att.ConvergeDelta:0.#####})");
                }
                foreach (var kv in Views)
                {
                    var v = kv.Value;
                    sb.Append($" | ghost {kv.Key}: {(v.Visible ? "shown" : "hidden")} gap {v.Gap:0.#}x radius {v.Radius:0.###} scale {v.CoreScale:0.###} " +
                              $"(drawn radius {v.CoreScale * _coreRadiusPerUnit:0.###}) attacher {v.AttachScale:0.###} stuck {v.Stuck.Count}");
                    var c = v.Cousin;
                    if (c != null && c.Ready && c.OujiTransform != null)
                        sb.Append($" cousin prince {c.OujiTransform.lossyScale.x:0.####} (authored {c.AuthoredScale:0.####}, drawn {DrawnHeight(c.Model):0.####} tall)");
                    float sunk = 0f;
                    int n = 0;
                    foreach (var go in v.Stuck) { try { if (go != null) { sunk += go.transform.localPosition.magnitude; n++; } } catch { } }
                    if (n > 0) sb.Append($" stuck avg distance {sunk / n:0.####}");
                }
                int biggest = 0;
                foreach (var m in HiddenMonos) { try { biggest = Mathf.Max(biggest, RequireSize(m)); } catch { } }
                sb.Append($" | hidden by others {HiddenMonos.Count} (largest needs {KatamariState.SizeText(biggest)})");
            }
            catch (Exception e) { sb.Append(" (failed: " + e.Message + ")"); }
            return sb.ToString();
        }

        public static bool TryObjectPose(uint key, out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero;
            rot = Quaternion.identity;
            if (!ByKey.TryGetValue(key, out var mono) || mono == null) return false;
            var t = mono.transform;
            pos = t.position;
            rot = t.rotation;
            return true;
        }

        // ---------------------------------------------------------------- helpers

        public static string DescribeHierarchy(Transform t, int depth)
        {
            var sb = new StringBuilder();
            Describe(t, depth, sb);
            return sb.ToString();
        }

        private static void Describe(Transform t, int depth, StringBuilder sb)
        {
            sb.Append(t.name).Append('[');
            var comps = t.GetComponents<Component>();
            for (int i = 0; i < comps.Length; i++)
            {
                if (i > 0) sb.Append(',');
                try { sb.Append(comps[i].GetIl2CppType().Name); } catch { sb.Append('?'); }
            }
            sb.Append(']');
            if (depth <= 0 || t.childCount == 0) return;
            sb.Append('{');
            for (int i = 0; i < t.childCount && i < 12; i++)
            {
                if (i > 0) sb.Append(", ");
                Describe(t.GetChild(i), depth - 1, sb);
            }
            if (t.childCount > 12) sb.Append($", +{t.childCount - 12}");
            sb.Append('}');
        }

        private static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (int i = 0; t != null && i < 8; i++, t = t.parent) parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static uint Fnv(string s)
        {
            uint h = 2166136261;
            foreach (char c in s ?? "") { h ^= c; h *= 16777619; }
            return h;
        }

        private static uint PositionHash(Vector3 p)
        {
            uint h = 2166136261;
            foreach (int v in new[] { Mathf.RoundToInt(p.x * 4f), Mathf.RoundToInt(p.y * 4f), Mathf.RoundToInt(p.z * 4f) })
            {
                h ^= (uint)v;
                h *= 16777619;
            }
            return h;
        }

        private static uint Mix(uint a, uint b) => (a * 16777619) ^ b;
    }
}
