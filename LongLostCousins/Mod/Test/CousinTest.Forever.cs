using System;
using System.Collections.Generic;
using System.Linq;
using LongLostCousins.Data;
using LongLostCousins.Dressing;
using UnityEngine;

namespace LongLostCousins.Test
{
    /// <summary>
    /// Slice three, stage 5: the word <c>forever</c> in cousintest.flag. The test then uses the Katamari Forever folder
    /// already in settings.json (without the word, the folder is treated as unset and the old style plays OUAK's
    /// motions everywhere, as before). On the ship and in the stage, with the old style: the copy was found and baked
    /// (clip count), her joints match the bake at the mapped time on at least two Forever states (world space), Forever's
    /// time runs at the expected pace, an unmapped state (0066) plays OUAK's motion with her waist raised, and the face
    /// card follows Forever's expression byte unless the game shows another face group. With the new style: Forever
    /// never plays. Pictures <c>forever_*</c>.
    /// </summary>
    internal static partial class CousinTest
    {
        private static bool _forever, _foreverLoaded;
        public static bool UsesForever => _forever;

        // A joint's world rotation and position against the bake: float rounding over her chain of at most 8 joints, as
        // Unity composes it, against the same composition in doubles (the world check of the copy uses 0.05 too).
        private const float BakeDegrees = 0.05f, BakeCm = 0.05f;
        // States the test plays itself when the game shows fewer than two Forever states on its own (requested rows with a clip).
        private static readonly string[] ForcedForever = { "0000", "0028", "0013", "0001", "0007", "0035" };
        private static readonly string[] FaceRows = { "0001", "0007", "0008", "0035", "0036", "0055", "0060", "0031" };
        private const string OuakOnlyState = "0066";

        /// <summary>Runs the steps; an exception is a FAIL (not just a log line) and ends them.</summary>
        private static IEnumerable<object> Fenced(string what, IEnumerable<object> steps)
        {
            using var e = steps.GetEnumerator();
            while (true)
            {
                bool more;
                try { more = e.MoveNext(); }
                catch (Exception ex) { Check($"{what} ran without an exception", false, $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); yield break; }
                if (!more) yield break;
                yield return e.Current;
            }
        }

        /// <summary>The Forever checks on one dressed model (the ship's walker or the stage player).</summary>
        private static IEnumerable<object> ForeverChecks(string where, DressedModel dm)
        {
            if (dm == null || dm.Root == null) { Check($"forever {where}: a dressed model to check", false, Why()); yield break; }
            if (_style != "old")
            {
                // The new style has no Forever map: it never plays a Forever clip.
                int frames = 0, forever = 0;
                for (float until = Time.unscaledTime + 2f; Time.unscaledTime < until && dm.Root != null; frames++)
                {
                    if (dm.Clip != null) forever++;
                    yield return 0f;
                }
                Check($"forever {where}: the new style never plays a Forever clip", frames > 0 && forever == 0, $"{forever} of {frames} frames on a Forever clip");
                yield break;
            }
            if (!_foreverLoaded)
            {
                yield return Until(() => ForeverMotions.Status != ForeverStatus.Reading, 60f, "Katamari Forever's motions to be read", false);
                _foreverLoaded = true;
                var rig = Catalog.Find(Cousin)?.ForeverOf("old");
                int want = rig != null ? rig.Clips().Count : -1;
                var set = ForeverMotions.SetFor(dm.Assets);
                Check("forever: the Katamari Forever copy in settings.json was found and baked", ForeverMotions.Status == ForeverStatus.Ready && set != null && set.Clips.Count == want && want > 0,
                      $"{ForeverMotions.Status}{(ForeverMotions.Problem != null ? " (" + ForeverMotions.Problem + ")" : "")}, {set?.Clips.Count ?? 0} clips baked of the map's {want}, " +
                      $"{set?.ByHash.Count ?? 0} states; read {ForeverMotions.ReadMs:0} ms, baked {ForeverMotions.BakeMs:0} ms off the main thread");
            }
            if (ForeverMotions.Status != ForeverStatus.Ready) yield break;
            if (dm.Anim == null) { Check($"forever {where}: the model's Animator was found", false, dm.Driver ?? "no driver"); yield break; }
            Msg($"forever {where}: driver {dm.Driver}, stage driver {dm.StageDriven}, OUAK's table {(dm.Rows != null ? dm.Rows.Name + $" ({dm.Rows.ByHash.Count} states)" : "not found")}");
            int before = dm.Anim.GetCurrentAnimatorStateInfo(0).m_Name;
            int faceBefore = dm.Face != null ? dm.Face.prevFaceIndex : -1;

            // ---- her joints against the bake, on at least two Forever states (the game's own first, then played by the test)
            var checkedStates = new List<string>();
            for (float until = Time.unscaledTime + 8f; Time.unscaledTime < until && checkedStates.Count < 2 && dm.Root != null;)
            {
                if (dm.Clip != null && !checkedStates.Contains(StateOf(dm)))
                {
                    checkedStates.Add(StateOf(dm));
                    CheckBakeMatch($"forever {where}, the game's own state {StateOf(dm)}", dm);
                    if (checkedStates.Count == 1) { Shot($"forever_{where}_{_style}"); yield return PictureTaken(); continue; }
                }
                yield return 0f;
            }
            foreach (string s in ForcedForever)
            {
                if (checkedStates.Count >= 2 || dm.Root == null) break;
                if (checkedStates.Contains(s)) continue;
                bool ok = false;
                foreach (var x in PlayState(dm, s, -1, r => ok = r)) yield return x;
                if (!ok) { Msg($"forever {where}: state {s} did not hold (the game played another), trying the next"); continue; }
                checkedStates.Add(s);
                CheckBakeMatch($"forever {where}, state {s} played by the test", dm);
            }
            Check($"forever {where}: her joints were checked against the bake on at least two Forever states", checkedStates.Count >= 2, string.Join(", ", checkedStates));

            // ---- Forever's time runs at the clip's pace (real time x Forever's speed x the game's pace)
            foreach (var x in Pace(where, dm)) yield return x;

            // ---- a state with no Forever clip: OUAK's motion with her waist raised
            {
                bool ok = false;
                foreach (var x in PlayState(dm, OuakOnlyState, -1, r => ok = r)) yield return x;
                if (!ok) Check($"forever {where}: the game holds state {OuakOnlyState} for the check", false, "another state kept playing");
                else CheckOuakRow($"forever {where}, state {OuakOnlyState} (no Forever clip)", dm);
                Shot($"forever_ouak_{where}_{_style}");
                yield return PictureTaken();
            }

            // ---- the face: Forever's expression on the row's own face group; the game's group otherwise
            foreach (var x in FaceOnForever(where, dm)) yield return x;

            // Back to the state and face the game had.
            if (dm.Root != null)
            {
                dm.Anim.Play(before, 0, 0f);
                if (dm.Face != null) { dm.Face.SetNoFace(); dm.Face.SetFace(faceBefore >= 0 ? faceBefore : 0); }
            }
            yield return 0.5f;
        }

        private static string StateOf(DressedModel dm)
        {
            var set = ForeverMotions.SetFor(dm.Assets);
            return set != null && set.StateNames.TryGetValue(dm.ClipState, out var n) ? n : "#" + dm.ClipState.ToString("X8");
        }

        /// <summary>
        /// Puts the game's Animator in <paramref name="state"/> (and the face group, when given) and returns in the first
        /// LateUpdate where the mod has posed her in it: Animator.Play acts at the next animation update, and a face set
        /// here reaches her card at the next dressed-model pass.
        /// </summary>
        private static IEnumerable<object> PlayState(DressedModel dm, string state, int face, Action<bool> done)
        {
            int h = Animator.StringToHash(state);
            int faceSetFrame = -1;
            bool asked = false;
            for (float until = Time.unscaledTime + 3f; Time.unscaledTime < until; )
            {
                if (dm.Root == null || dm.Anim == null) break;
                bool stateOk = dm.Anim.GetCurrentAnimatorStateInfo(0).m_Name == h;
                bool faceOk = face < 0 || (dm.Face != null && dm.Face.prevFaceIndex == face && Time.frameCount > faceSetFrame);
                if (asked && stateOk && faceOk) { done(true); yield break; }
                if (!stateOk) dm.Anim.Play(h, 0, 0.25f);
                if (face >= 0 && dm.Face != null && dm.Face.prevFaceIndex != face) { dm.Face.SetNoFace(); dm.Face.SetFace(face); faceSetFrame = Time.frameCount; }
                asked = true;
                yield return 0f;
            }
            done(false);
        }

        /// <summary>The row speed the test expects the stage drivers to have put into Animator.speed (1 elsewhere).</summary>
        private static float RowDivisor(DressedModel dm, int hash) =>
            dm.StageDriven && dm.Rows != null && dm.Rows.ByHash.TryGetValue(hash, out var r) && r.Speed > 1e-4f ? r.Speed : 1f;

        /// <summary>
        /// Her joints against the bake at the time the test works out from the Animator (world space, composed in doubles
        /// from the baked local values and her marker), and against OUAK's pose (they must differ: Forever plays, not a copy).
        /// </summary>
        private static void CheckBakeMatch(string what, DressedModel dm)
        {
            var clip = dm.Clip;
            if (clip == null) { Check($"{what}: a Forever clip plays", false, "the mod plays OUAK's motion on this state"); return; }
            var st = dm.Anim.GetCurrentAnimatorStateInfo(0);
            float speed = dm.Anim.speed;
            string timeNote;
            float x = dm.ClipFrame;
            if (st.m_Name != dm.ClipState) timeNote = "the Animator's state moved on since the mod's pass";
            else if (speed > 1e-4f)
            {
                double secs = st.m_NormalizedTime * (double)st.m_Length * speed * clip.Speed / RowDivisor(dm, st.m_Name);
                float tx = clip.FrameAt(secs);
                float dx = Math.Abs(tx - dm.ClipFrame);
                if (clip.Loops && clip.Span > 0) dx = Math.Min(dx, clip.Span - dx);
                timeNote = $"frame {U.F(dm.ClipFrame)} of {clip.Span} (the test's own {U.F(tx)}, nt {U.F(st.m_NormalizedTime)}, state length {U.F(st.m_Length)} s at Animator speed {U.F(speed)}, row speed divided out {U.F(RowDivisor(dm, st.m_Name))})";
                Check($"{what}: the mod's clip time is the Animator's mapped time", dx < 0.01f, timeNote);
            }
            else timeNote = "the Animator is paused";

            var data = dm.Data;
            int nj = data.Joints.Length;
            var mq = new double[nj * 4];
            var mp = new double[nj * 3];
            for (int j = 0; j < nj; j++)
            {
                clip.Sample(x, j, out float px, out float py, out float pz, out float qx, out float qy, out float qz, out float qw);
                int p = data.Joints[j].Parent;
                if (p < 0) { mq[j * 4] = qx; mq[j * 4 + 1] = qy; mq[j * 4 + 2] = qz; mq[j * 4 + 3] = qw; mp[j * 3] = px; mp[j * 3 + 1] = py; mp[j * 3 + 2] = pz; continue; }
                QMul(mq, p * 4, qx, qy, qz, qw, mq, j * 4);
                QRot(mq, p * 4, px, py, pz, out double rx, out double ry, out double rz);
                mp[j * 3] = mp[p * 3] + rx; mp[j * 3 + 1] = mp[p * 3 + 1] + ry; mp[j * 3 + 2] = mp[p * 3 + 2] + rz;
            }
            var m = dm.Marker.rotation;
            var mk = new double[] { m.x, m.y, m.z, m.w };
            float cm = Math.Abs(dm.Marker.lossyScale.y);
            if (cm <= 0f) cm = 1f;
            float worstA = 0f, worstP = 0f, ouak = 0f;
            string worstAName = "-", worstPName = "-", ouakName = "-";
            var w = new double[4];
            for (int j = 0; j < nj; j++)
            {
                var her = dm.HerJoints[j];
                QMul(mk, 0, mq[j * 4], mq[j * 4 + 1], mq[j * 4 + 2], mq[j * 4 + 3], w, 0);
                float a = U.Angle(her.rotation, U.Q((float)w[0], (float)w[1], (float)w[2], (float)w[3]));
                if (a > worstA) { worstA = a; worstAName = data.Joints[j].Name; }
                var wp = dm.Marker.TransformPoint(U.V3((float)mp[j * 3], (float)mp[j * 3 + 1], (float)mp[j * 3 + 2]));
                float d = U.Length(U.Sub(her.position, wp)) / cm;
                if (d > worstP) { worstP = d; worstPName = data.Joints[j].Name; }
                if (dm.GameJoints[j] != null)
                {
                    float o = U.Angle(her.rotation, dm.GameJoints[j].rotation);
                    if (o > ouak) { ouak = o; ouakName = data.Joints[j].Name; }
                }
            }
            Check($"{what}: her joints stand as the bake says (world)", worstA < BakeDegrees && worstP < BakeCm,
                  $"clip pack {clip.Pack} slot {clip.Slot}, {timeNote}; largest difference {U.F(worstA)} degrees ({worstAName}), {U.F(worstP)} cm ({worstPName}) over {nj} joints");
            Check($"{what}: her pose is Forever's, not a copy of the game's", ouak > 1f, $"largest difference from the game's joints {U.F(ouak)} degrees ({ouakName})");
        }

        /// <summary>Over about half a second on one looping Forever state, Forever's time advances by real time x its speed x the Animator's speed / the row speed.</summary>
        private static IEnumerable<object> Pace(string where, DressedModel dm)
        {
            for (int attempt = 0; attempt < 10 && dm.Root != null; attempt++)
            {
                if (dm.Clip == null || !dm.Clip.Loops)
                {
                    bool ok = false;
                    foreach (var x in PlayState(dm, attempt % 2 == 0 ? "0000" : "0028", -1, r => ok = r)) yield return x;
                    if (!ok || dm.Clip == null) continue;
                }
                int h = dm.ClipState;
                var clip = dm.Clip;
                double s0 = dm.ClipSeconds;
                float t0 = Time.unscaledTime;
                yield return 0.5f;
                if (dm.Root == null || dm.Clip != clip || dm.ClipState != h) continue;
                double ds = dm.ClipSeconds - s0;
                float dt = Time.unscaledTime - t0;
                double pace = clip.Speed * dm.Anim.speed / dm.RowDiv;
                if (ds <= 0 || dt <= 0.1f) continue;
                double ratio = ds / (dt * pace);
                Check($"forever {where}: Forever's time runs at the clip's pace on {StateOf(dm)}", Math.Abs(ratio - 1) < 0.05,
                      $"{ds:0.0000} s of clip in {dt:0.0000} s, expected pace {pace:0.000} (Forever speed {clip.Speed}, Animator {U.F(dm.Anim.speed)}, row {U.F(dm.RowDiv)}), ratio {ratio:0.0000}");
                yield break;
            }
            Check($"forever {where}: Forever's time runs at the clip's pace", false, "no looping Forever state held for half a second");
        }

        /// <summary>On a state with no Forever clip: today's copy (joints facing the game's, waist 7.415 cm up in model space) and her other joints back at rest offsets.</summary>
        private static void CheckOuakRow(string what, DressedModel dm)
        {
            Check($"{what}: the mod plays OUAK's motion", dm.Clip == null, dm.Clip != null ? $"a Forever clip plays (pack {dm.Clip.Pack} slot {dm.Clip.Slot})" : "no Forever clip");
            var data = dm.Data;
            float worst = 0f;
            string worstName = "-";
            int compared = 0;
            for (int i = 0; i < data.Joints.Length; i++)
            {
                if (dm.GameJoints[i] == null || !data.Joints[i].CopiesRotation) continue;
                compared++;
                float a = U.Angle(dm.HerJoints[i].rotation, dm.GameJoints[i].rotation);
                if (a > worst) { worst = a; worstName = data.Joints[i].Name; }
            }
            Check($"{what}: her joints face the way the game's do (world)", compared > 0 && worst < 0.05f, $"largest difference {U.F(worst)} degrees ({worstName}) over {compared} joints");
            int w = dm.Assets.FollowJoint >= 0 ? dm.Assets.FollowJoint : data.JointIndex("JNT_waist");
            if (w >= 0 && dm.GameJoints[w] != null && dm.GameJoints[w].parent != null)
            {
                // Where the copy puts it: the game's waist raised by the style's offset (7.415 cm up for the old style).
                var g = dm.GameJoints[w];
                var want = dm.Assets.FollowOffset;
                var expected = g.parent.TransformPoint(U.Add(g.localPosition, want));
                float cm = Math.Abs(dm.Model.lossyScale.y);
                if (cm <= 0f) cm = 1f;
                float off = U.Length(U.Sub(dm.HerJoints[w].position, expected)) / cm;
                var raised = U.Sub(dm.Marker.InverseTransformPoint(dm.HerJoints[w].position), dm.Model.InverseTransformPoint(g.position));
                Check($"{what}: her waist stands {U.F(want.y)} cm above the game's", off < 0.05f,
                      $"{U.F(off)} cm from where it belongs; hers is {U.F(raised)} cm from the game's in model space");
            }
            float far = 0f;
            string farName = "-";
            for (int i = 0; i < data.Joints.Length; i++)
            {
                var jt = data.Joints[i];
                if (jt.CopiesPosition) continue;
                var lp = dm.HerJoints[i].localPosition;
                float d = U.Length(U.Sub(lp, U.V3(jt.LocalPosition[0], jt.LocalPosition[1], jt.LocalPosition[2])));
                if (d > far) { far = d; farName = jt.Name; }
            }
            Check($"{what}: her other joints are back at their rest offsets", far < 0.001f, $"largest {U.F(far)} cm ({farName})");
        }

        /// <summary>The card on a Forever state: its expression's card with the row's own face group, the group's card with another group.</summary>
        private static IEnumerable<object> FaceOnForever(string where, DressedModel dm)
        {
            var face = dm.Data.Face;
            var set = ForeverMotions.SetFor(dm.Assets);
            if (dm.Face == null || dm.Rows == null || set == null) { Check($"forever {where}: a face component and OUAK's table for the face check", false, $"face {dm.Face != null}, table {dm.Rows != null}"); yield break; }
            string pick = null;
            int rowFace = -1, want = 0;
            ForeverClip clip = null;
            foreach (string s in FaceRows)
            {
                int h = Animator.StringToHash(s);
                if (!set.ByHash.TryGetValue(h, out var c) || !dm.Rows.ByHash.TryGetValue(h, out var row) || row.Face < 0 || row.Face >= face.Groups.Length) continue;
                int card = DressedModel.ExpressionCard(face, c.Expression);
                if (card == face.Groups[row.Face].Card || card == face.BlinkOverCard) continue;
                pick = s; rowFace = row.Face; want = card; clip = c;
                break;
            }
            if (pick == null) { Check($"forever {where}: a Forever state whose expression card differs from its group's card", false, "none of " + string.Join(", ", FaceRows)); yield break; }
            bool ok = false;
            foreach (var x in PlayState(dm, pick, rowFace, r => ok = r)) yield return x;
            if (!ok) { Check($"forever {where}: the game holds state {pick} with face group {rowFace} for the face check", false, "it did not hold"); yield break; }
            var on = ShownCards(dm);
            Check($"forever {where}: on {pick} with its own face group {rowFace}, the card of Forever's expression {clip.Expression} shows (card {want})",
                  dm.Clip == clip && on.Count == 1 && on[0] == want, $"shown: {(on.Count == 0 ? "none" : string.Join(", ", on))}; the group's own card {face.Groups[rowFace].Card}");
            Shot($"forever_face_{where}_{_style}");
            yield return PictureTaken();

            int other = new[] { 15, 9, 12, 10, 3 }.FirstOrDefault(g => g != rowFace && g < face.Groups.Length && face.Groups[g].Card != want && face.Groups[g].Card != face.BlinkOverCard && face.Groups[g].Card != 0);
            ok = false;
            foreach (var x in PlayState(dm, pick, other, r => ok = r)) yield return x;
            if (!ok) { Check($"forever {where}: the game holds state {pick} with face group {other}", false, "it did not hold"); yield break; }
            on = ShownCards(dm);
            Check($"forever {where}: on {pick} with another face group ({other}), the game's group wins (card {face.Groups[other].Card})",
                  on.Count == 1 && on[0] == face.Groups[other].Card, $"shown: {(on.Count == 0 ? "none" : string.Join(", ", on))}");
        }

        private static List<int> ShownCards(DressedModel dm) =>
            dm.Pieces.Where(p => p.Role == MeshRole.Card && p.Renderer.enabled).Select(p => p.Card).ToList();

        // ---------------------------------------------------------------- quaternions in doubles (x, y, z, w)

        /// <summary>o[oi..] = a[ai..] * (x, y, z, w) (Unity's order: the right one first).</summary>
        private static void QMul(double[] a, int ai, double x, double y, double z, double w, double[] o, int oi)
        {
            double ax = a[ai], ay = a[ai + 1], az = a[ai + 2], aw = a[ai + 3];
            o[oi] = aw * x + ax * w + ay * z - az * y;
            o[oi + 1] = aw * y - ax * z + ay * w + az * x;
            o[oi + 2] = aw * z + ax * y - ay * x + az * w;
            o[oi + 3] = aw * w - ax * x - ay * y - az * z;
        }

        /// <summary>Vector (x, y, z) turned by the quaternion a[ai..].</summary>
        private static void QRot(double[] a, int ai, double x, double y, double z, out double rx, out double ry, out double rz)
        {
            double qx = a[ai], qy = a[ai + 1], qz = a[ai + 2], qw = a[ai + 3];
            // v' = v + 2 w (q x v) + 2 q x (q x v)
            double cx = qy * z - qz * y, cy = qz * x - qx * z, cz = qx * y - qy * x;
            double dx = qy * cz - qz * cy, dy = qz * cx - qx * cz, dz = qx * cy - qy * cx;
            rx = x + 2 * (qw * cx + dx);
            ry = y + 2 * (qw * cy + dy);
            rz = z + 2 * (qw * cz + dz);
        }
    }
}
