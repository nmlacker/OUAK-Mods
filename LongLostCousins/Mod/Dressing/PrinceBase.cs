using System;
using System.Collections.Generic;
using Il2CppApp.KatamariSin;
using LongLostCousins.Select;
using OUAKModAPI;
using UnityEngine;

namespace LongLostCousins.Dressing
{
    /// <summary>
    /// While she is worn, every load of the local player's model builds the Prince (id 0) as her hidden base, and the
    /// game's save keeps the player's own cousin.
    /// <para>Stage and ending: GlobalManager._nowCharacterID (+0x1D4, a run-time copy the game never writes to the save) is
    /// set to 0 before each step of their StartSceneLoad, which picks the player's prefab from it. Ship and Plaza: inside
    /// one call of ModelLoader.SetUpPlayCharacterModel or one step of SelectHirobaLoad.HirobaLoad (each reads the saved
    /// cousin once, into ModelLoader._playerNow), the saved-cousin getter answers 0. The getter's one postfix is
    /// SelectScreen.AfterGetSelectPlayerId; this class holds the loader scope it reads (<see cref="Depth"/>), and
    /// OujiPresent.Start opens the same scope on the models those loads made (Hooks.BeforePresentStart).</para>
    /// <para>The getter's rewrites are suspended inside every save path: SaveDataController.SetSaveAction,
    /// SelectionMenuController.SetCharater, DataToConvert.MonoDataToSaveData and both StartSceneLoad steps (stage 20 keeps
    /// the getter's answer and later copies it back to the save). Those hooks are SelectScreen's always-installed slot set,
    /// so they never depend on this class; its StartSceneLoad prefixes call <see cref="ZeroForLoad"/> while these hooks
    /// are in. Suspension beats both scopes.</para>
    /// <para>The Plaza swap: CharacterMove.ChangeItokoHatoko undresses the model that becomes a cousin walking about, and
    /// CharacterMove.ChangeOuji has her taken off at the next LateUpdate (the save then holds the new cousin).</para>
    /// <para>The hooks exist while the settings name her or a dressed model is alive (the swap must undress it even after
    /// she was turned off). They come and go only in LateUpdate, never inside a hooked call. Every handler catches
    /// everything; none lets an exception reach the game.</para>
    /// </summary>
    internal static class PrinceBase
    {
        private static readonly ModLog Log = LongLostCousinsMod.Log.In("Prince base");
        private static readonly HashSet<string> Once = new(StringComparer.Ordinal);
        private static HookSet _set;
        private static bool _failed;

        /// <summary>
        /// SetUpPlayCharacterModel, HirobaLoad's MoveNext, ChangeItokoHatoko, ChangeOuji; and the other loaders of the
        /// player's model (<see cref="OtherLoaders"/>): the star map's two, Cosmo's, the mini theme's, the present collection
        /// popup's, and the AA results' asset step. (The StartSceneLoad steps and MonoDataToSaveData are SelectScreen's.)
        /// </summary>
        public const int Expected = 10;
        public static bool Installed => _set != null;
        public static int Count => _set?.Count ?? 0;

        private static int _loaderDepth, _presentDepth;
        /// <summary>Inside a player-model loader call, or OujiPresent.Start on a model one of them made: the saved cousin reads 0 here while she is worn.</summary>
        public static int Depth => _loaderDepth + _presentDepth;
        /// <summary>The mod set GlobalManager._nowCharacterID to 0 for her and has not put the save's cousin back yet.</summary>
        public static bool Zeroed;

        /// <summary>Counts for the test and the log (this session).</summary>
        public static int LoaderCalls { get; private set; }
        /// <summary>Getter reads answered 0 for her, inside a loader call and inside OujiPresent.Start (each counted once, the loader first).</summary>
        public static int LoaderRewrites { get; private set; }
        public static int PresentRewrites { get; private set; }
        public static int Rewrites => LoaderRewrites + PresentRewrites;
        public static int NowIdZeroed { get; private set; }
        public static int Undressed { get; private set; }

        /// <summary>The getter's postfix answered 0 for her in the scope: counted where it happened.</summary>
        public static void CountRewrite()
        {
            if (_loaderDepth > 0) LoaderRewrites++;
            else PresentRewrites++;
        }

        /// <summary>
        /// MelonMod.OnLateUpdate, right after the select screen's: no game method is on the stack, so the scope goes back
        /// to zero, and the hooks follow the settings and the dressed models. Never throws.
        /// </summary>
        public static void LateTick()
        {
            _loaderDepth = _presentDepth = 0;
            Hooks.ClearPresentMarks();
            try
            {
                bool want = (Settings.Active && Settings.Cousin != null) || Dresser.Models.Count > 0;
                if (want && _set == null && !_failed) Install();
                else if (!want && _set != null) Uninstall();
            }
            catch (Exception e) { Failed("the hooks", e); }
        }

        private static void Install()
        {
            var set = new HookSet("LongLostCousins.PrinceBase");
            set.Hook(typeof(ModelLoader), nameof(ModelLoader.SetUpPlayCharacterModel), typeof(PrinceBase),
                     nameof(EnterLoader), null, nameof(LeaveLoader), new[] { typeof(Vector3) });
            set.Hook(typeof(SelectHirobaLoad._HirobaLoad_d__15), nameof(SelectHirobaLoad._HirobaLoad_d__15.MoveNext), typeof(PrinceBase),
                     nameof(EnterLoader), null, nameof(LeaveLoader), Type.EmptyTypes);
            set.Prefix(typeof(CharacterMove), nameof(CharacterMove.ChangeItokoHatoko), typeof(PrinceBase), nameof(BeforeChangeItokoHatoko), Type.EmptyTypes);
            set.Postfix(typeof(CharacterMove), nameof(CharacterMove.ChangeOuji), typeof(PrinceBase), nameof(AfterChangeOuji), Type.EmptyTypes);
            OtherLoaders(set);
            _set = set;
            if (set.Count == Expected) Log.Info($"{set.Count} Prince-base hooks in place");
            else
            {
                // The made ones stay: each is safe alone, and the dresser refuses a base that is not the Prince.
                _failed = set.Count == 0;
                Log.Error($"only {set.Count} of the {Expected} Prince-base hooks could be made; where one is missing the game builds your own cousin and she is not shown there (see the warnings above)");
                if (_failed) { set.UnpatchAll(); _set = null; }
            }
        }

        private static void Uninstall()
        {
            _set.UnpatchAll();
            _set = null;
            Log.Info("Prince-base hooks removed (the settings no longer name her and no dressed model is left)");
        }

        private static void Failed(string where, Exception e)
        {
            try { if (Once.Add(where + e.GetType().Name)) Log.Warn($"{where}: {e.GetType().Name}: {e.Message}"); } catch { }
        }

        /// <summary>Into the scope for OujiPresent.Start on a model a scoped load made (Hooks).</summary>
        public static void EnterScope() => _presentDepth++;
        public static void LeaveScope() { if (_presentDepth > 0) _presentDepth--; }

        // ---------------------------------------------------------------- the loaders

        /// <summary>SetUpPlayCharacterModel and HirobaLoad's MoveNext, before them: one call (one iterator step) in the scope.</summary>
        public static void EnterLoader()
        {
            _loaderDepth++;
            LoaderCalls++;
        }

        /// <summary>
        /// The other places that load the player's model by reading the saved cousin once inside one call (or one iterator
        /// step), each scoped like the ship's loader so the read answers 0 and the place builds the Prince:
        /// OujiStarInitializer's LoadPlayerModel and Start steps (the star map; Start also gives the model its presents
        /// with the same read), CosmoSceneSetup's SetupCosmo step, MiniThemeManager's ModelLoad step,
        /// SutekiCollectionOujiPopup.Start (the present collection popup), and ResultAAMonoSetter's SetLoadMonoAsset step.
        /// That last one does not build a model (the AA results copy the stage's prefab, the Prince already): its read
        /// picks the results camera (cousins 73 and 74 get their own), so in the scope it picks the Prince's.
        /// The tutorial Plaza needs no scope: it always loads the Prince (id 0, not the saved cousin).
        /// The models these make are dressed from OujiPresent.Start (Contexts.PlayersPrince, then SortPending).
        /// </summary>
        private static void OtherLoaders(HookSet set)
        {
            set.Hook(typeof(OujiStarInitializer._LoadPlayerModel_d__9), nameof(OujiStarInitializer._LoadPlayerModel_d__9.MoveNext), typeof(PrinceBase),
                     nameof(EnterLoader), null, nameof(LeaveLoader), Type.EmptyTypes);
            set.Hook(typeof(OujiStarInitializer._Start_d__8), nameof(OujiStarInitializer._Start_d__8.MoveNext), typeof(PrinceBase),
                     nameof(EnterLoader), null, nameof(LeaveLoader), Type.EmptyTypes);
            set.Hook(typeof(CosmoSceneSetup._SetupCosmo_d__12), nameof(CosmoSceneSetup._SetupCosmo_d__12.MoveNext), typeof(PrinceBase),
                     nameof(EnterLoader), null, nameof(LeaveLoader), Type.EmptyTypes);
            set.Hook(typeof(MiniThemeManager._ModelLoad_d__50), nameof(MiniThemeManager._ModelLoad_d__50.MoveNext), typeof(PrinceBase),
                     nameof(EnterLoader), null, nameof(LeaveLoader), Type.EmptyTypes);
            set.Hook(typeof(SutekiCollectionOujiPopup), nameof(SutekiCollectionOujiPopup.Start), typeof(PrinceBase),
                     nameof(EnterLoader), null, nameof(LeaveLoader), Type.EmptyTypes);
            set.Hook(typeof(ResultAAMonoSetter._SetLoadMonoAsset_d__20), nameof(ResultAAMonoSetter._SetLoadMonoAsset_d__20.MoveNext), typeof(PrinceBase),
                     nameof(EnterLoader), null, nameof(LeaveLoader), Type.EmptyTypes);
        }

        /// <summary>Their finalizer: out of the scope, also when the game's method threw.</summary>
        public static Exception LeaveLoader(Exception __exception)
        {
            if (_loaderDepth > 0) _loaderDepth--;
            return __exception;
        }

        // ---------------------------------------------------------------- stage and ending

        /// <summary>
        /// A StartSceneLoad step, before it (called from SelectScreen's prefix, after it suspended the getter's rewrites for
        /// the step, and only while these hooks are in): while she is worn the run-time cousin id is 0, so the step builds
        /// the Prince.
        /// </summary>
        public static void ZeroForLoad(string what)
        {
            try
            {
                Chosen.RecordIfNone();
                if (GameRefs.IsMultiplay || !Chosen.Now) return;
                var g = GameRefs.Global;
                if (g == null) return;
                Zeroed = true;
                int was = g._nowCharacterID;
                if (was == 0) return;
                g._nowCharacterID = 0;
                NowIdZeroed++;
                Log.Info($"{what} load: GlobalManager._nowCharacterID {was} set to 0, so the {what} builds the Prince under her (the save keeps {Settings.DescribeRecord()})");
            }
            catch (Exception e) { Failed(what + " load", e); }
        }

        // ---------------------------------------------------------------- the Plaza swap

        /// <summary>
        /// CharacterMove.ChangeItokoHatoko, before it: this model stops being the player and walks off as a cousin, so it
        /// is undressed first (whether she is still worn or not: the order of the two swap calls does not matter).
        /// </summary>
        public static void BeforeChangeItokoHatoko(CharacterMove __instance)
        {
            try
            {
                if (__instance == null || Dresser.Models.Count == 0) return;
                var ca = __instance._animation;
                var dm = (ca != null ? Dresser.FindFor(ca.gameObject) : null) ?? Dresser.FindFor(__instance.gameObject);
                if (dm == null) return;
                Dresser.Undress(dm, "the Plaza swap made it a cousin walking about");
                Undressed++;
            }
            catch (Exception e) { Failed("CharacterMove.ChangeItokoHatoko", e); }
        }

        /// <summary>CharacterMove.ChangeOuji, after it: the save now holds the cousin swapped in, so she is taken off at the next LateUpdate.</summary>
        public static void AfterChangeOuji()
        {
            try
            {
                if (!Settings.Active) return;
                Log.Info($"Plaza swap: the save now holds {Chosen.Describe(Chosen.Effective(out int id, out int c) ? id : -1, c)}; {Settings.Describe()} is taken off at the next frame");
                Chosen.CheckSoon();
            }
            catch (Exception e) { Failed("CharacterMove.ChangeOuji", e); }
        }
    }
}
