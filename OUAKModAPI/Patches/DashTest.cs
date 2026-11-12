using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Il2CppApp.KatamariSin;
using Il2CppApp.KatamariSin.InputStatus;
using UnityEngine;
using Chance = Il2CppApp.KatamariSin.MultiplaySetting.KingChanceType;

namespace OUAKModAPI
{
    /// <summary>
    /// Test only (<see cref="PatchTest"/> tokens "dashstory" / "dashbattle"): real dashes, started with the sticks the way
    /// a player does (the dash wiggle, written into the game's pad buffers after <c>InputController.Tick</c>), to measure
    /// unlimited dash (<see cref="BattleDash"/>): a normal dash for comparison, then with Shoes (and in battles the King
    /// Chance) a dash that keeps going past its 1.5 s, braking, a second dash at no cost, and the dash stopping when the
    /// unlimited dash ends. Every dash end is classified by what ended it (a wall, QuitOujiDash, its unlimited dash
    /// ending, or its own timer).
    /// </summary>
    internal static class DashTest
    {
        internal static bool Running { get; private set; }
        internal static bool Done { get; private set; }

        private static Action<string, bool, string> _check;
        private static Action<string> _msg;
        private static bool _battle;
        private static IEnumerator<object> _script;
        private static object _wait;
        private static Vector2 _l, _r;
        private static bool _hooked;
        private static MainGameCore C => GameRefs.Main?.GetCore();

        // what ended the last dash
        private static string _lastEnd = "";
        private static float _endAt = -1f, _quitAt = -1f, _wallAt = -1f;
        private static bool _wasDashing;
        private static int _starts;
        private static float _unlimitedEndedAt = -1f;
        private static bool _wasUnlimited;

        private static readonly Vector2 Up = new(0f, 1f), Down = new(0f, -1f);

        private sealed class Wait { public int Frames = -1; public float Until = -1f; public Func<bool> Cond; public float Deadline; }
        private static Wait Frames(int n) => new() { Frames = n };
        private static Wait Secs(float s) => new() { Until = Time.time + s };
        private static Wait When(Func<bool> c, float timeout) => new() { Cond = c, Deadline = Time.time + timeout };

        internal static void Begin(bool battle, Action<string, bool, string> check, Action<string> msg)
        {
            _battle = battle; _check = check; _msg = msg;
            Hook();
            _script = Script().GetEnumerator();
            _wait = null;
            Done = false;
            Running = true;
        }

        private static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            var h = ApiCore.Instance.HarmonyInstance;
            h.Patch(AccessTools.Method(typeof(InputController), "Tick"), postfix: new HarmonyMethod(AccessTools.Method(typeof(DashTest), nameof(AfterInputTick))));
            h.Patch(AccessTools.Method(typeof(MainGameCore), "QuitOujiDash"), prefix: new HarmonyMethod(AccessTools.Method(typeof(DashTest), nameof(BeforeQuit))));
            h.Patch(AccessTools.Method(typeof(MainGameCore), "WallStop"), prefix: new HarmonyMethod(AccessTools.Method(typeof(DashTest), nameof(BeforeWall))),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(DashTest), nameof(AfterWall))));
            h.Patch(AccessTools.Method(typeof(MainGameCore), nameof(MainGameCore.ExecuteOujiDash)), postfix: new HarmonyMethod(AccessTools.Method(typeof(DashTest), nameof(AfterStart))));
        }

        private static bool Mine(MainGameCore c) { try { return Running && c != null && C != null && c.Pointer == C.Pointer; } catch { return false; } }
        private static void BeforeQuit(MainGameCore __instance) { if (Mine(__instance) && __instance._oujiDashTimer > 0f) _quitAt = Time.time; }
        private static void BeforeWall(MainGameCore __instance, out float __state)
        {
            __state = -1f;
            if (!Mine(__instance) || __instance._oujiDashTimer <= 0f) return;
            if (_wallGuard) { __state = __instance._oujiDashTimer; _guarded++; return; }
            _wallAt = Time.time;
        }

        // Test only: with the wall guard on, a wall doesn't end the dash (so a dash can be followed past 1.5 s on a cramped map).
        private static void AfterWall(MainGameCore __instance, float __state)
        {
            if (__state > 0f) __instance._oujiDashTimer = __state;
        }

        private static bool _wallGuard;
        private static int _guarded;
        private static void AfterStart(MainGameCore __instance) { if (Mine(__instance)) _starts++; }

        private static void AfterInputTick(InputController __instance)
        {
            if (!Running) return;
            try
            {
                Watch();
                Advance();
                Inject(__instance);
            }
            catch (Exception e) { _check("dash test ran", false, e.ToString()); Stop(); }
        }

        private static void Stop() { Running = false; Done = true; _l = _r = Vector2.zero; }

        // The end of each dash, and what ended it.
        private static void Watch()
        {
            var c = C;
            if (c == null) return;
            bool unl = BattleDash.Unlimited(c);
            if (_wasUnlimited && !unl) _unlimitedEndedAt = Time.time;
            _wasUnlimited = unl;
            bool dashing = c._oujiDashTimer > 0f;
            if (_wasDashing && !dashing)
            {
                float now = Time.time;
                _endAt = now;
                _lastEnd = now - _wallAt < 0.2f ? "wall" : now - _quitAt < 0.2f ? "brake / QuitOujiDash"
                    : now - _unlimitedEndedAt < 0.2f ? "the unlimited dash ended" : "its own timer";
            }
            _wasDashing = dashing;
        }

        private static void Advance()
        {
            for (int guard = 0; guard < 20; guard++)
            {
                if (_wait is Wait w)
                {
                    if (w.Frames >= 0) { if (w.Frames-- > 0) return; }
                    else if (w.Until >= 0f) { if (Time.time < w.Until) return; }
                    else if (w.Cond != null) { bool ok = false; try { ok = w.Cond(); } catch { } if (!ok && Time.time < w.Deadline) return; }
                }
                if (!_script.MoveNext()) { Stop(); return; }
                _wait = _script.Current;
            }
        }

        private static void Inject(InputController ic)
        {
            var pad = ic.Pad((InputController.ePadNo)0);
            var idx = pad?.bufferIndexNow;
            if (idx == null || idx.Length == 0) return;
            int cur = idx[0];
            var sl = pad.keylistStickLeft; var sr = pad.keylistStickRight;
            if (sl != null && cur < sl.Length) sl[cur] = _l;
            if (sr != null && cur < sr.Length) sr[cur] = _r;
        }

        private static void Fwd() { _l = _r = Up; }
        private static void Neutral() { _l = _r = Vector2.zero; }

        // The dash wiggle (tank controls): L up + R down, then L down + R up, 7 steps of 6 frames.
        private static IEnumerable<object> Wiggle()
        {
            for (int i = 0; i < 7; i++) { bool up = i % 2 == 0; _l = up ? Up : Down; _r = up ? Down : Up; yield return Frames(6); }
            Fwd();
        }

        private static Vector3 _spawn;
        private static float _best;

        private static IEnumerable<object> Place(float angle)
        {
            Neutral();
            yield return Secs(0.3f);
            C.TeleportPlayer(_spawn, angle, false);
            yield return Secs(0.5f);
        }

        private static string D(MainGameCore c) =>
            $"timer {c._oujiDashTimer:0.00}, used {c._oujiDashElapsedTimer:0.00}/{c.MaxOujiDashTime:0.0}, out-of-dash wait {c._oujiDashExhaustTimer:0.00}, " +
            $"speed {new Vector2(c._nowSpeed.x, c._nowSpeed.z).magnitude:0.00}, unlimited {BattleDash.Unlimited(c)}";

        // One dash with the sticks; returns through the callbacks how long it ran, how far, the top speed and what ended it.
        private static IEnumerable<object> Dash(string name, float maxWatch, Action<float, float, float, string> done, Action<float> during = null)
        {
            int starts = _starts;
            for (int attempt = 0; attempt < 3 && (_starts == starts || C._oujiDashTimer <= 0f); attempt++)
            {
                if (attempt > 0) { foreach (var x in Place(_best)) yield return x; Fwd(); yield return Secs(0.4f); starts = _starts; }
                foreach (var x in Wiggle()) yield return x;
                yield return When(() => _starts > starts && C._oujiDashTimer > 0f, 1.5f);
            }
            if (_starts == starts || C._oujiDashTimer <= 0f) { done(0f, 0f, 0f, "no dash started"); yield break; }
            float t0 = Time.time;
            var p0 = C.transform.position;
            float top = 0f;
            _msg($"{name}: dash started: {D(C)}");
            while (C._oujiDashTimer > 0f && Time.time - t0 < maxWatch)
            {
                top = Mathf.Max(top, new Vector2(C._nowSpeed.x, C._nowSpeed.z).magnitude);
                during?.Invoke(Time.time - t0);
                yield return Frames(1);
            }
            var p1 = C.transform.position;
            float len = Time.time - t0;
            string end = C._oujiDashTimer > 0f ? "still dashing" : _lastEnd;
            _msg($"{name}: ran {len:0.00} s, {new Vector2(p1.x - p0.x, p1.z - p0.z).magnitude:0.0} travelled, top speed {top:0.00}, ended by {end}; {D(C)}");
            done(len, new Vector2(p1.x - p0.x, p1.z - p0.z).magnitude, top, end);
        }

        private static void UseShoes()
        {
            var gm = GameRefs.Main;
            gm.EndItemEffect(true);
            gm.SetInventoryItem(eInstageItemType.Shoes, _battle, true);
            gm.ActivateItemEffect();
        }

        private static IEnumerable<object> Script()
        {
            var c = C;
            Neutral();
            yield return Secs(0.5f);
            _spawn = c.transform.position;
            // The most open heading from here (8 tries, 1.5 s of rolling each).
            float a0 = c._coreAngle, bestD = -1f;
            for (int k = 0; k < 8; k++)
            {
                float a = a0 + 45f * k;
                foreach (var x in Place(a)) yield return x;
                var p0 = C.transform.position;
                Fwd(); yield return Secs(1.5f);
                var p1 = C.transform.position;
                float d = new Vector2(p1.x - p0.x, p1.z - p0.z).magnitude;
                if (d > bestD) { bestD = d; _best = a; }
            }
            _msg($"most open heading {_best:0} ({bestD:0.0} in 1.5 s)");

            // 1. A normal dash, for comparison.
            foreach (var x in Place(_best)) yield return x;
            C._oujiDashElapsedTimer = 0f; C._oujiDashExhaustTimer = 0f;
            Fwd(); yield return Secs(0.4f);
            float nLen = 0f, nTop = 0f; string nEnd = "";
            foreach (var x in Dash("normal dash", 6f, (len, dist, top, end) => { nLen = len; nTop = top; nEnd = end; })) yield return x;
            float nUsed = C._oujiDashElapsedTimer;
            _check("a normal dash (no Shoes) times out or stops, and uses stamina", nLen > 0f && nLen < 1.8f && nUsed > 0.4f,
                $"ran {nLen:0.00} s, ended by {nEnd}, used {nUsed:0.00}");
            Neutral();
            yield return When(() => C._oujiDashTimer <= 0f, 3f);
            yield return Secs(0.5f);

            // 2. Unlimited dash (Shoes, made 12 s long for the test): nonstop, brake, a free second dash, the stop at the end.
            //    Walls end any dash within about a second here; to follow one dash past its 1.5 s the test keeps walls from
            //    ending it (test only), then lets them again.
            float oldStory = StoryFreebies.ShoesTime, oldBattle = BattleDash.ShoesTime;
            StoryFreebies.ShoesTime = 12f; BattleDash.ShoesTime = 12f;
            int free0 = BattleDash.FreeStarts, stop0 = BattleDash.StoppedAtEnd;
            try
            {
                foreach (var x in Place(_best)) yield return x;
                C._oujiDashElapsedTimer = 2.4f;   // nearly out of dash: Shoes must not care
                UseShoes();
                yield return When(() => BattleDash.Unlimited(C), 2f);
                _check("Shoes give unlimited dash", BattleDash.Unlimited(C), D(C));
                Fwd(); yield return Secs(0.3f);

                // a. With walls: what ends the dash is never its own timer.
                float uLen = 0f, uTop = 0f, uDist = 0f; string uEnd = "";
                float usedMax = 0f;
                foreach (var x in Dash("unlimited dash", 4.5f, (len, dist, top, end) => { uLen = len; uDist = dist; uTop = top; uEnd = end; },
                             _ => usedMax = Mathf.Max(usedMax, C._oujiDashElapsedTimer))) yield return x;
                _check("unlimited: a wall or a brake ends the dash, never its own timer", uLen > 0f && uEnd != "its own timer" && uEnd != "no dash started",
                    $"ran {uLen:0.00} s ({uDist:0.0} travelled, top speed {uTop:0.00} vs normal {nTop:0.00}), ended by {uEnd}");
                _check("unlimited: dashing uses no stamina, the start cost included", usedMax <= 0.001f && BattleDash.FreeStarts > free0,
                    $"most used {usedMax:0.00}, free starts {BattleDash.FreeStarts - free0}");
                Neutral();
                yield return When(() => C._oujiDashTimer <= 0f, 2f);
                yield return Secs(0.3f);

                // b. Walls kept from ending it (test only): the dash runs on past 1.5 s, then braking ends it.
                foreach (var x in Place(_best)) yield return x;
                Fwd(); yield return Secs(0.3f);
                _wallGuard = true; _guarded = 0;
                float gLen = 0f; string gEnd = "";
                float usedG = 0f;
                foreach (var x in Dash("unlimited dash, walls kept from ending it", 4f, (len, dist, top, end) => { gLen = len; gEnd = end; },
                             _ => usedG = Mathf.Max(usedG, C._oujiDashElapsedTimer))) yield return x;
                // Pushing into a wall the katamari climbs, and climbing ends a dash (QuitOujiDash): that's a wall too.
                _check("unlimited: the dash keeps going past 1.5 s (4 s watched; a climb or brake may end it, never its own timer)",
                    gLen >= 1.8f && gEnd != "its own timer" && usedG <= 0.001f,
                    $"ran {gLen:0.00} s, {gEnd}, walls hit {_guarded}x, most used {usedG:0.00}");
                _wallGuard = false;
                if (C._oujiDashTimer > 0f)
                {
                    // Brake: both sticks back.
                    float b0 = Time.time;
                    _l = _r = Down;
                    yield return When(() => C._oujiDashTimer <= 0f, 3f);
                    _check("unlimited: braking (both sticks back) or a wall stops the dash", C._oujiDashTimer <= 0f && _lastEnd != "its own timer",
                        $"ended by {_lastEnd} {Time.time - b0:0.00} s after pulling back; {D(C)}");
                }
                Neutral();
                yield return Secs(0.4f);

                // c. Another dash at once, free; 0.3 s into it the unlimited dash ends (battles: the host's own countdown,
                //    0.4 s left; story: the freebie ends): the running dash stops then. Walls kept from ending it.
                int free1 = BattleDash.FreeStarts;
                float used1 = C._oujiDashElapsedTimer;
                float sLen = 0f; string sEnd = "";
                float usedS = 0f;
                bool unlimitedAtStart = false, shortened = false;
                float endedAt = -1f;
                for (int attempt = 0; attempt < 3 && !shortened && BattleDash.Unlimited(C); attempt++)
                {
                    foreach (var x in Place(_best)) yield return x;
                    Fwd(); yield return Secs(0.3f);
                    unlimitedAtStart = BattleDash.Unlimited(C);
                    _wallGuard = true;
                    foreach (var x in Dash("second dash; its unlimited dash ends during it", 3f, (len, dist, top, end) => { sLen = len; sEnd = end; },
                                 t =>
                                 {
                                     if (BattleDash.Unlimited(C)) usedS = Mathf.Max(usedS, C._oujiDashElapsedTimer);
                                     if (shortened || t < 0.3f) return;
                                     shortened = true;
                                     endedAt = Time.time;
                                     if (_battle) { var st = GameRefs.Multiplay.GetUserStatusFromCoreNo(C.multiCoreNo); if (st != null) st.infinityDashTime = 0.4f; }
                                     else GameRefs.Main.EndItemEffect(true);
                                 })) yield return x;
                    _wallGuard = false;
                }
                _check("unlimited: you can dash again at once, still free", unlimitedAtStart && sLen > 0f && BattleDash.FreeStarts > free1 && usedS <= 0.001f,
                    $"free starts {BattleDash.FreeStarts - free1}, used before {used1:0.00}, most used while unlimited {usedS:0.00}");
                _check("unlimited: a running dash stops when the unlimited dash ends", shortened && sEnd == "the unlimited dash ended" && BattleDash.StoppedAtEnd > stop0,
                    $"ran {sLen:0.00} s, ended by {sEnd} {(endedAt > 0f ? $"(the end was started {Time.time - endedAt:0.00} s before the check)" : "(never shortened)")}; stopped at the end {BattleDash.StoppedAtEnd - stop0}x");
                yield return When(() => !BattleDash.Unlimited(C), 15f);
            }
            finally { _wallGuard = false; StoryFreebies.ShoesTime = oldStory; BattleDash.ShoesTime = oldBattle; }
            Neutral();
            yield return Secs(1f);

            // 3. After the Shoes: dashes are normal again.
            yield return When(() => !BattleDash.Unlimited(C), 15f);
            foreach (var x in Place(_best)) yield return x;
            C._oujiDashElapsedTimer = 0f;
            Fwd(); yield return Secs(0.3f);
            float aLen = 0f; string aEnd = "";
            foreach (var x in Dash("after the Shoes", 6f, (len, dist, top, end) => { aLen = len; aEnd = end; })) yield return x;
            _check("after the Shoes, a dash is normal again (times out or stops, uses stamina)", aLen > 0f && aLen < 1.8f && C._oujiDashElapsedTimer > 0.4f,
                $"ran {aLen:0.00} s, ended by {aEnd}, used {C._oujiDashElapsedTimer:0.00}");
            Neutral();
            yield return Secs(1f);

            // 4. Battles: the Unlimited dash King Chance, for every prince.
            if (_battle)
            {
                var mgr = GameRefs.Multiplay;
                KingChances.Fire(Chance.unexploded, filter: false);
                yield return When(() => BattleDash.Unlimited(C), 4f);
                var cores = KingChances.Cores(mgr).ToList();
                _check("King Chance: every prince has unlimited dash", cores.All(BattleDash.Unlimited),
                    string.Join(", ", cores.Select(x => $"{(x.isMultiCpu ? "cpu" : "you")} {BattleDash.Unlimited(x)}")));
                foreach (var x in Place(_best)) yield return x;
                C._oujiDashElapsedTimer = 2.9f;
                Fwd(); yield return Secs(0.3f);
                float kLen = 0f; string kEnd = "";
                float kUsed = 0f;
                _wallGuard = true;
                foreach (var x in Dash("King Chance dash, walls kept from ending it", 3f, (len, dist, top, end) => { kLen = len; kEnd = end; }, _ => kUsed = Mathf.Max(kUsed, C._oujiDashElapsedTimer))) yield return x;
                _wallGuard = false;
                _check("King Chance: the dash is free and never times out (3 s watched; a climb or wall may end it)",
                    kLen > 0f && kEnd != "its own timer" && kEnd != "no dash started" && kUsed <= 0.001f, $"ran {kLen:0.00} s, {kEnd}, most used {kUsed:0.00}");
                foreach (var core in cores) { var st = mgr.GetUserStatusFromCoreNo(core.multiCoreNo); if (st != null) st.infinityDashTime = 0f; mgr.SetInfinityDashOff(core); }
                Neutral();
            }
        }
    }
}
