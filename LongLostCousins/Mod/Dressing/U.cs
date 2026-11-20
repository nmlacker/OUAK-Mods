using System;
using System.Collections.Generic;
using UnityEngine;

namespace LongLostCousins.Dressing
{
    /// <summary>
    /// Small Unity helpers that write struct fields directly. The game's build has dropped many struct constructors and
    /// operators (Vector3, Quaternion, Matrix4x4), so the mod never calls them; Tools/audit_calls.ps1 checks that.
    /// </summary>
    internal static class U
    {
        public static Vector3 V3(float x, float y, float z) { Vector3 v = default; v.x = x; v.y = y; v.z = z; return v; }
        public static Vector2 V2(float x, float y) { Vector2 v = default; v.x = x; v.y = y; return v; }
        public static Quaternion Q(float x, float y, float z, float w) { Quaternion q = default; q.x = x; q.y = y; q.z = z; q.w = w; return q; }
        public static Vector3 One => V3(1f, 1f, 1f);
        public static Quaternion Identity => Q(0f, 0f, 0f, 1f);

        public static Vector3 Add(Vector3 a, Vector3 b) => V3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 Sub(Vector3 a, Vector3 b) => V3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static float Length(Vector3 a) => (float)Math.Sqrt(a.x * a.x + a.y * a.y + a.z * a.z);

        /// <summary>
        /// The angle between two rotations in degrees: 4 atan2(|a - b|, |a + b|) with b's sign matched to a's. Unlike
        /// acos of the dot product, it gives 0 (not a few hundredths of a degree of float noise) for equal rotations.
        /// </summary>
        public static float Angle(Quaternion a, Quaternion b)
        {
            double s = (double)a.x * b.x + (double)a.y * b.y + (double)a.z * b.z + (double)a.w * b.w < 0.0 ? -1.0 : 1.0;
            double dx = a.x - s * b.x, dy = a.y - s * b.y, dz = a.z - s * b.z, dw = a.w - s * b.w;
            double px = a.x + s * b.x, py = a.y + s * b.y, pz = a.z + s * b.z, pw = a.w + s * b.w;
            double diff = Math.Sqrt(dx * dx + dy * dy + dz * dz + dw * dw), sum = Math.Sqrt(px * px + py * py + pz * pz + pw * pw);
            return (float)(4.0 * Math.Atan2(diff, sum) * 180.0 / Math.PI);
        }

        /// <summary>The translation by (x, y, z): identity with m03, m13, m23 set.</summary>
        public static Matrix4x4 Translate(float x, float y, float z)
        {
            Matrix4x4 m = default;
            m.m00 = 1f; m.m11 = 1f; m.m22 = 1f; m.m33 = 1f;
            m.m03 = x; m.m13 = y; m.m23 = z;
            return m;
        }

        public static string F(float v) => v.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture);
        public static string F(Vector3 v) => $"({F(v.x)}, {F(v.y)}, {F(v.z)})";

        /// <summary>A descendant (or <paramref name="root"/> itself) with this exact name, breadth first; null if none.</summary>
        public static Transform FindDeep(Transform root, string name)
        {
            if (root == null) return null;
            var queue = new Queue<Transform>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var t = queue.Dequeue();
                if (t.name == name) return t;
                int n = t.childCount;
                for (int i = 0; i < n; i++) queue.Enqueue(t.GetChild(i));
            }
            return null;
        }

        /// <summary>The path of a transform from the scene root, for the log.</summary>
        public static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (int i = 0; t != null && i < 12; t = t.parent, i++) parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
