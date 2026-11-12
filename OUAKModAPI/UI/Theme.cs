using UnityEngine;

namespace OUAKModAPI
{
    /// <summary>
    /// The mod window's look: shared GUIStyles and colours, so every mod's page looks like one menu. Styles are
    /// rebuilt by themselves when Unity drops their textures (scene loads can); read them inside OnGUI.
    /// <para>Only rect-based GUI.* calls work in this game (GUILayout, GUI.Window and GUI.TextField are stripped):
    /// use these styles with GUI.Button / GUI.Label / GUI.Box, or the helpers in <see cref="Widgets"/>.</para>
    /// </summary>
    public static class Theme
    {
        public static readonly Color Accent = new(0.93f, 0.33f, 0.62f, 1f);

        private static bool _ready;
        private static Texture2D _texWindow, _texTitle, _texBtn, _texBtnHover, _texBtnActive, _texOn, _texOnHover, _texTab, _texList,
            _texThumb, _texDanger, _texField, _texFieldFocus, _texModal;
        private static GUIStyle _window, _title, _hint, _hintRight, _toast, _errCenter, _list, _row, _modal, _modalTitle, _btn, _btnCenter,
            _btnOn, _danger, _tab, _tabOn, _field, _fieldFocus, _fieldText, _fieldTextBold, _fieldHint, _label, _dim, _wrap, _header, _ok, _err, _none;

        public static GUIStyle Window { get { Ensure(); return _window; } }
        public static GUIStyle Title { get { Ensure(); return _title; } }
        /// <summary>Small right-aligned text in the title bar.</summary>
        public static GUIStyle Hint { get { Ensure(); return _hint; } }
        public static GUIStyle HintRight { get { Ensure(); return _hintRight; } }
        public static GUIStyle ToastText { get { Ensure(); return _toast; } }
        public static GUIStyle ErrCenter { get { Ensure(); return _errCenter; } }
        /// <summary>Dark panel behind lists.</summary>
        public static GUIStyle List { get { Ensure(); return _list; } }
        public static GUIStyle Row { get { Ensure(); return _row; } }
        public static GUIStyle Modal { get { Ensure(); return _modal; } }
        public static GUIStyle ModalTitle { get { Ensure(); return _modalTitle; } }
        /// <summary>Left-aligned button.</summary>
        public static GUIStyle Button { get { Ensure(); return _btn; } }
        public static GUIStyle ButtonCenter { get { Ensure(); return _btnCenter; } }
        /// <summary>A button that is "on" (green).</summary>
        public static GUIStyle ButtonOn { get { Ensure(); return _btnOn; } }
        /// <summary>Red button for anything that changes saves.</summary>
        public static GUIStyle Danger { get { Ensure(); return _danger; } }
        public static GUIStyle Tab { get { Ensure(); return _tab; } }
        public static GUIStyle TabOn { get { Ensure(); return _tabOn; } }
        public static GUIStyle Field { get { Ensure(); return _field; } }
        public static GUIStyle FieldFocus { get { Ensure(); return _fieldFocus; } }
        public static GUIStyle FieldText { get { Ensure(); return _fieldText; } }
        public static GUIStyle FieldHint { get { Ensure(); return _fieldHint; } }
        /// <summary>Bold white text (a selected choice).</summary>
        public static GUIStyle FieldTextBold { get { Ensure(); return _fieldTextBold; } }
        public static GUIStyle Label { get { Ensure(); return _label; } }
        /// <summary>Grey, word-wrapped text for explanations.</summary>
        public static GUIStyle Dim { get { Ensure(); return _dim; } }
        public static GUIStyle Wrap { get { Ensure(); return _wrap; } }
        public static GUIStyle Header { get { Ensure(); return _header; } }
        /// <summary>Green status text.</summary>
        public static GUIStyle Ok { get { Ensure(); return _ok; } }
        /// <summary>Red status text.</summary>
        public static GUIStyle Err { get { Ensure(); return _err; } }
        /// <summary>No background, no text settings (for groups).</summary>
        public static GUIStyle None { get { Ensure(); return _none; } }
        /// <summary>The scroll thumb's texture (accent colour).</summary>
        public static Texture2D Thumb { get { Ensure(); return _texThumb; } }

        private static void Ensure()
        {
            // Unity may destroy our textures on scene unloads; rebuild if any went missing.
            if (_ready && _texWindow != null && _texBtn != null && _texField != null && _texThumb != null) return;

            _texWindow = Solid(new Color(0.07f, 0.07f, 0.10f, 0.96f));
            _texTitle = Solid(new Color(0.55f, 0.16f, 0.42f, 1f));
            _texBtn = Solid(new Color(0.18f, 0.18f, 0.24f, 1f));
            _texBtnHover = Solid(new Color(0.27f, 0.27f, 0.36f, 1f));
            _texBtnActive = Solid(new Color(0.45f, 0.20f, 0.40f, 1f));
            _texOn = Solid(new Color(0.16f, 0.45f, 0.30f, 1f));
            _texOnHover = Solid(new Color(0.20f, 0.55f, 0.36f, 1f));
            _texTab = Solid(new Color(0.13f, 0.13f, 0.18f, 1f));
            _texList = Solid(new Color(0.04f, 0.04f, 0.06f, 0.9f));
            _texThumb = Solid(Accent);
            _texDanger = Solid(new Color(0.55f, 0.16f, 0.16f, 1f));
            _texField = Solid(new Color(0.02f, 0.02f, 0.03f, 1f));
            _texFieldFocus = Solid(new Color(0.10f, 0.06f, 0.10f, 1f));
            _texModal = Solid(new Color(0.11f, 0.09f, 0.14f, 1f));

            _none = new GUIStyle();
            _window = Bg(_texWindow);
            _title = Text(15, Color.white, TextAnchor.MiddleLeft, FontStyle.Bold);
            _title.normal.background = _texTitle;
            _title.contentOffset = new Vector2(12f, 0f);
            _hint = Text(12, new Color(1f, 0.85f, 0.93f), TextAnchor.MiddleRight);
            _list = Bg(_texList);
            _row = Bg(_texTab);
            _modal = Bg(_texModal);
            _modalTitle = Text(15, Accent, TextAnchor.MiddleLeft, FontStyle.Bold);

            _btn = ButtonStyle(_texBtn, _texBtnHover, _texBtnActive, TextAnchor.MiddleLeft);
            _btnCenter = ButtonStyle(_texBtn, _texBtnHover, _texBtnActive, TextAnchor.MiddleCenter);
            _btnOn = ButtonStyle(_texOn, _texOnHover, _texBtnActive, TextAnchor.MiddleLeft);
            _danger = ButtonStyle(_texDanger, _texBtnActive, _texBtnActive, TextAnchor.MiddleCenter);
            _tab = ButtonStyle(_texTab, _texBtnHover, _texBtnActive, TextAnchor.MiddleCenter);
            _tabOn = ButtonStyle(_texTitle, _texTitle, _texBtnActive, TextAnchor.MiddleCenter);
            _tabOn.fontStyle = FontStyle.Bold;

            _field = Bg(_texField);
            _fieldFocus = Bg(_texFieldFocus);
            _fieldText = Text(13, Color.white, TextAnchor.MiddleLeft);
            _fieldTextBold = Text(13, Color.white, TextAnchor.MiddleLeft, FontStyle.Bold);
            _fieldHint = Text(13, new Color(0.45f, 0.45f, 0.52f), TextAnchor.MiddleLeft);

            _label = Text(13, new Color(0.92f, 0.92f, 0.95f), TextAnchor.UpperLeft);
            _dim = Text(12, new Color(0.65f, 0.65f, 0.72f), TextAnchor.UpperLeft);
            _dim.wordWrap = true;
            _wrap = Text(12, new Color(0.88f, 0.88f, 0.92f), TextAnchor.UpperLeft);
            _wrap.wordWrap = true;
            _header = Text(13, Accent, TextAnchor.MiddleLeft, FontStyle.Bold);
            _ok = Text(12, new Color(0.55f, 0.9f, 0.65f), TextAnchor.UpperLeft);
            _ok.wordWrap = true;
            _err = Text(12, new Color(1f, 0.5f, 0.45f), TextAnchor.UpperLeft);
            _err.wordWrap = true;
            _hintRight = Text(11, new Color(0.55f, 0.55f, 0.62f), TextAnchor.MiddleRight);
            _toast = Text(14, Color.white, TextAnchor.MiddleCenter);
            _errCenter = Text(14, new Color(1f, 0.55f, 0.5f), TextAnchor.MiddleCenter);

            _ready = true;
        }

        /// <summary>A button style on solid-colour textures (hover and pressed backgrounds).</summary>
        public static GUIStyle ButtonStyle(Texture2D normal, Texture2D hover, Texture2D active, TextAnchor align)
        {
            var s = Text(13, Color.white, align);
            s.normal.background = normal;
            s.hover.background = hover;
            s.hover.textColor = Color.white;
            s.active.background = active;
            s.active.textColor = Color.white;
            s.contentOffset = new Vector2(align == TextAnchor.MiddleCenter ? 0f : 10f, 0f);
            return s;
        }

        /// <summary>A text style (clipped, no background).</summary>
        public static GUIStyle Text(int size, Color color, TextAnchor align, FontStyle font = FontStyle.Normal)
        {
            var s = new GUIStyle();
            s.fontSize = size;
            s.fontStyle = font;
            s.alignment = align;
            s.normal.textColor = color;
            s.clipping = TextClipping.Clip;
            return s;
        }

        /// <summary>A style that only draws <paramref name="tex"/> as its background.</summary>
        public static GUIStyle Bg(Texture2D tex)
        {
            var s = new GUIStyle();
            s.normal.background = tex;
            return s;
        }

        /// <summary>A 1x1 texture of one colour that survives scene loads.</summary>
        public static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1);
            t.hideFlags = HideFlags.HideAndDontSave;
            t.SetPixels(new[] { c });
            t.Apply();
            return t;
        }
    }
}
