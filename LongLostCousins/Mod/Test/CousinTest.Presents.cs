using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppApp.KatamariSin;
using LongLostCousins.Dressing;
using OUAKModAPI;
using UnityEngine;

namespace LongLostCousins.Test
{
    /// <summary>
    /// Slice three, stages 3 and 4: the presents she wears and her place in the stage HUD. The test's save wears
    /// BODY, FACE and HEAD presents (the slot's own, read from _wearingPresent); the rigid ones must hang on her attach
    /// nodes, shown, on her body's layer and near her joints; the skinned body suits must stay hidden; her antenna must
    /// follow the game's; and in the stage HUD she must fit the panel the UICamera draws.
    /// </summary>
    internal static partial class CousinTest
    {
        /// <summary>How far (cm) a present may stand from her joint beyond its own offset and the prefab's local place.</summary>
        private const float PresentSlackCm = 1f;
        /// <summary>How far (cm) an attach node may stand from where her joint and the fit put it.</summary>
        private const float NodeSlackCm = 0.5f;
        /// <summary>The share of the HUD panel kept free on each side.</summary>
        private const float HudMargin = 0.005f;

        private sealed class WornEntry
        {
            public PresentData Pd;
            public string List;
            public GameObject Obj;
            public int Id;
            public bool Skinned;
        }

        /// <summary>The entries of both present lists whose object exists now.</summary>
        private static List<WornEntry> WornEntries(OujiPresent op)
        {
            var o = new List<WornEntry>();
            if (op == null) return o;
            void Add(Il2CppSystem.Collections.Generic.List<PresentData> list, string name)
            {
                if (list == null) return;
                for (int i = 0; i < list.Count; i++)
                {
                    var pd = list[i];
                    var obj = pd != null ? pd._presentGameObject : null;
                    if (obj == null) continue;
                    o.Add(new WornEntry { Pd = pd, List = name, Obj = obj, Id = pd._preID, Skinned = obj.GetComponentInChildren<SkinnedMeshRenderer>(true) != null });
                }
            }
            Add(op._datas, "_datas");
            Add(op._stagePresentDatas, "_stagePresentDatas");
            return o;
        }

        private static readonly string[] SlotNames = { "BODY", "FACE", "HEAD" };

        /// <summary>
        /// Every present the save wears, on one dressed model: waits (up to 10 s) for the worn FACE and HEAD objects, then
        /// checks each rigid one and each skinned suit. <paramref name="fade"/>: also compare the fade list (the stage player).
        /// </summary>
        private static IEnumerable<object> CheckPresents(string name, DressedModel dm, bool fade)
        {
            if (dm == null || dm.Root == null) { Check($"{name}: presents: the model is there", false); yield break; }
            var worn = Wearing();
            Msg($"{name}: presents: the save wears [{string.Join(", ", worn)}] (BODY, FACE, HEAD); {Dresser_PresentsText(dm)}");
            if (dm.Op == null) { Check($"{name}: presents: the model has an OujiPresent the mod reads", false, "no OujiPresent"); yield break; }

            // FACE and HEAD are rigid in the test save (and probe_results.md): their objects must come.
            var wanted = new List<int>();
            for (int s = 1; s < worn.Length && s < 3; s++) if (worn[s] != 0) wanted.Add(worn[s]);
            yield return Until(() => dm.Root == null || dm.Op == null || wanted.All(id => WornEntries(dm.Op).Any(e => e.Id == id)),
                               10f, $"the worn FACE and HEAD presents on the {name}", false);
            if (dm.Root == null || dm.Op == null) { Check($"{name}: presents: the model is still there", false); yield break; }
            yield return 0.6f;   // one more present pass after the last object came

            var entries = WornEntries(dm.Op);
            var missing = wanted.Where(id => !entries.Any(e => e.Id == id)).ToList();
            Check($"{name}: presents: the worn FACE and HEAD objects exist", wanted.Count > 0 && missing.Count == 0,
                  wanted.Count == 0 ? "the save wears no FACE or HEAD present (the test needs one)" :
                  $"{entries.Count} present object(s) on the model ({string.Join(", ", entries.Select(e => $"{e.List} {e.Id}{(e.Skinned ? " skinned" : "")}"))}); missing [{string.Join(", ", missing)}]");
            if (worn.Length > 0 && worn[0] != 0 && !entries.Any(e => e.Id == worn[0]))
                Msg($"{name}: presents: the worn BODY {worn[0]} has no object on this model now");

            var mgo = dm.Root.GetComponentInChildren<MainGameOuji>(true);
            var fadeNow = new HashSet<IntPtr>();
            if (fade)
            {
                var l = mgo != null ? mgo.rendList : null;
                if (l != null) for (int i = 0; i < l.Count; i++) if (l[i] != null) fadeNow.Add(l[i].Pointer);
            }

            int rigid = 0;
            foreach (var e in entries)
            {
                string what = $"{name}: present {e.Id} ({e.Obj.name}, {e.List})";
                if (e.Skinned) { CheckSuit(what, dm, e); continue; }
                if (dm.Assets.Fit.CannotWear.Contains(e.Id)) { Msg($"{what}: on her cannot_wear list, left to the game"); continue; }
                rigid++;
                CheckRigid(what, dm, e, fade ? fadeNow : null, mgo != null);
            }
            Check($"{name}: presents: she wears at least one rigid present", rigid > 0, $"{rigid} rigid present object(s) checked");
        }

        private static string Dresser_PresentsText(DressedModel dm) =>
            $"{dm.PresentEntries} present entries on her nodes, {dm.PresentsMoved} object(s) moved, {dm.PresentRenderers.Count} present renderer(s) hers";

        private static void CheckRigid(string what, DressedModel dm, WornEntry e, HashSet<IntPtr> fadeNow, bool hasFadeList)
        {
            var t = e.Obj.transform;
            var node = t.parent;
            bool onNode = node != null && Presents.IsNode(node) && Presents.Under(node, dm.Marker);
            var bone = e.Pd._boneTransform;
            bool entryOnNode = bone != null && node != null && bone.Pointer == node.Pointer;
            Check($"{what}: hangs on her attach node, and its entry names that node", onNode && entryOnNode,
                  $"parent {(node != null ? U.PathOf(node) : "none")}, entry's joint {(bone != null ? U.PathOf(bone) : "none")}");

            bool rootActive = dm.Root.activeInHierarchy;
            bool active = rootActive ? e.Obj.activeInHierarchy : e.Obj.activeSelf;
            var rs = e.Obj.GetComponentsInChildren<Renderer>(true).Where(r => r != null).ToList();
            int on = rs.Count(r => r.enabled);
            // A stage costume (_stagePresentDatas: race, snorkel, space, camp) is made by the game on every stage but kept
            // switched off outside its own stage; the mod never calls SetActive, so only its renderers are checked.
            bool stageCostume = e.List == "_stagePresentDatas";
            if (stageCostume)
            {
                Msg($"{what}: a stage costume, active {active} as the game set it");
                Check($"{what}: its renderers are on", rs.Count > 0 && on == rs.Count, $"{on} of {rs.Count} renderer(s) on");
            }
            else
                Check($"{what}: is active with its renderers on", active && rs.Count > 0 && on == rs.Count,
                      $"active {active}{(rootActive ? "" : " (the model is inactive now; its own switch checked)")}, {on} of {rs.Count} renderer(s) on");

            int layer = dm.BodyLayer;
            int offLayer = e.Obj.GetComponentsInChildren<Transform>(true).Count(x => x.gameObject.layer != layer);
            Check($"{what}: is on her body's layer", offLayer == 0, $"layer {e.Obj.layer}, her body's {layer}, {offLayer} object(s) on another");

            if (onNode)
            {
                string joint = Presents.GameJointOf(node.name);
                var off = dm.Assets.Fit.OffsetFor(joint, e.Id);
                var offV = U.V3(off[0], off[1], off[2]);
                float cm = Math.Abs(dm.Marker.lossyScale.y);
                if (cm <= 0f) cm = 1f;
                int j = dm.Data.JointIndex(joint);
                var her = j >= 0 ? dm.HerJoints[j] : null;
                if (her != null)
                {
                    float nodeOff = U.Length(U.Sub(node.position, her.TransformPoint(offV))) / cm;
                    float dist = U.Length(U.Sub(t.position, her.position)) / cm;
                    float allowed = U.Length(offV) + U.Length(t.localPosition) + PresentSlackCm;
                    Check($"{what}: stands by her {joint}", nodeOff < NodeSlackCm && dist <= allowed,
                          $"{U.F(dist)} cm from her joint (allowed {U.F(allowed)}: offset {U.F(offV)}, the prefab's own place {U.F(t.localPosition)}), node {U.F(nodeOff)} cm from where it belongs");
                }
                else
                {
                    // A joint she lacks: her nearest parent joint or (model level, pre_root) her marker holds the node.
                    var game = U.FindDeep(dm.Model, joint);
                    if (game != null && game.parent != null && game.parent.Pointer == dm.Model.Pointer)
                    {
                        var want = dm.Marker.TransformPoint(U.Add(game.localPosition, offV));
                        float nodeOff = U.Length(U.Sub(node.position, want)) / cm;
                        Check($"{what}: stands at her model-level {joint}", nodeOff < NodeSlackCm, $"node {U.F(nodeOff)} cm from where it belongs");
                    }
                    else Msg($"{what}: hangs on {joint}, which she has no joint of; its node sits under {U.PathOf(node.parent)} (place not checked)");
                }
            }

            if (fadeNow != null)
            {
                if (!hasFadeList) { Check($"{what}: the fade list holds it when the game's did", false, "no MainGameOuji on the model"); return; }
                int same = 0, known = 0;
                var diff = new List<string>();
                foreach (var r in rs)
                {
                    if (!dm.FadeAtTakeOver.TryGetValue(r.Pointer, out bool was)) { diff.Add(r.name + " never seen by the mod"); continue; }
                    known++;
                    bool now = fadeNow.Contains(r.Pointer);
                    if (now == was) same++; else diff.Add($"{r.name} in the list {now}, the game had it {was}");
                }
                Check($"{what}: the fade list holds it exactly when the game's own present was in it", rs.Count > 0 && same == rs.Count,
                      $"{same} of {rs.Count} renderer(s) as the game had them ({known} seen at take-over){(diff.Count > 0 ? ": " + string.Join("; ", diff) : "")}");
            }
        }

        private static void CheckSuit(string what, DressedModel dm, WornEntry e)
        {
            var rs = e.Obj.GetComponentsInChildren<Renderer>(true).Where(r => r != null).ToList();
            int on = rs.Count(r => r.enabled);
            bool worn = rs.Any(r => Presents.IsWorn(dm, r));
            var bone = e.Pd._boneTransform;
            bool entryOnNode = bone != null && Presents.IsNode(bone);
            Check($"{what}: a skinned suit, stays hidden on the game's joints", on == 0 && !worn && !entryOnNode,
                  $"{on} of {rs.Count} renderer(s) on, under her nodes {worn}, entry's joint {(bone != null ? bone.name : "none")}");
        }

        /// <summary>
        /// The walker only: the game's AntennaEnabled flips its antenna object; hers must follow both ways. The game's state
        /// is put back.
        /// </summary>
        private static IEnumerable<object> AntennaFlip(DressedModel dm)
        {
            if (dm == null || dm.Op == null) { Check("antenna: the walker has an OujiPresent", false); yield break; }
            var a = dm.Op._antennaObject;
            if (dm.Antenna == null)
            {
                Msg($"antenna: the {_style} style has no antenna piece of its own (antena_m); nothing to follow{(a == null ? ", and the game's model has no antenna object" : "")}");
                yield break;
            }
            if (a == null) { Check("antenna: the game's model has an antenna object", false, "OujiPresent._antennaObject is null"); yield break; }
            bool was = a.activeSelf;
            Check("antenna: hers shows exactly when the game's does (as found)", dm.Antenna.Renderer.enabled == was,
                  $"the game's antenna active {was}, hers on {dm.Antenna.Renderer.enabled}");
            Msg($"[direct] OujiPresent.AntennaEnabled({!was})");
            dm.Op.AntennaEnabled(!was);
            yield return 0.2f;
            if (dm.Root == null) { Check("antenna: the walker is still there", false); yield break; }
            Check($"antenna: hers follows the game's switched {(was ? "off" : "on")}", a.activeSelf == !was && dm.Antenna.Renderer.enabled == !was,
                  $"the game's active {a.activeSelf}, hers on {dm.Antenna.Renderer.enabled}");
            Shot($"present_antenna_{(was ? "off" : "on")}_{_style}");
            yield return PictureTaken();
            Msg($"[direct] OujiPresent.AntennaEnabled({was}) (back as it was)");
            dm.Op.AntennaEnabled(was);
            yield return 0.2f;
            if (dm.Root == null) yield break;
            Check($"antenna: hers follows the game's switched back {(was ? "on" : "off")}", a.activeSelf == was && dm.Antenna.Renderer.enabled == was,
                  $"the game's active {a.activeSelf}, hers on {dm.Antenna.Renderer.enabled}");
        }

        /// <summary>
        /// The stage HUD copy against the panel: the UICamera (the HUD layer's camera that draws to the screen) is the panel,
        /// as the probe found (no RawImage shows OujiRender). Her renderers and the presents she wears must sit inside it with
        /// a margin; she must be no wider than the hidden Prince (the fit's hud_scale); her marker must carry the fit.
        /// </summary>
        private static void CheckHud(MainGameManager main, DressedModel hd)
        {
            if (hd == null) { Check("HUD: the HUD copy is dressed", false); return; }
            var fit = hd.Assets.Fit;
            var ms = hd.Marker.localScale;
            var mp = hd.Marker.localPosition;
            var wantP = U.V3(fit.HudOffset[0], fit.HudOffset[1], fit.HudOffset[2]);
            Check("HUD: her marker carries the style's HUD scale and offset", Math.Abs(ms.x - fit.HudScale) < 0.0001f && Math.Abs(ms.y - fit.HudScale) < 0.0001f &&
                  Math.Abs(ms.z - fit.HudScale) < 0.0001f && U.Length(U.Sub(mp, wantP)) < 0.0001f,
                  $"scale {U.F(ms)}, position {U.F(mp)}; the fit's hud_scale {U.F(fit.HudScale)}, hud_offset {U.F(wantP)}");

            int layer = HudLayer(hd, main.UIOuji);
            var cams = UnityEngine.Object.FindObjectsOfType<Camera>()
                .Where(c => c.enabled && c.gameObject.activeInHierarchy && (c.cullingMask & (1 << layer)) != 0).ToList();
            var screen = cams.FirstOrDefault(c => c.targetTexture == null);
            Check("HUD: a camera draws the HUD layer to the screen", screen != null,
                  $"layer {layer}; cameras that draw it: {(cams.Count == 0 ? "none" : string.Join(", ", cams.Select(c => c.name + (c.targetTexture != null ? " (to " + c.targetTexture.name + ")" : " (to the screen)"))))}");
            if (screen == null) return;

            VBox her = default, all = default, baseR = default;
            foreach (var p in hd.Pieces)
                if (p.Renderer.enabled && p.Go.activeInHierarchy) { AddBounds(ref her, screen, p.Renderer.bounds); AddBounds(ref all, screen, p.Renderer.bounds); }
            int presents = 0;
            foreach (var r in hd.Root.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || hd.Own.Contains(r.Pointer)) continue;
                if (Presents.IsWorn(hd, r))
                {
                    if (r.enabled && r.gameObject.activeInHierarchy) { AddBounds(ref all, screen, r.bounds); presents++; }
                }
                else if (r.gameObject.activeInHierarchy) AddBounds(ref baseR, screen, r.bounds);
            }
            bool inside = all.N > 0 && all.X0 >= HudMargin && all.X1 <= 1f - HudMargin && all.Y0 >= HudMargin && all.Y1 <= 1f - HudMargin;
            Check("HUD: she and the presents she wears sit inside the panel", inside,
                  $"{BoxText(all, screen)} ({presents} present renderer(s) in it); the panel is the {screen.name} viewport 0..1, kept {U.F(HudMargin)} from each side");
            // Logged, not checked: the Prince's renderers are switched off, so Unity may keep old bounds for them.
            float herW = her.X1 - her.X0, baseW = baseR.X1 - baseR.X0;
            Msg($"HUD: her width {herW * screen.pixelWidth:0} px ({BoxText(her, screen)}) against the hidden Prince's {baseW * screen.pixelWidth:0} px " +
                $"({BoxText(baseR, screen)}; switched off, so maybe old bounds); hud_scale {U.F(fit.HudScale)}");
            foreach (var c in cams)
            {
                if (c.targetTexture == null) continue;
                VBox rt = default;
                foreach (var p in hd.Pieces) if (p.Renderer.enabled && p.Go.activeInHierarchy) AddBounds(ref rt, c, p.Renderer.bounds);
                Msg($"HUD: in {c.name}'s picture ({c.targetTexture.name}, shown by no panel): {BoxText(rt, c)}");
            }
        }

        /// <summary>The HUD pictures: the screen (hud_) and every render texture that draws the HUD layer (hud_rt_).</summary>
        private static IEnumerable<object> HudPictures(MainGameManager main, DressedModel hd)
        {
            Shot("hud_" + _style);
            yield return PictureTaken();
            int layer = HudLayer(hd, main.UIOuji);
            int i = 0;
            foreach (var c in UnityEngine.Object.FindObjectsOfType<Camera>())
            {
                if (!c.enabled || !c.gameObject.activeInHierarchy || (c.cullingMask & (1 << layer)) == 0 || c.targetTexture == null) continue;
                string n = i == 0 ? "hud_rt_" + _style : $"hud_rt_{_style}_{i}";
                i++;
                string saved;
                try { saved = FrameCapture.SaveTexture(c.targetTexture, n, Shots); }
                catch (Exception e) { saved = "failed: " + e.Message; }
                Msg($"picture {n} ({c.name} -> {c.targetTexture.name}): {saved}");
            }
        }
    }
}
