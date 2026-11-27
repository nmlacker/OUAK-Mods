using System;
using System.Collections.Generic;
using Il2CppApp.KatamariSin;
using OUAKModAPI;
using UnityEngine;

namespace LongLostCousins.Dressing
{
    /// <summary>
    /// The player's presents on her. Each entry of the game model's present lists (OujiPresent._datas and
    /// _stagePresentDatas) names the joint its present hangs on (PresentData._boneTransform). For every present she can
    /// wear, that joint is pointed at an attach node under her own joint of the same name (<c>llc_att_JNT_head</c>), so
    /// the game parents every present it makes from then on there itself, by all its paths (the async stage load reads the
    /// joint only after the object is made). Presents that already exist are moved there once. Her attach nodes sit under
    /// her marker, which the hide pass leaves alone, and the renderers of presents the mod had switched off are switched
    /// back on.
    /// <para>Not worn by her: an entry with no joint (the free suit), an entry whose object is skinned (the body suits
    /// prebaked under pre_root, bound to the game's joints), and the ids her data's "cannot_wear" lists. Their objects stay
    /// where the game put them, and the hide pass keeps them off. An entry that names pre_root but has a rigid object gets
    /// a node at her model's level, under her marker.</para>
    /// <para>The offsets come from cousins.json "fit" (her own data): one per game joint, plus one per present id where
    /// needed, the way the game fits presents per cousin with prefab variants. Copies of a dressed model (the Plaza's UI
    /// copy) carry her nodes and the presents under them; entries that still point at another model's nodes are pointed at
    /// the copy's own. Undressing gives every entry and present back to the game's joints.</para>
    /// </summary>
    internal static class Presents
    {
        private static readonly ModLog Log = LongLostCousinsMod.Log.In("Presents");
        private static readonly HashSet<string> Once = new(StringComparer.Ordinal);

        /// <summary>Her attach nodes are named this plus the game joint's name, plus <c>#id</c> for a present with its own offset.</summary>
        public const string AttPrefix = "llc_att_";

        /// <summary>True for one of the mod's attach nodes.</summary>
        public static bool IsNode(Transform t) => t != null && t.name.StartsWith(AttPrefix, StringComparison.Ordinal);

        /// <summary>The game joint's name an attach node stands for.</summary>
        public static string GameJointOf(string node)
        {
            string s = node.Substring(AttPrefix.Length);
            int cut = s.IndexOfAny(NameMarks);
            return cut >= 0 ? s.Substring(0, cut) : s;
        }

        /// <summary>True when <paramref name="t"/> is <paramref name="root"/> or below it.</summary>
        public static bool Under(Transform t, Transform root)
        {
            if (t == null || root == null) return false;
            IntPtr r = root.Pointer;
            for (int i = 0; t != null && i < 24; i++, t = t.parent)
                if (t.Pointer == r) return true;
            return false;
        }

        /// <summary>A renderer of a present she wears: it sits below one of her attach nodes in this model.</summary>
        public static bool IsWorn(DressedModel dm, Renderer r)
        {
            if (r == null || dm.Marker == null) return false;
            IntPtr m = dm.Marker.Pointer;
            bool node = false;
            for (var t = r.transform; t != null; t = t.parent)
            {
                if (t.Pointer == m) return node;
                if (IsNode(t)) node = true;
            }
            return false;
        }

        /// <summary>
        /// Points every present entry she can wear at her attach nodes and moves the present objects that exist onto
        /// them; switches on what the mod had switched off. Runs at dressing and in each half-second pass. Throws only on
        /// a broken model (the caller counts it).
        /// </summary>
        public static void Take(DressedModel dm)
        {
            var op = dm.Op;
            if (op == null || dm.Marker == null) return;
            int entries = 0;
            entries += TakeList(dm, op._datas);
            entries += TakeList(dm, op._stagePresentDatas);
            dm.PresentEntries = entries;
        }

        private static int TakeList(DressedModel dm, Il2CppSystem.Collections.Generic.List<PresentData> list)
        {
            if (list == null) return 0;
            int n = 0, count = list.Count;
            var fit = dm.Assets.Fit;
            for (int i = 0; i < count; i++)
            {
                var pd = list[i];
                if (pd == null) continue;
                var bone = pd._boneTransform;
                if (bone == null) continue;   // the game's own "no joint" entry (the free suit): never hers
                var obj = pd._presentGameObject;
                int id = pd._preID;
                // The skinned body suits hang on the model-level joint (pre_root). An entry there with no object yet is
                // left on the game's joint until its object exists and shows to be rigid, so a suit is never made under
                // her marker. This only times the move; whether an entry is hers is decided by its object below.
                if (obj == null && !IsNode(bone) && bone.parent != null && bone.parent.Pointer == dm.Model.Pointer) continue;
                bool skinned = obj != null && obj.GetComponentInChildren<SkinnedMeshRenderer>(true) != null;
                if (skinned || fit.CannotWear.Contains(id))
                {
                    // Left to the game, where the hide pass keeps it off; a copy's entry pointing at her nodes goes back.
                    if (IsNode(bone)) GiveBackEntry(dm, pd, bone);
                    continue;
                }
                var node = NodeFor(dm, bone, id, (int)pd._type);
                if (node == null) continue;
                if (bone.Pointer != node.Pointer) pd._boneTransform = node;
                n++;
                if (obj == null) continue;
                var t = obj.transform;
                if (t.parent == null || t.parent.Pointer != node.Pointer)
                {
                    t.SetParent(node, false);   // the game parents with false too: the prefab's own local pose stays
                    dm.PresentsMoved++;
                }
                Wear(dm, obj);
            }
            return n;
        }

        /// <summary>A present object on her node: its renderers are hers to keep on, on her body's layer.</summary>
        private static void Wear(DressedModel dm, GameObject obj)
        {
            int layer = dm.BodyLayer;
            SetLayer(obj.transform, layer);   // every object, not only the root: the game may re-layer parts on their own
            List<Renderer> fade = null;
            var rs = obj.GetComponentsInChildren<Renderer>(true);
            for (int k = 0; k < rs.Length; k++)
            {
                var r = rs[k];
                if (r == null) continue;
                bool first = dm.PresentRenderers.Add(r.Pointer);
                if (first)
                {
                    fade ??= FadeList(dm);
                    bool inFade = false;
                    if (fade != null) foreach (var f in fade) if (f.Pointer == r.Pointer) { inFade = true; break; }
                    dm.FadeAtTakeOver[r.Pointer] = inFade;
                }
                // The game never switches a present's renderer off; the mod's hide pass may have (before the present
                // was hers), and a copy carries what its source showed when it was made.
                if (!r.enabled && (dm.HidByMod(r) || (first && dm.Rebound))) r.enabled = true;
            }
        }

        /// <summary>The model's fade list (MainGameOuji.rendList), or null.</summary>
        private static List<Renderer> FadeList(DressedModel dm)
        {
            var mgo = dm.Root.GetComponentInChildren<MainGameOuji>(true);
            var l = mgo != null ? mgo.rendList : null;
            if (l == null) return null;
            var o = new List<Renderer>();
            for (int i = 0; i < l.Count; i++) if (l[i] != null) o.Add(l[i]);
            return o;
        }

        private static void SetLayer(Transform t, int layer)
        {
            var go = t.gameObject;
            if (go.layer != layer) go.layer = layer;
            int n = t.childCount;
            for (int i = 0; i < n; i++) SetLayer(t.GetChild(i), layer);
        }

        /// <summary>The game joint of this model a joint name stands for (pre_root directly under model), or null.</summary>
        private static Transform GameJoint(DressedModel dm, string name)
        {
            var direct = dm.Model.Find(name);
            return direct != null ? direct : U.FindDeep(dm.Model, name);
        }

        /// <summary>
        /// Her attach node for a present on <paramref name="bone"/> (a game joint, or one of her nodes in this model or
        /// in the model a copy was made from), made when missing. Null when no game joint of that name is in this model.
        /// </summary>
        private static Transform NodeFor(DressedModel dm, Transform bone, int id, int type)
        {
            string joint;
            Transform game;
            if (IsNode(bone))
            {
                if (Under(bone, dm.Marker) && bone.name == NodeName(GameJointOf(bone.name), id, type, dm)) return bone;
                joint = GameJointOf(bone.name);
                game = GameJoint(dm, joint);
            }
            else
            {
                joint = bone.name;
                game = Under(bone, dm.Model) ? bone : GameJoint(dm, joint);
            }
            if (game == null)
            {
                if (Once.Add("nojoint|" + joint)) Log.Warn($"{dm.Label}: a present hangs on {joint}, which this model does not have; left as it is");
                return null;
            }
            string name = NodeName(joint, id, type, dm);
            var parent = HerJointFor(dm, game, joint);
            var node = parent.Find(name);
            if (node != null) return node;

            var off = dm.Assets.Fit.OffsetFor(joint, id, type);
            var go = new GameObject(name);
            go.layer = dm.BodyLayer;
            node = go.transform;
            node.SetParent(parent, false);
            if (parent.Pointer == dm.Marker.Pointer && game.parent != null && game.parent.Pointer == dm.Model.Pointer)
            {
                // A model-level joint (pre_root): its own place under model, plus the offset.
                node.localPosition = U.Add(game.localPosition, U.V3(off[0], off[1], off[2]));
                node.localRotation = game.localRotation;
                node.localScale = game.localScale;
            }
            else
            {
                node.localPosition = U.V3(off[0], off[1], off[2]);
                node.localRotation = U.Identity;
                node.localScale = U.One;
            }
            return node;
        }

        /// <summary>
        /// The node's name: one per game joint, one per joint and present type that has its own offset (<c>@FACE</c>),
        /// and one per present id that has its own offset (<c>#120</c>).
        /// </summary>
        private static string NodeName(string joint, int id, int type, DressedModel dm)
        {
            var fit = dm.Assets.Fit;
            string s = AttPrefix + joint;
            if (fit.TypeOffsets.ContainsKey(type) && type >= 0 && type < Data.StyleFit.TypeNames.Length) s += "@" + Data.StyleFit.TypeNames[type];
            if (fit.PresentOffsets.ContainsKey(id)) s += "#" + id;
            return s;
        }

        private static readonly char[] NameMarks = { '@', '#' };

        /// <summary>Her joint of the game joint's name, else of its nearest parent she has, else her marker (model level).</summary>
        private static Transform HerJointFor(DressedModel dm, Transform game, string name)
        {
            var data = dm.Data;
            int j = data.JointIndex(name);
            for (var t = game.parent; j < 0 && t != null && t.Pointer != dm.Model.Pointer; t = t.parent) j = data.JointIndex(t.name);
            return j >= 0 && dm.HerJoints[j] != null ? dm.HerJoints[j] : dm.Marker;
        }

        // ---------------------------------------------------------------- giving them back

        /// <summary>
        /// Undressing: every entry pointing at one of her nodes points at the game's joint again, and every present object
        /// under her nodes goes back under it, before her marker is destroyed. Never throws.
        /// </summary>
        public static int GiveBack(DressedModel dm)
        {
            int n = 0;
            try
            {
                var op = dm.Op;
                if (op != null)
                {
                    n += GiveBackList(dm, op._datas);
                    n += GiveBackList(dm, op._stagePresentDatas);
                }
                // Objects no entry names (a copy's leftovers) go back too.
                if (dm.Marker != null)
                {
                    var nodes = new List<Transform>();
                    Collect(dm.Marker, nodes);
                    foreach (var node in nodes)
                    {
                        var game = GameJoint(dm, GameJointOf(node.name));
                        if (game == null) continue;
                        for (int c = node.childCount - 1; c >= 0; c--) { node.GetChild(c).SetParent(game, false); n++; }
                    }
                }
            }
            catch (Exception e) { Log.Warn($"{dm.Label}: giving the presents back: {e.GetType().Name}: {e.Message}"); }
            return n;
        }

        private static void Collect(Transform t, List<Transform> nodes)
        {
            int n = t.childCount;
            for (int i = 0; i < n; i++)
            {
                var c = t.GetChild(i);
                if (IsNode(c)) nodes.Add(c);
                else Collect(c, nodes);
            }
        }

        private static int GiveBackList(DressedModel dm, Il2CppSystem.Collections.Generic.List<PresentData> list)
        {
            if (list == null) return 0;
            int n = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var pd = list[i];
                var bone = pd != null ? pd._boneTransform : null;
                if (bone != null && IsNode(bone) && GiveBackEntry(dm, pd, bone)) n++;
            }
            return n;
        }

        private static bool GiveBackEntry(DressedModel dm, PresentData pd, Transform node)
        {
            var game = GameJoint(dm, GameJointOf(node.name));
            if (game == null) return false;
            pd._boneTransform = game;
            var obj = pd._presentGameObject;
            if (obj != null && obj.transform.parent != null && IsNode(obj.transform.parent)) obj.transform.SetParent(game, false);
            return true;
        }
    }
}
