using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace LongLostCousins.Data
{
    /// <summary>
    /// What a cousin style ships to play Katamari Forever's motions (cousins.json "forever"): which Forever node drives
    /// each of her joints, the scale and axes from Forever's units to hers, and which Forever clip (pack, slot) plays for
    /// each of OUAK's Animator states. Indices and numbers only: every curve, length, speed, loop and face byte is read
    /// from the player's own copy of Forever at run time.
    /// </summary>
    public sealed class ForeverRig
    {
        /// <summary>Her centimetres per Forever unit.</summary>
        public float Scale;
        /// <summary>Forever's axes in hers: her axis i is Axes[i] times Forever's axis i.</summary>
        public float[] Axes = { 1f, 1f, 1f };
        /// <summary>Her joint name to the Forever node that drives it. Joints not listed keep their rest pose under their parents.</summary>
        public Dictionary<string, int> Joints = new(StringComparer.Ordinal);
        /// <summary>OUAK Animator state name to the Forever clip (pack 1 or 2, slot) played while the game is in that state.</summary>
        public Dictionary<string, (int Pack, int Slot)> States = new(StringComparer.Ordinal);

        /// <summary>The clips the states name, each once.</summary>
        public List<(int Pack, int Slot)> Clips()
        {
            var seen = new HashSet<(int, int)>();
            var list = new List<(int, int)>();
            foreach (var c in States.Values) if (seen.Add(c)) list.Add(c);
            return list;
        }
    }

    /// <summary>How a Forever folder checked out. The folder itself never appears in any of these words.</summary>
    public enum ForeverFind { Found, NotFound, WrongFiles }

    /// <summary>The four entries read from the player's Forever archive, inflated: the motion table, the Prince's model and his two motion packs.</summary>
    public sealed class ForeverFiles
    {
        public byte[] Sys, Model, PackA, PackB;
        public byte[] Pack(int pack) => pack == 1 ? PackA : pack == 2 ? PackB : null;
    }

    /// <summary>
    /// Reads the four entries this mod needs from the player's Katamari Forever disc folder (the PS3 disc's
    /// <c>PS3_GAME/USRDIR</c>, which holds the archive's table and data files). Each entry must match the SHA-256 of the
    /// copy the mod's map was made for. No Unity or game types here (the offline check runs it outside the game).
    /// </summary>
    public static class ForeverArchive
    {
        public const string TocName = "kdp_ps3_os.TOC", DatName = "kdp_ps3_os.DAT";
        private static readonly string[] Names = { "/OUJI/OUJI_SYS.BIN", "/OUJI/OUJI_00/OUJI.MDL", "/OUJI/OUJI_00/OUJI_A.MOT", "/OUJI/OUJI_00/OUJI_B.MOT" };
        private static readonly string[] Words = { "the motion table", "the Prince's model", "motion pack 1", "motion pack 2" };
        private static readonly string[] Sha256 =
        {
            "d523a567c348b3c9045043c26ce6e64c6322c5664029de7571eecf65cfad4729",
            "75e8e5b1d4a670c88ceedfd2d648003ad0143e0a2a0b9a45faa53ced986c1c96",
            "6e29659beb45ddaf73c522b053d51d6cd7ea24a0f0cd21b0616ace2214757be4",
            "0d8ac3c4c9141f7a6b9c0dd170c06042376dc820deb1f8eb9b049c63d76c2dca",
        };
        private const int MaxEntry = 16 << 20;

        /// <summary>The folder that holds the archive: the one given, its USRDIR, or its PS3_GAME/USRDIR; null when none does.</summary>
        public static string ArchiveFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return null;
            folder = folder.Trim().Trim('"');
            foreach (var d in new[] { folder, Path.Combine(folder, "USRDIR"), Path.Combine(folder, "PS3_GAME", "USRDIR") })
                if (File.Exists(Path.Combine(d, TocName)) && File.Exists(Path.Combine(d, DatName))) return d;
            return null;
        }

        /// <summary>
        /// Reads and checks the four entries. <paramref name="why"/> says what failed in words that never hold the folder.
        /// Never throws: an unreadable file is <see cref="ForeverFind.WrongFiles"/> with the exception's type.
        /// </summary>
        public static ForeverFind Read(string folder, out ForeverFiles files, out string why)
        {
            files = null;
            why = null;
            string dir;
            try { dir = ArchiveFolder(folder); }
            catch (Exception e) { why = "the folder could not be looked in (" + e.GetType().Name + ")"; return ForeverFind.NotFound; }
            if (dir == null) { why = "no Katamari Forever archive in the folder"; return ForeverFind.NotFound; }
            try
            {
                var toc = File.ReadAllBytes(Path.Combine(dir, TocName));
                if (toc.Length < 8 || toc[0] != 'B' || toc[1] != 'L' || toc[2] != 'D' || toc[3] != 'h') { why = "the archive's table has no BLDh mark"; return ForeverFind.WrongFiles; }
                int def = -1, defLen = 0, inf = -1, infLen = 0, tbl = -1, tblLen = 0;
                for (int p = 8; p + 8 <= toc.Length;)
                {
                    int len = (int)LeU32(toc, p + 4);
                    if (len < 0 || p + 8 + len > toc.Length) { why = "the archive's table is cut short"; return ForeverFind.WrongFiles; }
                    string tag = Encoding.ASCII.GetString(toc, p, 4);
                    if (tag == "def ") { def = p + 8; defLen = len; }
                    else if (tag == "inf ") { inf = p + 8; infLen = len; }
                    else if (tag == "tbl ") { tbl = p + 8; tblLen = len; }
                    p += 8 + len;
                }
                if (def < 0 || defLen < 20 || inf < 0 || tbl < 0) { why = "the archive's table lacks a chunk"; return ForeverFind.WrongFiles; }
                int count = (int)LeU32(toc, def + 4);
                long sector = LeU32(toc, def + 16);
                if (count <= 0 || (long)count * 40 > infLen || sector <= 0) { why = "the archive's table has no file list"; return ForeverFind.WrongFiles; }

                var index = new int[Names.Length];
                for (int k = 0; k < index.Length; k++) index[k] = -1;
                var wanted = new byte[Names.Length][];
                for (int k = 0; k < Names.Length; k++) wanted[k] = Encoding.ASCII.GetBytes(Names[k]);
                for (int i = 0, q = tbl, end = tbl + tblLen; i < count && q < end; i++)
                {
                    int z = q;
                    while (z < end && toc[z] != 0) z++;
                    for (int k = 0; k < wanted.Length; k++)
                        if (index[k] < 0 && z - q == wanted[k].Length && Same(toc, q, wanted[k])) index[k] = i;
                    q = z + 1;
                }
                for (int k = 0; k < index.Length; k++)
                    if (index[k] < 0) { why = $"the archive has no {Words[k]}"; return ForeverFind.WrongFiles; }

                var bodies = new byte[Names.Length][];
                using (var dat = new FileStream(Path.Combine(dir, DatName), FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    for (int k = 0; k < Names.Length; k++)
                    {
                        int e = inf + 40 * index[k];
                        long at = LeU32(toc, e) * sector;
                        long stored = LeU32(toc, e + 4), inflated = LeU32(toc, e + 8);
                        if (stored <= 0 || stored > MaxEntry || inflated > MaxEntry || at + stored > dat.Length) { why = $"{Words[k]} lies outside the archive"; return ForeverFind.WrongFiles; }
                        var blob = new byte[stored];
                        dat.Position = at;
                        int got = 0;
                        while (got < blob.Length)
                        {
                            int n = dat.Read(blob, got, blob.Length - got);
                            if (n <= 0) break;
                            got += n;
                        }
                        if (got != blob.Length) { why = $"{Words[k]} could not be read in full"; return ForeverFind.WrongFiles; }
                        var body = inflated == 0 ? blob : Inflate(blob, (int)inflated);
                        if (body == null) { why = $"{Words[k]} could not be inflated"; return ForeverFind.WrongFiles; }
                        if (Hex(SHA256.HashData(body)) != Sha256[k]) { why = $"{Words[k]} is not the one the mod's map was made for"; return ForeverFind.WrongFiles; }
                        bodies[k] = body;
                    }
                }
                files = new ForeverFiles { Sys = bodies[0], Model = bodies[1], PackA = bodies[2], PackB = bodies[3] };
                return ForeverFind.Found;
            }
            catch (Exception e)
            {
                // .NET's file errors name the path: only the type is kept.
                why = "the archive could not be read (" + e.GetType().Name + ")";
                return ForeverFind.WrongFiles;
            }
        }

        /// <summary>A GARC container: chunks zlib, inf, dat; the dat payload is blocks of (u32 size, u32 0, zlib stream), 4-byte aligned, ended by size 0.</summary>
        private static byte[] Inflate(byte[] blob, int expected)
        {
            if (blob.Length < 8 || blob[0] != 'G' || blob[1] != 'A' || blob[2] != 'R' || blob[3] != 'C') return null;
            int data = -1, dataLen = 0;
            for (int q = 8; q + 8 <= blob.Length;)
            {
                string tag = Encoding.ASCII.GetString(blob, q, 4);
                int len = (int)LeU32(blob, q + 4);
                if (tag == "zlib") { q += 8; continue; }
                if (tag == "inf ") { q += 8 + len; continue; }
                if (tag == "dat ") { data = q + 8; dataLen = len; break; }
                return null;
            }
            if (data < 0 || dataLen < 0 || data + dataLen > blob.Length) return null;
            using var outStream = new MemoryStream(expected);
            int r = 0;
            while (r + 8 <= dataLen)
            {
                int cs = (int)LeU32(blob, data + r);
                if (cs == 0) break;
                if (cs < 0 || r + 8 + cs > dataLen) return null;
                using (var src = new MemoryStream(blob, data + r + 8, cs, false))
                using (var z = new ZLibStream(src, CompressionMode.Decompress))
                    z.CopyTo(outStream);
                r = (r + 8 + cs + 3) & ~3;
                if (outStream.Length > MaxEntry) return null;
            }
            var res = outStream.ToArray();
            return res.Length == expected ? res : null;
        }

        private static bool Same(byte[] b, int at, byte[] want)
        {
            for (int i = 0; i < want.Length; i++) if (b[at + i] != want[i]) return false;
            return true;
        }

        internal static uint LeU32(byte[] b, int o) => (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24);

        private static string Hex(byte[] h)
        {
            var sb = new StringBuilder(h.Length * 2);
            foreach (byte x in h) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }
    }

    /// <summary>
    /// One Forever clip baked onto a cousin's rig: per frame, the local position and rotation of each of her joints, in
    /// her order. Built in memory at each start from the player's copy; never written anywhere.
    /// </summary>
    public sealed class ForeverClip
    {
        public int Pack, Slot;
        /// <summary>Frames start..end of the clip, end included (Frames = end - start + 1).</summary>
        public int Frames;
        public float Fps;
        /// <summary>The motion table's speed, loop byte and face expression for this slot.</summary>
        public float Speed;
        public bool Loops;
        public int Expression;
        public int Joints;
        /// <summary>frames x joints x (px, py, pz, qx, qy, qz, qw): her joints' local values.</summary>
        public float[] Pose;

        /// <summary>The last frame index (the clip's length in frames).</summary>
        public int Span => Frames - 1;

        /// <summary>The frame position for <paramref name="seconds"/> of play: wrapped when the clip loops, else held at the ends.</summary>
        public float FrameAt(double seconds)
        {
            double x = seconds * Fps;
            int span = Span;
            if (span <= 0 || double.IsNaN(x)) return 0f;
            if (Loops)
            {
                x %= span;
                if (x < 0) x += span;
            }
            else if (x < 0) x = 0;
            else if (x > span) x = span;
            return (float)x;
        }

        /// <summary>Joint <paramref name="j"/>'s local value at frame position <paramref name="x"/>: positions lerped, rotations nlerped with the sign matched.</summary>
        public void Sample(float x, int j, out float px, out float py, out float pz, out float qx, out float qy, out float qz, out float qw)
        {
            int span = Span;
            if (!(x > 0f)) x = 0f;
            if (x > span) x = span;
            int i0 = (int)x;
            if (i0 > span) i0 = span;
            int i1 = i0 < span ? i0 + 1 : i0;
            float f = x - i0;
            int a = (i0 * Joints + j) * 7, b = (i1 * Joints + j) * 7;
            var p = Pose;
            px = p[a] + (p[b] - p[a]) * f;
            py = p[a + 1] + (p[b + 1] - p[a + 1]) * f;
            pz = p[a + 2] + (p[b + 2] - p[a + 2]) * f;
            float bx = p[b + 3], by = p[b + 4], bz = p[b + 5], bw = p[b + 6];
            float ax = p[a + 3], ay = p[a + 4], az = p[a + 5], aw = p[a + 6];
            if (ax * bx + ay * by + az * bz + aw * bw < 0f) { bx = -bx; by = -by; bz = -bz; bw = -bw; }
            qx = ax + (bx - ax) * f;
            qy = ay + (by - ay) * f;
            qz = az + (bz - az) * f;
            qw = aw + (bw - aw) * f;
            float n = (float)Math.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
            if (n > 1e-8f) { qx /= n; qy /= n; qz /= n; qw /= n; }
            else { qx = 0f; qy = 0f; qz = 0f; qw = 1f; }
        }
    }

    /// <summary>
    /// Forever's formats (big-endian section bodies) and the retarget onto a cousin's rig: the port of the project's
    /// numpy forward kinematics (verify_kfm/raw_fk.py), in doubles. Per frame: every channel evaluated (type 1 keys
    /// linear; types 3 and 6 cubic Hermite with tangents times the key gap; held outside the keys), forward kinematics of
    /// the Prince's nodes (local = S Rx Ry Rz with T in the last row, row vectors), each mapped joint takes its node's
    /// world rest-to-pose change turned into her axes (R = A D^T A, t = scale A t_D), the other joints keep their rest
    /// local pose under their parents, and all are stored as local values.
    /// </summary>
    public static class ForeverBaker
    {
        public sealed class Node
        {
            public double[] T = new double[3], R = new double[3], S = new double[3];
            public int Parent;
        }

        /// <summary>The motion table's speed, loop byte and face expression for a slot.</summary>
        public static void Record(byte[] sys, int slot, out float speed, out bool loops, out int expression)
        {
            if (sys == null || sys.Length < 12) throw new InvalidDataException("the motion table is too short");
            int motions = BeU16(sys, 10);
            int o = 0xE90 + 0x30 * slot;
            if (slot < 0 || slot >= motions || o + 0x30 > sys.Length) throw new InvalidDataException($"the motion table has no slot {slot}");
            speed = BeF32(sys, o + 0x18);
            loops = sys[o + 0x1C] == 1;
            expression = sys[o + 0x1D];
            if (!(speed > 0f && speed < 100f)) speed = 1f;
        }

        private static int[] Sections(byte[] b)
        {
            if (b == null || b.Length < 0x10 || b[0] != 'X' || b[1] != 'M' || b[2] != 'D') throw new InvalidDataException("not an XMD file");
            bool le = b[3] == 0;
            int n = (int)U32(b, 0xC, le);
            if (n < 0 || 0x10 + 4L * n > b.Length) throw new InvalidDataException("an XMD file's section list runs past its end");
            var offs = new int[n];
            for (int i = 0; i < n; i++) offs[i] = (int)U32(b, 0x10 + 4 * i, le);
            return offs;
        }

        private static bool IsResource(byte[] b, int o) =>
            o >= 0 && o + 12 <= b.Length && Encoding.ASCII.GetString(b, o, 12) == "RESOURCE:GCM";

        /// <summary>The model's nodes (list 6 of its first resource section): rest T, R (radians), S and parent.</summary>
        public static Node[] Nodes(byte[] mdl)
        {
            foreach (int s in Sections(mdl))
            {
                if (!IsResource(mdl, s)) continue;
                int count = (int)BeU32(mdl, s + 0x28 + 4 * 6);
                int list = (int)BeU32(mdl, s + 0x50 + 4 * 6);
                if (count <= 0 || count > 256 || s + list + 0xE0L * count > mdl.Length) throw new InvalidDataException("the model's node list is out of range");
                var nodes = new Node[count];
                for (int i = 0; i < count; i++)
                {
                    int no = s + list + i * 0xE0;
                    var n = new Node();
                    for (int a = 0; a < 3; a++)
                    {
                        n.T[a] = BeF32(mdl, no + 4 * a);
                        n.R[a] = BeF32(mdl, no + 0x10 + 4 * a);
                        n.S[a] = BeF32(mdl, no + 0x20 + 4 * a);
                    }
                    int par = BeU16(mdl, no + 0xC6);
                    n.Parent = par == 0xFFFF ? -1 : par;
                    if (n.Parent >= count) throw new InvalidDataException($"node {i}'s parent {par} is out of range");
                    nodes[i] = n;
                }
                return nodes;
            }
            throw new InvalidDataException("the model has no resource section");
        }

        /// <summary>The slots of a motion pack that hold a clip.</summary>
        public static List<int> ClipSlots(byte[] pack)
        {
            var offs = Sections(pack);
            var list = new List<int>();
            for (int i = 0; i < offs.Length; i++) if (IsClip(pack, offs[i])) list.Add(i);
            return list;
        }

        private static bool IsClip(byte[] b, int s)
        {
            if (!IsResource(b, s) || s + 0xB4 > b.Length) return false;
            for (int k = 0; k < 10; k++) if (BeU32(b, s + 0x28 + 4 * k) != 0) return false;
            return BeU32(b, s + 0xA0) == 2;
        }

        private sealed class Channel
        {
            public int Interp, Size, Count;
            public float[] Keys;
        }

        private sealed class RawClip
        {
            public double Start, End, Fps;
            /// <summary>[node * 6 + channel id]: null for a channel with no track.</summary>
            public Channel[] Channels;
        }

        private static RawClip ReadClip(byte[] b, int slot, int nodeCount)
        {
            var offs = Sections(b);
            if (slot < 0 || slot >= offs.Length || !IsClip(b, offs[slot])) throw new InvalidDataException($"slot {slot} holds no clip");
            int h = offs[slot] + 0xA0;
            int ntr = (int)BeU32(b, h + 4);
            var c = new RawClip { Start = BeF32(b, h + 8), End = BeF32(b, h + 12), Fps = BeF32(b, h + 16), Channels = new Channel[nodeCount * 6] };
            if (ntr < 0 || ntr > 1024 || h + 0x14 + 4L * ntr > b.Length) throw new InvalidDataException($"slot {slot}: track list out of range");
            for (int t = 0; t < ntr; t++)
            {
                int tp = h + (int)BeU32(b, h + 0x14 + 4 * t);
                if (tp < 0 || tp + 8 > b.Length) throw new InvalidDataException($"slot {slot}: track {t} out of range");
                int node = (int)BeU32(b, tp), nch = (int)BeU32(b, tp + 4);
                if (nch < 0 || nch > 64 || tp + 8 + 4L * nch > b.Length) throw new InvalidDataException($"slot {slot}: channel list out of range");
                for (int k = 0; k < nch; k++)
                {
                    int cp = tp + (int)BeU32(b, tp + 8 + 4 * k);
                    if (cp < 0 || cp + 16 > b.Length) throw new InvalidDataException($"slot {slot}: channel out of range");
                    int id = (int)BeU32(b, cp + 4), interp = (int)BeU32(b, cp + 8), nk = (int)BeU32(b, cp + 12);
                    int size = interp == 1 ? 2 : interp == 3 ? 3 : interp == 6 ? 4 : 0;
                    if (size == 0) throw new InvalidDataException($"slot {slot}: key type {interp}");
                    if (nk <= 0 || nk > 100000 || cp + 16 + 4L * size * nk > b.Length) throw new InvalidDataException($"slot {slot}: keys out of range");
                    // Scale channels (6..8) and nodes this model lacks are not played.
                    if (id < 0 || id > 5 || node < 0 || node >= nodeCount) continue;
                    var keys = new float[size * nk];
                    for (int i = 0; i < keys.Length; i++) keys[i] = BeF32(b, cp + 16 + 4 * i);
                    c.Channels[node * 6 + id] = new Channel { Interp = interp, Size = size, Count = nk, Keys = keys };
                }
            }
            return c;
        }

        /// <summary>A channel at time t (frames), as raw_fk.py's eval_key.</summary>
        private static double Eval(Channel ch, double t)
        {
            var k = ch.Keys;
            int s = ch.Size, n = ch.Count;
            if (n == 1 || t <= k[0]) return k[1];
            if (t >= k[(n - 1) * s]) return k[(n - 1) * s + 1];
            int j = 0;
            while (j + 1 < n && k[(j + 1) * s] <= t) j++;
            if (j >= n - 1) return k[(n - 1) * s + 1];
            int a = j * s, b = (j + 1) * s;
            double t0 = k[a], t1 = k[b], dt = t1 - t0;
            if (dt <= 0) return k[b + 1];
            double u = (t - t0) / dt, v0 = k[a + 1], v1 = k[b + 1];
            if (ch.Interp == 1) return v0 + (v1 - v0) * u;
            double m0, m1;
            if (ch.Interp == 3) { m0 = k[a + 2]; m1 = k[b + 2]; }
            else { m0 = k[a + 3]; m1 = -k[b + 2]; }
            double u2 = u * u, u3 = u2 * u;
            return (2 * u3 - 3 * u2 + 1) * v0 + (u3 - 2 * u2 + u) * dt * m0 + (-2 * u3 + 3 * u2) * v1 + (u3 - u2) * dt * m1;
        }

        // ---------------------------------------------------------------- 3x3 helpers (row-major arrays)

        private static void Mul(double[] a, double[] b, double[] o)
        {
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    o[r * 3 + c] = a[r * 3] * b[c] + a[r * 3 + 1] * b[3 + c] + a[r * 3 + 2] * b[6 + c];
        }

        /// <summary>Row vector v times matrix m.</summary>
        private static void VecMul(double[] v, double[] m, double[] o)
        {
            double x = v[0], y = v[1], z = v[2];
            for (int c = 0; c < 3; c++) o[c] = x * m[c] + y * m[3 + c] + z * m[6 + c];
        }

        /// <summary>Matrix m times column vector v.</summary>
        private static void MulVec(double[] m, double x, double y, double z, double[] o)
        {
            for (int r = 0; r < 3; r++) o[r] = m[r * 3] * x + m[r * 3 + 1] * y + m[r * 3 + 2] * z;
        }

        private static void Inverse(double[] m, double[] o)
        {
            double a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], i = m[8];
            double A = e * i - f * h, B = -(d * i - f * g), C = d * h - e * g;
            double det = a * A + b * B + c * C;
            if (Math.Abs(det) < 1e-12) throw new InvalidDataException("a node's matrix cannot be inverted");
            double k = 1.0 / det;
            o[0] = A * k; o[1] = -(b * i - c * h) * k; o[2] = (b * f - c * e) * k;
            o[3] = B * k; o[4] = (a * i - c * g) * k; o[5] = -(a * f - c * d) * k;
            o[6] = C * k; o[7] = -(a * h - b * g) * k; o[8] = (a * e - b * d) * k;
        }

        /// <summary>local = diag(S) Rx(rx) Ry(ry) Rz(rz) (row vectors), as raw_fk.py's local().</summary>
        private static void Local(double[] r, double[] s, double[] o)
        {
            double cx = Math.Cos(r[0]), sx = Math.Sin(r[0]), cy = Math.Cos(r[1]), sy = Math.Sin(r[1]), cz = Math.Cos(r[2]), sz = Math.Sin(r[2]);
            // Rx Ry
            double[] xy =
            {
                cy, 0, -sy,
                sx * sy, cx, sx * cy,
                cx * sy, -sx, cx * cy,
            };
            double[] z = { cz, sz, 0, -sz, cz, 0, 0, 0, 1 };
            Mul(xy, z, o);
            for (int row = 0; row < 3; row++)
                for (int c = 0; c < 3; c++) o[row * 3 + c] *= s[row];
        }

        /// <summary>A unit quaternion (x, y, z, w) from a rotation matrix acting on column vectors (Unity's convention).</summary>
        public static void ToQuat(double[] m, out double x, out double y, out double z, out double w)
        {
            double tr = m[0] + m[4] + m[8];
            if (tr > 0)
            {
                double s = Math.Sqrt(tr + 1.0) * 2;
                w = 0.25 * s; x = (m[7] - m[5]) / s; y = (m[2] - m[6]) / s; z = (m[3] - m[1]) / s;
            }
            else if (m[0] > m[4] && m[0] > m[8])
            {
                double s = Math.Sqrt(1.0 + m[0] - m[4] - m[8]) * 2;
                w = (m[7] - m[5]) / s; x = 0.25 * s; y = (m[1] + m[3]) / s; z = (m[2] + m[6]) / s;
            }
            else if (m[4] > m[8])
            {
                double s = Math.Sqrt(1.0 + m[4] - m[0] - m[8]) * 2;
                w = (m[2] - m[6]) / s; x = (m[1] + m[3]) / s; y = 0.25 * s; z = (m[5] + m[7]) / s;
            }
            else
            {
                double s = Math.Sqrt(1.0 + m[8] - m[0] - m[4]) * 2;
                w = (m[3] - m[1]) / s; x = (m[2] + m[6]) / s; y = (m[5] + m[7]) / s; z = 0.25 * s;
            }
            double n = Math.Sqrt(x * x + y * y + z * z + w * w);
            x /= n; y /= n; z /= n; w /= n;
        }

        /// <summary>The rotation matrix (column vectors) of a unit quaternion.</summary>
        public static void FromQuat(double x, double y, double z, double w, double[] m)
        {
            m[0] = 1 - 2 * (y * y + z * z); m[1] = 2 * (x * y - z * w); m[2] = 2 * (x * z + y * w);
            m[3] = 2 * (x * y + z * w); m[4] = 1 - 2 * (x * x + z * z); m[5] = 2 * (y * z - x * w);
            m[6] = 2 * (x * z - y * w); m[7] = 2 * (y * z + x * w); m[8] = 1 - 2 * (x * x + y * y);
        }

        /// <summary>Parents-first order of the nodes.</summary>
        private static int[] Order(Node[] nodes)
        {
            var order = new List<int>(nodes.Length);
            var seen = new bool[nodes.Length];
            void Visit(int i, int depth)
            {
                if (seen[i]) return;
                if (depth > nodes.Length) throw new InvalidDataException("the model's node parents form a loop");
                if (nodes[i].Parent >= 0) Visit(nodes[i].Parent, depth + 1);
                seen[i] = true;
                order.Add(i);
            }
            for (int i = 0; i < nodes.Length; i++) Visit(i, 0);
            return order.ToArray();
        }

        /// <summary>World matrices (3x3 and translation) of every node for the given T and R values.</summary>
        private static void World(Node[] nodes, int[] order, Func<int, int, double> t, Func<int, int, double> r, double[][] w3, double[][] wt)
        {
            var l3 = new double[9];
            var tv = new double[3];
            var rv = new double[3];
            var tmp = new double[3];
            foreach (int i in order)
            {
                var n = nodes[i];
                for (int a = 0; a < 3; a++) { tv[a] = t(i, a); rv[a] = r(i, a); }
                Local(rv, n.S, l3);
                if (n.Parent < 0)
                {
                    Array.Copy(l3, w3[i], 9);
                    Array.Copy(tv, wt[i], 3);
                }
                else
                {
                    Mul(l3, w3[n.Parent], w3[i]);
                    VecMul(tv, w3[n.Parent], tmp);
                    for (int a = 0; a < 3; a++) wt[i][a] = tmp[a] + wt[n.Parent][a];
                }
            }
        }

        /// <summary>
        /// Bakes the given clips onto <paramref name="her"/> rig. Throws <see cref="InvalidDataException"/> on anything
        /// malformed. Runs without Unity (safe on a worker thread).
        /// </summary>
        public static Dictionary<(int Pack, int Slot), ForeverClip> Bake(ForeverFiles files, LlcmModel her, ForeverRig rig, IEnumerable<(int Pack, int Slot)> clips)
        {
            var nodes = Nodes(files.Model);
            int nn = nodes.Length;
            int[] order = Order(nodes);
            int nj = her.Joints.Length;

            // Her joints: the driving node (or -1), rest local values, rest model position and rotation.
            var drive = new int[nj];
            var restModelRot = new double[nj][];
            var restLocalRot = new double[nj][];
            for (int j = 0; j < nj; j++)
            {
                var jt = her.Joints[j];
                drive[j] = rig.Joints.TryGetValue(jt.Name, out int node) ? node : -1;
                if (drive[j] >= nn) throw new InvalidDataException($"{jt.Name} is mapped to node {drive[j]}; the model has {nn}");
                restLocalRot[j] = new double[9];
                FromQuat(jt.LocalRotation[0], jt.LocalRotation[1], jt.LocalRotation[2], jt.LocalRotation[3], restLocalRot[j]);
                restModelRot[j] = new double[9];
                if (jt.Parent < 0) Array.Copy(restLocalRot[j], restModelRot[j], 9);
                else Mul(restModelRot[jt.Parent], restLocalRot[j], restModelRot[j]);
            }
            foreach (var name in rig.Joints.Keys)
                if (her.JointIndex(name) < 0) throw new InvalidDataException($"the map names joint {name}, which she lacks");

            var w3 = new double[nn][];
            var wt = new double[nn][];
            var r3 = new double[nn][];
            var rt = new double[nn][];
            for (int i = 0; i < nn; i++) { w3[i] = new double[9]; wt[i] = new double[3]; r3[i] = new double[9]; rt[i] = new double[3]; }
            World(nodes, order, (i, a) => nodes[i].T[a], (i, a) => nodes[i].R[a], r3, rt);
            var rInv = new double[nn][];
            var rInvT = new double[nn][];
            for (int i = 0; i < nn; i++)
            {
                rInv[i] = new double[9];
                Inverse(r3[i], rInv[i]);
                rInvT[i] = new double[3];
                VecMul(rt[i], rInv[i], rInvT[i]);
                for (int a = 0; a < 3; a++) rInvT[i][a] = -rInvT[i][a];
            }

            double scale = rig.Scale;
            double[] ax = { rig.Axes[0], rig.Axes[1], rig.Axes[2] };
            var result = new Dictionary<(int, int), ForeverClip>();
            var d3 = new double[9];
            var dt = new double[3];
            var ru = new double[9];
            var tmp = new double[9];
            var v = new double[3];
            var modelRot = new double[nj][];
            var modelPos = new double[nj][];
            for (int j = 0; j < nj; j++) { modelRot[j] = new double[9]; modelPos[j] = new double[3]; }
            var lr = new double[9];
            var prevQ = new double[nj * 4];

            foreach (var key in clips)
            {
                if (result.ContainsKey(key)) continue;
                var pack = files.Pack(key.Pack) ?? throw new InvalidDataException($"pack {key.Pack} does not exist");
                var raw = ReadClip(pack, key.Slot, nn);
                Record(files.Sys, key.Slot, out float speed, out bool loops, out int expr);
                int f0 = (int)Math.Round(raw.Start), f1 = (int)Math.Round(raw.End);
                if (f1 < f0 || f1 - f0 > 100000) throw new InvalidDataException($"pack {key.Pack} slot {key.Slot}: frames {f0}..{f1}");
                int frames = f1 - f0 + 1;
                var clip = new ForeverClip
                {
                    Pack = key.Pack, Slot = key.Slot, Frames = frames, Fps = raw.Fps > 0 && raw.Fps < 1000 ? (float)raw.Fps : 30f,
                    Speed = speed, Loops = loops, Expression = expr, Joints = nj, Pose = new float[frames * nj * 7],
                };
                var ch = raw.Channels;
                for (int fi = 0; fi < frames; fi++)
                {
                    double t = f0 + fi;
                    World(nodes, order,
                        (i, a) => ch[i * 6 + a] != null ? Eval(ch[i * 6 + a], t) : nodes[i].T[a],
                        (i, a) => ch[i * 6 + 3 + a] != null ? Eval(ch[i * 6 + 3 + a], t) * Math.PI / 180.0 : nodes[i].R[a],
                        w3, wt);
                    for (int j = 0; j < nj; j++)
                    {
                        var jt = her.Joints[j];
                        int p = jt.Parent;
                        int n = drive[j];
                        if (n >= 0)
                        {
                            // D = Wrest^-1 Wpose (row vectors); her rotation R = A D^T A, her move t = scale A t_D.
                            Mul(rInv[n], w3[n], d3);
                            VecMul(rInvT[n], w3[n], dt);
                            for (int a = 0; a < 3; a++) dt[a] += wt[n][a];
                            for (int r = 0; r < 3; r++)
                                for (int c = 0; c < 3; c++) ru[r * 3 + c] = ax[r] * d3[c * 3 + r] * ax[c];
                            // Kept a pure rotation (a scaled node would otherwise leak its scale into her local rotations).
                            ToQuat(ru, out double ux, out double uy, out double uz, out double uw);
                            FromQuat(ux, uy, uz, uw, ru);
                            Mul(ru, restModelRot[j], modelRot[j]);
                            MulVec(ru, jt.ModelPosition[0], jt.ModelPosition[1], jt.ModelPosition[2], v);
                            for (int a = 0; a < 3; a++) modelPos[j][a] = v[a] + scale * ax[a] * dt[a];
                        }
                        else if (p < 0)
                        {
                            Array.Copy(restLocalRot[j], modelRot[j], 9);
                            for (int a = 0; a < 3; a++) modelPos[j][a] = jt.LocalPosition[a];
                        }
                        else
                        {
                            Mul(modelRot[p], restLocalRot[j], modelRot[j]);
                            MulVec(modelRot[p], jt.LocalPosition[0], jt.LocalPosition[1], jt.LocalPosition[2], v);
                            for (int a = 0; a < 3; a++) modelPos[j][a] = modelPos[p][a] + v[a];
                        }

                        // Local values under her parent (the marker for the root): R_parent^T R, R_parent^T (p - p_parent).
                        double lx, ly, lz;
                        if (p < 0)
                        {
                            Array.Copy(modelRot[j], lr, 9);
                            lx = modelPos[j][0]; ly = modelPos[j][1]; lz = modelPos[j][2];
                        }
                        else
                        {
                            var pr = modelRot[p];
                            for (int r = 0; r < 3; r++)
                                for (int c = 0; c < 3; c++)
                                    tmp[r * 3 + c] = pr[r] * modelRot[j][c] + pr[3 + r] * modelRot[j][3 + c] + pr[6 + r] * modelRot[j][6 + c];
                            Array.Copy(tmp, lr, 9);
                            double ex = modelPos[j][0] - modelPos[p][0], ey = modelPos[j][1] - modelPos[p][1], ez = modelPos[j][2] - modelPos[p][2];
                            lx = pr[0] * ex + pr[3] * ey + pr[6] * ez;
                            ly = pr[1] * ex + pr[4] * ey + pr[7] * ez;
                            lz = pr[2] * ex + pr[5] * ey + pr[8] * ez;
                        }
                        ToQuat(lr, out double qx, out double qy, out double qz, out double qw);
                        // Each frame's rotation on the same side as the last, so neighbouring frames blend the short way.
                        if (fi > 0 && qx * prevQ[j * 4] + qy * prevQ[j * 4 + 1] + qz * prevQ[j * 4 + 2] + qw * prevQ[j * 4 + 3] < 0)
                        { qx = -qx; qy = -qy; qz = -qz; qw = -qw; }
                        prevQ[j * 4] = qx; prevQ[j * 4 + 1] = qy; prevQ[j * 4 + 2] = qz; prevQ[j * 4 + 3] = qw;
                        int o = (fi * nj + j) * 7;
                        var pose = clip.Pose;
                        pose[o] = (float)lx; pose[o + 1] = (float)ly; pose[o + 2] = (float)lz;
                        pose[o + 3] = (float)qx; pose[o + 4] = (float)qy; pose[o + 5] = (float)qz; pose[o + 6] = (float)qw;
                    }
                }
                result[key] = clip;
            }
            return result;
        }

        // ---------------------------------------------------------------- byte order

        private static uint U32(byte[] b, int o, bool le) => le ? ForeverArchive.LeU32(b, o) : BeU32(b, o);
        private static uint BeU32(byte[] b, int o)
        {
            if (o < 0 || o + 4 > b.Length) throw new InvalidDataException("a read runs past the end of an entry");
            return (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
        }
        private static int BeU16(byte[] b, int o)
        {
            if (o < 0 || o + 2 > b.Length) throw new InvalidDataException("a read runs past the end of an entry");
            return b[o] << 8 | b[o + 1];
        }
        private static float BeF32(byte[] b, int o) => BitConverter.Int32BitsToSingle((int)BeU32(b, o));
    }
}
