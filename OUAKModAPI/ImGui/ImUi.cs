using System;
using System.Collections.Generic;
using ImGuiNET;
using UnityEngine;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace OUAKModAPI
{
    /// <summary>
    /// A window of your own, drawn with Dear ImGui (<see cref="ModWindows.Add"/>). Your <see cref="Draw"/> callback
    /// makes the ImGui.Begin / ImGui.End calls itself, so it decides the title, size and flags.
    /// </summary>
    public sealed class ModWindow : IDisposable
    {
        public readonly string Id;
        public readonly string Owner;
        /// <summary>Draw it (every frame while true). Set it false to hide the window, or pass <c>ref window.Open</c> to ImGui.Begin for a close button.</summary>
        public bool Open;
        /// <summary>
        /// While open: the mouse pointer is shown, the game's controls are locked and a controller can move through
        /// the window (like the mod window). Turn it off for things that only show information (a HUD panel).
        /// </summary>
        public bool TakesControl;
        /// <summary>Your ImGui calls, ImGui.Begin to ImGui.End.</summary>
        public Action Draw;
        internal int Order, Added;

        internal ModWindow(string id, string owner) { Id = id; Owner = owner; }

        /// <summary>Removes the window.</summary>
        public void Dispose() => ModWindows.Remove(this);
    }

    /// <summary>
    /// Mods' own Dear ImGui windows, drawn above the game and the IMGUI overlays, with one shared ImGui context
    /// (fonts, colours and scale match the mod window). Use ImGui.NET's ImGui.* calls inside the callback:
    /// <code>var win = ModWindows.Add("mymod.stats", () =&gt;
    /// {
    ///     if (ImGui.Begin("My stats", ref win.Open)) ImGui.Text($"Size: {Katamari.SizeText}");
    ///     ImGui.End();
    /// });
    /// win.Open = true;</code>
    /// A window whose callback throws is closed and the error logged.
    /// </summary>
    public static class ModWindows
    {
        private static readonly List<ModWindow> _windows = new();
        private static int _added;

        /// <summary>Adds a window (closed unless <paramref name="open"/>). Adding an id that exists replaces it.</summary>
        public static ModWindow Add(string id, Action draw, bool open = false, bool takesControl = true, int order = 0, string owner = null)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("A window needs an id.", nameof(id));
            _windows.RemoveAll(w => w.Id == id);
            var win = new ModWindow(id, owner ?? draw?.Method.DeclaringType?.Assembly.GetName().Name ?? "")
            { Draw = draw, Open = open, TakesControl = takesControl, Order = order, Added = _added++ };
            _windows.Add(win);
            _windows.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.Added.CompareTo(b.Added));
            return win;
        }

        internal static void Remove(ModWindow w) => _windows.Remove(w);

        public static IReadOnlyList<ModWindow> All => _windows;

        public static bool AnyOpen
        {
            get
            {
                for (int i = 0; i < _windows.Count; i++) if (_windows[i].Open) return true;
                return false;
            }
        }

        /// <summary>A window that takes the controls is open.</summary>
        public static bool AnyTakesControl
        {
            get
            {
                for (int i = 0; i < _windows.Count; i++) if (_windows[i].Open && _windows[i].TakesControl) return true;
                return false;
            }
        }

        private static bool _hadControl;

        internal static void Update()
        {
            bool control = AnyTakesControl;
            if (control) GameInput.BlockThisFrame("mod window");
            else if (_hadControl) GameInput.HoldBriefly();
            _hadControl = control;
        }

        internal static void DrawAll()
        {
            for (int i = 0; i < _windows.Count; i++)
            {
                var w = _windows[i];
                if (!w.Open || w.Draw == null) continue;
                try { w.Draw(); }
                catch (Exception e)
                {
                    w.Open = false;
                    ApiCore.Log.Warn($"Window {w.Id} ({w.Owner}) closed after an error: {e.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Small Dear ImGui helpers in the mods' shared look. Everything else is plain ImGui.NET (ImGui.Button,
    /// ImGui.Checkbox, ImGui.SliderFloat, ImGui.BeginTable…).
    /// </summary>
    public static class ImUi
    {
        /// <summary>The scale everything is drawn at (1 at 1080p).</summary>
        public static float Scale => ImGuiHost.Scale;

        /// <summary>The bold title font (ImGui.PushFont / PopFont).</summary>
        public static ImFontPtr BigFont => ImGuiHost.BigFont;

        /// <summary>A size in pixels at 1080p, scaled like the windows.</summary>
        public static float Px(float v) => v * ImGuiHost.Scale;

        /// <summary>Triangles' corners drawn in the last ImGui frame (0 while no ImGui window is up).</summary>
        public static int LastVertices => ImGuiHost.LastVertices;

        /// <summary>The shader ImGui is drawn with.</summary>
        public static string ShaderName => ImGuiHost.ShaderName;

        /// <summary>True on the frame a double click picked an item of <see cref="ChoiceGrid"/>.</summary>
        public static bool LastClickDouble { get; private set; }

        /// <summary>A section title with a line.</summary>
        public static void Header(string text)
        {
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Text, ImTheme.Accent);
            ImGui.SeparatorText(text);
            ImGui.PopStyleColor();
        }

        /// <summary>Big bold text.</summary>
        public static void Title(string text)
        {
            ImGui.PushFont(ImGuiHost.BigFont);
            ImGui.TextUnformatted(text);
            ImGui.PopFont();
        }

        /// <summary>Wrapped grey text: explanations and hints.</summary>
        public static void Hint(string text) => Wrapped(text, ImTheme.Dim);

        public static void Ok(string text) => Wrapped(text, ImTheme.Ok);
        public static void Err(string text) => Wrapped(text, ImTheme.Err);

        /// <summary>Wrapped text in a colour.</summary>
        public static void Wrapped(string text, Vector4 color)
        {
            if (string.IsNullOrEmpty(text)) return;
            ImGui.PushStyleColor(ImGuiCol.Text, color);
            ImGui.PushTextWrapPos(0f);
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();
            ImGui.PopStyleColor();
        }

        /// <summary>A tooltip for the item just drawn.</summary>
        public static void Tip(string text)
        {
            if (!string.IsNullOrEmpty(text) && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled | ImGuiHoveredFlags.DelayShort))
            {
                ImGui.BeginTooltip();
                ImGui.PushTextWrapPos(Px(420f));
                ImGui.TextUnformatted(text);
                ImGui.PopTextWrapPos();
                ImGui.EndTooltip();
            }
        }

        /// <summary>A button as wide as the space left (or <paramref name="width"/>), disabled when <paramref name="enabled"/> is false.</summary>
        public static bool Button(string label, bool enabled = true, float width = -1f)
        {
            ImGui.BeginDisabled(!enabled);
            bool hit = ImGui.Button(label, new Vector2(width, Px(30f)));
            ImGui.EndDisabled();
            return hit && enabled;
        }

        /// <summary>A button in the accent colour (the main action on a page).</summary>
        public static bool Primary(string label, bool enabled = true, float width = -1f) =>
            Colored(label, ImTheme.Green, ImTheme.GreenHover, enabled, width);

        /// <summary>A red button (deleting, overwriting).</summary>
        public static bool Danger(string label, bool enabled = true, float width = -1f) =>
            Colored(label, ImTheme.Red, new Vector4(0.7f, 0.22f, 0.22f, 1f), enabled, width);

        private static bool Colored(string label, Vector4 c, Vector4 hover, bool enabled, float width)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, c);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hover);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, hover);
            bool hit = Button(label, enabled, width);
            ImGui.PopStyleColor(3);
            return hit;
        }

        /// <summary>A row of equal-width buttons filling the line; returns the index clicked, or -1.</summary>
        public static int Buttons(params string[] labels)
        {
            int hit = -1;
            float sp = ImGui.GetStyle().ItemSpacing.X;
            float w = (ImGui.GetContentRegionAvail().X - sp * (labels.Length - 1)) / Math.Max(1, labels.Length);
            for (int i = 0; i < labels.Length; i++)
            {
                if (i > 0) ImGui.SameLine();
                if (ImGui.Button(labels[i], new Vector2(w, Px(30f)))) hit = i;
            }
            return hit;
        }

        /// <summary>One choice out of a few, as joined buttons. Returns true when it changed.</summary>
        public static bool Segments(string id, IList<string> labels, ref int selected)
        {
            bool changed = false;
            ImGui.PushID(id);
            float sp = Px(2f);
            float w = (ImGui.GetContentRegionAvail().X - sp * (labels.Count - 1)) / Math.Max(1, labels.Count);
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(sp, ImGui.GetStyle().ItemSpacing.Y));
            for (int i = 0; i < labels.Count; i++)
            {
                if (i > 0) ImGui.SameLine();
                bool on = i == selected;
                if (on) ImGui.PushStyleColor(ImGuiCol.Button, ImTheme.Deep);
                if (ImGui.Button(labels[i] + "##" + i, new Vector2(w, Px(30f))) && !on) { selected = i; changed = true; }
                if (on) ImGui.PopStyleColor();
            }
            ImGui.PopStyleVar();
            ImGui.PopID();
            return changed;
        }

        /// <summary>The grid without tooltips (kept for mods built against API 3.0.0).</summary>
        public static int ChoiceGrid(string id, int count, int columns, Func<int, string> label, int selected,
            Func<int, Texture> icon, float cellHeight, Func<int, bool> enabled)
            => ChoiceGrid(id, count, columns, label, selected, icon, cellHeight, enabled, null);

        /// <summary>
        /// A grid of choices with optional icons. Returns the index clicked this frame (or -1);
        /// <see cref="LastClickDouble"/> tells whether it was a double click. <paramref name="tip"/> gives a cell's
        /// tooltip (shown like <see cref="Tip"/>, also over disabled cells).
        /// </summary>
        public static int ChoiceGrid(string id, int count, int columns, Func<int, string> label, int selected,
            Func<int, Texture> icon = null, float cellHeight = 34f, Func<int, bool> enabled = null, Func<int, string> tip = null)
        {
            int hit = -1;
            LastClickDouble = false;
            columns = Math.Max(1, columns);
            float sp = ImGui.GetStyle().ItemSpacing.X, h = Px(cellHeight), pad = Px(5f);
            float w = (ImGui.GetContentRegionAvail().X - sp * (columns - 1)) / columns;
            var dl = ImGui.GetWindowDrawList();
            uint bg = ImGui.GetColorU32(ImGuiCol.FrameBg), sel = ImGui.GetColorU32(ImTheme.Deep);
            uint text = ImGui.GetColorU32(ImGuiCol.Text), dim = ImGui.GetColorU32(ImGuiCol.TextDisabled);
            float round = ImGui.GetStyle().FrameRounding;
            ImGui.PushID(id);
            for (int i = 0; i < count; i++)
            {
                if (i % columns != 0) ImGui.SameLine();
                bool on = enabled == null || enabled(i);
                var p = ImGui.GetCursorScreenPos();
                dl.AddRectFilled(p, p + new Vector2(w, h), i == selected ? sel : bg, round);
                ImGui.PushID(i);
                ImGui.BeginDisabled(!on);
                if (ImGui.Selectable("##c", i == selected, ImGuiSelectableFlags.AllowDoubleClick, new Vector2(w, h)) && on)
                {
                    hit = i;
                    LastClickDouble = ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
                }
                ImGui.EndDisabled();
                if (tip != null) Tip(tip(i));
                ImGui.PopID();
                float tx = p.X + pad;
                var tex = icon?.Invoke(i);
                if (tex != null)
                {
                    float s = h - 2 * pad;
                    dl.AddImage(ImGuiHost.TextureId(tex), new Vector2(p.X + pad, p.Y + pad), new Vector2(p.X + pad + s, p.Y + pad + s), new Vector2(0f, 1f), new Vector2(1f, 0f));
                    tx += s + pad;
                }
                string l = label(i) ?? "";
                var ts = ImGui.CalcTextSize(l);
                dl.PushClipRect(p, p + new Vector2(w - pad, h), true);
                dl.AddText(new Vector2(tx, p.Y + (h - ts.Y) / 2f), on ? text : dim, l);
                dl.PopClipRect();
            }
            ImGui.PopID();
            return hit;
        }

        /// <summary>A Unity texture in the layout (upright).</summary>
        public static void Image(Texture tex, float width, float height)
        {
            if (tex == null) { ImGui.Dummy(new Vector2(width, height)); return; }
            ImGui.Image(ImGuiHost.TextureId(tex), new Vector2(width, height), new Vector2(0f, 1f), new Vector2(1f, 0f));
        }

        /// <summary>An id for ImGui.Image / draw lists for a Unity texture. Unity's v runs upwards: use uv0 (0,1), uv1 (1,0).</summary>
        public static IntPtr TextureId(Texture tex) => ImGuiHost.TextureId(tex);

        /// <summary>On the same line, moves to where an item <paramref name="width"/> wide ends at the right edge.</summary>
        public static void RightAlign(float width)
        {
            ImGui.SameLine();
            float x = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - width;
            if (x > ImGui.GetCursorPosX()) ImGui.SetCursorPosX(x);
        }

        /// <summary>A label on the left and a widget on the right, lined up across rows (call before the widget).</summary>
        public static void Field(string label, float labelWidth = 190f)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(label);
            ImGui.SameLine(Px(labelWidth));
            ImGui.SetNextItemWidth(-1f);
        }

        /// <summary>A text box bound to a <see cref="TextField"/>. Returns true when Enter was pressed in it.</summary>
        public static bool Input(string id, TextField field, bool focus = false)
        {
            if (focus) ImGui.SetKeyboardFocusHere();
            string t = field.Text ?? "";
            ImGui.SetNextItemWidth(-1f);
            bool enter = ImGui.InputTextWithHint(id, field.Placeholder ?? "", ref t, (uint)Math.Max(1, field.MaxLength), ImGuiInputTextFlags.EnterReturnsTrue);
            field.Text = t;
            return enter;
        }
    }
}
