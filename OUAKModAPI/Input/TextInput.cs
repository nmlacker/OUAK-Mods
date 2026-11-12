using Il2Cpp;
using Il2CppApp.KatamariSin;
using UnityEngine;

namespace OUAKModAPI
{
    /// <summary>The text and limits of one text box (<see cref="TextInput.Draw(Rect, TextField)"/>).</summary>
    public sealed class TextField
    {
        public string Text = "";
        public readonly int MaxLength;
        public readonly string Placeholder;

        public TextField(int maxLength, string placeholder)
        {
            MaxLength = maxLength;
            Placeholder = placeholder;
        }
    }

    /// <summary>
    /// A text box for IMGUI, built on raw key events, because GUI.TextField is stripped from this game's build.
    /// Draw it with <see cref="Draw(Rect, TextField)"/> every OnGUI call (it needs the key events, not just Repaint). While a box
    /// has focus, the game's own input is locked (<see cref="GameInput"/>) so typing doesn't move the prince or
    /// press menu buttons, and the API's hotkeys are ignored.
    /// </summary>
    public static class TextInput
    {
        public static TextField Focused;
        /// <summary>A text box has the keyboard: an IMGUI one (<see cref="Focused"/>) or a Dear ImGui one.</summary>
        public static bool IsTyping => Focused != null || ImGuiHost.WantsText;

        private static bool _clickPending, _clickHitField;

        /// <summary>The mod window calls this first in each OnGUI call (other windows with text boxes do the same).</summary>
        public static void BeginFrame(Event e)
        {
            _clickPending = e.rawType == EventType.MouseDown;
            _clickHitField = false;
        }

        /// <summary>After drawing: a click outside every text box drops the focus.</summary>
        public static void EndFrame()
        {
            // A click that landed on anything other than a text box drops keyboard focus.
            if (_clickPending && !_clickHitField) Focused = null;
            _clickPending = false;
        }

        /// <summary>Draws the box with the mod window's look and handles typing. Returns true when Enter is pressed in it.</summary>
        public static bool Draw(Rect r, TextField field) =>
            Draw(r, field, Theme.Field, Theme.FieldFocus, Theme.FieldText, Theme.FieldHint);

        /// <summary>Draws the box and handles typing. Returns true when Enter is pressed in it.</summary>
        public static bool Draw(Rect r, TextField field, GUIStyle box, GUIStyle boxFocused, GUIStyle text, GUIStyle placeholder)
        {
            var e = Event.current;
            bool submitted = false;

            if (e.rawType == EventType.MouseDown && r.Contains(e.mousePosition))
            {
                _clickHitField = true;
                Focused = field;
                if (e.type == EventType.MouseDown) e.Use();
            }

            if (Focused == field && e.type == EventType.KeyDown)
            {
                switch (e.keyCode)
                {
                    case KeyCode.Backspace:
                        if (field.Text.Length > 0) field.Text = field.Text.Substring(0, field.Text.Length - 1);
                        break;
                    case KeyCode.Return:
                    case KeyCode.KeypadEnter:
                        submitted = true;
                        break;
                    case KeyCode.Escape:
                        Focused = null;
                        break;
                    case KeyCode.V when e.control:
                        Append(field, GUIUtility.systemCopyBuffer);
                        break;
                    default:
                        if (e.character != '\0' && !char.IsControl(e.character)) Append(field, e.character.ToString());
                        break;
                }
                e.Use();
            }

            bool focused = Focused == field;
            GUI.Box(r, GUIContent.Temp(""), focused ? boxFocused : box);
            var inner = new Rect(r.x + 8f, r.y, r.width - 16f, r.height);
            if (field.Text.Length == 0 && !focused)
            {
                GUI.Label(inner, field.Placeholder, placeholder);
            }
            else
            {
                string shown = field.Text + (focused && (int)(Time.unscaledTime * 2f) % 2 == 0 ? "|" : "");
                GUI.Label(inner, shown, text);
            }
            return submitted;
        }

        private static void Append(TextField field, string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            s = s.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
            int room = field.MaxLength - field.Text.Length;
            if (room <= 0) return;
            field.Text += s.Length > room ? s.Substring(0, room) : s;
        }
    }
}
