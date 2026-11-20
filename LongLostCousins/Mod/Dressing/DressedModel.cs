using System;
using System.Collections.Generic;
using Il2CppApp.KatamariSin;
using LongLostCousins.Data;
using UnityEngine;

namespace LongLostCousins.Dressing
{
    /// <summary>One of her renderers on a dressed model.</summary>
    internal sealed class Piece
    {
        public int Mesh;
        public MeshRole Role;
        public SkinnedMeshRenderer Renderer;
        public GameObject Go;
        /// <summary>A renderer shown only by a face group's show list (eye_sing_m, the second mouth_05_m).</summary>
        public bool Extra;
        /// <summary>Parented under the game's face slot object, so the game's SetActive shows and hides it.</summary>
        public bool UnderSlot;
        /// <summary>Whether the piece shows when no hide list applies (false for a slot piece without its slot, unless group 0 has it).</summary>
        public bool Base = true;
        /// <summary>The card number (old style).</summary>
        public int Card;
    }

    /// <summary>A game cousin model the mod has dressed: her bones, her pieces, and what each LateUpdate keeps in step.</summary>
    internal sealed class DressedModel
    {
        public GameObject Root;
        public IntPtr RootPtr;
        public string Context;
        public CousinAssets Assets;
        public Transform Model, Marker;
        public Transform[] GameJoints, HerJoints;
        public MainGameOujiFace Face;
        public GameObject LayerSource;
        public readonly List<Piece> Pieces = new();
        /// <summary>The pointers of her renderers, to tell them from the game's.</summary>
        public readonly HashSet<IntPtr> Own = new();
        public bool Rebound;
        public float DressedAt;

        /// <summary>Game renderers switched off by the last hide pass, and in all.</summary>
        public int LastHidden, TotalHidden;
        public float NextHide;
        public int LastGroup = int.MinValue, LastCard = int.MinValue, LastLayer = int.MinValue;
        public bool Failed;
        public int Errors;

        public LlcmModel Data => Assets.Model;
        public string Label => $"{Context} ({Assets.Label})";

        /// <summary>Copies the game joints' pose onto her joints: rotation and scale on every joint, the waist's position plus the offset.</summary>
        public void Follow()
        {
            var joints = Data.Joints;
            for (int i = 0; i < joints.Length; i++)
            {
                var g = GameJoints[i];
                if (g == null) continue;
                var h = HerJoints[i];
                var j = joints[i];
                if (j.CopiesRotation) h.localRotation = g.localRotation;
                if (j.CopiesScale) h.localScale = g.localScale;
                if (j.CopiesPosition)
                {
                    var p = g.localPosition;
                    if (i == Assets.FollowJoint) p = U.Add(p, Assets.FollowOffset);
                    h.localPosition = p;
                }
            }
        }

        /// <summary>The face group the game shows now (prevFaceIndex), or -1.</summary>
        public int CurrentGroup()
        {
            if (Face == null) return -1;
            return Face.prevFaceIndex;
        }

        /// <summary>
        /// New style: the show and hide lists of the current group (the game's own slot switching moves the other pieces).
        /// Old style: the card of the current group, or the blink card while the game blinks.
        /// Only touches renderers when the group or card changed, unless <paramref name="force"/>.
        /// </summary>
        public void ApplyFace(bool force)
        {
            var face = Data.Face;
            if (Data.Cards)
            {
                int card = CardNow(face);
                if (card == LastCard && !force) return;
                LastCard = card;
                foreach (var p in Pieces)
                    if (p.Role == MeshRole.Card) p.Renderer.enabled = p.Card == card;
                return;
            }
            int g = CurrentGroup();
            if (g == LastGroup && !force) return;
            LastGroup = g;
            var grp = g >= 0 && g < face.Groups.Length ? face.Groups[g] : null;
            foreach (var p in Pieces)
            {
                if (p.Extra) p.Renderer.enabled = grp != null && Array.IndexOf(grp.Show, (byte)p.Mesh) >= 0;
                else if (p.Role != MeshRole.Body && Assets.SometimesHidden.Contains(p.Mesh))
                    p.Renderer.enabled = p.Base && !(grp != null && Array.IndexOf(grp.Hide, (byte)p.Mesh) >= 0);
            }
        }

        /// <summary>The old-style card to show now: 0 for none.</summary>
        public int CardNow(LlcmFace face)
        {
            if (Face == null) return face.NoFaceCard;
            int g = Face.prevFaceIndex;
            if (g < 0 || g >= face.Groups.Length) return 0;
            int card = face.Groups[g].Card;
            if (card == face.BlinkOverCard && face.BlinkCard != 0 && Face.playBlink && Face.blinkState == 1) card = face.BlinkCard;
            return card;
        }

        /// <summary>Keeps her objects on the layer of the game's body_m (the game re-layers whole models later).</summary>
        public void FollowLayer()
        {
            int layer = LayerSource != null ? LayerSource.layer : Model.gameObject.layer;
            if (layer == LastLayer) return;
            LastLayer = layer;
            Marker.gameObject.layer = layer;
            foreach (var p in Pieces) p.Go.layer = layer;
        }

        /// <summary>Switches off every renderer under the model that is not hers (presents load later, so this repeats).</summary>
        public int HideGame()
        {
            int n = 0;
            var all = Root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < all.Length; i++)
            {
                var r = all[i];
                if (r is null || Own.Contains(r.Pointer)) continue;
                if (r.enabled) { r.enabled = false; n++; }
            }
            LastHidden = n;
            TotalHidden += n;
            return n;
        }
    }
}
