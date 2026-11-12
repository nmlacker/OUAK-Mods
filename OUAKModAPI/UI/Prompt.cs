using System;
using System.Collections.Generic;
using ImGuiNET;
using UnityEngine;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace OUAKModAPI
{
    /// <summary>One choice in a <see cref="Prompt"/>.</summary>
    public sealed class PromptOption
    {
        public string Label;
        /// <summary>A line explaining the choice, shown while it's selected.</summary>
        public string Detail;
        public bool Enabled = true;
        /// <summary>Shown instead of the detail when the choice is disabled.</summary>
        public string DisabledReason;
        /// <summary>Runs on the main thread (from Update, never inside OnGUI) when picked.</summary>
        public Action Chosen;

        public PromptOption() { }
        public PromptOption(string label, Action chosen, string detail = null)
        {
            Label = label;
            Chosen = chosen;
            Detail = detail;
        }
    }

    /// <summary>
    /// A centred popup with a few choices, for keyboard, controller and mouse alike: Up/Down (W/S, D-pad) move,
    /// Enter/Space (A) picks, Esc/Backspace (B) cancels, or click a choice. The game's controls are locked while
    /// it's open. One prompt at a time; a new one replaces the old (the old one counts as cancelled).
    /// <code>Prompt.Show("Restart the stage", "Who restarts?", new[] {
    ///     new PromptOption("Everyone", () =&gt; ...), new PromptOption("Only me", () =&gt; ...) });</code>
    /// </summary>
    public static class Prompt
    {
        private sealed class Shown
        {
            public string Title, Body;
            public List<PromptOption> Options;
            public Action Cancelled;
            public int Selected;
        }

        private static Shown _p;
        // The game's own UI (the pause menu's buttons) mustn't take the clicks meant for the prompt.
        private static UnityEngine.EventSystems.EventSystem _pausedEvents;
        private static bool _armed;

        public static bool Active => _p != null;
        /// <summary>The title of the prompt on screen, or null.</summary>
        public static string CurrentTitle => _p?.Title;

        /// <summary>Shows a prompt. <paramref name="cancelled"/> runs when it's closed without a choice.</summary>
        public static void Show(string title, string body, IList<PromptOption> options, Action cancelled = null)
        {
            if (options == null || options.Count == 0) throw new ArgumentException("A prompt needs at least one option.", nameof(options));
            var old = _p;
            HoldGameUi(true);
            _p = new Shown { Title = title ?? "", Body = body ?? "", Options = new List<PromptOption>(options), Cancelled = cancelled };
            _p.Selected = Math.Max(0, _p.Options.FindIndex(o => o.Enabled));
            // The key or button that opened it must be let go first, or it would pick right away.
            _armed = false;
            if (old != null && old.Cancelled != null) ModTasks.Later(old.Cancelled);
        }

        /// <summary>Closes the prompt as if cancelled.</summary>
        public static void Close() => Finish(null);

        /// <summary>Self-test: picks choice <paramref name="index"/> as if the player had.</summary>
        public static bool Choose(int index)
        {
            if (_p == null || index < 0 || index >= _p.Options.Count || !_p.Options[index].Enabled) return false;
            Finish(_p.Options[index]);
            return true;
        }

        private static void Finish(PromptOption chosen)
        {
            var p = _p;
            if (p == null) return;
            _p = null;
            HoldGameUi(false);
            GameInput.HoldBriefly(4);
            if (chosen != null) { if (chosen.Chosen != null) ModTasks.Later(chosen.Chosen); }
            else if (p.Cancelled != null) ModTasks.Later(p.Cancelled);
        }

        internal static void Update()
        {
            var p = _p;
            if (p == null) return;
            GameInput.BlockThisFrame("prompt");
            if (TextInput.IsTyping) return;
            if (!_armed)
            {
                if (ConfirmHeld() || BackHeld()) return;
                _armed = true;
                return;
            }
            if (Key(KeyCode.UpArrow) || Key(KeyCode.W) || Pad.UpPressed || Pad.LeftPressed) Move(p, -1);
            else if (Key(KeyCode.DownArrow) || Key(KeyCode.S) || Pad.DownPressed || Pad.RightPressed) Move(p, 1);
            else if (Key(KeyCode.Return) || Key(KeyCode.KeypadEnter) || Key(KeyCode.Space) || Pad.ConfirmPressed)
            {
                var o = p.Options[p.Selected];
                if (o.Enabled) Finish(o);
            }
            else if (Key(KeyCode.Escape) || Key(KeyCode.Backspace) || Pad.BackPressed) Finish(null);
        }

        private static void HoldGameUi(bool hold)
        {
            try
            {
                if (hold)
                {
                    var es = UnityEngine.EventSystems.EventSystem.current;
                    if (es != null && es.enabled && _pausedEvents == null)
                    {
                        es.enabled = false;
                        _pausedEvents = es;
                    }
                }
                else if (_pausedEvents != null)
                {
                    _pausedEvents.enabled = true;
                    _pausedEvents = null;
                }
            }
            catch { _pausedEvents = null; }
        }

        private static void Move(Shown p, int dir)
        {
            for (int i = 1; i <= p.Options.Count; i++)
            {
                int n = ((p.Selected + dir * i) % p.Options.Count + p.Options.Count) % p.Options.Count;
                if (p.Options[n].Enabled) { p.Selected = n; return; }
            }
        }

        private static bool Key(KeyCode k)
        {
            try { return Input.GetKeyDown(k); } catch { return false; }
        }

        private static bool ConfirmHeld()
        {
            try
            {
                if (Input.GetKey(KeyCode.Return) || Input.GetKey(KeyCode.KeypadEnter) || Input.GetKey(KeyCode.Space)) return true;
                var pad = UnityEngine.InputSystem.Gamepad.current;
                return pad != null && pad.buttonSouth.isPressed;
            }
            catch { return false; }
        }

        private static bool BackHeld()
        {
            try
            {
                if (Input.GetKey(KeyCode.Escape) || Input.GetKey(KeyCode.Backspace)) return true;
                var pad = UnityEngine.InputSystem.Gamepad.current;
                return pad != null && pad.buttonEast.isPressed;
            }
            catch { return false; }
        }

        /// <summary>Inside the API's ImGui frame: the prompt, centred and on top.</summary>
        internal static void Draw()
        {
            var p = _p;
            if (p == null) return;
            var vp = ImGui.GetMainViewport();
            // Dim the game behind it.
            ImGui.GetBackgroundDrawList().AddRectFilled(vp.Pos, vp.Pos + vp.Size, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.35f)));
            float w = ImUi.Px(580f);
            ImGui.SetNextWindowPos(vp.Pos + vp.Size * 0.5f, ImGuiCond.Always, new Vector2(0.5f, 0.5f));
            ImGui.SetNextWindowSizeConstraints(new Vector2(w, 0f), new Vector2(w, vp.Size.Y * 0.9f));
            ImGui.SetNextWindowFocus();
            ImGui.PushStyleColor(ImGuiCol.Border, ImTheme.Accent);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, ImUi.Px(2f));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(ImUi.Px(20f), ImUi.Px(16f)));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, ImUi.Px(12f));
            const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoSavedSettings |
                                           ImGuiWindowFlags.NoMove | ImGuiWindowFlags.AlwaysAutoResize;
            if (ImGui.Begin("##ouak.prompt", flags))
            {
                ImUi.Title(p.Title);
                if (!string.IsNullOrEmpty(p.Body))
                {
                    ImGui.Spacing();
                    ImUi.Wrapped(p.Body, new Vector4(0.9f, 0.9f, 0.95f, 1f));
                }
                ImGui.Spacing();
                ImGui.Spacing();
                var io = ImGui.GetIO();
                for (int i = 0; i < p.Options.Count; i++)
                {
                    var o = p.Options[i];
                    bool selected = i == p.Selected;
                    ImGui.BeginDisabled(!o.Enabled);
                    ImGui.PushStyleColor(ImGuiCol.Header, ImTheme.Deep);
                    ImGui.PushStyleColor(ImGuiCol.HeaderHovered, ImTheme.Deep);
                    var cell = ImGui.GetCursorScreenPos();
                    float cw = ImGui.GetContentRegionAvail().X, ch = ImUi.Px(38f);
                    ImGui.GetWindowDrawList().AddRectFilled(cell, cell + new Vector2(cw, ch),
                        ImGui.GetColorU32(o.Enabled ? new Vector4(0.18f, 0.17f, 0.25f, 1f) : new Vector4(0.12f, 0.12f, 0.15f, 1f)), ImUi.Px(5f));
                    if (ImGui.Selectable((selected ? ">  " : "    ") + o.Label + "##opt" + i, selected, ImGuiSelectableFlags.None, new Vector2(cw, ch)) && o.Enabled)
                    {
                        p.Selected = i;
                        Finish(o);
                    }
                    // The mouse picks what it points at, so the detail line follows it.
                    if (o.Enabled && ImGui.IsItemHovered() && (io.MouseDelta.X != 0f || io.MouseDelta.Y != 0f)) p.Selected = i;
                    ImGui.PopStyleColor(2);
                    ImGui.EndDisabled();
                }
                if (_p == p)
                {
                    var sel = p.Options[p.Selected];
                    string detail = sel.Enabled ? sel.Detail : sel.DisabledReason ?? sel.Detail;
                    ImGui.Spacing();
                    ImUi.Wrapped(detail, new Vector4(0.75f, 0.85f, 1f, 1f));
                }
                ImGui.Spacing();
                ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
                ImGui.TextDisabled("Up / Down or D-pad: choose  ·  Enter or A: select  ·  Esc or B: cancel");
                ImGui.PopStyleVar();
            }
            ImGui.End();
            ImGui.PopStyleVar(3);
            ImGui.PopStyleColor();
        }
    }
}
