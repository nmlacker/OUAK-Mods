using System;
using Il2CppApp.KatamariSin;
using OUAKModAPI;

namespace LongLostCousins.Dressing
{
    /// <summary>
    /// The mod's seven dressing hooks on the game. They exist only while a cousin is worn (<see cref="Chosen"/>, or the
    /// test runs): with no cousin worn, only the select screen's slot hooks (and, while the settings name her, its marker hooks) are in
    /// place (Select/SelectScreen.cs). Each handler
    /// only looks at the model and hands it on; none of them changes what the game method does, and none lets an
    /// exception reach the game.
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
            set.Postfix(typeof(OujiPresent), nameof(OujiPresent.Start), typeof(Hooks), nameof(AfterPresentStart), Type.EmptyTypes);
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

        public static void AfterPresentStart(OujiPresent __instance)
        {
            try { Contexts.PresentStarted(__instance); }
            catch (Exception e) { Failed("OujiPresent.Start", e); }
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
