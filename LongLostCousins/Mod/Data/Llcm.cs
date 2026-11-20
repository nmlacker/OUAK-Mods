using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LongLostCousins.Data
{
    /// <summary>
    /// One .llcm file read into plain arrays: a cousin in one style (Cousins/FORMAT.md, version 1). This file uses no
    /// Unity or game types, so it can be checked outside the game; <see cref="Dressing.CousinAssets"/> turns it into
    /// meshes and textures.
    /// </summary>
    public sealed class LlcmModel
    {
        public const int Version = 1;
        public const byte None8 = 0xFF;

        public string Id, Name, Style, Marker, Source;
        /// <summary>The face is cards (old style).</summary>
        public bool Cards;
        /// <summary>The face is slot pieces (new style).</summary>
        public bool Slots;
        public int ChunkCount, FileLength;
        public string[] Strings;
        public LlcmJoint[] Joints;
        public LlcmFollow[] Follows;
        public LlcmTexture[] Textures;
        public LlcmMaterial[] Materials;
        public LlcmMesh[] Meshes;
        public LlcmFace Face;

        /// <summary>The joint index of a name, or -1.</summary>
        public int JointIndex(string name)
        {
            for (int i = 0; i < Joints.Length; i++) if (Joints[i].Name == name) return i;
            return -1;
        }

        /// <summary>The mesh index of a name, or -1.</summary>
        public int MeshIndex(string name)
        {
            for (int i = 0; i < Meshes.Length; i++) if (Meshes[i].Name == name) return i;
            return -1;
        }

        public override string ToString() =>
            $"{Name} ({Style}): {Joints.Length} joints, {Meshes.Length} meshes, {Textures.Length} textures, {Materials.Length} materials, " +
            $"{(Cards ? "face cards" : "face slot pieces")}";
    }

    public sealed class LlcmJoint
    {
        public string Name;
        public int Parent;
        /// <summary>bit0 copy localRotation, bit1 copy localPosition plus the FOLW offset, bit2 copy localScale.</summary>
        public byte Follow;
        public float[] LocalPosition = new float[3];
        public float[] LocalRotation = new float[4];
        public float[] ModelPosition = new float[3];

        public bool CopiesRotation => (Follow & 1) != 0;
        public bool CopiesPosition => (Follow & 2) != 0;
        public bool CopiesScale => (Follow & 4) != 0;
    }

    public sealed class LlcmFollow
    {
        public int Joint;
        public int Mode;
        public float[] Offset = new float[3];
    }

    public sealed class LlcmTexture
    {
        public string Name;
        public int Width, Height, Encoding, Flags, Filter, Wrap;
        public byte[] Bytes;
        public bool HasAlpha => (Flags & 1) != 0;
        public bool Srgb => (Flags & 2) != 0;
    }

    public sealed class LlcmMaterial
    {
        public string Name;
        public int Texture;
        public bool Blends;
    }

    /// <summary>Where a mesh goes and when it shows (FORMAT.md, MESH roles).</summary>
    public enum MeshRole : byte { Body = 0, FacePlate = 1, EyeOrMouth = 2, Overlay = 3, Extra = 4, Card = 5 }

    public sealed class LlcmMesh
    {
        public string Name;
        public MeshRole Role;
        public byte Flags;
        /// <summary>Index into the face component's faceObject array, or 0xFF.</summary>
        public int Slot;
        public string SlotName;
        /// <summary>1..11 for a face card, 0 otherwise.</summary>
        public int Card;
        public int Material;
        /// <summary>The game renderer object whose shared material is cloned (body_m, eye_01_m, ...).</summary>
        public string Template;
        public int RootBone;
        /// <summary>Joint index per entry of this mesh's bones array.</summary>
        public byte[] Bones;
        public float[] BoundsMin = new float[3], BoundsMax = new float[3];
        public int VertexCount, TriangleCount;
        public float[] Positions, Normals, Uvs;
        public byte[] BoneIndices;
        public ushort[] BoneWeights;
        public int[] Triangles;

        public bool HiddenInBlend => (Flags & 1) != 0;
        public bool CastsShadows => (Flags & 2) != 0;
        public bool IsSlotPiece => Role == MeshRole.FacePlate || Role == MeshRole.EyeOrMouth || Role == MeshRole.Overlay;
        public bool IsFacePiece => Role != MeshRole.Body;
    }

    public sealed class LlcmGroup
    {
        /// <summary>The slots the game turns on: face plate, eye, mouth, overlay (0xFF for none).</summary>
        public byte[] GameSlots = new byte[4];
        /// <summary>New style: her mesh indices for face plate, eye, mouth, overlay (0xFF for none).</summary>
        public byte[] Pieces = new byte[4];
        public int Card;
        public bool Blinks;
        /// <summary>Extra renderers shown only while this group is current.</summary>
        public byte[] Show;
        /// <summary>Her slot pieces forced off while this group is current.</summary>
        public byte[] Hide;

        public bool HasOverride => Show.Length > 0 || Hide.Length > 0;
    }

    public sealed class LlcmFace
    {
        public string[] SlotNames;
        public int BlinkSlot;
        public float BlinkOpenSeconds, BlinkClosedSeconds;
        public int BlinkCard, BlinkOverCard, NoFaceCard;
        public LlcmGroup[] Groups;
        public byte[] ExpressionCards;
        public (int Row, int Card)[] RowCards;

        /// <summary>Mesh indices that appear in any group's show list.</summary>
        public HashSet<int> AllShown()
        {
            var s = new HashSet<int>();
            foreach (var g in Groups) foreach (byte b in g.Show) s.Add(b);
            return s;
        }

        /// <summary>Mesh indices that appear in any group's hide list.</summary>
        public HashSet<int> AllHidden()
        {
            var s = new HashSet<int>();
            foreach (var g in Groups) foreach (byte b in g.Hide) s.Add(b);
            return s;
        }
    }

    /// <summary>Reads .llcm bytes. Throws <see cref="InvalidDataException"/> with the reason on anything malformed.</summary>
    public static class LlcmReader
    {
        public static LlcmModel Read(byte[] file)
        {
            if (file == null || file.Length < 16) throw new InvalidDataException("shorter than the 16-byte header");
            var r = new Cursor(file, 0, file.Length, "header");
            if (r.U8() != (byte)'L' || r.U8() != (byte)'L' || r.U8() != (byte)'C' || r.U8() != (byte)'M')
                throw new InvalidDataException("not an LLCM file");
            int version = r.U16();
            if (version != LlcmModel.Version) throw new InvalidDataException($"version {version}; this mod reads version {LlcmModel.Version}");
            int flags = r.U16();
            var m = new LlcmModel { Cards = (flags & 1) != 0, Slots = (flags & 2) != 0 };
            if (m.Cards == m.Slots) throw new InvalidDataException($"header flags {flags}: exactly one of cards and slots must be set");
            m.ChunkCount = (int)r.U32();
            m.FileLength = (int)r.U32();
            if (m.FileLength != file.Length) throw new InvalidDataException($"the header says {m.FileLength} bytes, the file has {file.Length}");

            var meshes = new List<LlcmMesh>();
            var textures = new List<LlcmTexture>();
            int pos = 16;
            bool info = false;
            for (int c = 0; c < m.ChunkCount; c++)
            {
                if (pos + 8 > file.Length) throw new InvalidDataException($"chunk {c} starts past the end");
                string tag = Encoding.ASCII.GetString(file, pos, 4);
                int len = (int)BitConverter.ToUInt32(file, pos + 4);
                int start = pos + 8;
                if (len < 0 || start + len > file.Length) throw new InvalidDataException($"chunk {c} ({tag}) runs past the end");
                var k = new Cursor(file, start, start + len, tag);
                switch (tag)
                {
                    case "STRS": m.Strings = ReadStrings(k); break;
                    case "INFO":
                        m.Id = Str(m, k.U16()); m.Name = Str(m, k.U16()); m.Style = Str(m, k.U16());
                        m.Marker = Str(m, k.U16()); m.Source = Str(m, k.U16());
                        info = true;
                        break;
                    case "SKEL": m.Joints = ReadSkeleton(m, k); break;
                    case "FOLW": m.Follows = ReadFollows(k); break;
                    case "TEXR": textures.Add(ReadTexture(m, k)); break;
                    case "MATL": m.Materials = ReadMaterials(m, k); break;
                    case "MESH": meshes.Add(ReadMesh(m, k)); break;
                    case "FACE": m.Face = ReadFace(m, k); break;
                    default: k.Pos = k.End; break;   // a tag this version does not know: skipped
                }
                if (k.Pos != k.End) throw new InvalidDataException($"chunk {c} ({tag}) has {k.End - k.Pos} bytes left over");
                pos = start + ((len + 3) & ~3);
            }
            if (pos != file.Length) throw new InvalidDataException($"{file.Length - pos} bytes after the last chunk");
            if (m.Strings == null || !info || m.Joints == null || m.Follows == null || m.Materials == null || m.Face == null)
                throw new InvalidDataException("a required chunk (STRS, INFO, SKEL, FOLW, MATL, FACE) is missing");
            m.Textures = textures.ToArray();
            m.Meshes = meshes.ToArray();
            Validate(m);
            return m;
        }

        private static string[] ReadStrings(Cursor k)
        {
            int n = k.U16();
            var s = new string[n];
            for (int i = 0; i < n; i++)
            {
                int len = k.U16();
                s[i] = Encoding.UTF8.GetString(k.Bytes(len));
            }
            return s;
        }

        private static string Str(LlcmModel m, int index)
        {
            if (index == 0xFFFF) return null;
            if (m.Strings == null) throw new InvalidDataException("a string is used before the STRS chunk");
            if (index >= m.Strings.Length) throw new InvalidDataException($"string {index} of {m.Strings.Length}");
            return m.Strings[index];
        }

        private static LlcmJoint[] ReadSkeleton(LlcmModel m, Cursor k)
        {
            int n = k.U8();
            var j = new LlcmJoint[n];
            for (int i = 0; i < n; i++)
            {
                var x = new LlcmJoint { Name = Str(m, k.U16()), Parent = k.I8(), Follow = k.U8() };
                for (int a = 0; a < 3; a++) x.LocalPosition[a] = k.F32();
                for (int a = 0; a < 4; a++) x.LocalRotation[a] = k.F32();
                for (int a = 0; a < 3; a++) x.ModelPosition[a] = k.F32();
                if (x.Name == null) throw new InvalidDataException($"joint {i} has no name");
                if (x.Parent >= i) throw new InvalidDataException($"joint {x.Name}'s parent {x.Parent} does not come before it");
                j[i] = x;
            }
            return j;
        }

        private static LlcmFollow[] ReadFollows(Cursor k)
        {
            int n = k.U8();
            var f = new LlcmFollow[n];
            for (int i = 0; i < n; i++)
            {
                f[i] = new LlcmFollow { Joint = k.U8(), Mode = k.U8() };
                for (int a = 0; a < 3; a++) f[i].Offset[a] = k.F32();
            }
            return f;
        }

        private static LlcmTexture ReadTexture(LlcmModel m, Cursor k)
        {
            var t = new LlcmTexture
            {
                Name = Str(m, k.U16()), Width = k.U16(), Height = k.U16(),
                Encoding = k.U8(), Flags = k.U8(), Filter = k.U8(), Wrap = k.U8(),
            };
            int count = (int)k.U32();
            t.Bytes = k.Bytes(count);
            if (t.Encoding != 0) throw new InvalidDataException($"texture {t.Name}: encoding {t.Encoding}; version 1 has PNG (0) only");
            return t;
        }

        private static LlcmMaterial[] ReadMaterials(LlcmModel m, Cursor k)
        {
            int n = k.U8();
            var mats = new LlcmMaterial[n];
            for (int i = 0; i < n; i++) mats[i] = new LlcmMaterial { Name = Str(m, k.U16()), Texture = k.U8(), Blends = k.U8() != 0 };
            return mats;
        }

        private static LlcmMesh ReadMesh(LlcmModel m, Cursor k)
        {
            var x = new LlcmMesh
            {
                Name = Str(m, k.U16()), Role = (MeshRole)k.U8(), Flags = k.U8(), Slot = k.U8(),
            };
            x.SlotName = Str(m, k.U16());
            x.Card = k.U8();
            x.Material = k.U8();
            x.Template = Str(m, k.U16());
            x.RootBone = k.U8();
            int nb = k.U8();
            x.Bones = k.Bytes(nb);
            for (int a = 0; a < 3; a++) x.BoundsMin[a] = k.F32();
            for (int a = 0; a < 3; a++) x.BoundsMax[a] = k.F32();
            int n = x.VertexCount = k.U16();
            int t = x.TriangleCount = k.U16();
            x.Positions = k.Floats(3 * n);
            x.Normals = k.Floats(3 * n);
            x.Uvs = k.Floats(2 * n);
            x.BoneIndices = k.Bytes(4 * n);
            x.BoneWeights = k.UShorts(4 * n);
            var tri = k.UShorts(3 * t);
            x.Triangles = new int[tri.Length];
            for (int i = 0; i < tri.Length; i++) x.Triangles[i] = tri[i];
            if ((byte)x.Role > (byte)MeshRole.Card) throw new InvalidDataException($"mesh {x.Name}: role {(int)x.Role}");
            return x;
        }

        private static LlcmFace ReadFace(LlcmModel m, Cursor k)
        {
            var f = new LlcmFace();
            int s = k.U8();
            f.SlotNames = new string[s];
            for (int i = 0; i < s; i++) f.SlotNames[i] = Str(m, k.U16());
            f.BlinkSlot = k.U8();
            f.BlinkOpenSeconds = k.F32();
            f.BlinkClosedSeconds = k.F32();
            f.BlinkCard = k.U8();
            f.BlinkOverCard = k.U8();
            f.NoFaceCard = k.U8();
            int g = k.U8();
            f.Groups = new LlcmGroup[g];
            for (int i = 0; i < g; i++)
            {
                var x = new LlcmGroup { GameSlots = k.Bytes(4), Pieces = k.Bytes(4), Card = k.U8(), Blinks = k.U8() != 0 };
                x.Show = k.Bytes(k.U8());
                x.Hide = k.Bytes(k.U8());
                f.Groups[i] = x;
            }
            f.ExpressionCards = k.Bytes(k.U8());
            int w = k.U8();
            f.RowCards = new (int, int)[w];
            for (int i = 0; i < w; i++) f.RowCards[i] = (k.U8(), k.U8());
            return f;
        }

        /// <summary>Indices in range, weights summing to 65535, triangles in range: what the builder relies on.</summary>
        private static void Validate(LlcmModel m)
        {
            int nj = m.Joints.Length;
            foreach (var f in m.Follows)
                if (f.Joint >= nj) throw new InvalidDataException($"FOLW joint {f.Joint} of {nj}");
            foreach (var mat in m.Materials)
                if (mat.Texture >= m.Textures.Length) throw new InvalidDataException($"material {mat.Name}: texture {mat.Texture} of {m.Textures.Length}");
            for (int i = 0; i < m.Meshes.Length; i++)
            {
                var x = m.Meshes[i];
                string w = $"mesh {i} ({x.Name})";
                if (x.Name == null) throw new InvalidDataException($"mesh {i} has no name");
                if (x.Material >= m.Materials.Length) throw new InvalidDataException($"{w}: material {x.Material} of {m.Materials.Length}");
                if (x.RootBone >= nj) throw new InvalidDataException($"{w}: root bone {x.RootBone} of {nj}");
                if (x.Bones.Length == 0) throw new InvalidDataException($"{w}: no bones");
                foreach (byte b in x.Bones) if (b >= nj) throw new InvalidDataException($"{w}: bone {b} of {nj}");
                if (x.IsSlotPiece && (x.Slot == LlcmModel.None8 || x.SlotName == null)) throw new InvalidDataException($"{w}: a slot piece without a slot");
                if (x.Role == MeshRole.Card && x.Card == 0) throw new InvalidDataException($"{w}: a card without a number");
                for (int v = 0; v < x.VertexCount; v++)
                {
                    int sum = 0;
                    for (int a = 0; a < 4; a++)
                    {
                        sum += x.BoneWeights[4 * v + a];
                        if (x.BoneWeights[4 * v + a] != 0 && x.BoneIndices[4 * v + a] >= x.Bones.Length)
                            throw new InvalidDataException($"{w}: vertex {v} uses bone {x.BoneIndices[4 * v + a]} of {x.Bones.Length}");
                    }
                    if (sum != 65535) throw new InvalidDataException($"{w}: vertex {v}'s weights sum to {sum}, not 65535");
                }
                foreach (int t in x.Triangles) if (t >= x.VertexCount) throw new InvalidDataException($"{w}: triangle index {t} of {x.VertexCount} vertices");
            }
            var face = m.Face;
            foreach (var g in face.Groups)
            {
                foreach (byte p in g.Pieces) if (p != LlcmModel.None8 && p >= m.Meshes.Length) throw new InvalidDataException($"FACE piece {p} of {m.Meshes.Length}");
                foreach (byte p in g.Show) if (p >= m.Meshes.Length) throw new InvalidDataException($"FACE show {p} of {m.Meshes.Length}");
                foreach (byte p in g.Hide) if (p >= m.Meshes.Length) throw new InvalidDataException($"FACE hide {p} of {m.Meshes.Length}");
            }
        }

        /// <summary>Reads fields one after another inside one chunk, never past its end.</summary>
        private sealed class Cursor
        {
            private readonly byte[] _b;
            public int Pos;
            public readonly int End;
            private readonly string _what;

            public Cursor(byte[] b, int start, int end, string what) { _b = b; Pos = start; End = end; _what = what; }

            private int Take(int n)
            {
                if (n < 0 || Pos + n > End) throw new InvalidDataException($"{_what}: needs {n} more bytes at {Pos}, the chunk ends at {End}");
                int p = Pos;
                Pos += n;
                return p;
            }

            public byte U8() => _b[Take(1)];
            public sbyte I8() => unchecked((sbyte)_b[Take(1)]);
            public ushort U16() => BitConverter.ToUInt16(_b, Take(2));
            public uint U32() => BitConverter.ToUInt32(_b, Take(4));
            public float F32() => BitConverter.ToSingle(_b, Take(4));

            public byte[] Bytes(int n)
            {
                int p = Take(n);
                var o = new byte[n];
                Buffer.BlockCopy(_b, p, o, 0, n);
                return o;
            }

            public float[] Floats(int n)
            {
                int p = Take(4 * n);
                var o = new float[n];
                Buffer.BlockCopy(_b, p, o, 0, 4 * n);
                return o;
            }

            public ushort[] UShorts(int n)
            {
                int p = Take(2 * n);
                var o = new ushort[n];
                Buffer.BlockCopy(_b, p, o, 0, 2 * n);
                return o;
            }
        }
    }
}
