using System;
using System.Collections.Generic;
using Il2CppApp.KatamariSin;
using MelonLoader;
using UnityEngine;
using OUAKModAPI;

namespace OUAKMultiplayer
{
    /// <summary>
    /// What a player's character is doing, as its own Animator has it: the state (full path hash) it's in or
    /// blending into, how far through it is, how fast it plays, and the face. The move number Katamari Ball sends
    /// (MainGameOuji.NowAnime) misses a lot: the look-up/look-around poses are held at a point by
    /// ApplySpecialAnime (Animator.Play(state, -1, t) every frame, not a new move), the animator's speed is
    /// AnimeSpeed times each move's own speed (SetOujiAnimeSpeed), emotes and other hub moves come from another
    /// table (CharacterAnimation's), and a repeated move doesn't count as a new one. Mirroring the Animator itself
    /// shows all of it, whatever table started it.
    /// </summary>
    internal struct PoseSync
    {
        /// <summary>0 = unknown (an older game, or no Animator): fall back to the move number.</summary>
        public int Hash;
        public float Time, Speed;
        /// <summary>MainGameOujiFace's face index, -1 if unknown.</summary>
        public sbyte Face;

        public PacketWriter Write(PacketWriter w) => w.I32(Hash).F32(Time).F32(Speed).U8((byte)Face);

        public static PoseSync Read(PacketReader r)
        {
            var p = new PoseSync { Hash = r.I32(), Time = r.F32(), Speed = r.F32(), Face = (sbyte)r.U8() };
            if (!float.IsFinite(p.Time) || p.Time < 0f || p.Time > 1e6f) p.Time = 0f;
            p.Speed = float.IsFinite(p.Speed) ? Mathf.Clamp(p.Speed, 0f, 20f) : 1f;
            return p;
        }

        public static PoseSync None => new() { Face = -1 };
    }

    /// <summary>
    /// The sender's side: reads its character's Animator. The speed sent is how fast the state's time really
    /// moved since the last read (in the Animator's units), so a pose the game holds still by replaying it at the
    /// same point every frame goes out as speed 0, and plays as a still pose on the other side.
    /// </summary>
    internal sealed class PoseReader
    {
        private int _hash;
        private float _time, _at = -1f, _speed = 1f;

        public PoseSync Read(Animator a, MainGameOujiFace face)
        {
            var p = PoseSync.None;
            if (a == null) return p;
            try
            {
                bool blending = a.IsInTransition(0);
                var info = blending ? a.GetNextAnimatorStateInfo(0) : a.GetCurrentAnimatorStateInfo(0);
                p.Hash = info.fullPathHash;
                p.Time = Mathf.Max(0f, info.normalizedTime);
                float now = UnityEngine.Time.unscaledTime, len = info.length, dt = now - _at;
                float animSpeed = a.speed;
                if (p.Hash == _hash && _at >= 0f && dt > 0.02f && len > 0.01f && float.IsFinite(len) && !blending)
                {
                    // Seconds of the state played per second: the Animator's speed when it plays freely, about 0 when held.
                    float measured = (p.Time - _time) * len / dt;
                    _speed = !float.IsFinite(measured) || measured < 0f ? animSpeed : Mathf.Clamp(measured, 0f, 20f);
                    if (Mathf.Abs(_speed - animSpeed) < 0.15f) _speed = animSpeed;
                }
                else if (p.Hash != _hash || _at < 0f) _speed = animSpeed;
                if (dt > 0.02f || p.Hash != _hash)
                {
                    _hash = p.Hash;
                    _time = p.Time;
                    _at = now;
                }
                p.Speed = _speed;
            }
            catch { p.Hash = 0; }
            try { if (face != null) p.Face = (sbyte)Mathf.Clamp(face.prevFaceIndex, -1, 127); }
            catch { }
            return p;
        }
    }

    /// <summary>
    /// The receiver's side: plays the sender's Animator state on a cousin puppet (Animator.Play / CrossFade by
    /// hash), at their speed, and keeps it in step: a moving state is corrected when it drifts by a quarter of a
    /// loop, a held pose follows exactly. The puppet's own CharacterAnimation is told to stand aside (IsPlay off)
    /// so its Update doesn't start other moves. When the puppet's Animator has no such state (another rig or an
    /// older sender), it hands back to the move number (CharacterAnimation.ReqestCharacterAnime).
    /// </summary>
    internal sealed class PoseTrack
    {
        public int Hash, AnimNo = -1;
        public bool Missing, Driving;
        public sbyte Face = -2;
        public MainGameOujiFace FaceComp;
        public bool FaceLooked;
        public readonly Dictionary<int, bool> Known = new();

        public void Reset()
        {
            Hash = 0;
            AnimNo = -1;
            Missing = false;
            Face = -2;
        }
    }

    internal static class PosePlayer
    {
        public static int Played, Corrected, FellBack;

        /// <summary>
        /// Shows <paramref name="p"/> on the puppet; <paramref name="age"/> is how long ago it was read on the sender.
        /// Falls back to the move number <paramref name="animNo"/> (and <paramref name="speed"/>) when it can't.
        /// </summary>
        public static void Apply(CousinPuppet c, PoseTrack t, PoseSync p, float age, int animNo, float speed)
        {
            var a = c.Animator;
            if (a == null) return;
            if (p.Hash != 0 && Drive(c, t, p, age))
            {
                if (Mathf.Abs(a.speed - p.Speed) > 1e-3f) a.speed = p.Speed;
                if (p.Face >= 0 && p.Face != t.Face)
                {
                    t.Face = p.Face;
                    var face = FaceOf(c, t);
                    if (face != null) face.SetFace(p.Face);
                }
                return;
            }
            // The move number, the way Katamari Ball shows a remote prince.
            FellBack++;
            if (t.Driving)
            {
                t.Driving = false;
                if (c.CharAnim != null) c.CharAnim.IsPlay = true;
            }
            if (c.CharAnim != null && animNo >= 0 && animNo != t.AnimNo)
            {
                t.AnimNo = animNo;
                c.CharAnim.ReqestCharacterAnime(animNo);
            }
            if (Mathf.Abs(a.speed - speed) > 1e-3f) a.speed = speed;
        }

        private static bool Drive(CousinPuppet c, PoseTrack t, PoseSync p, float age)
        {
            var a = c.Animator;
            if (!t.Known.TryGetValue(p.Hash, out bool has))
            {
                try { has = a.HasState(0, p.Hash); }
                catch { has = false; }
                t.Known[p.Hash] = has;
            }
            if (!has)
            {
                t.Hash = p.Hash;
                t.Missing = true;
                return false;
            }
            if (!t.Driving)
            {
                t.Driving = true;
                t.AnimNo = -1;
                if (c.CharAnim != null) c.CharAnim.IsPlay = false;
            }
            float target = p.Time;
            if (p.Hash != t.Hash || t.Missing)
            {
                t.Hash = p.Hash;
                t.Missing = false;
                a.CrossFade(p.Hash, 0.12f, 0, target);
                Played++;
                return true;
            }
            if (a.IsInTransition(0)) return true;
            var info = a.GetCurrentAnimatorStateInfo(0);
            float len = Mathf.Max(0.01f, info.length);
            target = p.Time + p.Speed * Mathf.Clamp(age, 0f, 0.5f) / len;
            bool still = p.Speed < 1e-3f;
            if (info.fullPathHash != p.Hash || Mathf.Abs(target - info.normalizedTime) > (still ? 0.004f : 0.25f))
            {
                a.Play(p.Hash, 0, target);
                Corrected++;
            }
            return true;
        }

        private static MainGameOujiFace FaceOf(CousinPuppet c, PoseTrack t)
        {
            if (t.FaceLooked) return t.FaceComp;
            t.FaceLooked = true;
            try
            {
                t.FaceComp = c.CharAnim != null ? c.CharAnim.MainGameOujiFace : null;
                if (t.FaceComp == null && c.Model != null) t.FaceComp = c.Model.GetComponentInChildren<MainGameOujiFace>(true);
            }
            catch { t.FaceComp = null; }
            return t.FaceComp;
        }

        public static string Describe() => $"{Played} state(s) played, {Corrected} correction(s), {FellBack} frame(s) by move number";
    }
}
