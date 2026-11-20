using System;
using System.Collections.Generic;
using Il2CppApp.KatamariSin;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using LongLostCousins.Data;
using OUAKModAPI;
using UnityEngine;
using UnityEngine.Rendering;

namespace LongLostCousins.Dressing
{
    /// <summary>
    /// Dresses a game cousin model as the chosen cousin, and keeps every dressed model in step each LateUpdate.
    /// <para>Dressing switches the game's renderers off (Renderer.enabled, never SetActive or Destroy) and adds her
    /// skinned meshes under the game model: a marker root (<c>llc_Burda_new</c>) under the game's <c>model</c> node holds
    /// her bones (<c>llc_JNT_*</c>), her body and her extra face pieces; new-style face pieces go under the game's own face
    /// slot objects so the game's face switching (blink included) shows and hides them. A model that already carries a
    /// marker is a copy of a dressed model: it is re-bound, never built twice.</para>
    /// <para>The player's presents go on her joints (<see cref="Presents"/>); in the stage HUD's copy her marker takes the
    /// style's HUD scale.</para>
    /// </summary>
    internal static class Dresser
    {
        private static readonly ModLog Log = LongLostCousinsMod.Log.In("Dress");
        private static readonly HashSet<string> Once = new(StringComparer.Ordinal);

        /// <summary>Every live dressed model.</summary>
        public static readonly List<DressedModel> Models = new();

        /// <summary>The last model that could not be dressed, and why (for the tab).</summary>
        public static string LastProblem { get; private set; }
        public static int DressedCount { get; private set; }

        private const string Prefix = "llc_";
        private const string ExtraPrefix = "llc_x_";

        /// <summary>The dressed model whose root is <paramref name="go"/> (or holds it), or null.</summary>
        public static DressedModel Find(GameObject go)
        {
            if (go is null) return null;
            IntPtr p = go.Pointer;
            foreach (var m in Models) if (m.RootPtr == p) return m;
            return null;
        }

        public static bool IsDressed(IntPtr rootPtr)
        {
            foreach (var m in Models) if (m.RootPtr == rootPtr) return true;
            return false;
        }

        /// <summary>The object a model is known by: the one holding its CharacterAnimation, else <paramref name="root"/> itself.</summary>
        public static GameObject ModelRootOf(GameObject root)
        {
            var ca = root.GetComponentInChildren<CharacterAnimation>(true);
            return ca != null ? ca.gameObject : root;
        }

        /// <summary>The dressed model at or under <paramref name="root"/>, or null.</summary>
        public static DressedModel FindFor(GameObject root) => root == null ? null : Find(ModelRootOf(root));

        /// <summary>
        /// Dresses the cousin model at or under <paramref name="root"/> in the chosen style, or re-binds it when it is a copy
        /// of a dressed model. Never throws; returns null (logged once per reason) when the model is left as it is.
        /// </summary>
        public static DressedModel Dress(GameObject root, string context)
        {
            if (root == null) return null;
            try
            {
                var modelRoot = ModelRootOf(root);
                var known = Find(modelRoot);
                if (known != null) return known;

                var anim = modelRoot.GetComponentInChildren<Animator>(true);
                if (anim == null) return Soft(context, modelRoot, "it has no Animator");
                var model = anim.transform.Find("model");
                if (model == null) return Soft(context, modelRoot, $"its Animator object {anim.name} has no child called model");

                var marker = MarkerOf(model);
                if (marker != null) return Rebind(modelRoot, model, marker, context);
                if (!Chosen.Now) return null;
                // Her hidden base is always the Prince (PrinceBase steers the loads): any other cousin here was built by a
                // load the mod does not steer, and she is not put on it.
                var op = modelRoot.GetComponentInChildren<OujiPresent>(true);
                if (op == null) return Soft(context, modelRoot, "it has no OujiPresent to tell which cousin it is");
                if (op._oujiID != 0) return Soft(context, modelRoot, $"it is cousin {op._oujiID}, not the Prince she is built over (a load the mod does not steer)");
                var assets = CousinAssets.Get(Settings.Cousin, Settings.Style);
                if (assets == null) return Soft(context, modelRoot, $"{Settings.Describe()} can't be read (see the log)");
                return Build(modelRoot, model, assets, context);
            }
            catch (Exception e)
            {
                return Soft(context, root, e.GetType().Name + ": " + e.Message, e.StackTrace);
            }
        }

        /// <summary>The mod's marker root under the game's model node, or null.</summary>
        public static Transform MarkerOf(Transform model)
        {
            int n = model.childCount;
            for (int i = 0; i < n; i++)
            {
                var c = model.GetChild(i);
                if (c.name.StartsWith(Prefix, StringComparison.Ordinal)) return c;
            }
            return null;
        }

        /// <summary>True when <paramref name="root"/> holds a model that carries the mod's marker (a copy of a dressed model).</summary>
        public static bool CarriesMarker(GameObject root)
        {
            var anim = root.GetComponentInChildren<Animator>(true);
            var model = anim != null ? anim.transform.Find("model") : null;
            return model != null && MarkerOf(model) != null;
        }

        private static DressedModel Soft(string context, GameObject root, string why, string detail = null)
        {
            string name = "?";
            try { name = root != null ? root.name : "?"; } catch { }
            LastProblem = $"{context}: {name} was left as it is: {why}";
            if (Once.Add(context + "|" + why)) Log.Warn(LastProblem + (detail != null ? "\n" + detail : ""));
            return null;
        }

        /// <summary>The game joints matching her joints, by name under model/JNT_root; null entries for missing ones.</summary>
        private static Transform[] GameJoints(Transform model, LlcmModel data, List<string> missing)
        {
            var g = new Transform[data.Joints.Length];
            for (int i = 0; i < g.Length; i++)
            {
                var j = data.Joints[i];
                Transform t = j.Parent < 0 ? model.Find(j.Name) : g[j.Parent] != null ? g[j.Parent].Find(j.Name) : null;
                if (t == null) t = U.FindDeep(g[0] != null ? g[0] : model, j.Name);
                if (t == null) missing.Add(j.Name);
                g[i] = t;
            }
            return g;
        }

        private static GameObject LayerSourceOf(Transform model)
        {
            var body = model.Find("body_root/body_m");
            if (body == null) body = U.FindDeep(model, "body_m");
            return body != null ? body.gameObject : null;
        }

        /// <summary>Slot names match when they agree without underscores and case (some cousins name them face01).</summary>
        private static bool SameSlot(string a, string b) =>
            a != null && b != null && string.Equals(a.Replace("_", ""), b.Replace("_", ""), StringComparison.OrdinalIgnoreCase);

        /// <summary>The game's face slot object for a piece, or null when the component, the array or the entry is missing or named otherwise.</summary>
        private static GameObject SlotObject(Il2CppReferenceArray<GameObject> slots, LlcmMesh m)
        {
            if (slots == null || m.Slot == LlcmModel.None8 || m.Slot >= slots.Length) return null;
            var go = slots[m.Slot];
            if (go == null) return null;
            return SameSlot(go.name, m.SlotName) ? go : null;
        }

        // ---------------------------------------------------------------- building

        private static DressedModel Build(GameObject modelRoot, Transform model, CousinAssets assets, string context)
        {
            var data = assets.Model;
            var missing = new List<string>();
            var game = GameJoints(model, data, missing);
            if (missing.Count > 0) return Soft(context, modelRoot, "the game model lacks joints " + string.Join(", ", missing));

            var face = modelRoot.GetComponentInChildren<MainGameOujiFace>(true);
            Il2CppReferenceArray<GameObject> slots = null;
            if (face != null) slots = face.faceObject;
            var layerSource = LayerSourceOf(model);
            int layer = layerSource != null ? layerSource.layer : model.gameObject.layer;
            var bodyRenderer = layerSource != null ? layerSource.GetComponent<Renderer>() : null;

            var templates = new Dictionary<string, Material>(StringComparer.Ordinal);
            var materials = new Dictionary<(IntPtr, int), Material>();
            Material MaterialFor(LlcmMesh m, GameObject slot)
            {
                var template = Template(model, slot, m.Template ?? "body_m", bodyRenderer, templates);
                if (template == null)
                {
                    if (Once.Add("nomaterial|" + m.Template)) Log.Warn($"{context}: no game material to copy for {m.Name} (template {m.Template})");
                    return null;
                }
                int tex = data.Materials[m.Material].Texture;
                if (materials.TryGetValue((template.Pointer, tex), out var mat)) return mat;
                mat = new Material(template);
                mat.name = template.name;
                var t = assets.TextureOf(m.Material);
                mat.SetTexture("_BaseMap", t);
                mat.SetTexture("_MainTex", t);
                // Her texture carries her colours: no tint or tiling from the game's material (a designed cousin's may have them).
                var c = mat.GetColor("_BaseColor");
                mat.SetColor("_BaseColor", new Color(1f, 1f, 1f, c.a));
                mat.SetVector("_BaseMap_ST", new Vector4(1f, 1f, 0f, 0f));
                materials[(template.Pointer, tex)] = mat;
                return mat;
            }

            // Every mesh, texture and material is made before the model is touched: a failure here leaves the game's
            // model as it was, with no marker for a later pass to take for a finished one.
            for (int i = 0; i < data.Meshes.Length; i++)
            {
                var m = data.Meshes[i];
                assets.Mesh(i);
                MaterialFor(m, m.IsSlotPiece ? SlotObject(slots, m) : null);
                if (m.IsSlotPiece) MaterialFor(m, null);
            }

            // Every lookup is done; from here on the model is changed.
            var dm = new DressedModel
            {
                Root = modelRoot, RootPtr = modelRoot.Pointer, Context = context, Assets = assets, Model = model,
                GameJoints = game, Face = face, LayerSource = layerSource, DressedAt = Time.unscaledTime,
            };
            var marker = new GameObject(data.Marker);
            marker.layer = layer;
            dm.Marker = marker.transform;
            Place(dm.Marker, model, U.V3(0f, 0f, 0f), U.Identity);

            var her = new Transform[data.Joints.Length];
            for (int i = 0; i < her.Length; i++)
            {
                var j = data.Joints[i];
                var b = new GameObject(Prefix + j.Name);
                b.layer = layer;
                her[i] = b.transform;
                Place(her[i], j.Parent < 0 ? dm.Marker : her[j.Parent],
                    U.V3(j.LocalPosition[0], j.LocalPosition[1], j.LocalPosition[2]),
                    U.Q(j.LocalRotation[0], j.LocalRotation[1], j.LocalRotation[2], j.LocalRotation[3]));
            }
            dm.HerJoints = her;

            int underSlots = 0, withoutSlot = 0;
            for (int i = 0; i < data.Meshes.Length; i++)
            {
                var m = data.Meshes[i];
                switch (m.Role)
                {
                    case MeshRole.Body:
                        AddPiece(dm, i, dm.Marker, Prefix + m.Name, layer, MaterialFor(m, null), true);
                        break;
                    case MeshRole.FacePlate:
                    case MeshRole.EyeOrMouth:
                    case MeshRole.Overlay:
                    {
                        var slot = SlotObject(slots, m);
                        if (slot != null)
                        {
                            var p = AddPiece(dm, i, slot.transform, Prefix + m.Name, layer, MaterialFor(m, slot), true);
                            p.UnderSlot = true;
                            underSlots++;
                        }
                        else
                        {
                            bool show = assets.Group0Pieces.Contains(i);
                            var p = AddPiece(dm, i, dm.Marker, Prefix + m.Name, layer, MaterialFor(m, null), show);
                            p.Base = show;
                            withoutSlot++;
                        }
                        if (assets.ShownExtras.Contains(i))
                            AddPiece(dm, i, dm.Marker, ExtraPrefix + m.Name, layer, MaterialFor(m, slot), false).Extra = true;
                        break;
                    }
                    case MeshRole.Extra:
                        AddPiece(dm, i, dm.Marker, Prefix + m.Name, layer, MaterialFor(m, null), false).Extra = true;
                        break;
                    case MeshRole.Card:
                        AddPiece(dm, i, dm.Marker, Prefix + m.Name, layer, MaterialFor(m, null), false).Card = m.Card;
                        break;
                }
            }

            dm.LastLayer = layer;
            if (context == HudContext) FitHud(dm);
            AfterBuild(dm, modelRoot);
            ForeverPlayer.Attach(dm);
            int hidden = dm.HideGame();
            Pose(dm);
            dm.ApplyFace(true);
            dm.FollowAntenna();
            dm.NextHide = Time.unscaledTime + 0.5f;
            Models.Add(dm);
            DressedCount++;
            Log.Info($"dressed {U.PathOf(modelRoot.transform)} as {assets.Label} for the {context}: {dm.Pieces.Count} renderers " +
                     $"({underSlots} under the game's face slots{(withoutSlot > 0 ? $", {withoutSlot} without a slot to follow" : "")}), " +
                     $"{materials.Count} materials, {hidden} game renderers off, layer {layer}{(face == null ? ", no face component" : "")}; " +
                     PresentsText(dm));
            if (Test.CousinTest.Probing) Test.CousinTest.ProbeDressed(dm);
            return dm;
        }

        /// <summary>The stage HUD's context name (Contexts.StageHud): her marker there takes the style's HUD scale and offset.</summary>
        public const string HudContext = "stage HUD";

        /// <summary>
        /// The stage HUD copy only: her marker takes the style's hud_scale and hud_offset (cousins.json "fit"), so she fits
        /// the corner the way the Prince does. Scaled about her feet (the marker sits at the model's origin).
        /// </summary>
        private static void FitHud(DressedModel dm)
        {
            var fit = dm.Assets.Fit;
            float s = fit.HudScale;
            var o = fit.HudOffset;
            if (Math.Abs(s - 1f) < 0.0001f && o[0] == 0f && o[1] == 0f && o[2] == 0f) return;
            dm.Marker.localScale = U.V3(s, s, s);
            dm.Marker.localPosition = U.V3(o[0], o[1], o[2]);
            dm.HudScale = s;
        }

        /// <summary>Her antenna piece and the player's presents, at dressing (before the first hide pass, so presents she takes are never switched off).</summary>
        private static void AfterBuild(DressedModel dm, GameObject modelRoot)
        {
            dm.Op = modelRoot.GetComponentInChildren<OujiPresent>(true);
            if (dm.Assets.AntennaMesh >= 0)
                foreach (var p in dm.Pieces)
                    if (p.Mesh == dm.Assets.AntennaMesh && !p.Extra) { dm.Antenna = p; break; }
            TakePresents(dm);
        }

        private static void TakePresents(DressedModel dm)
        {
            try { Presents.Take(dm); dm.PresentErrors = 0; }
            catch (Exception e)
            {
                if (!dm.PresentsFailed) { dm.PresentsFailed = true; Log.Warn($"{dm.Label}: putting the presents on her failed: {e.GetType().Name}: {e.Message}"); }
                // Presents alone: after 10 failures in a row they are left to the game's joints (and the hide pass).
                if (++dm.PresentErrors >= 10) { Presents.GiveBack(dm); dm.Op = null; Log.Warn($"{dm.Label}: presents left to the game after 10 failures in a row"); }
            }
        }

        private static string PresentsText(DressedModel dm) =>
            dm.Op == null ? "no OujiPresent (no presents)" :
            $"{dm.PresentEntries} present entries on her nodes, {dm.PresentsMoved} present object(s) moved onto them{(dm.HudScale != 1f ? $", HUD scale {U.F(dm.HudScale)}" : "")}";

        private static void Place(Transform t, Transform parent, Vector3 pos, Quaternion rot)
        {
            t.SetParent(parent, false);
            t.localPosition = pos;
            t.localRotation = rot;
            t.localScale = U.One;
        }

        private static Piece AddPiece(DressedModel dm, int mesh, Transform parent, string name, int layer, Material mat, bool enabled)
        {
            var data = dm.Data;
            var m = data.Meshes[mesh];
            var go = new GameObject(name);
            go.layer = layer;
            Place(go.transform, parent, U.V3(0f, 0f, 0f), U.Identity);
            var smr = go.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = dm.Assets.Mesh(mesh);
            var bones = new Il2CppReferenceArray<Transform>(m.Bones.Length);
            for (int k = 0; k < m.Bones.Length; k++) bones[k] = dm.HerJoints[m.Bones[k]];
            smr.bones = bones;
            smr.rootBone = dm.HerJoints[m.RootBone];
            smr.updateWhenOffscreen = true;
            smr.shadowCastingMode = m.CastsShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            smr.receiveShadows = true;
            if (mat != null) smr.sharedMaterial = mat;
            smr.enabled = enabled;
            var p = new Piece { Mesh = mesh, Role = m.Role, Renderer = smr, Go = go, Card = m.Card };
            dm.Pieces.Add(p);
            dm.Own.Add(smr.Pointer);
            return p;
        }

        /// <summary>
        /// The game material her piece copies: the shared material of the game renderer named <paramref name="name"/> (under
        /// the piece's slot first, then anywhere under model), else body_m's, else any renderer's.
        /// </summary>
        private static Material Template(Transform model, GameObject slot, string name, Renderer body, Dictionary<string, Material> cache)
        {
            if (cache.TryGetValue(name, out var mat)) return mat;
            Renderer r = null;
            if (slot != null)
            {
                var t = slot.transform.Find(name);
                if (t != null) r = t.GetComponent<Renderer>();
            }
            if (r == null)
            {
                var t = U.FindDeep(model, name);
                if (t != null) r = t.GetComponent<Renderer>();
            }
            mat = r != null ? r.sharedMaterial : null;
            if (mat == null && body != null) mat = body.sharedMaterial;
            if (mat == null)
            {
                var all = model.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < all.Length && mat == null; i++)
                    if (!all[i].name.StartsWith(Prefix, StringComparison.Ordinal)) mat = all[i].sharedMaterial;
            }
            cache[name] = mat;
            return mat;
        }

        // ---------------------------------------------------------------- copies of dressed models

        private static DressedModel Rebind(GameObject modelRoot, Transform model, Transform marker, string context)
        {
            var assets = CousinAssets.ByMarker(marker.name);
            if (assets == null) return Soft(context, modelRoot, $"it carries {marker.name}, which this mod has no cousin for");
            var data = assets.Model;
            var missing = new List<string>();
            var game = GameJoints(model, data, missing);
            var her = new Transform[data.Joints.Length];
            for (int i = 0; i < her.Length; i++)
            {
                her[i] = U.FindDeep(marker, Prefix + data.Joints[i].Name);
                if (her[i] == null) missing.Add(Prefix + data.Joints[i].Name);
            }
            if (missing.Count > 0) return Soft(context, modelRoot, "a copy of a dressed model lacks joints " + string.Join(", ", missing));

            var dm = new DressedModel
            {
                Root = modelRoot, RootPtr = modelRoot.Pointer, Context = context + ", a copy", Assets = assets, Model = model, Marker = marker,
                GameJoints = game, HerJoints = her, Face = modelRoot.GetComponentInChildren<MainGameOujiFace>(true),
                LayerSource = LayerSourceOf(model), Rebound = true, DressedAt = Time.unscaledTime,
            };
            IntPtr markerPtr = marker.Pointer;
            var all = modelRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int k = 0; k < all.Length; k++)
            {
                var smr = all[k];
                var go = smr.gameObject;
                string name = go.name;
                if (!name.StartsWith(Prefix, StringComparison.Ordinal)) continue;
                bool extra = name.StartsWith(ExtraPrefix, StringComparison.Ordinal);
                int i = data.MeshIndex(name.Substring(extra ? ExtraPrefix.Length : Prefix.Length));
                if (i < 0) continue;
                var m = data.Meshes[i];
                var parent = go.transform.parent;
                bool underSlot = m.IsSlotPiece && !extra && parent != null && parent.Pointer != markerPtr;
                var p = new Piece
                {
                    Mesh = i, Role = m.Role, Renderer = smr, Go = go, Card = m.Card, UnderSlot = underSlot,
                    Extra = extra || m.Role == MeshRole.Extra,
                    Base = !m.IsSlotPiece || extra || underSlot || assets.Group0Pieces.Contains(i),
                };
                dm.Pieces.Add(p);
                dm.Own.Add(smr.Pointer);
            }
            // A copy keeps the marker's scale it was made with (a copy of the HUD copy keeps the HUD's).
            dm.HudScale = marker.localScale.x;
            AfterBuild(dm, modelRoot);
            ForeverPlayer.Attach(dm);
            // A copy may carry a Forever pose: an OUAK state first puts her rest positions back.
            dm.ForeverOn = dm.Anim != null;
            dm.HideGame();
            Pose(dm);
            dm.ApplyFace(true);
            dm.FollowLayer();
            dm.FollowAntenna();
            dm.NextHide = Time.unscaledTime + 0.5f;
            Models.Add(dm);
            DressedCount++;
            Log.Info($"re-bound {U.PathOf(modelRoot.transform)} ({assets.Label}) for the {context}: a copy of a dressed model, {dm.Pieces.Count} of her renderers found; {PresentsText(dm)}");
            if (Test.CousinTest.Probing) Test.CousinTest.ProbeDressed(dm);
            return dm;
        }

        // ---------------------------------------------------------------- undressing

        /// <summary>
        /// Takes her off one model: the game renderers the mod switched off go back on, her objects (the marker root and
        /// the pieces under the game's face slots) are destroyed, and the model is forgotten. Never throws.
        /// </summary>
        public static void Undress(DressedModel dm, string why)
        {
            if (dm == null) return;
            Models.Remove(dm);
            int back = 0, gone = 0;
            try
            {
                // The presents go back to the game's joints first: her nodes go with her marker.
                int given = Presents.GiveBack(dm);
                if (given > 0) Log.Info($"{dm.Label}: {given} present entr(ies) and object(s) given back to the game's joints");
                foreach (var r in dm.HiddenByMod)
                    try { if (r != null && !r.enabled) { r.enabled = true; back++; } }
                    catch { }
                foreach (var p in dm.Pieces)
                    try { if (p.UnderSlot && p.Go != null) { UnityEngine.Object.Destroy(p.Go); gone++; } }
                    catch { }
                if (dm.Marker != null) { UnityEngine.Object.Destroy(dm.Marker.gameObject); gone++; }
                Log.Info($"undressed {(dm.Root != null ? U.PathOf(dm.Root.transform) : "a model")} ({dm.Label}): {why}; {back} game renderer(s) on again, {gone} of her object(s) removed");
            }
            catch (Exception e) { Log.Warn($"undressing {dm.Label}: {e.GetType().Name}: {e.Message}"); }
        }

        // ---------------------------------------------------------------- every frame

        /// <summary>Her pose this frame: Forever's clip on a state the style's map names (once the copy is baked), else the copy of the game's joints.</summary>
        private static void Pose(DressedModel dm)
        {
            if (!ForeverPlayer.Step(dm)) dm.Follow();
        }

        /// <summary>
        /// MelonMod.OnLateUpdate: for every live dressed model, the bones, the face, the layer, and every 0.5 s the game's
        /// renderers. Dead models are dropped. Never throws.
        /// </summary>
        public static void LateTick()
        {
            if (Models.Count == 0) return;
            float now = Time.unscaledTime;
            for (int i = Models.Count - 1; i >= 0; i--)
            {
                var dm = Models[i];
                try
                {
                    if (dm.Root == null || dm.Marker == null) { Models.RemoveAt(i); continue; }
                    Pose(dm);
                    dm.ApplyFace(false);
                    dm.FollowLayer();
                    if (now >= dm.NextHide)
                    {
                        // Presents often arrive after dressing (0.02 to 0.25 s, measured): their own try, before the hide pass.
                        TakePresents(dm);
                        dm.HideGame();
                        dm.ApplyFace(true);
                        dm.NextHide = now + 0.5f;
                    }
                    // Last, so a face refresh above never leaves her antenna on under a hat that hides the game's.
                    dm.FollowAntenna();
                    dm.Errors = 0;
                }
                catch (Exception e)
                {
                    if (!dm.Failed) { dm.Failed = true; Log.Warn($"{dm.Label}: keeping it in step failed: {e.GetType().Name}: {e.Message}"); }
                    if (++dm.Errors >= 10) { Models.RemoveAt(i); Log.Warn($"{dm.Label}: dropped after 10 failures in a row"); }
                }
            }
        }
    }
}
