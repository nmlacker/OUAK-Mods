using System;
using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using MelonLoader;
using UnityEngine;
using Vector2 = System.Numerics.Vector2;

namespace OUAKModAPI
{
    /// <summary>A page in the mod window (<see cref="ModMenu.AddTab"/>).</summary>
    public sealed class ModTab
    {
        public string Id;
        public string Title;
        /// <summary>Draws the page with ImGui calls (every frame while the tab is shown), inside a scrolling area.</summary>
        public Action Draw;
        /// <summary>Tabs are sorted by this (lower first); ties keep the order they were added in.</summary>
        public int Order;
        public string Owner;
        /// <summary>When set and false, the tab is hidden.</summary>
        public Func<bool> Visible;
        internal int Added;
    }

    /// <summary>
    /// The one window every mod shares (Right Shift by default, rebindable), drawn with Dear ImGui. Each mod adds
    /// its pages as tabs, listed down the left side, and draws them with ImGui.NET (and <see cref="ImUi"/>):
    /// <code>ModMenu.AddTab("mymod", "My Mod", () =&gt; { ImGui.Text("Hi!"); if (ImGui.Button("Do it")) ...; });</code>
    /// The window draws the tab list, the status line (<see cref="ModTasks.Status"/>), the "I understand"
    /// confirmation (<see cref="ConfirmDialog"/>) in place of the page, and toasts while closed. While it's open
    /// the mouse pointer is shown, the game's controls are locked (by default) and a controller moves through it
    /// (D-pad / left stick, A to press, B to back out).
    /// For a window of your own instead, see <see cref="ModWindows"/>.
    /// </summary>
    public static class ModMenu
    {
        /// <summary>The window's first size at 1080p.</summary>
        public const float W = 900f, H = 760f;

        /// <summary>The window's title bar text.</summary>
        public static string Title = "Once Upon a Katamari  ·  Mods";

        private static readonly List<ModTab> _tabs = new();
        private static int _added;
        private static bool _open, _focus, _selectPending;
        private static string _current;
        private static MelonPreferences_Entry<KeyCode> _toggleKey;
        private static MelonPreferences_Entry<float> _uiScale;
        private static MelonPreferences_Entry<bool> _blockInput;

        public static bool IsOpen => _open;

        /// <summary>The scale the windows draw at (UI scale setting, or by screen height).</summary>
        public static float Scale => ImUi.Scale;

        /// <summary>UI scale setting: 0 = automatic (by screen height).</summary>
        public static float UiScaleSetting
        {
            get => _uiScale?.Value ?? 0f;
            set { if (_uiScale != null) { _uiScale.Value = value; MelonPreferences.Save(); } }
        }

        /// <summary>Lock the game's controls while the window is open (always while typing).</summary>
        public static bool BlockGameInput
        {
            get => _blockInput?.Value ?? true;
            set { if (_blockInput != null) { _blockInput.Value = value; MelonPreferences.Save(); } }
        }

        internal static KeyCode LegacyToggleKey => _toggleKey?.Value ?? KeyCode.RightShift;

        internal static void Init(MelonPreferences_Category cat)
        {
            // Kept only to seed the window key for people upgrading from 1.0/1.1 of the mod menu.
            _toggleKey = cat.GetEntry<KeyCode>("ToggleKey") ?? cat.CreateEntry("ToggleKey", KeyCode.RightShift, "Toggle key (legacy)", "Superseded by [OUAKModMenu_Keys] Key_OpenMenu.");
            _uiScale = cat.GetEntry<float>("UiScale") ?? cat.CreateEntry("UiScale", 0f, "UI scale", "Window scale. 0 = automatic (based on screen height).");
            _blockInput = cat.GetEntry<bool>("BlockGameInputWhileOpen") ?? cat.CreateEntry("BlockGameInputWhileOpen", true, "Block game input while open",
                "Stop the game from reacting to keys/controllers while the mod window is open. Always on while typing.");
        }

        // ---------------------------------------------------------------- tabs

        /// <summary>
        /// Adds a page. Dispose the result to remove it again (a hot mod's tabs are removed when it unloads).
        /// Adding an id that already exists replaces that tab.
        /// </summary>
        public static IDisposable AddTab(string id, string title, Action draw, int order = 100, string owner = null, Func<bool> visible = null)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("A tab needs an id.", nameof(id));
            _tabs.RemoveAll(t => t.Id == id);
            var tab = new ModTab { Id = id, Title = title ?? id, Draw = draw, Order = order, Owner = owner ?? "", Visible = visible, Added = _added++ };
            _tabs.Add(tab);
            return new Remover(() => _tabs.Remove(tab));
        }

        /// <summary>The tabs shown now, in order.</summary>
        public static IReadOnlyList<ModTab> Tabs =>
            _tabs.Where(t => { try { return t.Visible == null || t.Visible(); } catch { return false; } })
                 .OrderBy(t => t.Order).ThenBy(t => t.Added).ToList();

        /// <summary>The id of the tab shown (or to show when the window opens).</summary>
        public static string CurrentTab
        {
            get => _current;
            set { _current = value; _selectPending = true; }
        }

        public static void Open() => SetOpen(true);
        public static void Close() => SetOpen(false);
        public static void Toggle() => SetOpen(!_open);

        private static void SetOpen(bool open)
        {
            if (open == _open) return;
            _open = open;
            if (open) { _focus = true; _selectPending = true; }
            else
            {
                Hotkeys.CancelCapture();
                GameInput.HoldBriefly();
            }
        }

        internal static void Update()
        {
            if (!_open) return;
            if (BlockGameInput || TextInput.IsTyping || Hotkeys.Capturing != null) GameInput.BlockThisFrame("mod window");
        }

        // ---------------------------------------------------------------- drawing (inside the API's ImGui frame)

        internal static void Draw()
        {
            if (!_open)
            {
                string toast = ModTasks.CurrentToast(out bool error);
                if (toast != null) DrawToast(toast, error);
                return;
            }
            float s = ImUi.Scale;
            ImGui.SetNextWindowPos(new Vector2(40f, 40f) * s, ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSize(new Vector2(W, H) * s, ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSizeConstraints(new Vector2(520f, 380f) * s, new Vector2(float.MaxValue, float.MaxValue));
            if (_focus) { ImGui.SetNextWindowFocus(); _focus = false; }
            bool open = true;
            if (ImGui.Begin(Title + "###ouak.modwindow", ref open, ImGuiWindowFlags.NoCollapse))
            {
                var tabs = Tabs;
                if (tabs.Count > 0 && (_current == null || tabs.All(t => t.Id != _current))) _current = tabs[0].Id;
                float footer = ImGui.GetTextLineHeightWithSpacing() * 2.2f + ImGui.GetStyle().ItemSpacing.Y;

                if (ConfirmDialog.Active != null)
                {
                    ImGui.BeginChild("confirm", new Vector2(0f, -footer), ImGuiChildFlags.Borders);
                    DrawConfirm();
                    ImGui.EndChild();
                }
                else
                {
                    ImGui.BeginChild("tabs", new Vector2(ImUi.Px(175f), -footer), ImGuiChildFlags.Borders);
                    foreach (var t in tabs)
                    {
                        bool on = t.Id == _current;
                        if (ImGui.Selectable(t.Title + "##" + t.Id, on, ImGuiSelectableFlags.None, new Vector2(0f, ImUi.Px(30f))))
                            _current = t.Id;
                        if (on && _selectPending) { ImGui.SetItemDefaultFocus(); ImGui.SetScrollHereY(); }
                        // A controller moving down the list shows each page as it goes.
                        if (ImGui.IsItemFocused() && ImGui.GetIO().NavVisible && !on) _current = t.Id;
                    }
                    _selectPending = false;
                    ImGui.EndChild();
                    ImGui.SameLine();
                    ImGui.BeginChild("page", new Vector2(0f, -footer), ImGuiChildFlags.None);
                    var tab = tabs.FirstOrDefault(t => t.Id == _current);
                    if (tab == null) ImUi.Hint("No mod has added a page yet.");
                    else
                    {
                        ImGui.PushID(tab.Id);
                        try { tab.Draw?.Invoke(); }
                        catch (Exception ex)
                        {
                            ModTasks.SetStatus($"The {tab.Title} page failed: {ex.Message}", true);
                            ApiCore.Log.Error($"Tab {tab.Id} ({tab.Owner}) failed: {ex}");
                            _current = tabs[0].Id == tab.Id && tabs.Count > 1 ? tabs[1].Id : tabs[0].Id;
                        }
                        ImGui.PopID();
                    }
                    ImGui.EndChild();
                }

                ImGui.Separator();
                ImUi.Wrapped(ModTasks.Status, ModTasks.StatusIsError ? ImTheme.Err : ImTheme.Ok);
                ImGui.TextDisabled($"{Hotkeys.MenuKeyName} closes this window  ·  drag the title to move it, the corner to resize it");
            }
            ImGui.End();
            if (!open) Close();
        }

        internal static bool FocusPhrase;

        private static void DrawConfirm()
        {
            var req = ConfirmDialog.Active;
            ImUi.Title(req.Title);
            ImGui.Spacing();
            ImUi.Wrapped(req.Body, new System.Numerics.Vector4(0.92f, 0.92f, 0.96f, 1f));
            ImGui.Spacing();
            if (req.MakesBackup)
            {
                ImUi.Hint("A backup of all your save files is made first (Backups tab).");
                ImGui.Spacing();
                ImGui.TextUnformatted("Backup note (optional):");
                ImUi.Input("##note", ConfirmDialog.NoteField);
                ImGui.Spacing();
            }
            ImGui.TextUnformatted($"Type \"{ConfirmDialog.Phrase}\" to continue:");
            bool submit = ImUi.Input("##phrase", ConfirmDialog.PhraseField, FocusPhrase);
            FocusPhrase = false;
            ImGui.Spacing();

            bool ok = ConfirmDialog.PhraseAccepted;
            float bw = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2f;
            if (ImUi.Danger(req.ConfirmLabel, ok, bw) || (submit && ok)) { ConfirmDialog.Accept(); return; }
            ImGui.SameLine();
            if (ImUi.Button("Cancel", true, bw) || (ImGui.IsKeyPressed(ImGuiKey.Escape) && !ImGui.IsAnyItemActive())) { ConfirmDialog.Close(); return; }
            if (!ok && ConfirmDialog.PhraseField.Text.Length > 0) ImUi.Hint("Type the phrase exactly (capital letters don't matter).");
        }

        private static void DrawToast(string text, bool error)
        {
            var vp = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(new Vector2(vp.Pos.X + vp.Size.X / 2f, vp.Pos.Y + ImUi.Px(18f)), ImGuiCond.Always, new Vector2(0.5f, 0f));
            ImGui.SetNextWindowBgAlpha(0.92f);
            const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoInputs |
                                           ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoMove;
            if (ImGui.Begin("##ouak.toast", flags))
            {
                if (error) ImGui.TextColored(ImTheme.Err, text);
                else ImGui.TextUnformatted(text);
            }
            ImGui.End();
        }

        internal sealed class Remover : IDisposable
        {
            private Action _remove;
            public Remover(Action remove) => _remove = remove;
            public void Dispose()
            {
                _remove?.Invoke();
                _remove = null;
            }
        }
    }

    /// <summary>
    /// Things drawn over the game every frame (name tags, HUD additions, popups), below the mod window. The API
    /// calls overlays in order (lower first) on every OnGUI call; check <c>Event.current.type</c> yourself if
    /// you only draw on Repaint. An overlay that throws is switched off (and logged) instead of breaking the rest.
    /// </summary>
    public static class ModGui
    {
        private sealed class Overlay
        {
            public Action Draw;
            public int Order, Added;
            public string Owner;
            public bool Broken;
        }

        private static readonly List<Overlay> _overlays = new();
        private static int _added;

        /// <summary>GUI scale by screen height: 1 at 1080p, 2 at 4K.</summary>
        public static float GuiScale => Mathf.Clamp(Screen.height / 1080f, 1f, 3f);

        /// <summary>Adds an overlay; dispose the result to remove it.</summary>
        public static IDisposable AddOverlay(Action draw, int order = 0, string owner = null)
        {
            var o = new Overlay { Draw = draw, Order = order, Owner = owner ?? draw?.Method.DeclaringType?.Name, Added = _added++ };
            _overlays.Add(o);
            _overlays.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.Added.CompareTo(b.Added));
            return new ModMenu.Remover(() => _overlays.Remove(o));
        }

        internal static void DrawOverlays()
        {
            for (int i = 0; i < _overlays.Count; i++)
            {
                var o = _overlays[i];
                if (o.Broken || o.Draw == null) continue;
                var m = GUI.matrix;
                try { o.Draw(); }
                catch (Exception e)
                {
                    o.Broken = true;
                    ApiCore.Log.Warn($"Overlay from {o.Owner} turned off: {e.Message}");
                }
                finally
                {
                    GUI.matrix = m;
                    GUI.enabled = true;
                }
            }
        }
    }
}
