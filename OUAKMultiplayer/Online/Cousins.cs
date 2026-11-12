using System;
using System.Collections.Generic;
using System.Text;
using Il2CppApp.KatamariSin;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using UnityEngine;
using CustomCharaData = Il2CppApp.KatamariSin.GlobalSaveData.CustomCharaData;
using OUAKModAPI;

namespace OUAKMultiplayer
{
    /// <summary>
    /// A player's look, using the game's own multiplayer data:
    /// - sending: the NetworkUserInfo that MultiplayGameManager.SetMyUserData builds from the save (chosen
    ///   character, multiplayer core, custom cousin colours/parts, worn presents), as GetByteData() bytes;
    /// - receiving: the same model build offline training does for a remote player (StartTrainingMode):
    ///   MultiplayModelLoader.OujiModelLoad(data, pos, presents, character, customIndex, false), where a custom
    ///   cousin (character 999) gets a fresh CustomCharaData filled by NetworkUserInfo.GetColorStatus.
    /// </summary>
    internal static class Looks
    {
        private const int CustomCousin = 999;

        public static byte[] LocalLook(string name)
        {
            try
            {
                var save = GameRefs.Save;
                if (save == null) return null;
                var ui = new NetworkUserInfo();
                ui.userName = name;
                int chosen = save.GetSelectPlayerId;
                // In a hub, the cousin you walk as (the plaza's / ship's ModelLoader.PlayerNow, what it loaded) is what the
                // others should see, even if the save's choice says otherwise (the user's report, 2026-11-08: a cousin
                // swapped in a plaza kept showing as the old one).
                int walking = WalkingCharacter();
                if (walking >= 0 && walking != chosen)
                {
                    if (walking != _lastMismatch)
                    {
                        _lastMismatch = walking;
                        MelonLogger.Msg($"[Online] You walk as character {walking}, the save's choice is {chosen}: others see {walking}.");
                    }
                    chosen = walking;
                }
                ui.characterID = (uint)chosen;
                ui.coreID = (uint)save._multiPlayerCore;
                int idx = save._customPlayerIndex;
                ui.customPlayerIndex = (sbyte)idx;
                var data = save._data;
                if (idx >= 0 && data != null && idx < data.Length && data[idx] != null) ui.SetColorStatus(data[idx]);
                ui.SetWearingPresent();
                return ui.GetByteData();
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[Online] Couldn't read your cousin's look: {e.Message}");
                return null;
            }
        }

        private static int _lastMismatch = int.MinValue;
        private static ModelLoader _walker;
        private static int _walkerScene = int.MinValue;

        /// <summary>
        /// The character your hub's ModelLoader loaded for you to walk as (PlayerNow, once LoadPlayCharacter is done), or -1
        /// outside the hubs or when there isn't one. Looked up once per scene.
        /// </summary>
        public static int WalkingCharacter()
        {
            try
            {
                if (HubPresence.HubKey == 0) return -1;
                int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().m_Handle;
                if (scene != _walkerScene || _walker == null)
                {
                    _walkerScene = scene;
                    _walker = UnityEngine.Object.FindObjectOfType<ModelLoader>();
                }
                if (_walker == null || !_walker.LoadPlayCharacter) return -1;
                return _walker.PlayerNow;
            }
            catch { return -1; }
        }

        /// <summary>The character id in a look ("custom cousin" = 999), or -1.</summary>
        public static int CharacterOf(byte[] look)
        {
            if (look == null) return -1;
            try
            {
                var ui = new NetworkUserInfo();
                ui.SetByteData(look, 0);
                return (int)ui.characterID;
            }
            catch { return -1; }
        }

        public static bool SameLook(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        public static string Describe(byte[] look)
        {
            try
            {
                var ui = new NetworkUserInfo();
                ui.SetByteData(look, 0);
                return ui.characterID == CustomCousin ? $"custom cousin #{ui.customPlayerIndex}" : $"character {ui.characterID}";
            }
            catch { return "?"; }
        }

        /// <summary>Starts loading a cousin model; poll the result each frame.</summary>
        /// <param name="studio">For a portrait (PortraitStudio): kept opaque, never shown in the world.</param>
        public static CousinPuppet Load(byte[] look, bool studio = false)
        {
            var ui = new NetworkUserInfo();
            ui.SetByteData(look, 0);
            int character = (int)ui.characterID;
            var save = GameRefs.Save;
            int idx = Math.Max(0, save != null ? save._customPlayerIndex : 0);

            Il2CppReferenceArray<CustomCharaData> data;
            if (character == CustomCousin || save?._data == null)
            {
                data = new Il2CppReferenceArray<CustomCharaData>(idx + 1);
                var custom = new CustomCharaData();
                ui.GetColorStatus(ref custom);
                data[idx] = custom;
            }
            else
            {
                data = save._data;
            }

            var go = new GameObject("OUAK online cousin loader");
            UnityEngine.Object.DontDestroyOnLoad(go);
            var loader = go.AddComponent<MultiplayModelLoader>();
            var presents = ui.wearingPresent ?? new Il2CppStructArray<int>(0);
            loader.OujiModelLoad(data, Vector3.zero, presents, character, idx, false);
            return new CousinPuppet(go, loader, Time.unscaledTime, studio);
        }
    }

    /// <summary>
    /// The game's own Katamari Ball core packet. Sending fills NetworkCoreInfo with the same values
    /// MainGameCore.OnSyncProcess sends for your katamari (position, _coreAngle, CoreMesh rotation, the prince's
    /// NowAnime / AnimeSpeed, _nowAction, size, _oujiYPos); receiving reads it back with SetByteData.
    /// (OnSyncProcess's sRate comes from a match-only UserStatus, so it's sent as 0.)
    /// </summary>
    internal static class CoreInfoCodec
    {
        private static NetworkCoreInfo _writer, _reader;
        private static int _length = -1;

        public static byte[] Encode(MainGameCore core, MainGameOuji ouji, int size)
        {
            var mesh = core.CoreMesh;
            return Encode(core.transform.position, core._coreAngle, mesh != null ? mesh.rotation : core.transform.rotation,
                ouji != null ? ouji.NowAnime : 0, ouji != null ? ouji.AnimeSpeed : 1f, (int)core._nowAction, size, core._oujiYPos);
        }

        public static byte[] Encode(Vector3 pos, float angle, Quaternion rot, int anim, float speed, int action, int size, float oujiY)
        {
            _writer ??= new NetworkCoreInfo();
            _writer.SetParameter(pos, angle, rot, anim, speed, 0f, action, (uint)Math.Max(0, size), oujiY);
            return _writer.GetByteData();
        }

        /// <summary>Fills the decoded fields of <paramref name="s"/>; false if the bytes aren't a valid packet.</summary>
        public static bool Decode(ref KatamariState s)
        {
            try
            {
                _reader ??= new NetworkCoreInfo();
                if (_length < 0) _length = ((byte[])new NetworkCoreInfo().GetByteData()).Length;
                if (s.CoreInfo == null || s.CoreInfo.Length != _length) return false;
                _reader.SetByteData(s.CoreInfo, 0);
                var p = _reader.position;
                if (!float.IsFinite(p.x) || !float.IsFinite(p.y) || !float.IsFinite(p.z)) return false;
                s.CorePos = p;
                var q = _reader.coreRotation;
                float len = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
                s.CoreRot = len > 0.5f && len < 1.5f ? q : Quaternion.identity;
                s.AnimNo = _reader.animeNo;
                s.AnimSpeed = float.IsFinite(_reader.animeSpeed) ? Mathf.Clamp(_reader.animeSpeed, 0f, 10f) : 1f;
                s.Size = (int)Math.Min(_reader.coreSize, (uint)int.MaxValue);
                return true;
            }
            catch { return false; }
        }
    }

    internal sealed class CousinPuppet
    {
        private readonly GameObject _loaderGo;
        private readonly MultiplayModelLoader _loader;
        private readonly float _started;
        private readonly bool _studio;
        private static bool _hierarchyLogged;

        public GameObject Model { get; private set; }
        public Animator Animator { get; private set; }
        /// <summary>The game's animation driver for display princes: plays prince move numbers (with faces).</summary>
        public CharacterAnimation CharAnim { get; private set; }
        /// <summary>Model size at scale 1: height, depth, and how far its feet are below its root.</summary>
        public float ModelHeight { get; private set; } = 1f;
        public float ModelDepth { get; private set; } = 0.3f;
        public float FeetOffset { get; private set; }
        /// <summary>
        /// The model's MainGameOuji transform: the one MainGameOuji.InitializeOuji scales in the game (the same
        /// scale for every prince on a map). Ghosts give it their prince's world scale.
        /// </summary>
        public Transform OujiTransform { get; private set; }
        /// <summary>That transform's scale as the loader left it (before any prince scale is applied).</summary>
        public float AuthoredScale { get; private set; } = 1f;
        public bool Failed { get; private set; }
        public bool Ready => Model != null;

        public CousinPuppet(GameObject loaderGo, MultiplayModelLoader loader, float started, bool studio = false)
        {
            _loaderGo = loaderGo;
            _loader = loader;
            _started = started;
            _studio = studio;
        }

        /// <summary>Call each frame until Ready or Failed.</summary>
        public void Poll()
        {
            if (Ready || Failed) return;
            try
            {
                if (_loader == null) { Failed = true; return; }
                if (!_loader.GetModelLoadCompletion)
                {
                    if (Time.unscaledTime - _started > 20f) Fail("the model didn't finish loading in 20 s");
                    return;
                }
                var model = _loader.GetInstanceGameObject;
                if (model == null) { Fail("the loader made no model"); return; }
                Setup(model);
            }
            catch (Exception e) { Fail(e.Message); }
        }

        private void Fail(string why)
        {
            Failed = true;
            MelonLogger.Warning($"[Online] Cousin model failed ({why}); showing the ball only.");
        }

        private void Setup(GameObject model)
        {
            if (!_hierarchyLogged)
            {
                _hierarchyLogged = true;
                MelonLogger.Msg("[Online] Cousin model: " + Ghosts.DescribeHierarchy(model.transform, 3));
            }
            model.transform.SetParent(null, true);
            UnityEngine.Object.DontDestroyOnLoad(model);
            try
            {
                var ouji = model.GetComponentInChildren<MainGameOuji>(true);
                OujiTransform = ouji != null ? ouji.transform : null;
                if (OujiTransform != null) AuthoredScale = OujiTransform.localScale.x;
            }
            catch { OujiTransform = null; }
            // A puppet: no physics, no game scripts (they expect a live match around them).
            foreach (var c in model.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            foreach (var rb in model.GetComponentsInChildren<Rigidbody>(true)) rb.isKinematic = true;
            foreach (var mb in model.GetComponentsInChildren<MonoBehaviour>(true)) mb.enabled = false;
            Measure(model);
            // Ghosts are see-through (the game's "invincible" look); a studio cousin keeps its colours for its portrait.
            foreach (var r in model.GetComponentsInChildren<Renderer>(true))
            {
                if (!_studio)
                {
                    var mats = r.materials;
                    for (int i = 0; i < mats.Length; i++) GhostLook.MakeTranslucent(mats[i], GhostLook.CousinAlpha);
                }
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            Animator = model.GetComponentInChildren<Animator>();
            if (Animator != null) Animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            // The one script kept running: Katamari Ball's own animation path for a prince that isn't yours
            // (CharacterAnimation.ReqestCharacterAnime → its Update plays the move and sets the face).
            try
            {
                var anim = model.GetComponentInChildren<CharacterAnimation>(true);
                if (anim != null)
                {
                    anim.enabled = true;
                    anim.Init();
                    CharAnim = anim;
                }
            }
            catch (Exception e)
            {
                CharAnim = null;
                MelonLogger.Warning($"[Online] Cousin animations unavailable ({e.Message}).");
            }
            Model = model;
        }

        private void Measure(GameObject model)
        {
            var t = model.transform;
            t.position = Vector3.zero;
            t.rotation = Quaternion.identity;
            t.localScale = Vector3.one;
            bool any = false;
            var bounds = new Bounds();
            foreach (var r in model.GetComponentsInChildren<Renderer>(true))
            {
                if (!any) { bounds = r.bounds; any = true; }
                else bounds.Encapsulate(r.bounds);
            }
            if (!any) return;
            ModelHeight = Mathf.Max(0.01f, bounds.size.y);
            ModelDepth = Mathf.Max(0.01f, bounds.size.z);
            FeetOffset = -bounds.min.y;
        }

        public void Destroy()
        {
            try { if (Model != null) UnityEngine.Object.Destroy(Model); } catch { }
            try { if (_loaderGo != null) UnityEngine.Object.Destroy(_loaderGo); } catch { }
            Model = null;
        }
    }

    /// <summary>
    /// Player-list icons: one render of the cousin's head into a small texture, taken when the model loads, by
    /// a disabled camera that only sees an unused layer the model sits on for that moment.
    /// </summary>
    internal static class Portrait
    {
        private const int Size = 128;
        private static int _layer = -2;
        private static bool _broken;

        public static Texture Take(GameObject model, float height)
        {
            if (_broken) return null;
            int layer = FreeLayer();
            if (layer < 0) return null;
            GameObject camGo = null;
            var saved = new List<(Transform t, int layer)>();
            try
            {
                foreach (var t in model.GetComponentsInChildren<Transform>(true))
                {
                    saved.Add((t, t.gameObject.layer));
                    t.gameObject.layer = layer;
                }
                var rt = new RenderTexture(Size, Size, 16) { hideFlags = HideFlags.HideAndDontSave };
                camGo = new GameObject("OUAK portrait camera");
                var cam = camGo.AddComponent<Camera>();
                cam.enabled = false;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                cam.cullingMask = 1 << layer;
                cam.fieldOfView = 24f;
                cam.nearClipPlane = 0.01f * height;
                cam.farClipPlane = 20f * height;
                cam.targetTexture = rt;
                var mt = model.transform;
                var head = mt.position + Vector3.up * (height * 0.72f);
                camGo.transform.position = head + mt.forward * (height * 1.5f) + Vector3.up * (height * 0.05f);
                camGo.transform.LookAt(head);
                cam.Render();
                cam.targetTexture = null;
                return rt;
            }
            catch (Exception e)
            {
                _broken = true;
                MelonLogger.Warning($"[Online] Cousin icons unavailable ({e.Message}).");
                return null;
            }
            finally
            {
                foreach (var (t, l) in saved)
                    try { t.gameObject.layer = l; } catch { }
                if (camGo != null) UnityEngine.Object.Destroy(camGo);
            }
        }

        /// <summary>A whole object, framed by its bounds (used for the biscuit place icon).</summary>
        public static Texture TakeObject(GameObject go)
        {
            var r = go.GetComponentInChildren<Renderer>();
            if (r == null) return null;
            var b = r.bounds;
            float size = Mathf.Max(0.01f, b.size.magnitude);
            int layer = FreeLayer();
            if (layer < 0 || _broken) return null;
            GameObject camGo = null;
            try
            {
                int old = go.layer;
                go.layer = layer;
                var rt = new RenderTexture(Size, Size, 16) { hideFlags = HideFlags.HideAndDontSave };
                camGo = new GameObject("OUAK icon camera");
                var cam = camGo.AddComponent<Camera>();
                cam.enabled = false;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                cam.cullingMask = 1 << layer;
                cam.fieldOfView = 30f;
                cam.nearClipPlane = 0.01f * size;
                cam.farClipPlane = 20f * size;
                cam.targetTexture = rt;
                camGo.transform.position = b.center + new Vector3(0.6f, 0.8f, -1f).normalized * (size * 1.8f);
                camGo.transform.LookAt(b.center);
                cam.Render();
                cam.targetTexture = null;
                go.layer = old;
                return rt;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[Online] Icon render failed: {e.Message}");
                return null;
            }
            finally { if (camGo != null) UnityEngine.Object.Destroy(camGo); }
        }

        private static int FreeLayer()
        {
            if (_layer != -2) return _layer;
            _layer = -1;
            for (int i = 31; i >= 8; i--)
                if (string.IsNullOrEmpty(LayerMask.LayerToName(i))) { _layer = i; break; }
            return _layer;
        }
    }

    /// <summary>
    /// The "invincible" look: the material switch MainGameOuji.SetAlpha makes (RenderType Transparent,
    /// alpha test off, premultiplied blending, transparent queue, colour alpha), applied to our own copies.
    /// </summary>
    internal static class GhostLook
    {
        public const float CousinAlpha = 0.55f, StuckAlpha = 0.6f;
        private static readonly Dictionary<IntPtr, Material> Shared = new();

        public static void MakeTranslucent(Material m, float alpha)
        {
            if (m == null) return;
            try
            {
                m.SetOverrideTag("RenderType", "Transparent");
                m.DisableKeyword("_ALPHATEST_ON");
                m.EnableKeyword("_ALPHAPREMULTIPLY_ON");
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1f);
                if (m.HasProperty("_SrcBlend")) m.SetFloat("_SrcBlend", 1f);
                if (m.HasProperty("_DstBlend")) m.SetFloat("_DstBlend", 10f);
                if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 0f);
                if (m.HasProperty("_AlphaToMask")) m.SetFloat("_AlphaToMask", 0f);
                m.renderQueue = 3001;
                foreach (var prop in new[] { "_BaseColor", "_Color" })
                {
                    if (!m.HasProperty(prop)) continue;
                    var c = m.GetColor(prop);
                    c.a = alpha;
                    m.SetColor(prop, c);
                }
            }
            catch { }
        }

        /// <summary>Translucent copies of shared materials, one per original (objects stuck to remote katamaris).</summary>
        public static Il2CppReferenceArray<Material> TranslucentCopies(Il2CppReferenceArray<Material> src)
        {
            var result = new Il2CppReferenceArray<Material>(src.Length);
            for (int i = 0; i < src.Length; i++)
            {
                var m = src[i];
                if (m == null) continue;
                if (!Shared.TryGetValue(m.Pointer, out var copy) || copy == null)
                {
                    copy = new Material(m);
                    copy.hideFlags = HideFlags.HideAndDontSave;
                    MakeTranslucent(copy, StuckAlpha);
                    Shared[m.Pointer] = copy;
                }
                result[i] = copy;
            }
            return result;
        }

        public static void ForgetMaterials() => Shared.Clear();
    }
}
