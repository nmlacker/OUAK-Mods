using System;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using Il2CppApp.KatamariSin;
using MelonLoader;
using UnityEngine;
using OUAKModAPI;

namespace OUAKMultiplayer
{
    /// <summary>Where a player walks in a hub: the hub, their character's position, facing, size and move.</summary>
    internal struct HubState
    {
        public int Key;
        public Vector3 Pos;
        public float Yaw;
        /// <summary>World scale of their character's Animator transform (its rig), to draw them the same size.</summary>
        public float Scale;
        /// <summary>The move their character plays (CharacterAnimation._nowAnime, the game's OUJI_MOT numbers), -1 if unknown.</summary>
        public int Anim;
        /// <summary>Their character's Animator (state, time, speed, face): emotes and every other move as it plays.</summary>
        public PoseSync Pose;

        public PacketWriter Write(PacketWriter w) => w.I32(Key).Vec(Pos).F32(Yaw).F32(Scale).I32(Anim).Then(Pose.Write);

        public static HubState Read(PacketReader r) =>
            new() { Key = r.I32(), Pos = r.Vec(), Yaw = r.F32(), Scale = r.F32(), Anim = r.I32(), Pose = PoseSync.Read(r) };
    }

    /// <summary>
    /// Free roam: in the hubs (the Prince's Ship and the plazas) everyone walks around on their own; nobody is
    /// pulled along when someone changes hub (stages are chosen by vote, StageGate). You see the others who are in
    /// the same place: each game sends where its walking character is (the plaza's CharacterManager owner, an
    /// OujiCharacterController; on the ship the prince's OujiShipAnimationController), its facing, the world scale
    /// of its rig and the move it plays (CharacterAnimation._nowAnime). Others show as their own cousin, playing
    /// those moves through the game's CharacterAnimation the way Katamari Ball shows other players.
    /// Hub keys: 3000 + plaza index (SelectHiroba, GlobalSaveData._stageIndex), 3900 for the ship.
    /// </summary>
    internal static class HubPresence
    {
        public const int ShipKey = 3900, IdleAnim = 0, WalkAnim = 41;

        public static int HubKey { get; private set; }
        /// <summary>Cousins rebuilt because their player's look changed (self-test).</summary>
        public static int LookRebuilds { get; private set; }

        /// <summary>Self-test: the cousin model shown for a player here, or null.</summary>
        public static GameObject CousinOf(byte id) =>
            Views.TryGetValue(id, out var v) && v.Cousin != null && v.Cousin.Ready ? v.Cousin.Model : null;

        private sealed class View
        {
            public CousinPuppet Cousin;
            public byte[] Look;
            public Vector3 Shown;
            public float Yaw, Height;
            /// <summary>Their character's Animator as last shown on the cousin (PoseSync).</summary>
            public readonly PoseTrack Pose = new();
            public bool Placed, Visible, NearHidden;
        }

        private static readonly Dictionary<byte, View> Views = new();
        private static readonly Dictionary<int, string> PlaceNames = new();
        private static OujiShipAnimationController _ship;
        private static int _shipScene = -1;
        private static float _myHeight, _nextHeight;
        private static bool _broken, _sourceLogged;
        private static int _brokenKey;
        private static GUIStyle _tag, _tagShadow;
        private static readonly PoseReader HubPose = new();

        /// <summary>
        /// The ship by its scene; a plaza by its manager (SelectHirobaManager), since the plaza's active scene is
        /// the era's map ("Map_Edo"…), not "SelectHiroba".
        /// </summary>
        public static int ComputeKey()
        {
            if (GameRefs.Main != null) return 0;
            string scene = SceneLoader.ActiveScene;
            if (scene == "OujiShip") return ShipKey;
            if (SingletonMonoBehaviour<SelectHirobaManager>.instance == null) return 0;
            int idx = 0;
            try { idx = GameRefs.Save != null ? GameRefs.Save._stageIndex : 0; } catch { }
            return 3000 + Mathf.Clamp(idx, 0, 800);
        }

        /// <summary>Your walking character in the hub you're in (false when you're not in one).</summary>
        public static bool TryLocal(out HubState s, out float height)
        {
            s = new HubState { Key = HubKey, Anim = -1, Scale = 1f };
            height = _myHeight;
            if (s.Key == 0) return false;
            try
            {
                Transform body = null;
                string source = null;
                var owner = LocalCharacter(out source);
                if (owner != null)
                {
                    var anim = owner.GetCharacterAnimation;
                    var animator = anim != null ? anim.IsAnimator : null;
                    // In a plaza, wait for your cousin's model: without it the bare object's position and size (rig
                    // scale) were sent, and the others drew you wrong or not at all (after matches, 2026-11-12).
                    if (animator == null && SingletonMonoBehaviour<SelectHirobaManager>.instance != null) { _bodyName = "no model yet"; return false; }
                    body = animator != null ? animator.transform : owner.transform;
                    if (anim != null) s.Anim = anim._nowAnime;
                    s.Pose = HubPose.Read(animator, anim != null ? anim.MainGameOujiFace : null);
                }
                else
                {
                    var ship = ShipPrince();
                    if (ship != null)
                    {
                        body = ship.transform;
                        s.Anim = ship.CurrentMoveState ? WalkAnim : IdleAnim;
                        s.Pose = HubPose.Read(ship.m_SelfAnimator, null);
                        source = "the ship's prince (OujiShipAnimationController)";
                    }
                }
                if (body == null) return false;
                _bodyName = body.name;
                if (_foundSure) _ownBody = body.name;
                if (!_sourceLogged)
                {
                    _sourceLogged = true;
                    MelonLogger.Msg($"[Online] Free roam: you're {PlaceName(s.Key)}, shown to others from {source} ({body.name}).");
                }
                s.Pos = body.position;
                s.Yaw = body.eulerAngles.y;
                s.Scale = body.lossyScale.x;
                if (Time.unscaledTime >= _nextHeight)
                {
                    _nextHeight = Time.unscaledTime + 5f;
                    float h = DrawnHeight(body.gameObject);
                    if (h > 0f) _myHeight = h;
                }
                height = _myHeight;
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Teleport: puts your walking character at <paramref name="pos"/> facing <paramref name="yaw"/> through its
        /// CharacterMove (SetCharacterPosition/Rotation = its Rigidbody's), the move the plaza gather uses to stand
        /// you at a fan. False (with why) when no walking character is found here.
        /// </summary>
        public static bool TryMoveLocal(Vector3 pos, float yaw, out string how)
        {
            how = "";
            if (HubKey == 0) { how = "you're not on the ship or in a plaza"; return false; }
            try
            {
                var owner = LocalCharacter(out string source);
                var move = owner != null ? owner.GetCharacter : null;
                if (move == null) { how = "no walking character found here"; return false; }
                move.SetCharacterPosition = pos;
                move.SetCharacterRotation = Quaternion.Euler(0f, yaw, 0f);
                how = source;
                return true;
            }
            catch (Exception e) { how = e.Message; return false; }
        }

        private static OujiCharacterController _found;
        private static string _foundSource;
        private static int _foundScene = -1;
        private static float _nextFind;
        private static bool _foundSure;
        private static int _plazaWaitScene = -1;
        private static float _plazaWaitSince;

        /// <summary>Where your walking character was last found (self-test, logs).</summary>
        public static string LocalSource => _foundSource ?? "not found yet";
        /// <summary>The name of the walking character used for you (self-test).</summary>
        public static string LocalBody => _bodyName;
        private static string _bodyName = "-", _ownBody;

        /// <summary>
        /// Your walking character: the plaza's CharacterManager owner (asked every time: right after a plaza loads it
        /// isn't set yet); on the ship, the one its PlayCharacterAdominist set up (its own CharacterManager, or the
        /// player object it made). In a plaza nothing else is taken: its other walking characters are the cousins
        /// standing around (2026-11-08: after a match the first OujiCharacterController found, SelectHirobaOujiSetting11,
        /// was taken for you and kept for the whole visit, so the others saw "you" at that cousin's spot). Elsewhere, as
        /// a last resort, any OujiCharacterController in the scene, looked for again twice a second.
        /// </summary>
        private static OujiCharacterController LocalCharacter(out string source)
        {
            source = _foundSource;
            int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().m_Handle;
            if (scene != _foundScene) { _foundScene = scene; _found = null; _foundSure = false; _foundSource = null; _nextFind = 0f; }
            var cm = SingletonMonoBehaviour<CharacterManager>.instance;
            var owner = cm != null ? cm.GetCharacterController : null;
            if (owner != null)
            {
                if (_found == null || _found.Pointer != owner.Pointer) Remember(owner, "the plaza's walking character (CharacterManager)", true, out _);
                source = _foundSource;
                return owner;
            }
            if (_found != null && _foundSure) return _found;
            bool plaza = HubKey >= 3000 && HubKey < ShipKey;
            if (plaza)
            {
                // Not the scene's first walking character (that may be one of the plaza's cousins): the player model the
                // plaza's ModelLoader loaded for you (_selectPlayerObject). After a match CharacterManager can stay unset
                // (measured 2026-11-12: 25 s and more).
                if (_found != null && _found.Pointer != IntPtr.Zero) { source = _foundSource; return _found; }
                // Only once CharacterManager has had 3 s (it usually answers within a second or two).
                if (_plazaWaitScene != scene) { _plazaWaitScene = scene; _plazaWaitSince = Time.unscaledTime; }
                if (Time.unscaledTime - _plazaWaitSince >= 3f && Time.unscaledTime >= _nextFind)
                {
                    _nextFind = Time.unscaledTime + 0.5f;
                    // The walking character wearing your own model (its rig's name as last seen as you, e.g.
                    // OUJI47_D_Custom). Not the plaza ModelLoader's object: that was another one (OUJI_D, elsewhere).
                    try
                    {
                        if (!string.IsNullOrEmpty(_ownBody))
                            foreach (var c in UnityEngine.Object.FindObjectsOfType<OujiCharacterController>())
                            {
                                var a = c.GetCharacterAnimation?.IsAnimator;
                                if (a != null && a.transform.name == _ownBody) return Remember(c, "your own model in the plaza", false, out source);
                            }
                    }
                    catch { }
                }
                _found = null;
                source = _foundSource = "waiting for the plaza's walking character";
                return null;
            }
            if (_found != null) return _found;
            if (Time.unscaledTime < _nextFind) return null;
            _nextFind = Time.unscaledTime + 0.5f;
            try
            {
                var admin = UnityEngine.Object.FindObjectOfType<PlayCharacterAdominist>();
                if (admin != null)
                {
                    var c = admin.m_CharacterManager != null ? admin.m_CharacterManager.GetCharacterController : null;
                    if (c == null && admin.m_Selectplayer != null) c = admin.m_Selectplayer.GetComponentInChildren<OujiCharacterController>(true);
                    if (c != null) return Remember(c, "the ship's walking character (PlayCharacterAdominist)", true, out source);
                }
                var any = UnityEngine.Object.FindObjectOfType<OujiCharacterController>();
                if (any != null) return Remember(any, "the scene's walking character (OujiCharacterController)", false, out source);
            }
            catch { }
            return null;
        }

        private static OujiCharacterController Remember(OujiCharacterController c, string source, bool sure, out string outSource)
        {
            _found = c;
            _foundSure = sure;
            _foundSource = outSource = source;
            _sourceLogged = false;
            return c;
        }

        private static OujiShipAnimationController ShipPrince()
        {
            int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().m_Handle;
            if (scene != _shipScene || _ship == null)
            {
                _shipScene = scene;
                _ship = UnityEngine.Object.FindObjectOfType<OujiShipAnimationController>();
            }
            return _ship;
        }

        // ---------------------------------------------------------------- per frame

        public static void Tick()
        {
            try { HubKey = ComputeKey(); } catch { HubKey = 0; }
            if (_broken && HubKey != _brokenKey) { _broken = false; MelonLogger.Msg("[Online] Free roam: trying to show other players again."); }
            if (HubKey == 0) _sourceLogged = false;
            if (_broken || !NetSession.Connected || HubKey == 0)
            {
                if (Views.Count > 0) ClearAll();
                return;
            }
            // A scene is changing (SceneSwitchGuard): nobody else's cousin is around while it sets itself up.
            if (SceneSwitchGuard.Holding)
            {
                HideAllNow();
                return;
            }
            float now = Time.unscaledTime, k = 1f - Mathf.Exp(-12f * Time.unscaledDeltaTime);
            bool haveMe = TryLocal(out var me, out float myHeight);
            foreach (var p in NetSession.Peers.Values)
            {
                Views.TryGetValue(p.Id, out var v);
                bool here = p.HubKey == HubKey && p.HubAt > 0f && now - p.HubAt < 3f && p.Look != null;
                if (!here)
                {
                    if (v != null) Hide(v, true);
                    continue;
                }
                if (v == null) Views[p.Id] = v = new View();
                try
                {
                    if (!Looks.SameLook(p.Look, v.Look))
                    {
                        v.Cousin?.Destroy();
                        v.Look = p.Look;
                        v.Pose.Reset();
                        bool changed = v.Look != null;
                        v.Cousin = Looks.Load(p.Look);
                        if (changed) LookRebuilds++;
                        MelonLogger.Msg(changed
                            ? $"[Online] Free roam: {p.Name} changed cousin ({Looks.Describe(p.Look)}): showing the new one."
                            : $"[Online] Free roam: {p.Name} is here too ({PlaceName(HubKey)}).");
                    }
                    v.Cousin?.Poll();
                }
                catch (Exception e)
                {
                    _broken = true;
                    _brokenKey = HubKey;
                    MelonLogger.Warning($"[Online] Free roam: can't show other players here ({e.Message}); trying again in the next place.");
                    ClearAll();
                    return;
                }

                float jump = Mathf.Max(3f, myHeight * 20f);
                if (!v.Placed || (v.Shown - p.HubPos).sqrMagnitude > jump * jump)
                {
                    v.Shown = p.HubPos;
                    v.Yaw = p.HubYaw;
                    v.Placed = true;
                }
                else
                {
                    v.Shown = Vector3.Lerp(v.Shown, p.HubPos, k);
                    v.Yaw = Mathf.LerpAngle(v.Yaw, p.HubYaw, k);
                }

                // Your ghost settings: off hides them; "vanish near you" hides one standing on top of you.
                bool vis = GhostSettings.Shown;
                if (vis && haveMe && myHeight > 0f && GhostSettings.NearSteps[GhostSettings.Near] >= 0f)
                {
                    var d = v.Shown - me.Pos;
                    d.y = 0f;
                    float r = myHeight * 0.45f;
                    v.NearHidden = v.NearHidden ? d.magnitude < r * 1.3f : d.magnitude < r;
                    vis = !v.NearHidden;
                }
                v.Visible = vis;
                var c = v.Cousin;
                if (c == null || !c.Ready) continue;
                if (!vis)
                {
                    Hide(v, false);
                    continue;
                }
                Place(c, v, p);
            }

            List<byte> gone = null;
            foreach (byte id in Views.Keys)
                if (!NetSession.Peers.ContainsKey(id)) (gone ??= new List<byte>()).Add(id);
            if (gone != null)
                foreach (byte id in gone) { Views[id].Cousin?.Destroy(); Views.Remove(id); }
        }

        /// <summary>Every other player's cousin switched off at once (a scene change: SceneSwitchGuard).</summary>
        public static void HideAllNow()
        {
            foreach (var v in Views.Values) Hide(v, true);
        }

        /// <summary>Their cousin at their character's spot, facing, rig size and move.</summary>
        private static void Place(CousinPuppet c, View v, Peer p)
        {
            try
            {
                var model = c.Model;
                if (!model.activeSelf) model.SetActive(true);
                var t = model.transform;
                t.rotation = Quaternion.Euler(0f, v.Yaw, 0f);
                var rig = c.Animator != null ? c.Animator.transform : c.OujiTransform != null ? c.OujiTransform : t;
                t.localScale = Vector3.one;
                float unscaled = rig.lossyScale.x;
                float scale = p.HubScale > 0f && unscaled > 1e-6f ? p.HubScale / unscaled : 1f;
                t.localScale = Vector3.one * scale;
                t.position = v.Shown - (rig.position - t.position);
                v.Height = c.ModelHeight * scale;
                // Their character's Animator as it is (emotes and all); the move number when it can't be.
                PosePlayer.Apply(c, v.Pose, p.HubPose, Time.unscaledTime - p.HubAt, p.HubAnim, 1f);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[Online] Free roam: showing {p.Name} failed ({e.Message}).");
                Hide(v, false);
            }
        }

        private static void Hide(View v, bool unplace)
        {
            try
            {
                if (v.Cousin?.Model != null && v.Cousin.Model.activeSelf)
                {
                    v.Cousin.Model.SetActive(false);
                    v.Pose.Reset();
                }
            }
            catch { }
            if (unplace) v.Placed = false;
            v.Visible = false;
        }

        /// <summary>Self-test: the first other player's cousin shown in this hub, or null.</summary>
        public static GameObject FirstCousinModel()
        {
            foreach (var v in Views.Values)
            {
                try { if (v.Cousin != null && v.Cousin.Ready && v.Cousin.Model.activeSelf) return v.Cousin.Model; }
                catch { }
            }
            return null;
        }

        private static void ClearAll()
        {
            foreach (var v in Views.Values) v.Cousin?.Destroy();
            Views.Clear();
        }

        // ---------------------------------------------------------------- names and tags

        /// <summary>"on the Prince's Ship", "in the Edo plaza"... for the player bar and logs.</summary>
        public static string PlaceName(int key)
        {
            if (key == ShipKey) return "on the Prince's Ship";
            if (key < 3000 || key >= ShipKey) return "";
            if (PlaceNames.TryGetValue(key, out var name)) return name;
            name = "in a plaza";
            try
            {
                foreach (var e in StageCatalog.Entries)
                {
                    if (e.Kind != MapKind.Plaza || string.IsNullOrEmpty(e.PlazaKey)) continue;
                    if (3000 + SelectHirobaManager.GetStageIndex(e.PlazaKey) == key)
                    {
                        name = e.Name.IndexOf("plaza", StringComparison.OrdinalIgnoreCase) >= 0 ? "in the " + e.Name : $"in the {e.Name} plaza";
                        break;
                    }
                }
            }
            catch { }
            PlaceNames[key] = name;
            return name;
        }

        public static void OnGUI()
        {
            if (!NetSession.Connected || HubKey == 0 || !GhostSettings.Shown || Event.current.type != EventType.Repaint) return;
            bool tags = GhostSettings.Tags, bubbles = GhostSettings.Bubbles;
            if (!tags && !bubbles) return;
            Camera cam = null;
            try { cam = Camera.main; } catch { }
            if (cam == null) return;
            if (_tag == null)
            {
                _tag = TagStyle(Color.white);
                _tagShadow = TagStyle(new Color(0f, 0f, 0f, 0.85f));
            }
            foreach (var kv in Views)
            {
                var v = kv.Value;
                if (!v.Visible || v.Cousin == null || !v.Cousin.Ready || !NetSession.Peers.TryGetValue(kv.Key, out var p)) continue;
                var sp = cam.WorldToScreenPoint(v.Shown + Vector3.up * Mathf.Max(0.05f, v.Height * 1.05f));
                if (sp.z <= 0f) continue;
                float y = Screen.height - sp.y;
                if (tags)
                {
                    GUI.Label(new Rect(sp.x - 119f, y - 25f, 240f, 22f), p.Name, _tagShadow);
                    GUI.Label(new Rect(sp.x - 120f, y - 26f, 240f, 22f), p.Name, _tag);
                    Voice.TagMark(kv.Key, sp.x, y - 26f, 22f, p.Name, _tag);
                }
                // What they said in the chat, over the tag (Chat.DrawBubbles draws it after every tag).
                if (bubbles) Chat.QueueBubble(kv.Key, sp.x, tags ? y - 28f : y - 4f, sp.z);
            }
        }

        private static GUIStyle TagStyle(Color color)
        {
            var s = new GUIStyle();
            s.fontSize = 16;
            s.fontStyle = FontStyle.Bold;
            s.alignment = TextAnchor.MiddleCenter;
            s.normal.textColor = color;
            return s;
        }

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

        /// <summary>Self-test: what free roam sees right now.</summary>
        public static string Describe()
        {
            string me = TryLocal(out var s, out float h)
                ? $"you {PlaceName(s.Key)} (key {s.Key}) at {s.Pos}, rig scale {s.Scale:0.####}, {h:0.###} tall, move {s.Anim}"
                : $"no walking character found (hub key {HubKey}, scene {SceneLoader.ActiveScene})";
            var others = Views.Select(kv =>
            {
                var v = kv.Value;
                var c = v.Cousin;
                string state = c == null ? "no model" : !c.Ready ? (c.Failed ? "model failed" : "loading") : v.Visible ? "shown" : "hidden";
                float rig = c != null && c.Ready && c.Animator != null ? c.Animator.transform.lossyScale.x : 0f;
                return $"player {kv.Key}: {state}, rig scale {rig:0.####}, {v.Height:0.###} tall, move {(NetSession.Peers.TryGetValue(kv.Key, out var hp) ? hp.HubAnim : -1)}, pose {(v.Pose.Driving ? "mirrored" : "by move number")}";
            });
            return me + " | " + (Views.Count == 0 ? "nobody else here" : string.Join(" | ", others));
        }
    }
}
