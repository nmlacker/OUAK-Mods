using System;
using System.Collections.Generic;
using System.Linq;
using MelonLoader;
using UnityEngine;

namespace OUAKModAPI
{
    /// <summary>A key with modifiers, e.g. "Ctrl+R", "F5", "RightShift" (legacy Input KeyCode names).</summary>
    public struct KeyBinding
    {
        public KeyCode Key;
        public bool Ctrl, Alt, Shift;

        public bool IsNone => Key == KeyCode.None;

        public override string ToString()
        {
            if (IsNone) return "None";
            string s = (Ctrl ? "Ctrl+" : "") + (Alt ? "Alt+" : "") + (Shift ? "Shift+" : "");
            return s + Key;
        }

        public string Display => IsNone ? "(unbound)" : ToString().Replace("Alpha", "").Replace("Keypad", "Num ");

        public static KeyBinding Parse(string text)
        {
            var b = new KeyBinding();
            if (string.IsNullOrWhiteSpace(text)) return b;
            foreach (string raw in text.Split('+'))
            {
                string part = raw.Trim();
                if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) b.Ctrl = true;
                else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) b.Alt = true;
                else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) b.Shift = true;
                else if (Enum.TryParse(part, true, out KeyCode k)) b.Key = k;
            }
            return b;
        }

        public bool Same(KeyBinding o) => Key == o.Key && Ctrl == o.Ctrl && Alt == o.Alt && Shift == o.Shift;
    }

    /// <summary>One rebindable action (<see cref="Hotkeys.Register"/>).</summary>
    public sealed class HotkeyAction
    {
        public string Id;
        public string Label;
        /// <summary>Runs on the main thread (through <see cref="ModTasks"/>); returns the message to show.</summary>
        public Func<string> Run;
        public KeyBinding Binding;
        public MelonPreferences_Entry<string> Pref;
        /// <summary>The mod it belongs to (the Keys tab groups by it).</summary>
        public string Owner = "";
        /// <summary>Show the action's message on screen (default true).</summary>
        public bool Toast = true;
    }

    /// <summary>
    /// Rebindable hotkeys for every mod, saved in MelonPreferences (legacy Input, which this game keeps enabled).
    /// Register yours once at startup:
    /// <code>Hotkeys.Register(myCategory, "Boost", "Give a boost", "Ctrl+B", () =&gt; { ...; return "Boost!"; }, "My Mod");</code>
    /// The player rebinds them in the mod window's Keys tab (OUAK Mod Menu). Hotkeys are ignored while a text
    /// box has focus, while a confirmation or prompt is open, and while a binding is being captured.
    /// </summary>
    public static class Hotkeys
    {
        public static readonly List<HotkeyAction> Actions = new();
        public static HotkeyAction Capturing { get; private set; }
        /// <summary>The key that opens and closes the mod window.</summary>
        public static HotkeyAction Menu { get; private set; }
        private static bool _legacyInputBroken;

        private static KeyCode[] _captureKeys;
        private static KeyCode _heldModifier = KeyCode.None;
        private static int _captureFrame;

        internal static void Init(MelonPreferences_Category cat, KeyCode legacyMenuKey)
        {
            Menu = Register(cat, "OpenMenu", "Open / close the mod window", legacyMenuKey.ToString(), null, "OUAK API");
            _captureKeys = Enum.GetValues(typeof(KeyCode)).Cast<KeyCode>()
                .Where(k => k > KeyCode.None && k < KeyCode.Mouse0 && k != KeyCode.Escape)
                .Distinct().ToArray();
        }

        /// <summary>
        /// Adds a rebindable action. Its binding is stored in <paramref name="cat"/> as "Key_&lt;id&gt;" (so ids
        /// must be unique within the category). Registering the same category and id again (a hot mod reloading)
        /// updates the existing action instead of adding a second one.
        /// </summary>
        /// <param name="defaultBinding">e.g. "F5", "Ctrl+R", "Alt+Shift+K", "RightShift" or "None".</param>
        public static HotkeyAction Register(MelonPreferences_Category cat, string id, string label, string defaultBinding, Func<string> run, string owner = null)
        {
            string key = "Key_" + id;
            var existing = Actions.FirstOrDefault(a => a.Pref != null && a.Pref.Category == cat && a.Pref.Identifier == key);
            if (existing != null)
            {
                existing.Label = label;
                existing.Run = run;
                existing.Owner = owner ?? existing.Owner;
                return existing;
            }
            var pref = cat.GetEntry<string>(key) ?? cat.CreateEntry(key, defaultBinding ?? "None", label, "Key binding, e.g. F5, Ctrl+R, Alt+Shift+K, RightShift or None.");
            var action = new HotkeyAction { Id = id, Label = label, Run = run, Pref = pref, Binding = KeyBinding.Parse(pref.Value), Owner = owner ?? cat.DisplayName };
            Actions.Add(action);
            return action;
        }

        /// <summary>Removes an action (its saved binding stays for next time).</summary>
        public static void Unregister(HotkeyAction action)
        {
            if (action == null || action == Menu) return;
            Actions.Remove(action);
            if (Capturing == action) Capturing = null;
        }

        /// <summary>Every frame (the API calls it): the window key, binding capture and the other actions.</summary>
        internal static void Update()
        {
            if (Capturing != null)
            {
                CaptureTick();
                return;
            }
            // Shift is needed for capitals, so hotkeys are ignored while a text box has focus.
            if (TextInput.IsTyping) return;
            if (MenuPressed()) ModMenu.Toggle();
            if (ConfirmDialog.Active != null || Prompt.Active) return;
            try { Poll(); }
            catch (Exception e) { ApiCore.Log.Warn($"Hotkeys failed: {e.Message}"); }
        }

        private static bool MenuPressed()
        {
            var binding = Menu.Binding;
            if (!_legacyInputBroken)
            {
                try { return Pressed(binding); }
                catch { _legacyInputBroken = true; ApiCore.Log.Info("Legacy input unavailable; using the Input System for the window key."); }
            }
            try
            {
                // Only knows Right Shift; the game keeps legacy input on, so this shouldn't be needed.
                var kb = UnityEngine.InputSystem.Keyboard.current;
                return kb != null && kb.rightShiftKey.wasPressedThisFrame;
            }
            catch { return false; }
        }

        public static string MenuKeyName => Menu.Binding.IsNone ? "(no key)" : Menu.Binding.Display;

        /// <summary>True when the binding's key went down this frame with exactly its modifiers held.</summary>
        public static bool Pressed(KeyBinding b)
        {
            if (b.IsNone || !Input.GetKeyDown(b.Key)) return false;
            if (IsModifier(b.Key)) return true;
            return Held(KeyCode.LeftControl, KeyCode.RightControl) == b.Ctrl
                && Held(KeyCode.LeftAlt, KeyCode.RightAlt, KeyCode.AltGr) == b.Alt
                && Held(KeyCode.LeftShift, KeyCode.RightShift) == b.Shift;
        }

        /// <summary>Runs bound actions (other than the window key).</summary>
        private static void Poll()
        {
            for (int i = 0; i < Actions.Count; i++)
            {
                var a = Actions[i];
                if (a == Menu || a.Run == null || !Pressed(a.Binding)) continue;
                ModTasks.Run(a.Run, toast: a.Toast);
            }
        }

        public static void BeginCapture(HotkeyAction action)
        {
            Capturing = action;
            _heldModifier = KeyCode.None;
            _captureFrame = Time.frameCount;
        }

        public static void CancelCapture() => Capturing = null;

        public static void Clear(HotkeyAction action) => Assign(action, new KeyBinding());

        /// <summary>Called every frame while capturing. Esc cancels; a modifier pressed and released on its own binds by itself.</summary>
        private static void CaptureTick()
        {
            if (Capturing == null || Time.frameCount == _captureFrame) return;
            if (Input.GetKeyDown(KeyCode.Escape)) { Capturing = null; return; }

            foreach (var k in _captureKeys)
            {
                if (!Input.GetKeyDown(k)) continue;
                if (IsModifier(k)) { _heldModifier = k; continue; }
                Assign(Capturing, new KeyBinding
                {
                    Key = k,
                    Ctrl = Held(KeyCode.LeftControl, KeyCode.RightControl),
                    Alt = Held(KeyCode.LeftAlt, KeyCode.RightAlt, KeyCode.AltGr),
                    Shift = Held(KeyCode.LeftShift, KeyCode.RightShift),
                });
                return;
            }
            if (_heldModifier != KeyCode.None && Input.GetKeyUp(_heldModifier))
                Assign(Capturing, new KeyBinding { Key = _heldModifier });
        }

        /// <summary>Other actions that share this action's binding.</summary>
        public static IEnumerable<HotkeyAction> Conflicts(HotkeyAction action) =>
            action.Binding.IsNone ? Enumerable.Empty<HotkeyAction>() : Actions.Where(a => a != action && a.Binding.Same(action.Binding));

        private static void Assign(HotkeyAction action, KeyBinding binding)
        {
            action.Binding = binding;
            action.Pref.Value = binding.ToString();
            MelonPreferences.Save();
            if (Capturing == action) Capturing = null;
            ModTasks.SetStatus($"{action.Label}: {binding.Display}", false);
        }

        private static bool Held(params KeyCode[] keys)
        {
            foreach (var k in keys) if (Input.GetKey(k)) return true;
            return false;
        }

        private static bool IsModifier(KeyCode k) => k is KeyCode.LeftShift or KeyCode.RightShift or KeyCode.LeftControl
            or KeyCode.RightControl or KeyCode.LeftAlt or KeyCode.RightAlt or KeyCode.AltGr
            or KeyCode.LeftCommand or KeyCode.RightCommand or KeyCode.LeftWindows or KeyCode.RightWindows;
    }
}
