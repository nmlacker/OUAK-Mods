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

        /// <summary>The game model's OujiPresent (the player's presents), or null.</summary>
        public OujiPresent Op;
        /// <summary>Her antenna piece, which follows the game's antenna object (null when the style has none of its own).</summary>
        public Piece Antenna;
        /// <summary>Her marker's scale in this model (the stage HUD copy only; 1 elsewhere).</summary>
        public float HudScale = 1f;
        /// <summary>The renderers of the presents she wears (game objects under her attach nodes), and whether each was in the fade list when the mod first saw it.</summary>
        public readonly HashSet<IntPtr> PresentRenderers = new();
        public readonly Dictionary<IntPtr, bool> FadeAtTakeOver = new();
        /// <summary>Present objects moved onto her attach nodes, in all; entries pointed at her nodes now.</summary>
        public int PresentsMoved, PresentEntries;
        public bool PresentsFailed;
        public int PresentErrors;

        /// <summary>Game renderers switched off by the last hide pass, and in all.</summary>
        public int LastHidden, TotalHidden;
        public float NextHide;
        public int LastGroup = int.MinValue, LastCard = int.MinValue, LastLayer = int.MinValue;
        public bool Failed;
        public int Errors;

        // Katamari Forever's motions (ForeverPlayer), for a style with a Forever map only.
        /// <summary>The game model's Animator, whose state picks the clip.</summary>
        public Animator Anim;
        /// <summary>OUAK's motion table as the model's driver uses it (row speed and face group by state), or null.</summary>
        public OuakRows Rows;
        /// <summary>The driver multiplies the row speed into Animator.speed (MainGameOuji, UIMainGameOuji).</summary>
        public bool StageDriven;
        public string Driver;
        /// <summary>Each state's length at Animator speed 1, kept for frames when the Animator is paused.</summary>
        public readonly Dictionary<int, float> StateLengths = new();
        /// <summary>The bake wrote her joints last frame (the copy's rest values go back when it stops).</summary>
        public bool ForeverOn;
        /// <summary>This frame's Forever clip, or null on a state that plays OUAK's motion.</summary>
        public ForeverClip Clip;
        public int ClipState;
        public double ClipSeconds;
        public float ClipFrame, RowDiv = 1f;
        /// <summary>The playing row's own face group, or <see cref="ForeverPlayer.NoRowFace"/> when OUAK's table was not found.</summary>
        public int RowFace = ForeverPlayer.NoRowFace;

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

        /// <summary>
        /// The old-style card to show now: 0 for none. On a Forever clip, the card of the clip's face expression while the
        /// game shows the row's own face group; when the game shows another group (an override face, no face, the whistle),
        /// the game's group wins. The blink card stands in for the neutral card on the game's blink timer.
        /// </summary>
        public int CardNow(LlcmFace face)
        {
            if (Face == null) return face.NoFaceCard;
            int g = Face.prevFaceIndex;
            int card;
            if (Clip != null && g >= 0 && (RowFace == ForeverPlayer.NoRowFace || g == RowFace))
                card = ExpressionCard(face, Clip.Expression);
            else
            {
                if (g < 0 || g >= face.Groups.Length) return 0;
                card = face.Groups[g].Card;
            }
            if (card == face.BlinkOverCard && face.BlinkCard != 0 && Face.playBlink && Face.blinkState == 1) card = face.BlinkCard;
            return card;
        }

        /// <summary>The card for a Forever face expression (the .llcm's expression cards), the neutral card when out of range.</summary>
        public static int ExpressionCard(LlcmFace face, int expression) =>
            expression >= 0 && expression < face.ExpressionCards.Length && face.ExpressionCards[expression] != 0 ? face.ExpressionCards[expression] : face.NoFaceCard;

        /// <summary>The layer of the game's body_m (her objects follow it).</summary>
        public int BodyLayer => LayerSource != null ? LayerSource.layer : Model.gameObject.layer;

        /// <summary>Keeps her objects on the layer of the game's body_m (the game re-layers whole models later).</summary>
        public void FollowLayer()
        {
            int layer = BodyLayer;
            if (layer == LastLayer) return;
            LastLayer = layer;
            Marker.gameObject.layer = layer;
            foreach (var p in Pieces) p.Go.layer = layer;
        }

        /// <summary>Her antenna piece shows while the game's antenna object is active (hats that hide the antenna switch it off).</summary>
        public void FollowAntenna()
        {
            if (Antenna == null || Op == null) return;
            var a = Op._antennaObject;
            bool on = a == null || a.activeSelf;
            if (Antenna.Renderer.enabled != on) Antenna.Renderer.enabled = on;
        }

        /// <summary>The game's renderers HideGame switched off, to switch back on when the model is undressed.</summary>
        public readonly List<Renderer> HiddenByMod = new();
        private readonly HashSet<IntPtr> _hiddenPtrs = new();

        /// <summary>True when the mod's hide pass switched this renderer off.</summary>
        public bool HidByMod(Renderer r) => r != null && _hiddenPtrs.Contains(r.Pointer);

        /// <summary>
        /// Switches off every renderer under the model that is not hers (presents the game loads later included, so this
        /// repeats). Everything under her marker is left alone: her joints, her attach nodes and the presents she wears.
        /// </summary>
        public int HideGame()
        {
            int n = 0;
            var all = Root.GetComponentsInChildren<Renderer>(true);
            var underMarker = new HashSet<IntPtr>();
            if (Marker != null)
            {
                foreach (var r in Marker.GetComponentsInChildren<Renderer>(true)) if (r != null) underMarker.Add(r.Pointer);
                // A game skinned renderer under her marker is a body suit made on one of her nodes before the present
                // pass gave its entry back: it is bound to the game's joints and is never hers, so it is hidden too.
                foreach (var s in Marker.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    if (s != null && !Own.Contains(s.Pointer)) underMarker.Remove(s.Pointer);
            }
            for (int i = 0; i < all.Length; i++)
            {
                var r = all[i];
                if (r is null || Own.Contains(r.Pointer) || underMarker.Contains(r.Pointer)) continue;
                if (r.enabled)
                {
                    r.enabled = false;
                    n++;
                    if (_hiddenPtrs.Add(r.Pointer)) HiddenByMod.Add(r);
                }
            }
            LastHidden = n;
            TotalHidden += n;
            return n;
        }
    }
}
