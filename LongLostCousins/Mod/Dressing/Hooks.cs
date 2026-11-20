using System;
using System.Collections.Generic;
using Il2CppApp.KatamariSin;
using LongLostCousins.Select;
using OUAKModAPI;

namespace LongLostCousins.Dressing
{
    /// <summary>
    /// The mod's seven dressing hooks on the game. They exist only while a cousin is worn (<see cref="Chosen"/>): with no
    /// cousin worn, only the select screen's slot hooks (and, while the settings name her, its marker hooks and the
    /// Prince-base hooks) are in place (Select/SelectScreen.cs, PrinceBase.cs). Each handler only looks at the model and
    /// hands it on; the one exception is OujiPresent.Start on the player's Prince base, which reads the saved cousin as 0
    /// for that call (and names the place, for the places dressed from there). None lets an exception reach the game.
    /// </summary>
    internal static class Hooks
    {
        private static readonly ModLog Log = LongLostCousinsMod.Log.In("Hooks");
        private static HookSet _set;

        public const int Expected = 7;
        /// <summary>How many hooks are in place now.</summary>
        public static int Count => _set?.Count ?? 0;
        public static bool Installed => _set != null;

        /// <summary>Makes the hooks (once). Logs how many could be made; the ones that could not are left out.</summary>
        public static void Install()
        {
            if (_set != null) return;
            var set = new HookSet("LongLostCousins");
            set.Prefix(typeof(MainGameOuji), nameof(MainGameOuji.InitializeOuji), typeof(Hooks), nameof(BeforeInitializeOuji), new[] { typeof(MainGameCore) });
            set.Prefix(typeof(MainGameOuji), nameof(MainGameOuji.InitializeOujiByEndingCore), typeof(Hooks), nameof(BeforeInitializeOujiByEnding), new[] { typeof(EndingGameCore) });
            set.Prefix(typeof(UIMainGameOuji), nameof(UIMainGameOuji.Initialize), typeof(Hooks), nameof(BeforeStageHudInitialize), Type.EmptyTypes);
            set.Prefix(typeof(UIOuji), nameof(UIOuji.Initialize), typeof(Hooks), nameof(BeforePlazaUiInitialize), Type.EmptyTypes);
            set.Hook(typeof(OujiPresent), nameof(OujiPresent.Start), typeof(Hooks), nameof(BeforePresentStart), nameof(AfterPresentStart), nameof(FinallyPresentStart), Type.EmptyTypes);
            set.Postfix(typeof(ResultMonoSetter), nameof(ResultMonoSetter.SetPlayCharacter), typeof(Hooks), nameof(AfterResultSet), Type.EmptyTypes);
            set.Postfix(typeof(ResultAAMonoSetter), nameof(ResultAAMonoSetter.SetPlayCharacter), typeof(Hooks), nameof(AfterResultAASet), Type.EmptyTypes);
            _set = set;
            if (set.Count == Expected) Log.Info($"{set.Count} hooks in place");
            else Log.Warn($"only {set.Count} of {Expected} hooks could be made; the contexts without one stay the game's (see the warnings above)");
        }

        /// <summary>Removes every hook and forgets queued models. Models already dressed stay dressed until they are gone.</summary>
        public static void Uninstall()
        {
            if (_set == null) return;
            _set.UnpatchAll();
            _set = null;
            Contexts.ClearPending();
            Log.Info("hooks removed");
        }

        private static void Failed(string where, Exception e)
        {
            try { Log.Warn($"{where}: {e.GetType().Name}: {e.Message}"); } catch { }
        }

        public static void BeforeInitializeOuji(MainGameOuji __instance)
        {
            try { Contexts.StagePlayer(__instance); }
            catch (Exception e) { Failed("InitializeOuji", e); }
        }

        public static void BeforeInitializeOujiByEnding(MainGameOuji __instance)
        {
            try { Contexts.Ending(__instance); }
            catch (Exception e) { Failed("InitializeOujiByEndingCore", e); }
        }

        public static void BeforeStageHudInitialize(UIMainGameOuji __instance)
        {
            try { Contexts.StageHud(__instance); }
            catch (Exception e) { Failed("UIMainGameOuji.Initialize", e); }
        }

        public static void BeforePlazaUiInitialize(UIOuji __instance)
        {
            try { Contexts.PlazaUi(__instance); }
            catch (Exception e) { Failed("UIOuji.Initialize", e); }
        }

        // OujiPresent.Start runs a frame or more after the loader that made the model (probe_results.md), so the loader's
        // scope is over by then. On a Prince model that is the player's (Contexts.PlayersPrince), Start gets the scope
        // again: the saved cousin reads 0, and the game's own "is this the player's model" test (_installing) says yes.
        private static readonly Stack<bool> PresentMarks = new();
        // The place's name when Start found the player's Prince where no hook of its own dresses it (Contexts.PlayersPrince).
        private static readonly Stack<string> PresentLabels = new();

        /// <summary>How many OujiPresent.Start calls ran in the scope this session, and the last decision (for the test).</summary>
        public static int PresentScopes { get; private set; }
        public static string LastPresentDecision { get; private set; }

        /// <summary>PrinceBase.LateTick: no game method is on the stack, so no Start call is open.</summary>
        public static void ClearPresentMarks() { PresentMarks.Clear(); PresentLabels.Clear(); }

        public static void BeforePresentStart(OujiPresent __instance)
        {
            bool mark = false;
            string label = null;
            try
            {
                mark = Contexts.PlayersPrince(__instance, out string why, out label);
                if (why != null)
                {
                    LastPresentDecision = $"{U.PathOf(__instance.transform)}: {(mark ? "the player's Prince base" : "not the player's")} ({why})";
                    if (Test.CousinTest.Running) Log.Info("OujiPresent.Start on " + LastPresentDecision);
                }
            }
            catch (Exception e) { Failed("OujiPresent.Start (before)", e); mark = false; label = null; }
            PresentMarks.Push(mark);
            PresentLabels.Push(mark ? label : null);
            if (mark) { PrinceBase.EnterScope(); PresentScopes++; }
        }

        public static void AfterPresentStart(OujiPresent __instance)
        {
            try { Contexts.PresentStarted(__instance, PresentLabels.Count > 0 ? PresentLabels.Peek() : null); }
            catch (Exception e) { Failed("OujiPresent.Start", e); }
        }

        public static Exception FinallyPresentStart(Exception __exception)
        {
            try
            {
                if (PresentLabels.Count > 0) PresentLabels.Pop();
                if (PresentMarks.Count > 0 && PresentMarks.Pop()) PrinceBase.LeaveScope();
            }
            catch { }
            return __exception;
        }

        public static void AfterResultSet(ResultMonoSetter __instance)
        {
            try { var h = __instance.m_PlayCharacter; Contexts.ResultCharacterSet(h != null ? h.transform : null, "ResultMonoSetter"); }
            catch (Exception e) { Failed("ResultMonoSetter.SetPlayCharacter", e); }
        }

        public static void AfterResultAASet(ResultAAMonoSetter __instance)
        {
            try { var h = __instance.m_PlayCharacter; Contexts.ResultCharacterSet(h != null ? h.transform : null, "ResultAAMonoSetter"); }
            catch (Exception e) { Failed("ResultAAMonoSetter.SetPlayCharacter", e); }
        }
    }
}
