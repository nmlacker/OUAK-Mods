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
                else if (!Katamari.Playing || !gm.StartMessageDone) return false;
                if (ui.PauseObjs != null && ui.PauseObjs.isOpend) return false;
                if (ui.DemoObjs != null && ui.DemoObjs.isOpend) return false;
                if (ui.Layer00 != null && !ui.Layer00.gameObject.activeInHierarchy) return false;
                // The HUD canvas itself switched off, or its camera: the game's HUD isn't drawn, so neither is the bar.
                if (_hudCanvas != null && (!_hudCanvas.enabled || (_hudCanvas.renderMode != RenderMode.ScreenSpaceOverlay
                    && _hudCanvas.worldCamera != null && !_hudCanvas.worldCamera.isActiveAndEnabled))) return false;
                // A message window that would end up under the bar (it's on a canvas below ours): out of its way.
                if (_hideInMessages)
                {
                    var gui = SingletonMonoBehaviour<GlobalUIController>.Instance;
                    if (gui != null && gui.IsInMessage) return false;
                }
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
            _gauge.transform.SetParent(on ? _onTop.transform : _home, false);
            if (!on) _gauge.transform.SetSiblingIndex(_homeIndex);
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
            hrt.sizeDelta = new Vector2(220f, 90f);
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
