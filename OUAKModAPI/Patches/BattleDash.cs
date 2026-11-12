using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppApp.KatamariSin;

namespace OUAKModAPI
{
    /// <summary>
    /// Unlimited dash in battles (<see cref="GamePatches.BattleDash"/>, part of the API): the Shoes freebie and the
    /// "Unlimited dash" King Chance.
    /// <para>The game's bug (traced 2026-11-07): both go through <c>MultiplayGameManager.MultiplayInfinityDash</c> /
    /// <c>MainGameCore.SetMultiInfinityDash(true)</c>, which sets the prince's own flag (<c>requestInfinityDash</c> /
    /// <c>requestInfinityDashValue</c>); the host counts <c>UserStatus.infinityDashTime</c> down and switches it off again.
    /// But the dash timer (<c>MainGameCore.ExecuteOujiDashTimerSingle</c>, which battles use too, from <c>CoreMultiMove</c>;
    /// <c>ExecuteOujiDashTimerMulti</c> is the same and never called) never reads that flag: it only skips the
    /// stamina count when the debug switch <c>GlobalManager._debugInifiniteDash</c> is on. So the dash runs out as usual
    /// (TCRF: the Shoes' unlimited dash "no longer functions").</para>
    /// <para>The fix: while a prince's dash timer runs and its own flag is on, the debug switch is on for that call only,
    /// so the game's own skip applies to that prince and nobody else. Battle Shoes also never set the flag (the host's
    /// item switch has no Shoes case): they now start it for <see cref="ShoesTime"/> (5 s).</para>
    /// <para>What unlimited dash does (the user's choice, 2026-11-09: "Both"), in battles and with the API's story Shoes
    /// (<see cref="StoryFreebies"/>): <b>nonstop</b>, a running dash doesn't run out: it goes on until you brake, hit a
    /// wall or the unlimited dash ends (then it stops, the way the game's unused end of the King Chance,
    /// <c>ForceOujiDashMultiCoolTime</c>, stops a running dash). It uses the game's own <c>ExtendOujiDash</c> (the dash
    /// timer back to 1.5 s), which the dash input calls while you keep wiggling the sticks during a dash. And <b>free</b>:
    /// no stamina at all, no 0.5 start cost (<c>ExecuteOujiDash</c> adds it even under the game's switch), never out of
    /// dash, so you can dash again at once. The game's switch alone only skipped the drain during a dash, and every dash
    /// still timed out after 1.5 s, which made 5 s of "unlimited dash" look like a normal one (traced 2026-11-09).</para>
    /// </summary>
    public static class BattleDash
    {
        /// <summary>How long battle Shoes give unlimited dash: 5 s (the user's choice; the game's bigBattleShoesTime is 10).</summary>
        public static float ShoesTime = 5f;

        /// <summary>Battle Shoes used (their unlimited dash started).</summary>
        public static int ShoesUsed { get; private set; }

        /// <summary>Test only: run the game's broken behaviour (to measure the difference).</summary>
        internal static bool Off;

        /// <summary>Dash timer updates that ran with unlimited dash (counts up while it works).</summary>
        public static int Frames { get; private set; }

        /// <summary>True while <paramref name="core"/> has the game's unlimited dash in a battle (Shoes or the King Chance).</summary>
        public static bool Has(MainGameCore core)
        {
            try { return core != null && core.requestInfinityDash && core.requestInfinityDashValue; }
            catch { return false; }
        }

        /// <summary>
        /// True while <paramref name="core"/> has unlimited dash: in a battle its own flag (<see cref="Has"/>), in a story
        /// stage the API's Shoes (<see cref="StoryFreebies.UnlimitedDash"/>, your prince). A mod's own cheat that only
        /// turns the game's debug switch on keeps the game's behaviour (no drain while dashing).
        /// </summary>
        public static bool Unlimited(MainGameCore core)
        {
            if (core == null) return false;
            if (Has(core)) return true;
            try { return !GameRefs.IsMultiplay && StoryFreebies.UnlimitedDash && GameRefs.Main?.GetCore()?.Pointer == core.Pointer; }
            catch { return false; }
        }

        /// <summary>Frames a running dash was kept going by unlimited dash (nonstop).</summary>
        public static int NonstopFrames { get; private set; }
        /// <summary>Dash starts that cost nothing under unlimited dash (free).</summary>
        public static int FreeStarts { get; private set; }
        /// <summary>Running dashes stopped because their unlimited dash ended.</summary>
        public static int StoppedAtEnd { get; private set; }

        // The princes that had unlimited dash at their last dash timer update (to stop a running dash when it ends).
        private static readonly HashSet<IntPtr> _unlimited = new();

        internal static bool Install()
        {
            var h = ApiCore.Instance?.HarmonyInstance ?? throw new InvalidOperationException("the API isn't initialised");
            // Battles run the same timer as story stages (CoreMultiMove -> ExecuteOujiDashTimerSingle); the "Multi"
            // one has no callers in this build, patched too in case a path uses it.
            foreach (var name in new[] { nameof(MainGameCore.ExecuteOujiDashTimerSingle), nameof(MainGameCore.ExecuteOujiDashTimerMulti) })
            {
                var m = AccessTools.Method(typeof(MainGameCore), name) ?? throw new InvalidOperationException($"MainGameCore.{name} not found");
                h.Patch(m, prefix: new HarmonyMethod(AccessTools.Method(typeof(BattleDash), nameof(Before))),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(BattleDash), nameof(After))));
            }
            // Shoes: the host's item switch (HostGameProcess) sends them to its "nothing" case, so they never start the
            // unlimited dash their King Box text promises ("Unlimited dash ({0} sec.)"). Start it the way the King Chance
            // does (MultiplayInfinityDash), for bigBattleShoesTime; the host counts it down and switches it off.
            h.Patch(AccessTools.Method(typeof(MultiplayGameManager), nameof(MultiplayGameManager.LocalUseItem)),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(BattleDash), nameof(AfterLocalUseItem))));
            // Free dash starts: ExecuteOujiDash adds the start cost (AddTimePerDashActive, 0.5) whatever the switch says.
            h.Patch(AccessTools.Method(typeof(MainGameCore), nameof(MainGameCore.ExecuteOujiDash)),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(BattleDash), nameof(BeforeDashStart))),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(BattleDash), nameof(AfterDashStart))));
            return true;
        }

        private static void AfterLocalUseItem(MultiplayGameManager __instance, MainGameCore __0, MultiplaySetting.KingBoxRewardType __1)
        {
            if (__1 != MultiplaySetting.KingBoxRewardType.shoes || __0 == null) return;
            try
            {
                var st = __instance.GetUserStatusFromCoreNo(__0.multiCoreNo);
                if (st != null && st.infinityDashTime < ShoesTime) st.infinityDashTime = ShoesTime;
                __0.SetMultiInfinityDash(true);
                ShoesUsed++;
            }
            catch (Exception e) { ApiCore.Log.Warn($"Shoes: {e.Message}"); }
        }

        private static void BeforeDashStart(MainGameCore __instance, out float __state)
        {
            __state = -1f;
            if (Off) return;
            try { if (Unlimited(__instance)) __state = __instance._oujiDashElapsedTimer; } catch { }
        }

        private static void AfterDashStart(MainGameCore __instance, float __state)
        {
            if (__state < 0f) return;
            try
            {
                if (__instance._oujiDashElapsedTimer != __state) FreeStarts++;
                __instance._oujiDashElapsedTimer = __state;
            }
            catch { }
        }

        private static void Before(MainGameCore __instance, out bool __state)
        {
            __state = false;
            if (Off) return;
            try
            {
                var core = __instance;
                if (Unlimited(core))
                {
                    // Free: never out of dash, nothing used.
                    if (core._oujiDashElapsedTimer > core.MaxOujiDashTime)
                    {
                        // Out of dash when it started: end that the way the game's own wait does when it runs out.
                        core._oujiHuhEffectTimer = 0f;
                        core._oujiHuhFirst = false;
                    }
                    core._oujiDashElapsedTimer = 0f;
                    core._oujiDashExhaustTimer = 0f;
                    // Nonstop: a running dash doesn't time out (the game's own ExtendOujiDash: the timer back to 1.5 s).
                    if (core._oujiDashTimer > 0f) { core.ExtendOujiDash(); NonstopFrames++; }
                    _unlimited.Add(core.Pointer);
                }
                else if (_unlimited.Remove(core.Pointer) && core._oujiDashTimer > 0f)
                {
                    // Its unlimited dash just ended: the running dash ends now, through the game's own end of a dash.
                    core._oujiDashTimer = 0.0001f;
                    StoppedAtEnd++;
                }
                if (!Has(core)) return;
                var g = GameRefs.Global;
                if (g == null || g._debugInifiniteDash) return;   // already on (a mod's own cheat): leave it alone
                g._debugInifiniteDash = true;
                __state = true;
                Frames++;
            }
            catch { }
        }

        private static void After(bool __state)
        {
            if (!__state) return;
            try { var g = GameRefs.Global; if (g != null) g._debugInifiniteDash = false; } catch { }
        }
    }
}
