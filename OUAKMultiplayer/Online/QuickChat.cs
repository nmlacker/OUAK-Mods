using System;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using Il2CppApp.KatamariSin;
using MelonLoader;
using UnityEngine;
using OUAKModAPI;

namespace OUAKMultiplayer
{
    /// <summary>
    /// Quick chat for controllers: a pad's D-pad down opens a list of your own short messages at the left of the
    /// screen (the keyboard's chat box stays on T). D-pad up / down choose, A sends (through the normal chat, so
    /// the host's rate limit and the speech bubbles apply), B or D-pad left closes. With a mouse: click one; with
    /// the keyboard: 1-9 send that line. While the list is open the game's controls are held (the API's GameInput
    /// lock), the way the chat box holds them. The messages are edited on the Online tab (Chat), saved in
    /// MelonPreferences (OUAKModMenu_Online, QuickChatMessages).
    /// </summary>
    internal static class QuickChat
    {
        public const int MaxMessages = 9, MaxLength = 60;
        private const float IdleClose = 10f;
        /// <summary>
        /// Between the lines in the saved value. Must be printable: MelonPreferences is TOML, and a raw control
        /// character there (the first 2.2.0 build used U+001F) makes MelonLoader drop the whole file for every mod.
        /// </summary>
        private const char Sep = '|';
        private static readonly char[] ReadSeps = { Sep, '\u001F' };

        public static readonly string[] Defaults =
        {
            "Hi!", "Over here!", "Follow me!", "Wait for me!", "Let's go!", "Nice!", "Good game!", "Thanks!",
        };

        public static readonly List<string> Messages = new();
        public static bool Enabled = true;

        private static MelonPreferences_Entry<string> _msgPref;
        private static MelonPreferences_Entry<bool> _onPref;

        private static bool _open;
        private static int _sel, _openedFrame;
        private static float _lastInput;
        private static readonly List<Rect> Rows = new();
        private static bool _wasDown;
        private static GUIStyle _title, _row, _rowSel, _hint;
        private static Texture2D _back, _selBack;

        public static bool IsOpen => _open;
        public static int Sent { get; private set; }
        public static string LastSent { get; private set; } = "";

        public static void Init(MelonPreferences_Category cat)
        {
            _onPref = cat.CreateEntry("QuickChat", true, "Quick chat on D-pad down", "A controller's D-pad down opens your quick chat messages.");
            _msgPref = cat.CreateEntry("QuickChatMessages", string.Join(Sep, Defaults), "Quick chat messages",
                "Your quick chat lines, separated by | (edit them on the Online tab).");
            Enabled = _onPref.Value;
            Messages.Clear();
            string saved = _msgPref.Value ?? "";
            foreach (var m in saved.Split(ReadSeps))
            {
                string s = Clean(m);
                if (s.Length > 0 && Messages.Count < MaxMessages) Messages.Add(s);
            }
            if (Messages.Count == 0) Messages.AddRange(Defaults);
            // A value written by the first build (U+001F between the lines) is rewritten with "|".
            if (saved.IndexOf('\u001F') >= 0) Save();
        }

        public static void Save()
        {
            if (_msgPref == null) return;
            for (int i = Messages.Count - 1; i >= 0; i--)
            {
                Messages[i] = Clean(Messages[i]);
                if (Messages[i].Length == 0) Messages.RemoveAt(i);
            }
            _msgPref.Value = string.Join(Sep, Messages);
            _onPref.Value = Enabled;
            MelonPreferences.Save();
        }

        public static void ResetToDefaults()
        {
            Messages.Clear();
            Messages.AddRange(Defaults);
            Save();
        }

        /// <summary>One line, no control characters, at most MaxLength (the chat cleans it again when sent).</summary>
        public static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = new string(s.Where(c => !char.IsControl(c) && c != Sep).ToArray()).Trim();
            return s.Length > MaxLength ? s.Substring(0, MaxLength) : s;
        }

        /// <summary>
        /// When D-pad down may open the list: in a session, the chat box, the mod window, a prompt and the game's
        /// pause menu all closed, nobody typing.
        /// </summary>
        public static bool CanOpen =>
            Enabled && NetSession.Connected && Messages.Count > 0 && !Chat.IsOpen && !ModMenu.IsOpen && !Prompt.Active &&
            !TextInput.IsTyping && !GameEvents.Paused;

        /// <summary>How long D-pad down must be held to open the list.</summary>
        public const float HoldToOpen = 0.4f;
        private static float _downSince = -1f;
        private static bool _downUsed;

        /// <summary>
        /// You're walking or rolling, not in one of the game's menus: in a plaza its own state says walking
        /// (SelectHirobaController SELECTHIROBA, UI MAINGAME); in a stage the game is running; on the ship (no menu
        /// state found yet) only the hold protects the menus.
        /// </summary>
        public static bool FreeToMove()
        {
            try
            {
                var gm = GameRefs.Main;
                if (gm != null) return gm.IsGameRunning && !GameEvents.Paused;
                int hub = HubPresence.HubKey;
                if (hub >= 3000 && hub < HubPresence.ShipKey)
                {
                    var mgr = SingletonMonoBehaviour<SelectHirobaManager>.instance;
                    if (mgr == null) return false;
                    return mgr._hirobaController._gameState == SelectHirobaController.GameState.SELECTHIROBA
                           && mgr._uIController._uiStaus == SelectHirobaUIController.UI_STATUS.MAINGAME;
                }
                return hub == HubPresence.ShipKey;
            }
            catch { return false; }
        }

        public static void Open()
        {
            _open = true;
            _sel = Mathf.Clamp(_sel, 0, Math.Max(0, Messages.Count - 1));
            _openedFrame = Time.frameCount;
            _lastInput = Time.unscaledTime;
        }

        public static void Close()
        {
            if (!_open) return;
            _open = false;
            GameInput.HoldBriefly();
        }

        /// <summary>Sends line <paramref name="i"/> through the chat and closes the list.</summary>
        public static string SendLine(int i)
        {
            if (i < 0 || i >= Messages.Count) return "No such line.";
            string text = Messages[i];
            Close();
            string r = Chat.Send(text);
            Sent++;
            LastSent = text;
            return r;
        }

        public static void Tick()
        {
            if (!NetSession.Connected)
            {
                _open = false;
                return;
            }
            if (!_open)
            {
                // Held, not tapped (a tap of D-pad down moves through the game's menus), and only while you're free to move
                // (the user, 2026-11-12: "quick chat gets in the way of selecting interfaces").
                bool held = false;
                try { var pad = UnityEngine.InputSystem.Gamepad.current; held = pad != null && pad.dpad.down.isPressed; } catch { }
                if (!held) { _downSince = -1f; _downUsed = false; return; }
                float t0 = Time.unscaledTime;
                if (_downSince < 0f) _downSince = t0;
                if (t0 - _downSince >= HoldToOpen && !_downUsed && CanOpen && FreeToMove())
                {
                    _downUsed = true;
                    Open();
                    GameInput.BlockThisFrame("quick chat");
                }
                return;
            }
            if (Messages.Count == 0 || ModMenu.IsOpen || Prompt.Active || Chat.IsOpen || GameEvents.Paused) { Close(); return; }
            GameInput.BlockThisFrame("quick chat");
            float now = Time.unscaledTime;
            if (Time.frameCount != _openedFrame)
            {
                if (Pad.DownPressed) { _sel = (_sel + 1) % Messages.Count; _lastInput = now; }
                if (Pad.UpPressed) { _sel = (_sel - 1 + Messages.Count) % Messages.Count; _lastInput = now; }
                if (Pad.ConfirmPressed) { SendLine(_sel); return; }
                if (Pad.BackPressed || Pad.LeftPressed) { Close(); return; }
            }
            // Keyboard: 1-9 send that line, Esc closes.
            try
            {
                for (int i = 0; i < Messages.Count && i < 9; i++)
                    if (Input.GetKeyDown(KeyCode.Alpha1 + i)) { SendLine(i); return; }
                if (Input.GetKeyDown(KeyCode.Escape)) { Close(); return; }
            }
            catch { }
            // Mouse: click a line.
            try
            {
                bool down = Input.GetMouseButton(0);
                if (down && !_wasDown && Cursor.visible)
                {
                    float scale = Votes.GuiScale();
                    var m = Input.mousePosition;
                    var gui = new Vector2(m.x / scale, (Screen.height - m.y) / scale);
                    for (int i = 0; i < Rows.Count && i < Messages.Count; i++)
                        if (Rows[i].Contains(gui)) { _wasDown = true; SendLine(i); return; }
                }
                _wasDown = down;
            }
            catch { }
            if (now - _lastInput > IdleClose) Close();
        }

        /// <summary>Repaint, inside the bar's scaled GUI matrix: the list at the left, the chosen line lit.</summary>
        public static void OnGUI(float sh)
        {
            Rows.Clear();
            if (!_open || Messages.Count == 0) return;
            EnsureStyles();
            const float x = 16f, w = 300f, rowH = 26f, pad = 8f;
            float h = 26f + Messages.Count * rowH + 22f + pad * 2f;
            float y = Mathf.Max(20f, sh * 0.42f - h * 0.5f);
            GUI.DrawTexture(new Rect(x - pad, y - pad, w + pad * 2f, h), _back);
            GUI.Label(new Rect(x, y, w, 24f), "Quick chat", _title);
            y += 26f;
            for (int i = 0; i < Messages.Count; i++)
            {
                var r = new Rect(x, y, w, rowH - 2f);
                Rows.Add(r);
                bool sel = i == _sel;
                if (sel) GUI.DrawTexture(r, _selBack);
                GUI.Label(new Rect(r.x + 6f, r.y, r.width - 6f, r.height), $"{i + 1}  {Messages[i]}", sel ? _rowSel : _row);
                y += rowH;
            }
            GUI.Label(new Rect(x, y + 2f, w, 20f), "D-pad: choose   A: send   B: close", _hint);
        }

        private static void EnsureStyles()
        {
            if (_row != null && _back != null && _selBack != null) return;
            _title = new GUIStyle { fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
            _title.normal.textColor = new Color(1f, 0.85f, 0.4f);
            _row = new GUIStyle { fontSize = 15, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
            _row.normal.textColor = Color.white;
            _rowSel = new GUIStyle { fontSize = 15, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
            _rowSel.normal.textColor = new Color(1f, 0.95f, 0.6f);
            _hint = new GUIStyle { fontSize = 12, alignment = TextAnchor.MiddleLeft };
            _hint.normal.textColor = new Color(0.8f, 0.85f, 1f, 0.85f);
            _back = PlayerBar.Tex(new Color(0.05f, 0.04f, 0.1f, 0.82f));
            _selBack = PlayerBar.Tex(new Color(0.3f, 0.25f, 0.55f, 0.9f));
        }

        public static string Describe() =>
            $"quick chat {(Enabled ? "on" : "off")}, {Messages.Count} line(s), open {_open}, sent {Sent} (last \"{LastSent}\")";
    }
}
