using System;
using System.Collections.Generic;
using System.IO;
using ImGuiNET;
using UnityEngine;
using Color = UnityEngine.Color;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace OUAKModAPI
{
    /// <summary>
    /// The one Dear ImGui context every mod shares (ImGui.NET + cimgui.dll in UserLibs). Drawn by Unity itself, with
    /// no graphics hook: each frame ImGui's triangle lists go out through GL immediate mode at the end of the API's
    /// OnGUI (Repaint), every draw command clipped to its rectangle with the GL viewport. Nothing runs while no ImGui
    /// window is up. Mods don't call this directly: they add windows (<see cref="ModWindows"/>) or mod window tabs
    /// (<see cref="ModMenu.AddTab"/>) and use ImGui.NET's ImGui.* calls inside their callbacks.
    /// </summary>
    internal static class ImGuiHost
    {
        private static IntPtr _ctx;
        private static Material _mat;
        private static Texture2D _font;
        private static readonly Dictionary<IntPtr, Texture> _textures = new();
        private static readonly Dictionary<int, IntPtr> _ids = new();
        private static int _nextId = 2;
        private static float _lastFrame, _scale = 1f, _styleScale = 1f;
        private static bool _styled;

        public static bool Ready => _ctx != IntPtr.Zero && _mat != null;
        public static string Problem { get; private set; } = "";
        public static string ShaderName { get; private set; } = "";
        public static bool WantsMouse { get; private set; }
        public static bool WantsKeyboard { get; private set; }
        /// <summary>An ImGui text box has the keyboard.</summary>
        public static bool WantsText { get; private set; }
        public static int LastVertices { get; private set; }
        public static float Scale => _scale;

        public static unsafe void Init()
        {
            try
            {
                _ctx = ImGui.CreateContext();
                ApiCore.Log.Info($"Dear ImGui {ImGui.GetVersion()} loaded.");
                ImGui.SetCurrentContext(_ctx);
                var io = ImGui.GetIO();
                io.NativePtr->IniFilename = null;   // no imgui.ini in the game folder
                io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard | ImGuiConfigFlags.NavEnableGamepad;
                io.BackendFlags |= ImGuiBackendFlags.HasGamepad;
                // A mod whose window throws half way leaves Begin/End unbalanced: ImGui closes them at the end of the
                // frame and notes it in its debug log, instead of asserting. (This cimgui build keeps its asserts, and a
                // failed assert stops the game on a message box, so at least one of the three must stay on.)
                io.ConfigErrorRecovery = true;
                io.ConfigErrorRecoveryEnableAssert = false;
                io.ConfigErrorRecoveryEnableTooltip = false;
                io.ConfigErrorRecoveryEnableDebugLog = true;
                BuildFont(WantedScale);
                foreach (string name in new[] { "UI/Default", "Sprites/Default", "Unlit/Transparent" })
                {
                    var sh = Shader.Find(name);
                    if (sh == null) continue;
                    _mat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave, mainTexture = _font };
                    ShaderName = name;
                    break;
                }
                if (_mat == null) Problem = "no usable shader";
            }
            catch (Exception e)
            {
                _ctx = IntPtr.Zero;
                Problem = e is DllNotFoundException ? "cimgui.dll is missing from UserLibs" : e.Message;
                ApiCore.Log.Error($"Dear ImGui didn't start: {e}");
            }
        }

        /// <summary>The scale everything is drawn at: the window scale setting, or by screen height (1 at 1080p).</summary>
        private static float WantedScale => ModMenu.UiScaleSetting > 0f ? ModMenu.UiScaleSetting : Mathf.Clamp(Screen.height / 1080f, 1f, 3f);

        // Latin with its extensions, punctuation, arrows, shapes and a few symbols (Segoe UI has most of them).
        private static IntPtr _ranges;

        private static unsafe IntPtr Ranges()
        {
            if (_ranges != IntPtr.Zero) return _ranges;
            ushort[] r = { 0x0020, 0x024F, 0x2000, 0x206F, 0x20A0, 0x20CF, 0x2100, 0x21FF, 0x2200, 0x22FF, 0x2300, 0x23FF, 0x2500, 0x26FF, 0x2700, 0x27BF, 0 };
            _ranges = System.Runtime.InteropServices.Marshal.AllocHGlobal(r.Length * 2);
            for (int i = 0; i < r.Length; i++) ((ushort*)_ranges)[i] = r[i];
            return _ranges;
        }

        /// <summary>(Re)builds the font atlas and the style's sizes for a scale. Only between frames.</summary>
        private static unsafe void BuildFont(float scale)
        {
            var io = ImGui.GetIO();
            io.Fonts.Clear();
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            string font = Path.Combine(dir, "segoeui.ttf"), bold = Path.Combine(dir, "segoeuib.ttf");
            if (File.Exists(font))
            {
                io.Fonts.AddFontFromFileTTF(font, 17f * scale, null, Ranges());
                // A second, bigger bold face for titles (ImUi.Title).
                BigFont = File.Exists(bold) ? io.Fonts.AddFontFromFileTTF(bold, 21f * scale, null, Ranges()) : io.Fonts.AddFontFromFileTTF(font, 21f * scale, null, Ranges());
            }
            else
            {
                io.Fonts.AddFontDefault();
                BigFont = io.Fonts.AddFontDefault();
            }

            io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out int w, out int h, out int bpp);
            var data = new byte[w * h * bpp];
            System.Runtime.InteropServices.Marshal.Copy(pixels, data, 0, data.Length);
            // ImGui's v=0 is its first row and Unity's row 0 sits at v=0: the data goes in as it is.
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, name = "OUAK ImGui font" };
            tex.LoadRawTextureData(data);
            tex.Apply(false, false);
            if (_font != null) UnityEngine.Object.Destroy(_font);
            _font = tex;
            _textures[(IntPtr)1] = _font;
            io.Fonts.SetTexID((IntPtr)1);
            io.Fonts.ClearTexData();

            // The style's sizes follow the scale (relative to the scale they're at now).
            if (!_styled) { ImTheme.Apply(); _styled = true; }
            ImGui.GetStyle().ScaleAllSizes(scale / _styleScale);
            _styleScale = scale;
            _scale = scale;
        }

        /// <summary>The bold title font.</summary>
        public static ImFontPtr BigFont { get; private set; }

        /// <summary>An id ImGui.Image / ImageButton can use for a Unity texture (the same texture always gets the same id).</summary>
        public static IntPtr TextureId(Texture tex)
        {
            if (tex == null) return IntPtr.Zero;
            int key = tex.GetInstanceID();
            if (_ids.TryGetValue(key, out var id) && _textures.TryGetValue(id, out var have) && have != null) return id;
            id = (IntPtr)_nextId++;
            _ids[key] = id;
            _textures[id] = tex;
            return id;
        }

        // ---------------------------------------------------------------- input

        private static readonly (KeyCode unity, ImGuiKey imgui)[] Keys =
        {
            (KeyCode.Tab, ImGuiKey.Tab), (KeyCode.LeftArrow, ImGuiKey.LeftArrow), (KeyCode.RightArrow, ImGuiKey.RightArrow),
            (KeyCode.UpArrow, ImGuiKey.UpArrow), (KeyCode.DownArrow, ImGuiKey.DownArrow), (KeyCode.PageUp, ImGuiKey.PageUp),
            (KeyCode.PageDown, ImGuiKey.PageDown), (KeyCode.Home, ImGuiKey.Home), (KeyCode.End, ImGuiKey.End),
            (KeyCode.Delete, ImGuiKey.Delete), (KeyCode.Backspace, ImGuiKey.Backspace), (KeyCode.Return, ImGuiKey.Enter),
            (KeyCode.KeypadEnter, ImGuiKey.KeypadEnter), (KeyCode.Escape, ImGuiKey.Escape), (KeyCode.Space, ImGuiKey.Space),
            (KeyCode.A, ImGuiKey.A), (KeyCode.C, ImGuiKey.C), (KeyCode.V, ImGuiKey.V), (KeyCode.X, ImGuiKey.X),
            (KeyCode.Y, ImGuiKey.Y), (KeyCode.Z, ImGuiKey.Z),
        };
        private static readonly bool[] _down = new bool[Keys.Length];

        private static bool _active;

        /// <summary>Every OnGUI event: typed characters (only KeyDown events carry them), while ImGui is drawing.</summary>
        public static void OnGuiEvent(Event e)
        {
            if (!Ready || !_active || e == null || e.type != EventType.KeyDown) return;
            if (e.character != '\0' && !char.IsControl(e.character)) ImGui.GetIO().AddInputCharacter(e.character);
        }

        private static void FeedInput(bool pad)
        {
            var io = ImGui.GetIO();
            float now = Time.unscaledTime;
            io.DeltaTime = Mathf.Clamp(_lastFrame > 0f ? now - _lastFrame : 1f / 60f, 1f / 1000f, 0.25f);
            _lastFrame = now;
            io.DisplaySize = new Vector2(Screen.width, Screen.height);
            io.DisplayFramebufferScale = Vector2.One;
            var m = Input.mousePosition;
            // A hidden pointer (controller or idle) doesn't hover anything.
            if (CursorControl.Active && !CursorControl.Shown) io.AddMousePosEvent(-float.MaxValue, -float.MaxValue);
            else io.AddMousePosEvent(m.x, Screen.height - m.y);
            for (int b = 0; b < 3; b++) io.AddMouseButtonEvent(b, Input.GetMouseButton(b));
            var wheel = Input.mouseScrollDelta;
            if (wheel.sqrMagnitude > 0f) io.AddMouseWheelEvent(wheel.x, wheel.y);
            io.AddKeyEvent(ImGuiKey.ModCtrl, Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl));
            io.AddKeyEvent(ImGuiKey.ModShift, Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift));
            io.AddKeyEvent(ImGuiKey.ModAlt, Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt));
            for (int i = 0; i < Keys.Length; i++)
            {
                bool d = Input.GetKey(Keys[i].unity);
                if (d == _down[i]) continue;
                _down[i] = d;
                io.AddKeyEvent(Keys[i].imgui, d);
            }
            FeedPad(io, pad);
        }

        // Gamepad navigation, only while an API window has the controller (the mod window, a prompt, a confirmation).
        private static bool _padFed;

        private static void FeedPad(ImGuiIOPtr io, bool pad)
        {
            UnityEngine.InputSystem.Gamepad g = null;
            try { g = pad ? UnityEngine.InputSystem.Gamepad.current : null; } catch { }
            if (g == null)
            {
                if (_padFed) { io.ClearInputKeys(); _padFed = false; }
                return;
            }
            _padFed = true;
            void B(ImGuiKey k, UnityEngine.InputSystem.Controls.ButtonControl c) => io.AddKeyEvent(k, c.isPressed);
            B(ImGuiKey.GamepadFaceDown, g.buttonSouth);
            B(ImGuiKey.GamepadFaceRight, g.buttonEast);
            B(ImGuiKey.GamepadFaceLeft, g.buttonWest);
            B(ImGuiKey.GamepadFaceUp, g.buttonNorth);
            B(ImGuiKey.GamepadDpadUp, g.dpad.up);
            B(ImGuiKey.GamepadDpadDown, g.dpad.down);
            B(ImGuiKey.GamepadDpadLeft, g.dpad.left);
            B(ImGuiKey.GamepadDpadRight, g.dpad.right);
            B(ImGuiKey.GamepadL1, g.leftShoulder);
            B(ImGuiKey.GamepadR1, g.rightShoulder);
            B(ImGuiKey.GamepadStart, g.startButton);
            B(ImGuiKey.GamepadBack, g.selectButton);
            var ls = g.leftStick.ReadValue();
            const float dead = 0.25f;
            io.AddKeyAnalogEvent(ImGuiKey.GamepadLStickUp, ls.y > dead, Mathf.Max(0f, ls.y));
            io.AddKeyAnalogEvent(ImGuiKey.GamepadLStickDown, ls.y < -dead, Mathf.Max(0f, -ls.y));
            io.AddKeyAnalogEvent(ImGuiKey.GamepadLStickLeft, ls.x < -dead, Mathf.Max(0f, -ls.x));
            io.AddKeyAnalogEvent(ImGuiKey.GamepadLStickRight, ls.x > dead, Mathf.Max(0f, ls.x));
        }

        // ---------------------------------------------------------------- the mouse pointer

        private static bool _cursorHeld;
        private static CursorLockMode _savedLock;

        /// <summary>A mod window or prompt is using the mouse pointer.</summary>
        internal static bool CursorHeld => _cursorHeld;

        /// <summary>
        /// Every Update: frees the pointer from the game window's confinement while a window needs it, and puts the
        /// lock back after. Whether it shows is up to <see cref="CursorControl"/> (mouse used: shown; controller or
        /// 3 s idle: hidden).
        /// </summary>
        internal static void HoldCursor(bool want)
        {
            if (want)
            {
                if (!_cursorHeld)
                {
                    _savedLock = Cursor.lockState;
                    _cursorHeld = true;
                }
                if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
            }
            else if (_cursorHeld)
            {
                _cursorHeld = false;
                Cursor.lockState = _savedLock;
            }
        }

        // ---------------------------------------------------------------- a frame

        /// <summary>One ImGui frame: <paramref name="ui"/> draws everything, then it's drawn (OnGUI, Repaint only).</summary>
        public static void Frame(Action ui, bool pad)
        {
            if (!Ready) return;
            ImGui.SetCurrentContext(_ctx);
            float want = WantedScale;
            if (!Mathf.Approximately(want, _scale))
            {
                try { BuildFont(want); }
                catch (Exception e) { ApiCore.Log.Warn($"Couldn't rescale the windows: {e.Message}"); _scale = want; }
            }
            FeedInput(pad);
            _active = true;
            ImGui.NewFrame();
            try { ui(); }
            catch (Exception e) { ApiCore.Log.Warn($"ImGui frame failed: {e.Message}"); }
            finally { ImGui.Render(); }
            var io = ImGui.GetIO();
            WantsMouse = io.WantCaptureMouse;
            WantsKeyboard = io.WantCaptureKeyboard || io.WantTextInput;
            WantsText = io.WantTextInput;
            Draw(ImGui.GetDrawData());
        }

        /// <summary>Nothing of ImGui's on screen: forget the pointer and keys it had.</summary>
        public static void Idle()
        {
            WantsMouse = WantsKeyboard = WantsText = false;
            _lastFrame = 0f;
            if (_active)
            {
                _active = false;
                // Let go of every key and button ImGui still thinks is held.
                try { ImGui.SetCurrentContext(_ctx); ImGui.GetIO().ClearInputKeys(); ImGui.GetIO().ClearInputMouse(); } catch { }
                for (int i = 0; i < _down.Length; i++) _down[i] = false;
            }
        }

        private static void Draw(ImDrawDataPtr dd)
        {
            int sw = Screen.width, sh = Screen.height, verts = 0;
            IntPtr bound = (IntPtr)(-1);
            GL.PushMatrix();
            try
            {
                for (int n = 0; n < dd.CmdListsCount; n++)
                {
                    var list = dd.CmdLists[n];
                    var vtx = list.VtxBuffer;
                    var idx = list.IdxBuffer;
                    for (int c = 0; c < list.CmdBuffer.Size; c++)
                    {
                        var cmd = list.CmdBuffer[c];
                        if (cmd.UserCallback != IntPtr.Zero || cmd.ElemCount == 0) continue;
                        float x1 = Mathf.Max(0f, cmd.ClipRect.X), y1 = Mathf.Max(0f, cmd.ClipRect.Y);
                        float x2 = Mathf.Min(sw, cmd.ClipRect.Z), y2 = Mathf.Min(sh, cmd.ClipRect.W);
                        if (x2 <= x1 || y2 <= y1) continue;
                        if (cmd.TextureId != bound)
                        {
                            bound = cmd.TextureId;
                            _mat.mainTexture = _textures.TryGetValue(bound, out var tex) && tex != null ? tex : _font;
                            _mat.SetPass(0);
                        }
                        // Clip to the command's rectangle: the viewport is that rectangle (Unity's y goes up).
                        GL.Viewport(new Rect(x1, sh - y2, x2 - x1, y2 - y1));
                        GL.LoadPixelMatrix(x1, x2, y2, y1);
                        GL.Begin(4);   // GL.TRIANGLES
                        uint lastCol = 0;
                        bool first = true;
                        for (uint i = 0; i < cmd.ElemCount; i++)
                        {
                            var v = vtx[(int)(cmd.VtxOffset + idx[(int)(cmd.IdxOffset + i)])];
                            uint col = v.col;
                            if (first || col != lastCol)
                            {
                                GL.Color(new Color((col & 0xFF) / 255f, ((col >> 8) & 0xFF) / 255f, ((col >> 16) & 0xFF) / 255f, (col >> 24) / 255f));
                                lastCol = col;
                                first = false;
                            }
                            GL.TexCoord2(v.uv.X, v.uv.Y);
                            GL.Vertex3(v.pos.X, v.pos.Y, 0f);
                        }
                        GL.End();
                        verts += (int)cmd.ElemCount;
                    }
                }
            }
            finally
            {
                GL.PopMatrix();
                GL.Viewport(new Rect(0f, 0f, sw, sh));
            }
            LastVertices = verts;
        }
    }

    /// <summary>The OUAK look for ImGui (the mod window's pink and dark colours).</summary>
    public static class ImTheme
    {
        public static readonly Vector4 Accent = new(0.93f, 0.33f, 0.62f, 1f);
        public static readonly Vector4 Deep = new(0.55f, 0.16f, 0.42f, 1f);
        public static readonly Vector4 Green = new(0.16f, 0.45f, 0.30f, 1f);
        public static readonly Vector4 GreenHover = new(0.20f, 0.55f, 0.36f, 1f);
        public static readonly Vector4 Red = new(0.55f, 0.16f, 0.16f, 1f);
        public static readonly Vector4 Dim = new(0.65f, 0.65f, 0.72f, 1f);
        public static readonly Vector4 Ok = new(0.55f, 0.9f, 0.65f, 1f);
        public static readonly Vector4 Err = new(1f, 0.5f, 0.45f, 1f);

        internal static void Apply()
        {
            ImGui.StyleColorsDark();
            var s = ImGui.GetStyle();
            s.WindowRounding = 8f;
            s.ChildRounding = 6f;
            s.FrameRounding = 5f;
            s.GrabRounding = 5f;
            s.TabRounding = 5f;
            s.PopupRounding = 6f;
            s.SelectableTextAlign = new Vector2(0f, 0.5f);
            s.WindowTitleAlign = new Vector2(0.02f, 0.5f);
            var c = s.Colors;
            c[(int)ImGuiCol.WindowBg] = new Vector4(0.07f, 0.07f, 0.10f, 0.97f);
            c[(int)ImGuiCol.ChildBg] = new Vector4(0.04f, 0.04f, 0.06f, 0.6f);
            c[(int)ImGuiCol.PopupBg] = new Vector4(0.09f, 0.08f, 0.13f, 0.98f);
            c[(int)ImGuiCol.TitleBg] = Deep;
            c[(int)ImGuiCol.TitleBgActive] = Deep;
            c[(int)ImGuiCol.TitleBgCollapsed] = Deep;
            c[(int)ImGuiCol.Button] = new Vector4(0.18f, 0.18f, 0.24f, 1f);
            c[(int)ImGuiCol.ButtonHovered] = new Vector4(0.27f, 0.27f, 0.36f, 1f);
            c[(int)ImGuiCol.ButtonActive] = new Vector4(0.45f, 0.20f, 0.40f, 1f);
            c[(int)ImGuiCol.Header] = new Vector4(0.45f, 0.20f, 0.40f, 0.8f);
            c[(int)ImGuiCol.HeaderHovered] = Deep;
            c[(int)ImGuiCol.HeaderActive] = Accent;
            c[(int)ImGuiCol.CheckMark] = Accent;
            c[(int)ImGuiCol.SliderGrab] = Accent;
            c[(int)ImGuiCol.SliderGrabActive] = Accent;
            c[(int)ImGuiCol.FrameBg] = new Vector4(0.02f, 0.02f, 0.03f, 1f);
            c[(int)ImGuiCol.FrameBgHovered] = new Vector4(0.10f, 0.06f, 0.10f, 1f);
            c[(int)ImGuiCol.Tab] = new Vector4(0.13f, 0.13f, 0.18f, 1f);
            c[(int)ImGuiCol.TabHovered] = Accent;
            c[(int)ImGuiCol.TabSelected] = Deep;
            c[(int)ImGuiCol.NavCursor] = Accent;
            c[(int)ImGuiCol.Separator] = new Vector4(0.3f, 0.3f, 0.38f, 1f);
        }
    }
}
