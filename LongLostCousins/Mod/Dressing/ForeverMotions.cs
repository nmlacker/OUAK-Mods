using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Il2CppApp.KatamariSin;
using LongLostCousins.Data;
using OUAKModAPI;
using UnityEngine;

namespace LongLostCousins.Dressing
{
    /// <summary>One cousin style's Forever clips, baked onto her rig, by OUAK Animator state hash.</summary>
    internal sealed class ForeverSet
    {
        public CousinEntry Cousin;
        public string Style;
        public ForeverRig Rig;
        public Dictionary<(int Pack, int Slot), ForeverClip> Clips;
        public readonly Dictionary<int, ForeverClip> ByHash = new();
        /// <summary>The OUAK state name of each hash (for the log and the test).</summary>
        public readonly Dictionary<int, string> StateNames = new();
    }

    internal enum ForeverStatus { NoFolder, Reading, Ready, NotFound, WrongFiles, Failed, Skipped, NoMap }

    /// <summary>
    /// Katamari Forever's motions for the cousin styles that have a "forever" block in cousins.json, read from the
    /// player's own copy (the folder in settings.json) and baked in memory on a worker thread at start (file reads,
    /// inflating, hashing and the bake are plain .NET: no Unity or game call leaves the main thread). The main thread
    /// takes the result in <see cref="Poll"/>, hashes the state names and logs one line. The folder is never logged or
    /// shown: only found, not found or wrong files.
    /// </summary>
    internal static class ForeverMotions
    {
        private static readonly ModLog Log = LongLostCousinsMod.Log.In("Forever");

        public static ForeverStatus Status { get; private set; } = ForeverStatus.NoFolder;
        /// <summary>What went wrong, in words without the folder; null when nothing did.</summary>
        public static string Problem { get; private set; }
        public static int ClipCount { get; private set; }
        public static double ReadMs { get; private set; }
        public static double BakeMs { get; private set; }
        /// <summary>The test runs without the word forever: the folder is treated as unset.</summary>
        public static bool Skip;

        private static readonly List<ForeverSet> Sets = new();
        private static Job _job;
        private static int _generation;

        private sealed class Want
        {
            public CousinEntry Cousin;
            public string Style;
            public ForeverRig Rig;
            public LlcmModel Model;
            public Dictionary<(int, int), ForeverClip> Result;
        }

        private sealed class Job
        {
            public int Generation;
            public string Folder;
            public List<Want> Wants;
            public volatile bool Done;
            public ForeverFind Find;
            public string Why, Error;
            public double ReadMs, BakeMs;
        }

        /// <summary>The baked set for a dressed model's cousin style, or null (no copy, no map, or still reading).</summary>
        public static ForeverSet SetFor(CousinAssets a)
        {
            if (a == null || Status != ForeverStatus.Ready) return null;
            for (int i = 0; i < Sets.Count; i++)
                if (Sets[i].Cousin == a.Cousin && Sets[i].Style == a.Style) return Sets[i];
            return null;
        }

        /// <summary>The sets baked now (the test reads them).</summary>
        public static IReadOnlyList<ForeverSet> All => Sets;

        /// <summary>One line for the tab: whether the old style moves with Forever's motions.</summary>
        public static string Describe() => Status switch
        {
            ForeverStatus.Ready => $"Katamari Forever's motions: on ({ClipCount} motions read from your copy).",
            ForeverStatus.Reading => "Katamari Forever's motions: reading your copy...",
            ForeverStatus.NotFound => "Old style moves with this game's motions: no Katamari Forever copy found in the folder set. Set its folder to use Forever's motions.",
            ForeverStatus.WrongFiles => "Old style moves with this game's motions: the folder set is not the Katamari Forever copy the mod knows (" + Problem + ").",
            ForeverStatus.Failed => "Old style moves with this game's motions: your Katamari Forever copy could not be read (" + Problem + ").",
            ForeverStatus.Skipped => "Old style moves with this game's motions: the cousin test runs without Katamari Forever's motions.",
            ForeverStatus.NoMap => "Old style moves with this game's motions: no cousin here has a Katamari Forever map.",
            _ => "Old style moves with this game's motions: no Katamari Forever copy set. Set its folder to use Forever's motions.",
        };

        /// <summary>(Re)reads the copy in settings.json on a worker thread. Main thread. Never throws.</summary>
        public static void Start(string why)
        {
            try
            {
                _generation++;
                Sets.Clear();
                _job = null;
                ClipCount = 0;
                Problem = null;
                if (Skip) { Status = ForeverStatus.Skipped; Log.Info("Katamari Forever's motions: not used (the cousin test runs without the word forever)"); return; }
                string folder = Settings.ForeverFolder;
                if (string.IsNullOrWhiteSpace(folder)) { Status = ForeverStatus.NoFolder; Log.Info("Katamari Forever's motions: no folder set; the old style plays OUAK's motions"); return; }
                var wants = new List<Want>();
                foreach (var c in Catalog.Cousins)
                    foreach (var kv in c.Forever)
                    {
                        // Read on the main thread: the catalog's caches are not for workers.
                        var m = Catalog.Model(c, kv.Key);
                        if (m != null) wants.Add(new Want { Cousin = c, Style = kv.Key, Rig = kv.Value, Model = m });
                    }
                if (wants.Count == 0) { Status = ForeverStatus.NoMap; Log.Info("Katamari Forever's motions: no cousin style has a map"); return; }
                var job = new Job { Generation = _generation, Folder = folder, Wants = wants };
                _job = job;
                Status = ForeverStatus.Reading;
                Log.Info($"Katamari Forever's motions: reading the copy set in settings.json ({why})");
                new Thread(() => Run(job)) { IsBackground = true, Name = "LongLostCousins Forever" }.Start();
            }
            catch (Exception e)
            {
                Status = ForeverStatus.Failed;
                Problem = e.GetType().Name;
                Log.Warn("Katamari Forever's motions: could not start reading: " + e.GetType().Name);
            }
        }

        /// <summary>Worker thread: plain .NET only.</summary>
        private static void Run(Job job)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                job.Find = ForeverArchive.Read(job.Folder, out var files, out job.Why);
                job.ReadMs = sw.Elapsed.TotalMilliseconds;
                if (job.Find != ForeverFind.Found) return;
                sw.Restart();
                foreach (var w in job.Wants) w.Result = ForeverBaker.Bake(files, w.Model, w.Rig, w.Rig.Clips());
                job.BakeMs = sw.Elapsed.TotalMilliseconds;
            }
            catch (InvalidDataException e) { job.Error = e.Message; }
            catch (Exception e) { job.Error = e.GetType().Name; }
            finally { job.Done = true; }
        }

        /// <summary>MelonMod.OnLateUpdate: takes a finished read. Never throws.</summary>
        public static void Poll()
        {
            var job = _job;
            if (job == null || !job.Done) return;
            _job = null;
            if (job.Generation != _generation) return;
            try
            {
                ReadMs = job.ReadMs;
                BakeMs = job.BakeMs;
                if (job.Find == ForeverFind.NotFound) { Status = ForeverStatus.NotFound; Problem = job.Why; Log.Warn("Katamari Forever's motions: not found (" + job.Why + "); the old style plays OUAK's motions"); return; }
                if (job.Find == ForeverFind.WrongFiles) { Status = ForeverStatus.WrongFiles; Problem = job.Why; Log.Warn("Katamari Forever's motions: wrong files (" + job.Why + "); the old style plays OUAK's motions"); return; }
                if (job.Error != null) { Status = ForeverStatus.Failed; Problem = job.Error; Log.Warn("Katamari Forever's motions: found, but the bake failed (" + job.Error + "); the old style plays OUAK's motions"); return; }
                int clips = 0;
                var parts = new List<string>();
                foreach (var w in job.Wants)
                {
                    var set = new ForeverSet { Cousin = w.Cousin, Style = w.Style, Rig = w.Rig, Clips = w.Result };
                    foreach (var kv in w.Rig.States)
                    {
                        int h = Animator.StringToHash(kv.Key);
                        if (set.ByHash.ContainsKey(h) || !w.Result.TryGetValue(kv.Value, out var clip)) continue;
                        set.ByHash[h] = clip;
                        set.StateNames[h] = kv.Key;
                    }
                    Sets.Add(set);
                    clips += w.Result.Count;
                    parts.Add($"{w.Cousin.StyleLabel(w.Style)}: {w.Result.Count} clips for {set.ByHash.Count} states");
                }
                ClipCount = clips;
                Status = ForeverStatus.Ready;
                Log.Info($"Katamari Forever's motions: found; {string.Join("; ", parts)} (read {job.ReadMs:0} ms, baked {job.BakeMs:0} ms, off the main thread)");
            }
            catch (Exception e)
            {
                Sets.Clear();
                Status = ForeverStatus.Failed;
                Problem = e.GetType().Name;
                Log.Warn("Katamari Forever's motions: could not be taken in: " + e.GetType().Name);
            }
        }
    }

    /// <summary>
    /// OUAK's own motion table (an OujiAnimationSetting the game's drivers use), read at run time: each Animator state's
    /// row speed and face group, by state hash. Only the first row of a state counts (row 0 for 0000, which rows 77 to 79
    /// share and the game never asks for).
    /// </summary>
    internal sealed class OuakRows
    {
        private static readonly Dictionary<IntPtr, OuakRows> Cache = new();
        public readonly Dictionary<int, (float Speed, int Face)> ByHash = new();
        public string Name;

        public static OuakRows Of(OujiAnimationSetting s)
        {
            if (s == null) return null;
            if (Cache.TryGetValue(s.Pointer, out var r)) return r;
            r = new OuakRows { Name = s.name };
            var list = s.list;
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                {
                    var d = list[i];
                    if (d == null) continue;
                    string n = d.animatorStateName;
                    if (string.IsNullOrEmpty(n)) continue;
                    int h = Animator.StringToHash(n);
                    if (!r.ByHash.ContainsKey(h)) r.ByHash[h] = (d.animationSpeed, d.faceNo);
                }
            Cache[s.Pointer] = r;
            return r;
        }
    }

    /// <summary>
    /// Plays the baked Forever clips on dressed models. Each LateUpdate (after the game's Animator has run), the game
    /// Animator's state picks the clip: a state the map names plays Forever's clip on all of her joints; any other state
    /// keeps today's copy of the game's joints (<see cref="DressedModel.Follow"/>, her waist 7.415 cm above the game's).
    /// <para>Time: Forever seconds = normalizedTime x the state's length at Animator speed 1 x Forever's speed / the row
    /// speed the stage drivers multiply in. normalizedTime already runs at the Animator's speed times the state's own
    /// speed, so pause, the game's pace changes and the state speeds (0031 x2, 0041 x1.5, 0048 x2.5, 0055 x2, 0060 x3)
    /// come along, and the row speed is divided out where MainGameOuji or UIMainGameOuji put it there. Loops wrap at
    /// Forever's clip length when its loop byte is set; otherwise the last frame holds.</para>
    /// </summary>
    internal static class ForeverPlayer
    {
        private static readonly ModLog Log = LongLostCousinsMod.Log.In("Forever");
        public const int NoRowFace = int.MinValue;

        /// <summary>At dressing: her Animator, which driver plays it, and OUAK's motion table. Only for a style with a Forever map.</summary>
        public static void Attach(DressedModel dm)
        {
            if (dm.Assets.Cousin.ForeverOf(dm.Assets.Style) == null) return;
            dm.Anim = dm.Model != null && dm.Model.parent != null ? dm.Model.parent.GetComponent<Animator>() : null;
            if (dm.Anim == null) dm.Anim = dm.Root.GetComponentInChildren<Animator>(true);
            OujiAnimationSetting setting = null;
            // The stage drivers multiply the row speed into Animator.speed: found at or above the model.
            var t = dm.Root.transform;
            for (int i = 0; i < 10 && t != null && setting == null; i++, t = t.parent)
            {
                var mg = t.GetComponent<MainGameOuji>();
                if (mg != null) { setting = mg._animeSetting; dm.StageDriven = true; dm.Driver = "MainGameOuji"; break; }
                var ug = t.GetComponent<UIMainGameOuji>();
                if (ug != null) { setting = ug._animeSetting; dm.StageDriven = true; dm.Driver = "UIMainGameOuji"; break; }
                var ca = t.GetComponent<CharacterAnimation>();
                if (ca != null && ca._animeSetting != null) { setting = ca._animeSetting; dm.Driver = "CharacterAnimation"; break; }
                var uo = t.GetComponent<UIOuji>();
                if (uo != null && uo._animeSetting != null) { setting = uo._animeSetting; dm.Driver = "UIOuji"; break; }
                var pa = t.GetComponent<PlayCharacterAnimator>();
                if (pa != null && pa.m_OujiAnimations != null) { setting = pa.m_OujiAnimations; dm.Driver = "PlayCharacterAnimator"; break; }
            }
            if (setting == null)
            {
                var ca = dm.Root.GetComponentInChildren<CharacterAnimation>(true);
                if (ca != null && ca._animeSetting != null) { setting = ca._animeSetting; dm.Driver = "CharacterAnimation"; }
            }
            if (setting == null)
            {
                var g = GameRefs.Global;
                if (g != null && g.oujiAnimeSetting != null) { setting = g.oujiAnimeSetting; dm.Driver = "none found (GlobalManager's table)"; }
            }
            dm.Rows = OuakRows.Of(setting);
            if (dm.Driver == null) dm.Driver = "none found";
            Log.Info($"{dm.Label}: motions driven by {dm.Driver}{(dm.StageDriven ? " (row speed divided out)" : "")}, OUAK's table " +
                     (dm.Rows != null ? $"{dm.Rows.Name} with {dm.Rows.ByHash.Count} states" : "not found (faces follow the game's groups only off Forever states)") +
                     $", Animator {(dm.Anim != null ? "found" : "missing")}");
        }

        /// <summary>
        /// Each LateUpdate before the copy: on a state the map names, writes all of her joints from the bake and returns
        /// true; otherwise puts back what the bake wrote and the copy does not (rest local positions) and returns false.
        /// No managed allocation.
        /// </summary>
        public static bool Step(DressedModel dm)
        {
            dm.Clip = null;
            var set = ForeverMotions.SetFor(dm.Assets);
            var anim = dm.Anim;
            if (set == null || anim == null) { Leave(dm); return false; }
            var st = anim.GetCurrentAnimatorStateInfo(0);
            int h = st.m_Name;
            if (!set.ByHash.TryGetValue(h, out var clip)) { Leave(dm); return false; }

            // The state's length at Animator speed 1 (clip length / the state's own speed): m_Length is divided by the
            // Animator's speed as well. Kept per state for a paused Animator.
            float speed = anim.speed;
            float len = st.m_Length * speed;
            if (speed > 1e-4f && len > 0f && len < 1e5f) dm.StateLengths[h] = len;
            else if (!dm.StateLengths.TryGetValue(h, out len)) len = 0f;
            float div = 1f;
            int face = NoRowFace;
            if (dm.Rows != null && dm.Rows.ByHash.TryGetValue(h, out var row))
            {
                face = row.Face;
                if (dm.StageDriven && row.Speed > 1e-4f) div = row.Speed;
            }
            float nt = st.m_NormalizedTime;
            if (!(nt > 0f)) nt = 0f;
            double seconds = (double)nt * len * clip.Speed / div;
            float x = clip.FrameAt(seconds);

            var her = dm.HerJoints;
            if (!dm.ForeverOn)
            {
                // From the copy: it may have left a scale on a joint (OUAK's 0101 scales the mouth); the bake has none.
                for (int j = 0; j < her.Length; j++) her[j].localScale = U.One;
                dm.ForeverOn = true;
            }
            for (int j = 0; j < her.Length; j++)
            {
                clip.Sample(x, j, out float px, out float py, out float pz, out float qx, out float qy, out float qz, out float qw);
                her[j].SetLocalPositionAndRotation(U.V3(px, py, pz), U.Q(qx, qy, qz, qw));
            }
            dm.Clip = clip;
            dm.ClipState = h;
            dm.ClipSeconds = seconds;
            dm.ClipFrame = x;
            dm.RowFace = face;
            dm.RowDiv = div;
            return true;
        }

        /// <summary>Back from a Forever state: the rest local values the copy does not write (every position but the waist's).</summary>
        private static void Leave(DressedModel dm)
        {
            if (!dm.ForeverOn) return;
            dm.ForeverOn = false;
            var joints = dm.Data.Joints;
            var her = dm.HerJoints;
            for (int j = 0; j < her.Length; j++)
            {
                var jt = joints[j];
                if (!jt.CopiesPosition) her[j].localPosition = U.V3(jt.LocalPosition[0], jt.LocalPosition[1], jt.LocalPosition[2]);
                if (!jt.CopiesRotation) her[j].localRotation = U.Q(jt.LocalRotation[0], jt.LocalRotation[1], jt.LocalRotation[2], jt.LocalRotation[3]);
            }
        }
    }
}
