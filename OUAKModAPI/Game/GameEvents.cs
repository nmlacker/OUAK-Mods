using System;
using System.Collections.Generic;
using Il2CppApp.KatamariSin;
using UnityEngine;

namespace OUAKModAPI
{
    /// <summary>
    /// Things that happen in the game, as C# events, all raised on the main thread. Handlers that throw are logged
    /// (once each) and never stop the other handlers. Unsubscribe when your mod unloads (a <see cref="HotMod"/>
    /// can pass the unsubscribe to its Track method to have that done for it).
    /// <list type="bullet">
    /// <item><see cref="StageLoaded"/>: a stage (story stage or multiplayer map) was loaded and the player spawned.</item>
    /// <item><see cref="StagePlaying"/>: the player got control (after the fly-over and the King's talk), at every start and restart.</item>
    /// <item><see cref="StageRestarted"/>: the same stage started over (pause menu Retry, or a mod's restart).</item>
    /// <item><see cref="StageEnded"/>: the stage ended: the clear demo or the game over started (<c>cleared</c> says which).</item>
    /// <item><see cref="StageLeft"/>: the stage's scene went away.</item>
    /// </list>
    /// </summary>
    public static class GameEvents
    {
        /// <summary>Every frame, after the API's own work (MelonMod.OnUpdate time).</summary>
        public static event Action Update;
        /// <summary>Every frame, late (MelonMod.OnLateUpdate time).</summary>
        public static event Action LateUpdate;
        /// <summary>A scene finished loading (the scene's name).</summary>
        public static event Action<string> SceneLoaded;
        public static event Action<MainGameManager> StageLoaded;
        public static event Action<MainGameManager> StagePlaying;
        public static event Action<MainGameManager> StageRestarted;
        /// <summary>The stage ended; true when it was cleared (the goal was reached).</summary>
        public static event Action<MainGameManager, bool> StageEnded;
        public static event Action StageLeft;
        /// <summary>A plaza (hub) was entered; the era map's scene name, e.g. "Map_Edo".</summary>
        public static event Action<string> PlazaEntered;
        /// <summary>The Prince's Ship was entered.</summary>
        public static event Action ShipEntered;
        /// <summary>The pause menu opened (true) or closed (false).</summary>
        public static event Action<bool> PauseChanged;
        /// <summary>The game is closing.</summary>
        public static event Action Quit;

        /// <summary>True while the game's pause menu is open.</summary>
        public static bool Paused { get; private set; }

        private static IntPtr _stage;
        private static float _lastPlayTime = -1f;
        private static bool _wasRestarting, _wasRunning, _wasEnded, _plazaPending;
        private static float _plazaCheckAt, _lastRestartAt = -100f;
        private static readonly HashSet<string> _reported = new();

        internal static void RaiseUpdate() => Raise(Update);
        internal static void RaiseLateUpdate() => Raise(LateUpdate);
        internal static void RaiseQuit() => Raise(Quit);

        internal static void RaiseSceneLoaded(string scene)
        {
            Raise(SceneLoaded, scene);
            if (scene == "OujiShip") Raise(ShipEntered);
            // The plaza's active scene is its era map, which loads a moment after the hub scene.
            if (scene == "SelectHiroba") { _plazaPending = true; _plazaCheckAt = Time.unscaledTime + 0.5f; }
        }

        internal static void RaisePause(bool paused)
        {
            if (Paused == paused) return;
            Paused = paused;
            Raise(PauseChanged, paused);
        }

        /// <summary>Watches the stage manager (no hooks needed): new stage, restart, control, end.</summary>
        internal static void Tick()
        {
            if (_plazaPending && Time.unscaledTime >= _plazaCheckAt)
            {
                _plazaPending = false;
                Raise(PlazaEntered, UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
            }

            var gm = GameRefs.Main;
            if (gm == null || !gm.PlayerInitiated)
            {
                if (_stage != IntPtr.Zero && gm == null)
                {
                    _stage = IntPtr.Zero;
                    Paused = false;
                    Raise(StageLeft);
                }
                return;
            }
            if (gm.Pointer != _stage)
            {
                _stage = gm.Pointer;
                _lastPlayTime = -1f;
                _wasRestarting = _wasRunning = _wasEnded = false;
                Raise(StageLoaded, gm);
            }

            float played = gm._playTime;
            bool restarting = gm._isInRestartWait;
            // The clock dropping and the restart finishing are two signs of one restart: one event.
            float now = Time.unscaledTime;
            bool restarted = ((_wasRestarting && !restarting) || (_lastPlayTime >= 0f && played + 0.5f < _lastPlayTime)) && now - _lastRestartAt > 5f;
            _wasRestarting = restarting;
            _lastPlayTime = played;
            if (restarted)
            {
                _lastRestartAt = now;
                _wasRunning = _wasEnded = false;
                if (Paused) RaisePause(false);
                Raise(StageRestarted, gm);
            }

            bool ended = gm._isInClearDemo || gm._isInGameOver;
            if (ended && !_wasEnded) Raise(StageEnded, gm, gm._hasCleared);
            _wasEnded = ended;

            bool running = gm.IsGameRunning && !ended;
            // The pause menu closed some way other than its own Quit (a restart, leaving).
            if (Paused && running) RaisePause(false);
            if (running && !_wasRunning) Raise(StagePlaying, gm);
            if (running) _wasRunning = true;
        }

        // ---------------------------------------------------------------- safe raising

        private static void Raise(Action e)
        {
            if (e == null) return;
            foreach (var d in e.GetInvocationList())
                try { ((Action)d)(); } catch (Exception ex) { Report(d, ex); }
        }

        private static void Raise<T>(Action<T> e, T a)
        {
            if (e == null) return;
            foreach (var d in e.GetInvocationList())
                try { ((Action<T>)d)(a); } catch (Exception ex) { Report(d, ex); }
        }

        private static void Raise<T1, T2>(Action<T1, T2> e, T1 a, T2 b)
        {
            if (e == null) return;
            foreach (var d in e.GetInvocationList())
                try { ((Action<T1, T2>)d)(a, b); } catch (Exception ex) { Report(d, ex); }
        }

        internal static void Report(Delegate d, Exception ex)
        {
            string who = $"{d.Method.DeclaringType?.FullName}.{d.Method.Name}";
            if (_reported.Add(who)) ApiCore.Log.Warn($"Event handler {who} failed: {ex.InnerException?.Message ?? ex.Message}");
        }
    }
}
