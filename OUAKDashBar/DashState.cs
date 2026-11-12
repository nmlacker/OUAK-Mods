using System;
using Il2CppApp.KatamariSin;
using OUAKModAPI;
using UnityEngine;

namespace OUAKDashBar
{
    /// <summary>What the prince's dash is doing (see <see cref="DashState"/>).</summary>
    public enum DashPhase
    {
        /// <summary>No katamari, or the state can't be read.</summary>
        None,
        /// <summary>Full stamina, not dashing.</summary>
        Full,
        /// <summary>A full dash is running and using stamina.</summary>
        Dashing,
        /// <summary>Stamina ran out: no dashing until the wait ends, then the stamina is full again at once.</summary>
        Exhausted,
        /// <summary>Not dashing, stamina coming back (0.1 s of dash per second).</summary>
        Regenerating,
        /// <summary>Unlimited dash (Shoes, or the game's debug switch): dashing uses no stamina.</summary>
        Unlimited,
    }

    /// <summary>
    /// The prince's dash stamina, read from the game's own timers on <see cref="MainGameCore"/> each frame.
    /// <para>The game's rules (<c>MainGameCore.ExecuteOujiDashTimerSingle</c>, traced in GameAssembly.dll; the
    /// numbers are the game's constants and Stage_Big1's measured values):</para>
    /// <list type="bullet">
    /// <item><c>_oujiDashElapsedTimer</c> is the stamina <b>used</b>, in seconds of dash, out of <c>MaxOujiDashTime</c> (3).
    /// Every dash start adds <c>AddTimePerDashActive</c> (0.5); a full dash adds its running time too (up to 1.5 s).</item>
    /// <item>Exhausted (used &gt; max) is checked first: <c>_oujiDashExhaustTimer</c> counts up; past
    /// <c>OUJI_DASH_EXHAUST_TIME</c> (8 s) the stamina is full at once (used = 0).</item>
    /// <item>Dashing (<c>_oujiDashTimer</c> &gt; 0): used goes up by the frame time unless <c>GlobalManager._debugInifiniteDash</c>
    /// is on; going over max quits the dash and starts the exhaustion.</item>
    /// <item>Otherwise used goes down by <c>OUJI_DASH_RECOVERY_RATE</c> (0.1) per second, from the very first frame
    /// after the dash: there is no delay before regeneration except the exhaustion.</item>
    /// </list>
    /// </summary>
    public struct DashState
    {
        /// <summary>The game's <c>OUJI_DASH_EXHAUST_TIME</c>: seconds of exhaustion before the stamina refills.</summary>
        public const float ExhaustTime = 8f;
        /// <summary>The game's <c>OUJI_DASH_RECOVERY_RATE</c>: seconds of dash regained per second.</summary>
        public const float RecoveryRate = 0.1f;

        public DashPhase Phase;
        /// <summary>Stamina left, 0..1 (1 = full).</summary>
        public float Fill;
        /// <summary>Stamina used and the most there is, in seconds of dash.</summary>
        public float Used, Max;
        /// <summary>
        /// Seconds until the stamina starts coming back: the exhaustion's wait (then it's full at once), or while dashing
        /// the time left on the dash (plus the exhaustion if the dash will use the stamina up first). 0 when it already is.
        /// </summary>
        public float RegenIn;
        /// <summary>True when <see cref="RegenIn"/> ends in a full refill (exhaustion) rather than the slow regeneration.</summary>
        public bool RefillsAtOnce;
        /// <summary>Seconds until the stamina is full again at the regeneration rate (0 when full).</summary>
        public float FullIn;
        /// <summary>Seconds left of the Shoes' unlimited dash (-1 when unknown or not from Shoes).</summary>
        public float UnlimitedLeft;
        /// <summary>Raw game values, for logs.</summary>
        public float DashTimer, ExhaustTimer;

        public bool Valid => Phase != DashPhase.None;

        /// <summary>Reads <paramref name="core"/> (the player's katamari) now.</summary>
        public static DashState Read(MainGameCore core)
        {
            var s = new DashState { Phase = DashPhase.None, UnlimitedLeft = -1f };
            if (core == null) return s;
            try
            {
                s.Max = core.MaxOujiDashTime;
                s.Used = core._oujiDashElapsedTimer;
                s.DashTimer = core._oujiDashTimer;
                s.ExhaustTimer = core._oujiDashExhaustTimer;
            }
            catch { return s; }
            if (!(s.Max > 0f)) return s;

            // Unlimited dash: the API's BattleDash.Unlimited (battles: the prince's own flag, Shoes or the King Chance; story
            // stages: the API's Shoes), which the API makes free and nonstop (2026-11-09: no start cost, no drain, a running
            // dash doesn't time out). Also the game's own debug switch, which a mod's cheat may turn on (the game's
            // behaviour: no drain while dashing, starts still cost).
            bool unlimited = false, battle = false;
            try { unlimited = BattleDash.Unlimited(core); } catch { }
            try { battle = GameRefs.IsMultiplay && BattleDash.Has(core); } catch { }
            try { unlimited |= GameRefs.Global != null && GameRefs.Global._debugInifiniteDash; } catch { }

            s.Fill = Mathf.Clamp01(1f - s.Used / s.Max);
            bool exhausted = s.Used > s.Max;
            bool dashing = s.DashTimer > 0f;
            if (exhausted)
            {
                // The timer function's first branch: nothing else runs (the dash timer isn't even counted down).
                s.Phase = DashPhase.Exhausted;
                s.RegenIn = Mathf.Max(0f, ExhaustTime - s.ExhaustTimer);
                s.RefillsAtOnce = true;
            }
            else if (dashing && !unlimited)
            {
                s.Phase = DashPhase.Dashing;
                float left = s.Max - s.Used;          // stamina left; the dash uses it 1:1
                if (s.DashTimer > left) { s.RegenIn = left + ExhaustTime; s.RefillsAtOnce = true; }   // runs out mid-dash
                else s.RegenIn = s.DashTimer;
            }
            else if (unlimited)
            {
                s.Phase = DashPhase.Unlimited;
                s.UnlimitedLeft = battle ? BattleLeft(core) : ShoesLeft();
                s.RegenIn = dashing ? s.DashTimer : 0f;
            }
            else if (s.Used > 0f) s.Phase = DashPhase.Regenerating;
            else s.Phase = DashPhase.Full;

            if (s.RefillsAtOnce) s.FullIn = s.RegenIn;
            else if (s.Phase == DashPhase.Dashing) s.FullIn = s.DashTimer + (s.Used + s.DashTimer) / RecoveryRate;
            else s.FullIn = (dashing ? s.DashTimer : 0f) + Mathf.Max(0f, s.Used) / RecoveryRate;
            return s;
        }

        /// <summary>
        /// Battles: the time left on the prince's unlimited dash, <c>UserStatus.infinityDashTime</c> (the match's host
        /// counts it down and switches the dash off at 0), or -1 when there's none to read.
        /// </summary>
        private static float BattleLeft(MainGameCore core)
        {
            try
            {
                var st = GameRefs.Multiplay?.GetUserStatusFromCoreNo(core.multiCoreNo);
                return st != null && st.infinityDashTime > 0f ? st.infinityDashTime : -1f;
            }
            catch { return -1f; }
        }

        /// <summary>The Shoes' countdown on the HUD (the API's story-freebie patch runs it), or -1.</summary>
        private static float ShoesLeft()
        {
            try
            {
                if (!StoryFreebies.UnlimitedDash) return -1f;
                var gm = GameRefs.Main;
                if (gm == null || gm._nowInventoryItem != eInstageItemType.Shoes) return -1f;
                return Mathf.Max(0f, gm._itemEffectTimer);
            }
            catch { return -1f; }
        }

        public override string ToString() =>
            FormattableString.Invariant($"{Phase} fill {Fill:0.000} used {Used:0.000}/{Max:0.0} timer {DashTimer:0.000} exhaust {ExhaustTimer:0.000} regenIn {RegenIn:0.00}{(RefillsAtOnce ? " (refill)" : "")} fullIn {FullIn:0.0} shoes {UnlimitedLeft:0.0}");
    }
}
