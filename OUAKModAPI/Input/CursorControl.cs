using System;
using HarmonyLib;
using Il2CppApp.KatamariSin;
using UnityEngine;

namespace OUAKModAPI
{
    /// <summary>
    /// The API owns the mouse pointer: nothing moves it but the player, and one rule decides when it shows.
    /// <para>What the game did (InputPadInputSystem.UpdateInput): after 3 s without mouse movement, or on any key
    /// or pad press, it saved the pointer's position, warped it to (10, 10) (the bottom-left corner in the Input
    /// System's coordinates) and hid it; when the mouse moved while hidden, it warped it back. Anything else that
    /// showed the pointer meanwhile (the game's menus, UnityExplorer's "Force Unlock Mouse", a mod window) left it
    /// visible in the corner, and the game re-parked it every frame.</para>
    /// <para>Now:
    /// every warp of the desktop pointer is blocked (Unity's Mouse.WarpCursorPosition, which the game and Unity's
    /// virtual mouse use, and the game's own WarpMousePos);
    /// the game's own show / hide in UpdateInput is undone the moment it happens;
    /// the pointer shows on any mouse movement, click or scroll, and hides on controller input or after
    /// <see cref="HideAfter"/> seconds without mouse input. The keyboard doesn't hide it.</para>
    /// <para>The game's confinement to its window in stages (Cursor.lockState Confined) is left alone: it never moves
    /// the pointer. Mod windows and prompts unlock it while they're up.</para>
    /// </summary>
    public static class CursorControl
    {
        /// <summary>Seconds without mouse input before the pointer hides.</summary>
        public static float HideAfter = 3f;

        /// <summary>The rule shows the pointer now (mouse used recently, no controller since).</summary>
        public static bool Shown { get; private set; }

        /// <summary>The hooks are in place.</summary>
        public static bool Active { get; private set; }

        /// <summary>Counts for diagnostics.</summary>
        public static int WarpsBlocked, GameShowHideUndone;

        private static float _lastMouse = -999f;
        private static bool _pad, _visBefore, _havePos;
        private static Vector3 _lastPos;

        internal static void Init(HarmonyLib.Harmony harmony, MelonLoader.MelonPreferences_Category prefs)
        {
            var on = prefs.GetEntry<bool>("ControlMousePointer") ?? prefs.CreateEntry("ControlMousePointer", true, "Control the mouse pointer",
                "Stop the game moving the mouse pointer (to the bottom-left corner), and show it only while you use the mouse: hidden on controller input or after 3 s without mouse input. Restart the game after changing.");
            if (!on.Value) { ApiCore.Log.Info("Cursor control is off (ControlMousePointer = false): the game handles the pointer itself."); return; }
            int ok = 0;
            ok += Patch(harmony, AccessTools.Method(typeof(UnityEngine.InputSystem.Mouse), nameof(UnityEngine.InputSystem.Mouse.WarpCursorPosition)), prefix: nameof(NoWarp));
            ok += Patch(harmony, AccessTools.Method(typeof(InputPadInputSystem), nameof(InputPadInputSystem.WarpMousePos)), prefix: nameof(NoWarp));
            ok += Patch(harmony, AccessTools.Method(typeof(InputPadInputSystem), nameof(InputPadInputSystem.UpdateInput)), prefix: nameof(BeforeGameInput), postfix: nameof(AfterGameInput));
            Active = ok == 3;
            if (!Active) ApiCore.Log.Warn($"Cursor control: only {ok} of 3 hooks are in place.");
        }

        private static int Patch(HarmonyLib.Harmony harmony, System.Reflection.MethodInfo target, string prefix = null, string postfix = null)
        {
            try
            {
                if (target == null) throw new MissingMethodException("not found");
                harmony.Patch(target,
                    prefix: prefix != null ? new HarmonyMethod(AccessTools.Method(typeof(CursorControl), prefix)) : null,
                    postfix: postfix != null ? new HarmonyMethod(AccessTools.Method(typeof(CursorControl), postfix)) : null);
                return 1;
            }
            catch (Exception e)
            {
                ApiCore.Log.Warn($"Cursor control couldn't hook {target?.DeclaringType?.Name}.{target?.Name}: {e.Message}");
                return 0;
            }
        }

        public static string Describe() =>
            $"cursor control {(Active ? "on" : "off")}: {WarpsBlocked} pointer warp(s) blocked, the game's show/hide undone {GameShowHideUndone}x, shown now {Shown}";

        // ---------------------------------------------------------------- hooks

        private static bool NoWarp()
        {
            if (++WarpsBlocked == 1) ApiCore.Log.Info("Cursor: blocked the game moving the mouse pointer (it stays where you put it).");
            return false;
        }

        private static void BeforeGameInput()
        {
            try { _visBefore = Cursor.visible; } catch { }
        }

        private static void AfterGameInput()
        {
            try
            {
                if (Cursor.visible == _visBefore) return;
                Cursor.visible = _visBefore;
                GameShowHideUndone++;
            }
            catch { }
        }

        // ---------------------------------------------------------------- the rule

        /// <summary>LateUpdate (after the game's input): show the pointer for the mouse, hide it for a controller or when idle.</summary>
        internal static void LateTick()
        {
            if (!Active || !Application.isFocused) return;
            float now = Time.unscaledTime;
            if (MouseUsed()) { _lastMouse = now; _pad = false; }
            else if (PadUsed()) _pad = true;
            bool want = !_pad && now - _lastMouse < HideAfter;
            Shown = want;
            if (Cursor.visible != want) Cursor.visible = want;
        }

        private static bool MouseUsed()
        {
            try
            {
                var pos = Input.mousePosition;
                bool moved = _havePos && (pos - _lastPos).sqrMagnitude > 0.25f;
                _lastPos = pos;
                _havePos = true;
                if (moved) return true;
                if (Input.mouseScrollDelta.sqrMagnitude > 0f) return true;
                return Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2);
            }
            catch { return false; }
        }

        private static bool PadUsed()
        {
            try
            {
                var g = UnityEngine.InputSystem.Gamepad.current;
                if (g == null) return false;
                const float dead = 0.3f;
                if (g.leftStick.ReadValue().sqrMagnitude > dead * dead || g.rightStick.ReadValue().sqrMagnitude > dead * dead) return true;
                if (g.leftTrigger.ReadValue() > dead || g.rightTrigger.ReadValue() > dead) return true;
                return g.buttonSouth.isPressed || g.buttonEast.isPressed || g.buttonWest.isPressed || g.buttonNorth.isPressed ||
                       g.leftShoulder.isPressed || g.rightShoulder.isPressed || g.startButton.isPressed || g.selectButton.isPressed ||
                       g.dpad.up.isPressed || g.dpad.down.isPressed || g.dpad.left.isPressed || g.dpad.right.isPressed ||
                       g.leftStickButton.isPressed || g.rightStickButton.isPressed;
            }
            catch { return false; }
        }
    }
}
