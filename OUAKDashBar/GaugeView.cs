using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Il2Cpp;
using Il2CppApp.KatamariSin;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppTMPro;
using OUAKModAPI;
using UnityEngine;
using UnityEngine.UI;

namespace OUAKDashBar
{
    /// <summary>
    /// Drives the game's own dash gauge, <c>MainGameUIManager.MultiDashUI</c> (<c>UIMultiDashGauge</c>, the object
    /// <c>MainGameUICanvas/LAYER00/MultiDash</c>). It is in every story stage's HUD but switched off, and only the
    /// battle code's cool-time functions (<c>ExecuteOujiDashTimerMulti</c>, never called; <c>ResetOujiDashMultiCoolTime</c>,
    /// <c>ForceOujiDashMultiCoolTime</c>) ever set it. Measured 2026-11-07: switching the object on shows it at the
    /// bottom centre of the screen, and its own methods work there:
    /// <list type="bullet">
    /// <item><c>SetGaugeValue(v)</c> sets the fill of both bars (Base/Gauge, yellow; BaseMax/GaugeMax, white).</item>
    /// <item><c>SetDisplayMax(true)</c> shows the rainbow "Max" frame, which the game uses for unlimited dash (battle
    /// Shoes and the King Chance call <c>ResetOujiDashMultiCoolTime</c>: gauge full + Max look).</item>
    /// </list>
    /// The mod adds a countdown label (the stage clock's font) and uses the gauge's own dash icon (a vertical fill
    /// image the game never fills) to show the exhaustion's wait filling up.
    /// <para>Always on top (the user's choice): the gauge is moved from the HUD canvas (<c>LAYER00</c>, camera space,
    /// drawn by the game's <c>UICamera</c>) to an overlay canvas of the mod's own, under <c>MainGameUICanvas</c> so it goes
    /// with the stage's HUD, with the HUD's own scaler settings: it sits exactly where the game put it, and nothing drawn
    /// in camera space (the 3D scene, the game's other camera-space canvases) can be drawn over it. It goes back when the
    /// mod lets go of it. Measured 2026-11-07: 3D geometry right in front of the camera didn't cover the gauge in the
    /// game's canvas either (its camera is a separate one); the bar's frame art is semi-transparent, so busy scenery
    /// shows through an empty bar.</para>
    /// </summary>
    internal static class GaugeView
    {
        private static UIMultiDashGauge _gauge;
        private static bool _wasActive, _shown;
        private static Image _icon, _iconMax;
        private static TextMeshProUGUI _label;
        private static string _lastText;

        // Always on top: the mod's overlay canvas, the game's HUD canvas and where the gauge came from.
        internal const int OnTopOrder = -100;   // low: other overlay canvases (the game's, UnityExplorer...) stay above it
        private static GameObject _onTop;
        private static Canvas _hudCanvas;
        private static CanvasScaler _hudScaler, _topScaler;
        private static Transform _home;
        private static int _homeIndex;
        private static bool _hideInMessages;

        // Out of the way of other HUD at the bottom of the screen: the game's own sub-challenge counter and freebie slot,
        // and other mods' HUD (the API's HudSpace, e.g. OUAK Multiplayer's player bar). The gauge (with its countdown)
        // moves to the nearest free spot, in screen pixels from the game's own place (x right, y up).
        private static Vector2 _homePos, _offset, _target, _pending;
        private static float _scale = 1f, _targetScale = 1f, _pendingScale = 1f;
        private static string _dock = "home", _pendingDock = "home";
        private static int _pendingFrames;
        private static float _pendingSince;
        /// <summary>Other mods' HUD: the API's HudSpace claims (IMGUI screen rects), set by the mod.</summary>
        internal static Func<IEnumerable<(string owner, Rect rect)>> OtherHud;
        /// <summary>The gap kept from other HUD, in screen pixels at 1080p (scaled with the screen).</summary>
        internal const float LiftGap = 6f;
        /// <summary>The highest the bar's top may go, as a share of the screen height (it stays in the bottom of the screen).</summary>
        internal const float MaxTop = 0.42f;
        /// <summary>A better (less moved) spot must stay free this many frames and this long before the bar moves back.</summary>
        internal const int HoldFrames = 3;
        internal const float HoldTime = 0.5f;
        /// <summary>The smallest the bar is drawn beside the freebie slot (share of its size).</summary>
        internal const float MinScale = 0.55f;
        /// <summary>Where the bar is: "home", "up" (above what's under it) or "beside the freebie slot"; and its scale.</summary>
        internal static string Dock => _dock;
        internal static float Scale => _scale;
        /// <summary>How far the gauge is moved now / wants to be, in screen pixels (for the test).</summary>
        internal static float Lift => _offset.y;
        internal static float LiftTarget => _target.y;
        internal static float Shift => _offset.x;
        internal static float ShiftTarget => _target.x;
        /// <summary>The HUD the bar keeps clear of this frame (bottom-up screen rects), for the test and the log.</summary>
        internal static readonly List<(string name, Rect rect)> Obstacles = new();
        /// <summary>Times the bar's target place changed, for the test.</summary>
        internal static int LiftChanges { get; private set; }
        /// <summary>Times the bar went home and away again within a second (a dip), for the test.</summary>
        internal static int Dips { get; private set; }
        private static float _wentHomeAt = -10f;

        /// <summary>True while the gauge is on the mod's overlay canvas.</summary>
        internal static bool OnTop => _gauge != null && _onTop != null && _gauge.transform.parent != null && _gauge.transform.parent.Pointer == _onTop.transform.Pointer;
        /// <summary>Where the game's own canvas put the bar's frame, in screen pixels (measured before moving it).</summary>
        internal static Rect HomeScreenRect { get; private set; }
        /// <summary>The game's HUD canvas and every root canvas seen when the gauge was taken (for logs and the test).</summary>
        internal static Canvas HudCanvas => _hudCanvas;
        internal static string Canvases { get; private set; } = "";
        internal static bool HidesInMessages => _hideInMessages;

        /// <summary>The gauge in use (for the test).</summary>
        internal static UIMultiDashGauge Gauge => _gauge;
        /// <summary>The countdown label (for the test).</summary>
        internal static TextMeshProUGUI Label => _label;
        /// <summary>The label's text this frame ("" when hidden).</summary>
        internal static string LabelText => _label != null && _label.gameObject.activeInHierarchy ? _lastText ?? "" : "";
        internal static bool Shown => _shown && _gauge != null;
        internal static Image Icon => _icon;

        /// <summary>The game's HUD is up (not hidden for the pause menu, a cutscene, the opening or the results).</summary>
        internal static bool HudVisible(MainGameManager gm)
        {
            var ui = gm?.MainUI;
            if (ui == null) return false;
            try
            {
                if (GameRefs.IsMultiplay)
                {
                    // A match: only while the round runs (not the countdown before it, nor the results after it), and not
                    // while the match is paused.
                    if (MatchState() != "GameState") return false;
                    var mgr = GameRefs.Multiplay;
                    if (mgr != null && mgr.isGamePause) return false;
                }
                // A story stage: all the time you're in it (the user's choice, 2026-11-12), also while the King speaks (his
                // opening talk and his remarks), until the stage ends. Not in the pause menu or cutscenes (below).
                else if (!gm.PlayerInitiated || gm._isInClearDemo || gm._isInGameOver) return false;
                if (ui.PauseObjs != null && ui.PauseObjs.isOpend) return false;
                if (ui.DemoObjs != null && ui.DemoObjs.isOpend) return false;
                if (ui.Layer00 != null && !ui.Layer00.gameObject.activeInHierarchy) return false;
                // The HUD canvas itself switched off, or its camera: the game's HUD isn't drawn, so neither is the bar.
                if (_hudCanvas != null && (!_hudCanvas.enabled || (_hudCanvas.renderMode != RenderMode.ScreenSpaceOverlay
                    && _hudCanvas.worldCamera != null && !_hudCanvas.worldCamera.isActiveAndEnabled))) return false;
                return true;
            }
            catch { return false; }
        }

        /// <summary>The multiplayer match's state ("GameState" while the round runs), or "" outside matches.</summary>
        internal static string MatchState()
        {
            try { return GameRefs.IsMultiplay ? TrainingTrace.StateName(GameRefs.Multiplay?.stateMachine) : ""; }
            catch { return ""; }
        }

        internal static void Show(DashState s)
        {
            var gm = GameRefs.Main;
            if (!Attach(gm?.MainUI)) return;
            bool visible = HudVisible(gm) && !(DashBarMod.HideWhenFull && s.Phase == DashPhase.Full);
            if (!visible) { SetVisible(false); return; }
            SetVisible(true);
            SyncScaler();
            PlaceAroundOthers(gm);

            // Unlimited dash: the game's own Max (rainbow) look, which its battle code shows for unlimited dash
            // (ResetOujiDashMultiCoolTime). The bar still shows the real stamina: full under the API's free unlimited dash
            // (2026-11-09), and with a mod's cheat that only sets the game's switch, what the dash starts cost.
            // Compared with the gauge's real state, not with what was set last: the game's battle code writes to the gauge
            // too (Shoes and the King Chance: full in the Max look, ResetOujiDashMultiCoolTime; the King Chance's end:
            // empty in the normal look, ForceOujiDashMultiCoolTime).
            bool max = s.Phase == DashPhase.Unlimited;
            float fill = s.Fill;
            if (_gauge.MaxParent == null || _gauge.BaseParent == null || _gauge.MaxParent.activeSelf != max || _gauge.BaseParent.activeSelf == max)
                _gauge.SetDisplayMax(max);
            if (_gauge.BaseGauge == null || _gauge.MaxGauge == null || Mathf.Abs(_gauge.BaseGauge.fillAmount - fill) > 0.0005f
                || Mathf.Abs(_gauge.MaxGauge.fillAmount - fill) > 0.0005f)
                _gauge.SetGaugeValue(fill);

            // The dash icon: full, except while out of dash, when it fills up over the wait (empty at the start).
            float iconFill = s.Phase == DashPhase.Exhausted ? Mathf.Clamp01(1f - s.RegenIn / DashState.ExhaustTime) : 1f;
            if (_icon != null) _icon.fillAmount = iconFill;
            if (_iconMax != null) _iconMax.fillAmount = 1f;

            PlaceLabel();
            SetLabel(LabelFor(s), s.Phase);
        }

        // Keeps the countdown just right of the bar's frame. Done every frame: the frame's place comes from the canvas's
        // layout, which a freshly made canvas only has a frame later (placed once, the countdown ended up off screen).
        private static void PlaceLabel()
        {
            if (_label == null) return;
            try
            {
                var frame = _gauge.transform.Find("Base")?.TryCast<RectTransform>();
                var box = _label.transform.parent?.TryCast<RectTransform>();
                if (frame == null || box == null) return;
                var r = frame.rect;
                Vector3 right = frame.localPosition + Vector3.Scale(frame.localScale, new Vector3(r.xMax, r.center.y, 0f)) + new Vector3(18f, 0f, 0f);
                if ((box.localPosition - right).sqrMagnitude > 0.01f) box.localPosition = right;
            }
            catch { }
        }

        /// <summary>The countdown's text for <paramref name="s"/>, or null for none.</summary>
        internal static string LabelFor(DashState s)
        {
            if (!DashBarMod.ShowTimer) return null;
            switch (s.Phase)
            {
                case DashPhase.Exhausted:
                    return Seconds(s.RegenIn);
                case DashPhase.Dashing:
                    return DashBarMod.TimerWhileDashing ? Seconds(s.RegenIn) : null;
                case DashPhase.Unlimited:
                    // Shoes: their time left. Other unlimited dash (a mod's cheat) has no end to count down to.
                    return s.UnlimitedLeft >= 0f ? Seconds(s.UnlimitedLeft) : null;
                default:
                    return null;
            }
        }

        private static string Seconds(float t) => Mathf.Max(0f, t).ToString("0.0", CultureInfo.InvariantCulture);

        internal static void Hide()
        {
            if (_gauge == null) return;
            SetVisible(false);
        }

        /// <summary>Lets go of the gauge (leaving the stage, quitting): the game's state as it was.</summary>
        internal static void Release()
        {
            try
            {
                if (_gauge != null)
                {
                    SetVisible(false);
                    _gauge.SetDisplayMax(false);
                    _gauge.SetGaugeValue(1f);
                    if (_icon != null) _icon.fillAmount = 1f;
                    if (_label != null) UnityEngine.Object.Destroy(_label.transform.parent.gameObject);
                    if (_home != null && OnTop)
                    {
                        SetOffset(Vector2.zero);
                        _gauge.transform.SetParent(_home, false);
                        _gauge.transform.SetSiblingIndex(_homeIndex);
                    }
                }
                if (_onTop != null) UnityEngine.Object.Destroy(_onTop);
            }
            catch { }
            _gauge = null; _label = _shadow = null; _icon = _iconMax = null;
            _onTop = null; _hudCanvas = null; _hudScaler = _topScaler = null; _home = null; _hideInMessages = false;
            _shown = false; _lastText = null;
            _offset = _target = _pending = Vector2.zero; _pendingFrames = 0;
            _scale = _targetScale = _pendingScale = 1f; _dock = _pendingDock = "home";
        }

        private static void SetVisible(bool on)
        {
            if (_gauge == null || (_shown == on && _gauge.gameObject.activeSelf == (on || _wasActive))) return;
            try
            {
                _gauge.gameObject.SetActive(on || _wasActive);
                _shown = on;
            }
            catch { }
        }

        // Finds the stage's gauge (a new HUD comes with every stage load) and builds the label once.
        private static bool Attach(MainGameUIManager ui)
        {
            UIMultiDashGauge g = null;
            try { g = ui != null ? ui.MultiDashUI : null; } catch { }
            if (g == null) { if (_gauge != null) Release(); return false; }
            if (_gauge != null && _gauge.Pointer == g.Pointer) return true;
            if (_gauge != null) Release();
            _gauge = g;
            _wasActive = g.gameObject.activeSelf;
            _shown = _wasActive;
            _lastText = null;
            try { MoveOnTop(ui, g); }
            catch (Exception e) { DashBarMod.Log.Warn("On top: " + e.Message); }
            try
            {
                _icon = FindImage(g.transform, "Base/DashIcon");
                _iconMax = FindImage(g.transform, "BaseMax/DashIconMax");
                _label = MakeLabel(ui, g);
            }
            catch (Exception e) { DashBarMod.Log.Warn("Label: " + e.Message); }
            string hud = _hudCanvas != null ? $"{_hudCanvas.name}: {_hudCanvas.renderMode}, plane {_hudCanvas.planeDistance:0.##}, camera {_hudCanvas.worldCamera?.name}" : "?";
            DashBarMod.Log.Info($"Using the game's dash gauge ({g.name}, was {(_wasActive ? "on" : "off")}); icon {(_icon != null ? "found" : "missing")}, " +
                                $"label {(_label != null ? "made" : "missing")}, on top {OnTop} (from {hud}), hidden during messages {_hideInMessages}.");
            DashBarMod.Log.Info("Canvases: " + Canvases);
            return true;
        }

        // Moves the gauge from the game's HUD canvas (camera space) to an overlay canvas of the mod's own with the HUD's
        // scaler settings: same place on screen, drawn after everything in camera space.
        private static void MoveOnTop(MainGameUIManager ui, UIMultiDashGauge g)
        {
            var home = g.transform.parent;
            _hudCanvas = home != null ? home.GetComponentInParent<Canvas>() : null;
            if (_hudCanvas != null && !_hudCanvas.isRootCanvas) _hudCanvas = _hudCanvas.rootCanvas;
            _hudScaler = _hudCanvas != null ? _hudCanvas.GetComponent<CanvasScaler>() : null;
            var frame = g.transform.Find("Base")?.TryCast<RectTransform>();
            if (frame != null) HomeScreenRect = ScreenRect(frame, _hudCanvas);
            CheckMessages();
            if (_hudCanvas == null || home == null) { DashBarMod.Log.Warn("On top: no HUD canvas above the gauge; left where it is."); return; }
            if (_hudCanvas.renderMode == RenderMode.ScreenSpaceOverlay) return;   // already drawn over the 3D scene

            _onTop = new GameObject("OUAKDashBar_OnTop");
            _onTop.layer = home.gameObject.layer;
            _onTop.transform.SetParent(ui.transform, false);   // destroyed with the stage's HUD
            var c = _onTop.AddComponent<Canvas>();
            c.renderMode = RenderMode.ScreenSpaceOverlay;
            c.sortingOrder = OnTopOrder;
            // TextMeshPro's SDF text reads its scale from these vertex channels; a new canvas has none (the countdown was
            // invisible on it, measured 2026-11-07) and this build's TMP never asks for them.
            c.additionalShaderChannels = _hudCanvas.additionalShaderChannels | AdditionalCanvasShaderChannels.TexCoord1
                                         | AdditionalCanvasShaderChannels.Normal | AdditionalCanvasShaderChannels.Tangent;
            _topScaler = _onTop.AddComponent<CanvasScaler>();
            SyncScaler();
            _home = home;
            _homeIndex = g.transform.GetSiblingIndex();
            g.transform.SetParent(_onTop.transform, false);   // keeps its anchors and position inside a same-sized canvas
            var grt = g.transform.TryCast<RectTransform>();
            _homePos = grt != null ? grt.anchoredPosition : Vector2.zero;
            _offset = _target = _pending = Vector2.zero; _pendingFrames = 0;
            _scale = _targetScale = _pendingScale = 1f; _dock = _pendingDock = "home";
        }

        // The HUD's scaler settings (the game's display manager may change them, e.g. for the window's shape).
        private static void SyncScaler()
        {
            if (_topScaler == null || _hudScaler == null) return;
            try
            {
                var a = _hudScaler; var b = _topScaler;
                if (b.uiScaleMode != a.uiScaleMode) b.uiScaleMode = a.uiScaleMode;
                if (b.referenceResolution != a.referenceResolution) b.referenceResolution = a.referenceResolution;
                if (b.screenMatchMode != a.screenMatchMode) b.screenMatchMode = a.screenMatchMode;
                if (b.matchWidthOrHeight != a.matchWidthOrHeight) b.matchWidthOrHeight = a.matchWidthOrHeight;
                if (b.scaleFactor != a.scaleFactor) b.scaleFactor = a.scaleFactor;
                if (b.referencePixelsPerUnit != a.referencePixelsPerUnit) b.referencePixelsPerUnit = a.referencePixelsPerUnit;
            }
            catch { }
        }

        // Where the game's message windows draw: any on a canvas that would be under the mod's overlay canvas means
        // the bar steps out of the way while a message is up. Also lists the root canvases for the log.
        private static void CheckMessages()
        {
            var all = new List<string>();
            try
            {
                foreach (var c in Resources.FindObjectsOfTypeAll<Canvas>())
                {
                    if (c == null || !c.isRootCanvas || !c.gameObject.activeInHierarchy) continue;
                    string where = c.renderMode == RenderMode.ScreenSpaceOverlay ? "" : $", camera {c.worldCamera?.name}, plane {c.planeDistance:0.##}";
                    all.Add($"{c.name} ({c.renderMode}, order {c.sortingOrder}{where}{(c.enabled ? "" : ", off")})");
                }
            }
            catch (Exception e) { all.Add("(" + e.Message + ")"); }
            Canvases = string.Join("; ", all);

            _hideInMessages = true;   // unless the message windows are known to draw over the bar
            try
            {
                var gui = SingletonMonoBehaviour<GlobalUIController>.Instance;
                if (gui == null) return;
                var roots = gui.GetComponentsInChildren<Canvas>(true).Where(c => c != null && c.isRootCanvas).ToList();
                if (roots.Count > 0 && roots.All(c => c.renderMode == RenderMode.ScreenSpaceOverlay && c.sortingOrder > OnTopOrder)) _hideInMessages = false;
            }
            catch { }
        }

        private static Rect ScreenRect(RectTransform rt, Canvas root)
        {
            var c = new Il2CppStructArray<Vector3>(4);
            rt.GetWorldCorners(c);
            Camera cam = root != null && root.renderMode != RenderMode.ScreenSpaceOverlay ? root.worldCamera : null;
            Vector2 a = RectTransformUtility.WorldToScreenPoint(cam, c[0]), b = RectTransformUtility.WorldToScreenPoint(cam, c[2]);
            return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
        }

        /// <summary>Where the bar's frame is on screen now (for the test).</summary>
        internal static Rect CurrentScreenRect()
        {
            var frame = _gauge != null ? _gauge.transform.Find("Base")?.TryCast<RectTransform>() : null;
            if (frame == null) return default;
            return ScreenRect(frame, OnTop ? _onTop.GetComponent<Canvas>() : _hudCanvas);
        }

        /// <summary>Where the countdown label's box is on screen (for the test).</summary>
        internal static Rect LabelScreenRect()
        {
            var box = _label != null ? _label.transform.parent?.TryCast<RectTransform>() : null;
            if (box == null) return default;
            return ScreenRect(box, OnTop ? _onTop.GetComponent<Canvas>() : _hudCanvas);
        }

        /// <summary>Test only: puts the gauge back in the game's HUD canvas (false) or on the overlay (true).</summary>
        internal static void SetOnTop(bool on)
        {
            if (_gauge == null || _onTop == null || _home == null || OnTop == on) return;
            if (!on) SetOffset(Vector2.zero);
            _gauge.transform.SetParent(on ? _onTop.transform : _home, false);
            if (!on) _gauge.transform.SetSiblingIndex(_homeIndex);
        }

        // The game's own HUD: whatever it draws in the bottom part of the screen (where the bar can go), one obstacle per HUD
        // piece (each child of LAYER00 / LAYER01: the freebie slot, the stage's goal counter, the sub-challenge counter, the
        // song title...), the union of its visible images and texts. Rescanned 10 times a second.
        private static readonly List<(string name, Rect rect)> _gameHud = new();
        private static float _nextScan;
        // Where each piece was last seen and since when it hasn't moved: a piece only counts once it has stopped
        // (measured: the freebie slot slides up from below the screen when you get a freebie, through the bar's place).
        private static readonly Dictionary<string, (Rect rect, float since)> _pieceRest = new();
        internal const float SettleTime = 0.6f;
        // Pop-ups that come and go while you roll (the last-caught object's name, the song title) and the markers pointing at
        // other princes (they sit at the screen edge whenever one is behind you; measured in battles and Target): not worth moving for.
        private static readonly string[] Transient = { "MonoNameDisplay", "BGM", "SonarGuide" };
        /// <summary>Frames each game HUD piece was in the way so far (name -> count), for the test's log.</summary>
        internal static readonly Dictionary<string, int> SeenPieces = new();

        private static void ScanGameHud(MainGameUIManager ui)
        {
            _gameHud.Clear();
            if (ui == null) return;
            float band = Screen.height * MaxTop, now = Time.unscaledTime;
            var seen = new HashSet<string>();
            void Piece(Transform piece, Canvas canvas, string name)
            {
                if (piece == null || !piece.gameObject.activeInHierarchy) return;
                if (_gauge != null && piece.Pointer == _gauge.transform.Pointer) return;
                bool any = false;
                Rect u = default;
                foreach (var gr in piece.GetComponentsInChildren<Graphic>(false))
                {
                    if (gr == null || !gr.enabled || gr.color.a < 0.05f) continue;
                    if (gr.canvasRenderer.GetInheritedAlpha() < 0.05f) continue;   // canvas groups faded out
                    var tmp = gr.TryCast<TMP_Text>();
                    if (tmp != null && string.IsNullOrWhiteSpace(tmp.text)) continue;
                    var r = ScreenRect(gr.rectTransform, canvas);
                    if (r.width < 2f || r.height < 2f || r.yMin > band) continue;
                    if (r.yMax <= 0f || r.xMax <= 0f || r.xMin >= Screen.width) continue;   // off screen (parked below it)
                    if (r.width > Screen.width * 0.9f && r.height > Screen.height * 0.5f) continue;   // full-screen fades and frames
                    u = any ? Rect.MinMaxRect(Mathf.Min(u.xMin, r.xMin), Mathf.Min(u.yMin, r.yMin), Mathf.Max(u.xMax, r.xMax), Mathf.Max(u.yMax, r.yMax)) : r;
                    any = true;
                }
                if (any) Settled(name, u);
            }
            void Settled(string name, Rect u)
            {
                seen.Add(name);
                if (!_pieceRest.TryGetValue(name, out var rest) || Mathf.Abs(rest.rect.x - u.x) > 2f || Mathf.Abs(rest.rect.y - u.y) > 2f
                    || Mathf.Abs(rest.rect.width - u.width) > 2f || Mathf.Abs(rest.rect.height - u.height) > 2f)
                    rest = (u, now);
                _pieceRest[name] = (u, rest.since);
                if (now - rest.since < SettleTime) return;   // still moving
                _gameHud.Add((name, u));
                SeenPieces[name] = SeenPieces.TryGetValue(name, out int n) ? n + 1 : 1;
            }
            foreach (var layer in new[] { ui.Layer00, ui.Layer01 })
            {
                if (layer == null || !layer.gameObject.activeInHierarchy) continue;
                var canvas = layer.GetComponent<Canvas>();
                if (canvas != null && !canvas.isRootCanvas) canvas = canvas.rootCanvas;
                for (int i = 0; i < layer.childCount; i++)
                {
                    var piece = layer.GetChild(i);
                    if (piece != null && Array.IndexOf(Transient, piece.name) < 0) Piece(piece, canvas, $"the game's {piece.name}");
                }
            }
            // The prince's picture at the bottom right: a separate camera's render, placed by UIOujiBackLoader._myRect.
            try
            {
                var my = ui.OujiBackLoader != null ? ui.OujiBackLoader._myRect : null;
                if (my != null && my.gameObject.activeInHierarchy)
                {
                    // The render area is wider than what's drawn in it: measured at 2560x1440, the area spans x 1827..2627 and
                    // the prince with his backdrop disc starts at about x 2050 (28% across it).
                    var r = PieceRect(my);
                    r = Rect.MinMaxRect(r.xMin + r.width * 0.28f, r.yMin, r.xMax, r.yMax);
                    if (r.width > 2f && r.width < Screen.width * 0.4f && r.yMin < band && r.yMax > 0f) Settled("the game's prince picture", r);
                }
            }
            catch { }
            foreach (var k in _pieceRest.Keys.Where(k => !seen.Contains(k)).ToList()) _pieceRest.Remove(k);
        }

        private static void CollectObstacles(MainGameManager gm)
        {
            Obstacles.Clear();
            try
            {
                if (OtherHud != null)
                    foreach (var (owner, r) in OtherHud())
                        if (owner != DashBarMod.HudOwner) Obstacles.Add((owner, new Rect(r.x, Screen.height - r.yMax, r.width, r.height)));
            }
            catch (Exception e) { DashBarMod.Log.Warn("HUD space: " + e.Message + " (not followed any more)"); OtherHud = null; }
            float t = Time.unscaledTime;
            if (t >= _nextScan)
            {
                _nextScan = t + 0.1f;
                try { ScanGameHud(gm?.MainUI); }
                catch (Exception e) { DashBarMod.Log.Warn("Game HUD scan: " + e.Message); _gameHud.Clear(); _nextScan = t + 5f; }
            }
            Obstacles.AddRange(_gameHud);
        }

        private static Rect PieceRect(RectTransform rt)
        {
            if (rt == null) return default;
            var c = rt.GetComponentInParent<Canvas>();
            if (c != null && !c.isRootCanvas) c = c.rootCanvas;
            return ScreenRect(rt, c);
        }

        // The bar's footprint at the game's own place and size: its frame and the countdown's box.
        private static Rect HomeFootprint()
        {
            Rect frame = HomeScreenRect, label = LabelScreenRect();
            if (label.width > 0f && _scale > 0f)
            {
                // Back to the home place and size: the label scales and moves with the gauge.
                var c = new Vector2(Screen.width / 2f, Screen.height / 2f);
                Vector2 lmin = (label.min - _offset - c) / _scale + c, lmax = (label.max - _offset - c) / _scale + c;
                label = Rect.MinMaxRect(lmin.x, lmin.y, lmax.x, lmax.y);
            }
            return label.width > 0f ? Rect.MinMaxRect(frame.xMin, Mathf.Min(frame.yMin, label.yMin), Mathf.Max(frame.xMax, label.xMax), Mathf.Max(frame.yMax, label.yMax)) : frame;
        }

        // The footprint for a placement: the root is scaled about the screen centre, then moved by `offset`.
        private static Rect Placed(Rect home, Vector2 offset, float scale)
        {
            var c = new Vector2(Screen.width / 2f, Screen.height / 2f);
            Vector2 min = c + (home.min - c) * scale + offset, max = c + (home.max - c) * scale + offset;
            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }

        private static bool Clear(Rect r, float gap, out float overlap)
        {
            overlap = 0f;
            bool on = r.xMin >= gap && r.xMax <= Screen.width - gap && r.yMin >= 0f && r.yMax <= Screen.height * MaxTop;
            foreach (var (_, o) in Obstacles)
            {
                float w = Mathf.Min(r.xMax, o.xMax + gap) - Mathf.Max(r.xMin, o.xMin - gap), h = Mathf.Min(r.yMax, o.yMax + gap) - Mathf.Max(r.yMin, o.yMin - gap);
                if (w > 0f && h > 0f) overlap += w * h;
            }
            return on && overlap <= 0f;
        }

        // Three places, in order: the game's own; straight up above what's under it (a player bar, the goal counter); beside
        // the freebie slot, on its right at its height, drawn smaller if that's what it takes to fit before the next thing.
        // When none is free: whichever of them covers the least.
        private static (string dock, Vector2 offset, float scale) ChooseDock(Rect home, float gap)
        {
            var cands = new List<(string, Vector2, float)> { ("home", Vector2.zero, 1f) };
            // Straight up: above everything that overlaps the bar's column at or under it.
            float dy = 0f;
            foreach (var (_, o) in Obstacles)
                if (o.xMax > home.xMin - gap && o.xMin < home.xMax + gap && o.yMin < home.yMax + gap)
                    dy = Mathf.Max(dy, o.yMax + gap - home.yMin);
            if (dy > 0f) cands.Add(("up", new Vector2(0f, dy), 1f));
            // Beside the freebie slot.
            var slot = Obstacles.FirstOrDefault(o => o.name.Contains("ItemCanvas")).rect;
            if (slot.width > 0f)
            {
                float x0 = slot.xMax + gap, limit = Screen.width - gap, h = home.height;
                foreach (var (_, o) in Obstacles)
                    if (o.xMin > slot.xMax && o.yMin < slot.center.y + h / 2f && o.yMax > slot.center.y - h / 2f) limit = Mathf.Min(limit, o.xMin - gap);
                float sc = Mathf.Clamp((limit - x0) / home.width, MinScale, 1f);
                var c = new Vector2(Screen.width / 2f, Screen.height / 2f);
                var at = c + (home.min - c) * sc;   // the scaled footprint's corner before moving
                cands.Add(("beside the freebie slot", new Vector2(x0 - at.x, slot.center.y - home.height * sc / 2f - at.y), sc));
            }
            (string, Vector2, float) best = cands[0];
            float bestOverlap = float.MaxValue;
            foreach (var cand in cands)
            {
                if (Clear(Placed(home, cand.Item2, cand.Item3), gap, out float overlap)) return cand;
                if (overlap < bestOverlap) { bestOverlap = overlap; best = cand; }
            }
            return best;
        }

        private static void PlaceAroundOthers(MainGameManager gm)
        {
            if (!OnTop) { if (_offset != Vector2.zero || _scale != 1f) Apply(Vector2.zero, 1f); _target = Vector2.zero; _targetScale = 1f; _dock = "home"; return; }
            CollectObstacles(gm);
            float gap = LiftGap * Screen.height / 1080f, t = Time.unscaledTime;
            var home = HomeFootprint();
            var (dock, off, sc) = ChooseDock(home, gap);
            bool currentClear = Clear(Placed(home, _target, _targetScale), gap, out _);
            bool better = dock != _dock || (off - _target).sqrMagnitude > 4f || Mathf.Abs(sc - _targetScale) > 0.02f;
            bool move = false;
            if (better && !currentClear) move = true;   // its place got taken: move at once
            else if (better)
            {
                // A better place (an earlier one in the order): only once it has stayed free a moment, so the bar doesn't
                // jump around (and a claim that lapses for a frame, e.g. one long frame, doesn't make it dip).
                if (dock != _pendingDock || (off - _pending).sqrMagnitude > 4f || _pendingFrames == 0) { _pendingDock = dock; _pending = off; _pendingScale = sc; _pendingSince = t; _pendingFrames = 0; }
                if (++_pendingFrames >= HoldFrames && t - _pendingSince >= HoldTime) move = true;
            }
            else _pendingFrames = 0;
            if (move)
            {
                LiftChanges++;
                if (dock == "home") _wentHomeAt = t;
                else if (_dock == "home" && t - _wentHomeAt < 1f) Dips++;
                string around = string.Join(", ", Obstacles.Where(o => o.rect.Overlaps(home)).Select(o => o.name).Distinct());
                DashBarMod.Log.Info(dock == "home"
                    ? "Nothing in the way any more: back to where the game put it."
                    : $"Keeping clear of {(around != "" ? around : "other HUD")}: {dock} ({off.x:0} px across, {off.y:0} px up, {sc * 100f:0}% size).");
                _dock = dock; _target = off; _targetScale = sc; _pendingFrames = 0;
            }
            // Smoothed over at most 0.05 s per frame, so one long frame doesn't make it jump.
            float k = 1f - Mathf.Exp(-12f * Mathf.Min(Time.unscaledDeltaTime, 0.05f));
            bool done = (_target - _offset).sqrMagnitude < 0.25f && Mathf.Abs(_targetScale - _scale) < 0.002f;
            var o2 = done ? _target : Vector2.Lerp(_offset, _target, k);
            float s2 = done ? _targetScale : Mathf.Lerp(_scale, _targetScale, k);
            if ((o2 - _offset).sqrMagnitude > 0f || s2 != _scale) Apply(o2, s2);
        }

        private static void SetOffset(Vector2 px) => Apply(px, 1f);

        // Moves (screen px) and scales (about the screen centre) the gauge's root on the mod's canvas.
        private static void Apply(Vector2 px, float scale)
        {
            _offset = px;
            _scale = scale;
            try
            {
                var rt = _gauge != null ? _gauge.transform.TryCast<RectTransform>() : null;
                var c = _onTop != null ? _onTop.GetComponent<Canvas>() : null;
                if (rt == null || c == null) return;
                float k = c.scaleFactor > 0f ? c.scaleFactor : 1f;
                rt.anchoredPosition = _homePos + px / k;
                rt.localScale = new Vector3(scale, scale, 1f);
            }
            catch { }
        }

        private static Image FindImage(Transform root, string path)
        {
            var t = root.Find(path);
            return t != null ? t.GetComponent<Image>() : null;
        }

        // A TextMeshPro label to the right of the bar, in the stage clock's font and material, over a dark copy of itself.
        private static TextMeshProUGUI MakeLabel(MainGameUIManager ui, UIMultiDashGauge g)
        {
            var frame = g.transform.Find("Base")?.TryCast<RectTransform>();
            if (frame == null) return null;
            TextMeshProUGUI model = null;
            var clock = ui.transform.Find("LAYER00/TimerSelector/TimerClock1/Text (TMP)");
            if (clock != null) model = clock.GetComponent<TextMeshProUGUI>();
            if (model == null)
            {
                var size = ui.transform.Find("LAYER00/KatamariSize");
                if (size != null) model = size.GetComponentInChildren<TextMeshProUGUI>(true);
            }

            // A holder to the right of the frame, with a dark copy of the text behind it as a drop shadow (an outline
            // would need TMP's material instance, whose setters threw a NullReferenceException on a new label here).
            var holder = new GameObject("OUAKDashBar_Timer");
            holder.layer = g.gameObject.layer;
            holder.transform.SetParent(g.transform, false);
            var hrt = holder.AddComponent<RectTransform>();
            hrt.pivot = new Vector2(0f, 0.5f);
            hrt.anchorMin = hrt.anchorMax = new Vector2(0.5f, 0.5f);
            hrt.sizeDelta = new Vector2(130f, 90f);   // room for "8.8" (the label never wraps)
            hrt.localScale = Vector3.one;
            // Its place next to the frame is set every frame it shows (PlaceLabel).

            _shadow = MakeText(holder, model, "Shadow", new Color(0.12f, 0.08f, 0.05f, 0.85f), new Vector2(3f, -3f));
            var t = MakeText(holder, model, "Text", Color.white, Vector2.zero);
            holder.SetActive(false);
            return t;
        }

        private static TextMeshProUGUI _shadow;

        private static TextMeshProUGUI MakeText(GameObject parent, TextMeshProUGUI model, string name, Color color, Vector2 offset)
        {
            var go = new GameObject(name);
            go.layer = parent.layer;
            go.transform.SetParent(parent.transform, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            if (model != null)
            {
                t.font = model.font;
                t.fontSharedMaterial = model.fontSharedMaterial;
            }
            t.fontSize = 54f;
            t.enableWordWrapping = false;
            t.alignment = TextAlignmentOptions.MidlineLeft;
            t.raycastTarget = false;
            t.color = color;
            t.text = "";
            Try(() => t.characterSpacing = -14f, "spacing");   // the clock font's digits sit far apart on their own
            var rt = t.rectTransform;
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0f, 0.5f);
            rt.sizeDelta = Vector2.zero;
            rt.anchoredPosition = offset;
            rt.localScale = Vector3.one;
            return t;
        }

        // Cosmetic settings: one that fails is logged and skipped, never the whole label.
        private static void Try(Action a, string what)
        {
            try { a(); } catch (Exception e) { DashBarMod.Log.Warn($"Label {what}: {e.Message}"); }
        }

        private static readonly Color Exhausted = new(1f, 0.42f, 0.32f, 1f);

        private static void SetLabel(string text, DashPhase phase)
        {
            if (_label == null) return;
            bool on = !string.IsNullOrEmpty(text);
            var holder = _label.transform.parent.gameObject;
            if (holder.activeSelf != on) holder.SetActive(on);
            if (!on) { _lastText = null; return; }
            if (text != _lastText)
            {
                _label.text = text;
                if (_shadow != null) _shadow.text = text;
                _lastText = text;
            }
            var c = phase == DashPhase.Exhausted ? Exhausted : Color.white;
            if (_label.color != c) _label.color = c;
        }
    }
}
