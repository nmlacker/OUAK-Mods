using System;
using Il2Cpp;
using Il2CppApp.KatamariSin;
using OUAKModAPI;

namespace LongLostCousins.Test
{
    /// <summary>
    /// Test only: keeps the cousin test out of the player's save. While <see cref="Hold"/> is set, every save write the
    /// game makes (the save actions, a new stage result, the global save, star dust) is skipped. The test sets it for the
    /// whole game session and quits at the end, so nothing the test did is ever written.
    /// </summary>
    internal static class SaveGuard
    {
        private static readonly ModLog Log = LongLostCousinsMod.Log.In("Saves");
        private static readonly HookSet Set = new("LongLostCousins.SaveGuard");
        private static bool _installed;

        public static bool Hold;
        public static int Blocked { get; private set; }

        /// <summary>Makes the hooks. True only when every one is in place; the test must not load a slot otherwise.</summary>
        public static bool Install()
        {
            if (_installed) return true;
            bool ok = Set.Prefix(typeof(SaveDataController), nameof(SaveDataController.SetSaveAction), typeof(SaveGuard), nameof(Block))
                    & Set.Prefix(typeof(SaveDataController), nameof(SaveDataController.SetNewDataAction), typeof(SaveGuard), nameof(Block), Type.EmptyTypes)
                    & Set.Prefix(typeof(SaveDataController), nameof(SaveDataController.SetNewDataAction), typeof(SaveGuard), nameof(Block), new[] { typeof(int) })
                    & Set.Prefix(typeof(MainGameManager), nameof(MainGameManager.StoreGlobalSave), typeof(SaveGuard), nameof(Block))
                    & Set.Prefix(typeof(GlobalSaveData), nameof(GlobalSaveData.OnSaveNewData), typeof(SaveGuard), nameof(Block))
                    & Set.Prefix(typeof(SaveInfoResult.StarDustinfo), nameof(SaveInfoResult.StarDustinfo.AddStarDustStock), typeof(SaveGuard), nameof(Block));
            if (!ok) { Log.Error("could not hook every save write; the cousin test will not run"); Set.UnpatchAll(); return false; }
            _installed = true;
            return true;
        }

        public static bool Block()
        {
            if (!Hold) return true;
            Blocked++;
            try { Log.Info("blocked a save write (cousin test)"); } catch { }
            return false;
        }
    }
}
