using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppApp.KatamariSin;
using UnityEngine;

namespace OUAKModAPI
{
    /// <summary>
    /// Locks the game's own controls (keyboard and every pad) while a mod needs them: a menu, a text box, a
    /// prompt. It uses the flag the game itself sets while the Steam overlay is open (InputPadBase._steamLock):
    /// every game input query then reads "nothing pressed". Mods still read the keyboard and pads directly
    /// (<see cref="Input"/>, <see cref="Pad"/>).
    /// <para>Call <see cref="BlockThisFrame"/> every frame you need the lock (from Update or OnGUI); it lets go
    /// by itself the first frame nobody asks. The API never clears a lock it didn't set.</para>
    /// </summary>
    public static class GameInput
    {
        private static readonly HashSet<string> _requests = new();
        private static readonly HashSet<string> _last = new();
        private static bool _applied;
        private static int _holdFrames;

        /// <summary>Who holds the lock right now (for diagnostics).</summary>
        public static IReadOnlyCollection<string> Holders => _last;

        /// <summary>True while the API holds the game's controls.</summary>
        public static bool Blocked => _applied;

        /// <summary>Keeps the game's controls locked for this frame. <paramref name="who"/> names the holder.</summary>
        public static void BlockThisFrame(string who = "mod")
        {
            _requests.Add(who ?? "mod");
        }

        /// <summary>
        /// Keeps the lock a few more frames after the last request, so the key or button that closed a mod's
        /// popup doesn't also reach the game (Esc would open the pause menu, B would back out of it).
        /// </summary>
        public static void HoldBriefly(int frames = 3) => _holdFrames = Math.Max(_holdFrames, frames);

        /// <summary>Applies this frame's requests (the API calls it in LateUpdate).</summary>
        internal static void Apply()
        {
            if (TextInput.IsTyping) _requests.Add("text box");
            bool block = _requests.Count > 0;
            if (!block && _holdFrames > 0) { _holdFrames--; block = _applied; }
            _last.Clear();
            foreach (var r in _requests) _last.Add(r);
            _requests.Clear();
            if (!block && !_applied) return;
            InputController ic = null;
            try { ic = SingletonMonoBehaviour<InputController>.instance; } catch { }
            if (ic == null) { _applied = false; return; }
            var pads = ic.gamePad;
            if (pads == null) return;
            for (int i = 0; i < pads.Length; i++)
            {
                var pad = pads[i];
                if (pad != null && pad._steamLock != block) pad._steamLock = block;
            }
            _applied = block;
        }

        /// <summary>True when every one of the game's pads reads nothing (whoever locked them).</summary>
        public static bool PadsLocked
        {
            get
            {
                try
                {
                    var pads = SingletonMonoBehaviour<InputController>.instance?.gamePad;
                    if (pads == null || pads.Length == 0) return false;
                    for (int i = 0; i < pads.Length; i++)
                        if (pads[i] != null && !pads[i]._steamLock) return false;
                    return true;
                }
                catch { return false; }
            }
        }
    }

    /// <summary>
    /// The first gamepad, read directly through Unity's Input System (works while <see cref="GameInput"/> locks
    /// the game's controls). Every property is false when no pad is connected.
    /// </summary>
    public static class Pad
    {
        private static UnityEngine.InputSystem.Gamepad Current
        {
            get { try { return UnityEngine.InputSystem.Gamepad.current; } catch { return null; } }
        }

        public static bool Connected => Current != null;

        /// <summary>A / Cross (the bottom face button) went down this frame.</summary>
        public static bool ConfirmPressed => Pressed(p => p.buttonSouth);
        /// <summary>B / Circle (the right face button) went down this frame.</summary>
        public static bool BackPressed => Pressed(p => p.buttonEast);
        public static bool XPressed => Pressed(p => p.buttonWest);
        public static bool YPressed => Pressed(p => p.buttonNorth);
        public static bool UpPressed => Pressed(p => p.dpad.up);
        public static bool DownPressed => Pressed(p => p.dpad.down);
        public static bool LeftPressed => Pressed(p => p.dpad.left);
        public static bool RightPressed => Pressed(p => p.dpad.right);
        public static bool StartPressed => Pressed(p => p.startButton);
        public static bool SelectPressed => Pressed(p => p.selectButton);

        /// <summary>The left stick (x right, y up), -1..1.</summary>
        public static Vector2 LeftStick
        {
            get { try { var p = Current; return p != null ? p.leftStick.ReadValue() : Vector2.zero; } catch { return Vector2.zero; } }
        }

        /// <summary>The right stick (x right, y up), -1..1.</summary>
        public static Vector2 RightStick
        {
            get { try { var p = Current; return p != null ? p.rightStick.ReadValue() : Vector2.zero; } catch { return Vector2.zero; } }
        }

        private static bool Pressed(Func<UnityEngine.InputSystem.Gamepad, UnityEngine.InputSystem.Controls.ButtonControl> button)
        {
            try
            {
                var p = Current;
                return p != null && button(p).wasPressedThisFrame;
            }
            catch { return false; }
        }
    }
}
