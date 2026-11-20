using System;
using System.Collections.Generic;
using LongLostCousins.Data;
using OUAKModAPI;
using UnityEngine;

namespace LongLostCousins.Dressing
{
    /// <summary>
    /// The Unity side of one cousin in one style: a Mesh per .llcm mesh and a Texture2D per texture, made once and shared
    /// by every model dressed in that style. They are kept for the whole session (DontUnloadUnusedAsset), so a scene change
    /// never leaves a dressed model with a destroyed mesh or texture.
    /// </summary>
    internal sealed class CousinAssets
    {
        private static readonly ModLog Log = LongLostCousinsMod.Log.In("Assets");
        private static readonly Dictionary<string, CousinAssets> Cache = new(StringComparer.Ordinal);
        private static readonly HashSet<string> Reported = new(StringComparer.Ordinal);

        public readonly CousinEntry Cousin;
        public readonly string Style;
        public readonly LlcmModel Model;
        private Mesh[] _meshes;
        private Texture2D[] _textures;

        /// <summary>Mesh indices shown as extra renderers by some face group (new style: mouth_05_m, eye_sing_m).</summary>
        public readonly HashSet<int> ShownExtras;
        /// <summary>Mesh indices some face group forces off.</summary>
        public readonly HashSet<int> SometimesHidden;
        /// <summary>Group 0's pieces: what a piece without a slot to follow shows as (new style).</summary>
        public readonly HashSet<int> Group0Pieces = new();
        /// <summary>The joint index the FOLW offset applies to, and the offset.</summary>
        public readonly int FollowJoint = -1;
        public readonly Vector3 FollowOffset;
        /// <summary>How the player's presents and the stage HUD fit her in this style (cousins.json "fit").</summary>
        public readonly StyleFit Fit;
        /// <summary>Her antenna piece (new style: antena_m), which follows the game's antenna; -1 when the antenna is part of another mesh.</summary>
        public readonly int AntennaMesh = -1;

        private CousinAssets(CousinEntry c, string style, LlcmModel m)
        {
            Cousin = c;
            Style = style;
            Model = m;
            Fit = c.FitOf(style);
            AntennaMesh = m.MeshIndex("antena_m");
            ShownExtras = m.Face.AllShown();
            SometimesHidden = m.Face.AllHidden();
            if (m.Face.Groups.Length > 0)
                foreach (byte p in m.Face.Groups[0].Pieces) if (p != LlcmModel.None8) Group0Pieces.Add(p);
            foreach (var f in m.Follows)
                if (f.Mode == 1) { FollowJoint = f.Joint; FollowOffset = U.V3(f.Offset[0], f.Offset[1], f.Offset[2]); }
        }

        public string Label => Cousin.StyleLabel(Style);

        /// <summary>The assets of a cousin's style, read and built on first use; null (logged once) when they cannot be.</summary>
        public static CousinAssets Get(CousinEntry c, string style)
        {
            if (c == null || style == null) return null;
            string key = c.Id + "/" + style;
            if (Cache.TryGetValue(key, out var a)) return a;
            var m = Catalog.Model(c, style);
            if (m == null)
            {
                if (Reported.Add(key)) Log.Error($"{c.Name} ({style}) can't be read: {Catalog.ModelError(c, style) ?? "no such style"}");
                return null;
            }
            a = new CousinAssets(c, style, m);
            Cache[key] = a;
            Log.Info($"read {m}");
            return a;
        }

        /// <summary>The assets whose marker root is called <paramref name="marker"/> (a copy of a dressed model), or null.</summary>
        public static CousinAssets ByMarker(string marker)
        {
            foreach (var a in Cache.Values) if (a.Model.Marker == marker) return a;
            foreach (var c in Catalog.Cousins)
                foreach (var s in c.Styles)
                {
                    var m = Catalog.Model(c, s.Key);
                    if (m != null && m.Marker == marker) return Get(c, s.Key);
                }
            return null;
        }

        /// <summary>The Unity mesh of .llcm mesh <paramref name="i"/>, built on first use.</summary>
        public Mesh Mesh(int i)
        {
            _meshes ??= new Mesh[Model.Meshes.Length];
            var mesh = _meshes[i];
            if (mesh != null) return mesh;
            mesh = BuildMesh(Model.Meshes[i]);
            _meshes[i] = mesh;
            return mesh;
        }

        /// <summary>The texture of material <paramref name="material"/>, built on first use.</summary>
        public Texture2D TextureOf(int material)
        {
            int t = Model.Materials[material].Texture;
            _textures ??= new Texture2D[Model.Textures.Length];
            var tex = _textures[t];
            if (tex != null) return tex;
            tex = BuildTexture(Model.Textures[t]);
            _textures[t] = tex;
            return tex;
        }

        /// <summary>Bind pose k of a mesh: the translation by minus the rest model position of joint bones[k].</summary>
        private Matrix4x4 BindPose(int joint)
        {
            var p = Model.Joints[joint].ModelPosition;
            return U.Translate(-p[0], -p[1], -p[2]);
        }

        private Mesh BuildMesh(LlcmMesh m)
        {
            int n = m.VertexCount;
            var v = new Vector3[n];
            var nr = new Vector3[n];
            var uv = new Vector2[n];
            var bw = new BoneWeight[n];
            const float W = 1f / 65535f;
            for (int i = 0; i < n; i++)
            {
                v[i] = U.V3(m.Positions[3 * i], m.Positions[3 * i + 1], m.Positions[3 * i + 2]);
                nr[i] = U.V3(m.Normals[3 * i], m.Normals[3 * i + 1], m.Normals[3 * i + 2]);
                uv[i] = U.V2(m.Uvs[2 * i], m.Uvs[2 * i + 1]);
                BoneWeight b = default;
                b.m_BoneIndex0 = m.BoneIndices[4 * i]; b.m_Weight0 = m.BoneWeights[4 * i] * W;
                b.m_BoneIndex1 = m.BoneIndices[4 * i + 1]; b.m_Weight1 = m.BoneWeights[4 * i + 1] * W;
                b.m_BoneIndex2 = m.BoneIndices[4 * i + 2]; b.m_Weight2 = m.BoneWeights[4 * i + 2] * W;
                b.m_BoneIndex3 = m.BoneIndices[4 * i + 3]; b.m_Weight3 = m.BoneWeights[4 * i + 3] * W;
                bw[i] = b;
            }
            var bind = new Matrix4x4[m.Bones.Length];
            for (int k = 0; k < bind.Length; k++) bind[k] = BindPose(m.Bones[k]);

            var mesh = new Mesh();
            mesh.name = "llc_" + m.Name;
            mesh.vertices = v;
            mesh.normals = nr;
            mesh.uv = uv;
            mesh.triangles = m.Triangles;
            mesh.boneWeights = bw;
            mesh.bindposes = bind;
            mesh.RecalculateBounds();
            mesh.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return mesh;
        }

        private static Texture2D BuildTexture(LlcmTexture t)
        {
            var img = Png.Decode(t.Bytes);
            if (img.Width != t.Width || img.Height != t.Height)
                throw new InvalidOperationException($"{t.Name}: the PNG is {img.Width}x{img.Height}, the file says {t.Width}x{t.Height}");
            var tex = new Texture2D(t.Width, t.Height, TextureFormat.RGBA32, false, !t.Srgb);
            tex.name = "llc_" + t.Name;
            tex.filterMode = t.Filter == 0 ? FilterMode.Point : FilterMode.Bilinear;
            tex.wrapMode = t.Wrap == 0 ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
            tex.LoadRawTextureData(img.RgbaBottomUp());
            tex.Apply(false, true);
            tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return tex;
        }
    }
}
