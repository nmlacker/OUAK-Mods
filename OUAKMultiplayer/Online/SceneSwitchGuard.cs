using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Il2CppApp.KatamariSin;
using MelonLoader;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OUAKMultiplayer
{
    /// <summary>
    /// Keeps other players' ghosts and cousins out of the way while the game changes scene (the user's report,
    /// 2026-11-08: crashes going from the Prince's Ship to a scroll page, odd things in the plazas). The cousins are the
    /// game's own character models (MultiplayModelLoader: MainGameOuji, OujiPresent, ModelSetup... switched off, not
    /// removed) kept across scenes, and the ghosts' katamaris too; while a scene sets itself up, the game's code that
    /// looks for its characters must not find another player's. So:
    /// <list type="bullet">
    /// <item>the moment the game starts loading a scene (its AssetLoader.LoadScene / LoadSceneAsync, every scene change
    /// goes through them; additive loads within a scene don't count) every ghost and cousin is switched off;</item>
    /// <item>they stay off until the new scene has been the active one for 1.5 s (at most 15 s);</item>
    /// <item>a scene change by any other way is caught the frame after (the active scene changed): 1.5 s off too.</item>
    /// </list>
    /// </summary>
    internal static class SceneSwitchGuard
    {
        private const float MaxHold = 15f, Settle = 1.5f;
        private static float _holdUntil;
        private static int _sceneAtHold = int.MinValue, _lastScene = int.MinValue;
        private static int _logged;

        /// <summary>Times ghosts were switched off for a scene change (self-test), and why the last time.</summary>
        public static int Holds { get; private set; }
        public static string LastReason { get; private set; } = "";

        public static bool Holding => Time.unscaledTime < _holdUntil;

        public static void Init(HarmonyLib.Harmony harmony)
        {
            int n = 0;
            try
            {
                var prefix = new HarmonyMethod(AccessTools.Method(typeof(SceneSwitchGuard), nameof(BeforeLoad)));
                foreach (var m in AccessTools.GetDeclaredMethods(typeof(AssetLoader)).Where(m => m.Name is "LoadScene" or "LoadSceneAsync"))
                {
                    harmony.Patch(m, prefix: prefix);
                    n++;
                }
            }
            catch (Exception e) { MelonLogger.Warning($"[Online] Scene change guard: {e.Message}"); }
            MelonLogger.Msg($"[Online] Scene change guard: watching {n} of the game's scene loads.");
        }

        /// <summary>
        /// The game starts loading a scene. Additive loads (LoadSceneMode.Additive, or the bool overloads' true) add
        /// to the scene that's already there and aren't a change of place.
        /// </summary>
        private static void BeforeLoad(MethodBase __originalMethod, object[] __args)
        {
            try
            {
                string name = __args != null && __args.Length > 0 ? __args[0] as string ?? "?" : "?";
                bool additive = false;
                if (__args != null)
                    foreach (var a in __args.Skip(1))
                    {
                        if (a is LoadSceneMode mode) { additive = mode == LoadSceneMode.Additive; break; }
                        if (a is bool b) { additive = b; break; }
                    }
                if (_logged++ < 40)
                    MelonLogger.Msg($"[Online] Scene change guard: the game loads \"{name}\" ({__originalMethod?.Name}, {(additive ? "added to this scene" : "a new scene")}).");
                if (!additive) Hold($"loading {name}", MaxHold);
            }
            catch { }
        }

        public static void Hold(string why, float seconds)
        {
            _sceneAtHold = ActiveScene();
            _holdUntil = Mathf.Max(_holdUntil, Time.unscaledTime + seconds);
            Holds++;
            LastReason = why;
            try { Ghosts.HideAllNow(); } catch { }
            try { HubPresence.HideAllNow(); } catch { }
        }

        /// <summary>Each frame, before the ghosts update: a new active scene settles for 1.5 s.</summary>
        public static void Tick()
        {
            int scene = ActiveScene();
            if (_lastScene != int.MinValue && scene != _lastScene)
            {
                if (!Holding) Hold("the scene changed", Settle);
                else _holdUntil = Mathf.Min(_holdUntil, Time.unscaledTime + Settle);
            }
            else if (Holding && scene != _sceneAtHold && _holdUntil - Time.unscaledTime > Settle)
                _holdUntil = Time.unscaledTime + Settle;
            _lastScene = scene;
        }

        private static int ActiveScene()
        {
            try { return SceneManager.GetActiveScene().m_Handle; }
            catch { return 0; }
        }
    }
}
