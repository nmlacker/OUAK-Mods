using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using OUAKModAPI;

namespace OUAKMultiplayer
{
    /// <summary>
    /// Player-bar portraits, yours included: each look (NetworkUserInfo bytes) gets its own cousin model, loaded
    /// out of sight, kept opaque (ghosts are see-through), left to stand in its idle move for a moment so its
    /// pose and face are set, then photographed once (Portrait.Take) and put away. The picture is kept for as
    /// long as the game runs; a new look gets a new one. One model at a time.
    /// </summary>
    internal static class PortraitStudio
    {
        private const float Settle = 0.8f, GiveUp = 25f;
        private static readonly Vector3 Spot = new(0f, -4000f, 0f);
        private static readonly Dictionary<ulong, Texture> Shots = new();
        private static readonly HashSet<ulong> Failed = new(), Queued = new();
        private static readonly Queue<byte[]> Wanted = new();
        private static CousinPuppet _puppet;
        private static ulong _key;
        private static float _started, _shootAt;

        public static int Taken { get; private set; }

        /// <summary>The portrait for this look, or null while it's being taken (it's queued on the first ask).</summary>
        public static Texture For(byte[] look)
        {
            if (look == null || look.Length == 0) return null;
            ulong k = Key(look);
            if (Shots.TryGetValue(k, out var t) && t != null) return t;
            if (!Failed.Contains(k) && Queued.Add(k)) Wanted.Enqueue((byte[])look.Clone());
            return null;
        }

        /// <summary>Per frame (Update: it loads models and renders).</summary>
        public static void Tick()
        {
            float now = Time.unscaledTime;
            if (_puppet == null)
            {
                if (Wanted.Count == 0) return;
                var look = Wanted.Dequeue();
                _key = Key(look);
                try
                {
                    _puppet = Looks.Load(look, studio: true);
                    _started = now;
                    _shootAt = 0f;
                }
                catch (Exception e) { Done(null, e.Message); }
                return;
            }
            try
            {
                _puppet.Poll();
                if (_puppet.Failed || now - _started > GiveUp) { Done(null, "the model didn't load"); return; }
                if (!_puppet.Ready) return;
                // Far out of sight, upright and unscaled, in its idle move (the game's OUJI_MOT_WAIT).
                var t = _puppet.Model.transform;
                t.position = Spot;
                t.rotation = Quaternion.identity;
                t.localScale = Vector3.one;
                if (_shootAt <= 0f)
                {
                    _shootAt = now + Settle;
                    _puppet.CharAnim?.ReqestCharacterAnime(0);
                    return;
                }
                if (now < _shootAt) return;
                var tex = Portrait.Take(_puppet.Model, _puppet.ModelHeight);
                Done(tex, tex == null ? "the picture couldn't be taken" : null);
            }
            catch (Exception e) { Done(null, e.Message); }
        }

        private static void Done(Texture tex, string error)
        {
            if (tex != null)
            {
                Shots[_key] = tex;
                Taken++;
                MelonLogger.Msg($"[Online] Portrait taken ({Taken} so far).");
            }
            else
            {
                Failed.Add(_key);
                MelonLogger.Warning($"[Online] Couldn't take a portrait: {error}.");
            }
            Queued.Remove(_key);
            try { _puppet?.Destroy(); } catch { }
            _puppet = null;
        }

        /// <summary>FNV-1a over the look bytes.</summary>
        private static ulong Key(byte[] look)
        {
            ulong h = 14695981039346656037UL;
            foreach (byte b in look) { h ^= b; h *= 1099511628211UL; }
            return h;
        }

        public static string Describe() => $"{Taken} taken, {Wanted.Count} waiting, {(_puppet != null ? "one in progress" : "none in progress")}, {Failed.Count} failed";
    }

    /// <summary>Self-test: is a model animating? Its bones' rotations now against a moment ago.</summary>
    internal static class PoseProbe
    {
        private static readonly Dictionary<string, Quaternion[]> Last = new();

        /// <summary>Mean change of its bones' local rotations since the last sample under this name (degrees), or -1 on the first.</summary>
        public static float Sample(string name, GameObject model)
        {
            if (model == null) return -1f;
            var bones = model.GetComponentsInChildren<Transform>(true);
            var now = new Quaternion[bones.Length];
            for (int i = 0; i < bones.Length; i++) now[i] = bones[i].localRotation;
            float change = -1f;
            if (Last.TryGetValue(name, out var before) && before.Length == now.Length && now.Length > 0)
            {
                float sum = 0f;
                for (int i = 0; i < now.Length; i++)
                {
                    var a = before[i];
                    var b = now[i];
                    double dot = Math.Min(1.0, Math.Abs((double)a.x * b.x + (double)a.y * b.y + (double)a.z * b.z + (double)a.w * b.w));
                    sum += (float)(2.0 * Math.Acos(dot) * 180.0 / Math.PI);
                }
                change = sum / now.Length;
            }
            Last[name] = now;
            return change;
        }
    }
}
