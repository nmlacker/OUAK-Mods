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
    /// Text chat for the session. T opens a box at the bottom left (Enter sends, Esc closes); while it's open the
    /// game's controllers are locked the way the mod's own text boxes lock them (InputPadBase._steamLock), so
    /// typing doesn't roll the katamari. Messages go to the host, which stamps the sender, limits how fast anyone
    /// can post, and passes them on (Msg.Chat); the host also posts who joins and leaves. The last few lines show
    /// over the game and fade; with the box open, more of them stay. What someone says also shows for a few
    /// seconds in a speech bubble over their cousin (or their katamari in a stage), above the name tag.
    /// </summary>
    internal static class Chat
    {
        public const int MaxLength = 120;
        private const int Kept = 60, ShownOpen = 10, ShownClosed = 6;
        private const float Fade = 12f, RateWindow = 10f;
        private const int RateLimit = 6;
        public const byte SystemId = 254;

        private sealed class Line
        {
            public float At;
            public byte From;
            public string Name, Text;
        }

        private static readonly List<Line> Lines = new();
        private static readonly TextField Field = new(MaxLength, "Say something...  Enter sends, Esc closes");
        private static readonly Dictionary<byte, Queue<float>> Recent = new();
        private static bool _open, _locked;
        private static int _openedFrame;
        private static GUIStyle _line, _shadow, _box, _boxFocused, _text, _hint;
        private static Texture2D _back;

        public static bool IsOpen => _open;
        public static int Received { get; private set; }
        public static string LastText => Lines.Count > 0 ? $"{Lines[^1].Name}: {Lines[^1].Text}" : "";
        public static byte LastFrom => Lines.Count > 0 ? Lines[^1].From : (byte)0;

        // ---------------------------------------------------------------- sending and receiving

        /// <summary>Anyone: post a message (shown here at once; the host passes it on).</summary>
        public static string Send(string text)
        {
            if (!NetSession.Connected) return "Not in a session.";
            text = Clean(text);
            if (text.Length == 0) return "Nothing to send.";
            Add(NetSession.MyId, NetSession.MyName, text);
            if (NetSession.Mode == NetSession.Role.Host)
                NetSession.Broadcast(Proto.Begin(Msg.Chat, NetSession.MyId).U8(NetSession.MyId).Str(text));
            else
                NetSession.SendToHost(Proto.Begin(Msg.Chat, NetSession.MyId).Str(text));
            return "Sent.";
        }

        /// <summary>Host: a message from a player. Passed on to everyone else unless they're posting too fast.</summary>
        public static void HostOnChat(Peer from, string text)
        {
            text = Clean(text);
            if (text.Length == 0) return;
            float now = Time.unscaledTime;
            if (!Recent.TryGetValue(from.Id, out var q)) Recent[from.Id] = q = new Queue<float>();
            while (q.Count > 0 && now - q.Peek() > RateWindow) q.Dequeue();
            if (q.Count >= RateLimit) return;
            q.Enqueue(now);
            Add(from.Id, from.Name, text);
            var pkt = Proto.Begin(Msg.Chat, NetSession.MyId).U8(from.Id).Str(text);
            foreach (var p in NetSession.Peers.Values)
                if (p.Id != from.Id) NetSession.SendToPeer(p, pkt);
        }

        /// <summary>Client: a message the host passed on (or posted: SystemId).</summary>
        public static void ClientOnChat(byte from, string text)
        {
            text = Clean(text);
            if (text.Length == 0 || from == NetSession.MyId) return;
            Add(from, from == SystemId ? "" : Votes.NameOf(from), text);
        }

        /// <summary>Host: a notice for everyone (someone joined, left...).</summary>
        public static void Notice(string text)
        {
            if (NetSession.Mode != NetSession.Role.Host) return;
            text = Clean(text);
            if (text.Length == 0) return;
            Add(SystemId, "", text);
            NetSession.Broadcast(Proto.Begin(Msg.Chat, NetSession.MyId).U8(SystemId).Str(text));
        }

        private static void Add(byte from, string name, string text)
        {
            Lines.Add(new Line { At = Time.unscaledTime, From = from, Name = name ?? "", Text = text });
            if (from != SystemId) LastSaid[from] = new Said { At = Time.unscaledTime, Text = text };
            if (Lines.Count > Kept) Lines.RemoveAt(0);
            if (from != NetSession.MyId) Received++;
            MelonLogger.Msg(from == SystemId ? $"[Chat] {text}" : $"[Chat] {name}: {text}");
        }

        /// <summary>One line of plain text: no control characters, no rich-text tags, at most MaxLength.</summary>
        private static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var chars = text.Where(c => !char.IsControl(c)).Select(c => c == '<' ? '‹' : c == '>' ? '›' : c).ToArray();
            string s = new string(chars).Trim();
            return s.Length > MaxLength ? s.Substring(0, MaxLength) : s;
        }

        public static void Clear()
        {
            Lines.Clear();
            Recent.Clear();
            LastSaid.Clear();
            Queued.Clear();
            if (_open) CloseBox();
        }

        // ---------------------------------------------------------------- the box

        public static void OpenBox()
        {
            _open = true;
            _openedFrame = Time.frameCount;
            Field.Text = "";
            TextInput.Focused = Field;
        }

        public static void CloseBox()
        {
            _open = false;
            if (TextInput.Focused == Field) TextInput.Focused = null;
            Lock(false);
        }

        /// <summary>Update: T opens the box (not while typing elsewhere); Esc or a click elsewhere closes it.</summary>
        public static void Tick()
        {
            if (!NetSession.Connected)
            {
                if (_open) CloseBox();
                _wasConnected = false;
                return;
            }
            // Just joined or started hosting: say how to chat for a while.
            if (!_wasConnected)
            {
                _wasConnected = true;
                _hintUntil = Time.unscaledTime + 20f;
            }
            if (!_open)
            {
                bool t = false;
                try { t = Input.GetKeyDown(KeyCode.T); } catch { }
                if (t && !TextInput.IsTyping) OpenBox();
            }
            else if (TextInput.Focused != Field)
            {
                CloseBox();
            }
            Lock(_open);
        }

        /// <summary>The game's controllers ignore everything while the box is open (the API's GameInput lock).</summary>
        private static void Lock(bool on)
        {
            if (on) GameInput.BlockThisFrame("chat");
            else if (_locked) GameInput.HoldBriefly();
            _locked = on;
        }

        /// <summary>True while the box holds the controllers (self-test).</summary>
        public static bool PadsLocked
        {
            get
            {
                try
                {
                    var pads = SingletonMonoBehaviour<InputController>.instance?.gamePad;
                    if (pads == null) return false;
                    for (int i = 0; i < pads.Length; i++)
                        if (pads[i] != null && !pads[i]._steamLock) return false;
                    return pads.Length > 0;
                }
                catch { return false; }
            }
        }

        // ---------------------------------------------------------------- drawing

        /// <summary>From the mod's OnGUI, for every event (the box needs key events; the lines draw on Repaint).</summary>
        public static void OnGUI()
        {
            if (!NetSession.Connected) return;
            var e = Event.current;
            if (e == null) return;
            EnsureStyles();
            float scale = Votes.GuiScale();
            var old = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
            try
            {
                float sh = Screen.height / scale;
                const float x = 16f, w = 560f, rowH = 22f;
                float boxY = sh - 120f;
                if (_open)
                {
                    // The key that opened the box isn't typed into it.
                    if (Time.frameCount == _openedFrame && e.type == EventType.KeyDown) e.Use();
                    else if (TextInput.Draw(new Rect(x, boxY, w, 28f), Field, _box, _boxFocused, _text, _hint))
                    {
                        string said = Field.Text;
                        CloseBox();
                        Send(said);
                    }
                }
                if (e.type != EventType.Repaint) return;
                float now = Time.unscaledTime;
                int max = _open ? ShownOpen : ShownClosed;
                var shown = Lines.Where(l => _open || now - l.At < Fade).Skip(Math.Max(0, Lines.Count(l => _open || now - l.At < Fade) - max)).ToList();
                float y = boxY - 6f - shown.Count * rowH;
                if (_open && shown.Count > 0) GUI.DrawTexture(new Rect(x - 6f, y - 4f, w + 12f, shown.Count * rowH + 8f), _back);
                foreach (var l in shown)
                {
                    float alpha = _open ? 1f : Mathf.Clamp01((Fade - (now - l.At)) / 2f);
                    string text = l.From == SystemId
                        ? $"<color=#ffd27a>{l.Text}</color>"
                        : $"<color=#{Hex(PlayerColor(l.From))}><b>{EscapeName(l.Name)}</b></color>: {l.Text}";
                    // The shadow keeps the bold name (only the colours go), so it lines up with the text over it.
                    string shadow = l.From == SystemId ? l.Text : $"<b>{EscapeName(l.Name)}</b>: {l.Text}";
                    var prev = GUI.color;
                    GUI.color = new Color(1f, 1f, 1f, alpha);
                    GUI.Label(new Rect(x + 1f, y + 1f, w, rowH), shadow, _shadow);
                    GUI.Label(new Rect(x, y, w, rowH), text, _line);
                    GUI.color = prev;
                    y += rowH;
                }
                if (!_open && Lines.Count == 0 && NetSession.Peers.Count > 0 && now < _hintUntil)
                    GUI.Label(new Rect(x, boxY + 4f, w, rowH),
                        (Voice.Talk && !Voice.OpenMic ? $"Press T to chat, hold {Voice.KeyNames[Voice.Key]} to talk" : "Press T to chat") +
                        (QuickChat.Enabled && Pad.Connected ? ", D-pad down for quick chat" : ""), _hint);
            }
            finally { GUI.matrix = old; }
        }

        private static float _hintUntil;
        private static bool _wasConnected;

        // ---------------------------------------------------------------- speech bubbles

        private sealed class Said
        {
            public float At;
            public string Text;
            public GUIContent Content;
        }

        private struct Bubble
        {
            public byte From;
            public float X, Bottom, Depth;
        }

        private static readonly Dictionary<byte, Said> LastSaid = new();
        private static readonly List<Bubble> Queued = new();
        private static GUIStyle _bubbleText;
        private static int _bubbleFont;
        private static Texture2D _tail;

        /// <summary>Self-test: how many bubbles have been drawn, and the last one's text.</summary>
        public static int BubblesDrawn { get; private set; }
        public static string LastBubble { get; private set; } = "";

        /// <summary>How long a message stays over its sender: longer ones stay longer.</summary>
        private static float BubbleLife(string text) => Mathf.Clamp(4f + text.Length * 0.06f, 4f, 10f);

        /// <summary>What a player said in the last few seconds, and how visible its bubble is (it fades in and out).</summary>
        public static bool TryBubble(byte id, out string text, out float alpha)
        {
            text = null;
            alpha = 0f;
            if (!GhostSettings.Bubbles || !LastSaid.TryGetValue(id, out var s)) return false;
            float age = Time.unscaledTime - s.At, life = BubbleLife(s.Text);
            if (age < 0f || age >= life) return false;
            text = s.Text;
            alpha = Mathf.Clamp01(age / 0.15f) * Mathf.Clamp01((life - age) / 0.6f);
            return alpha > 0.01f;
        }

        /// <summary>
        /// Name-tag drawing (Repaint, screen pixels): a bubble for this player with its tail's tip at (x, bottom),
        /// if they said something lately. <see cref="DrawBubbles"/> draws them after every tag, farthest first.
        /// </summary>
        public static void QueueBubble(byte id, float x, float bottom, float depth)
        {
            if (!GhostSettings.Bubbles || !LastSaid.ContainsKey(id)) return;
            Queued.Add(new Bubble { From = id, X = x, Bottom = bottom, Depth = depth });
        }

        public static void DrawBubbles()
        {
            if (Queued.Count == 0) return;
            try
            {
                Queued.Sort((a, b) => b.Depth.CompareTo(a.Depth));
                foreach (var b in Queued)
                {
                    if (!LastSaid.TryGetValue(b.From, out var said) || !TryBubble(b.From, out _, out float alpha)) continue;
                    DrawBubble(b.X, b.Bottom, b.From, said, alpha);
                    BubblesDrawn++;
                    LastBubble = said.Text;
                }
            }
            finally { Queued.Clear(); }
        }

        /// <summary>A white rounded bubble edged in the player's colour, its tail pointing down at (x, bottom).</summary>
        private static void DrawBubble(float x, float bottom, byte from, Said said, float alpha)
        {
            float k = Votes.GuiScale();
            int font = Mathf.RoundToInt(15f * k);
            if (_bubbleText == null || _bubbleFont != font)
            {
                _bubbleFont = font;
                _bubbleText = new GUIStyle { fontSize = font, wordWrap = true, richText = false, alignment = TextAnchor.MiddleCenter };
                _bubbleText.normal.textColor = new Color(0.1f, 0.08f, 0.16f, 1f);
            }
            said.Content ??= new GUIContent(said.Text);
            float padX = 10f * k, padY = 6f * k, tailW = 16f * k, tailH = 9f * k, radius = 10f * k, edgeW = 2f * k;
            _bubbleText.CalcMinMaxWidth(said.Content, out _, out float oneLine);
            float tw = Mathf.Min(Mathf.Ceil(oneLine) + 2f, 280f * k);
            float th = Mathf.Ceil(_bubbleText.CalcHeight(said.Content, tw));
            float w = tw + padX * 2f, h = th + padY * 2f;
            var r = new Rect(Mathf.Clamp(x - w * 0.5f, 4f, Mathf.Max(4f, Screen.width - w - 4f)), bottom - tailH - h, w, h);
            bool tail = true;
            if (r.y < 4f) { r.y = 4f; tail = false; }

            var c = Ghosts.Colors[(from >= NetSession.MaxPlayers ? 0 : from) % Ghosts.Colors.Length];
            var edge = new Color(c.r * 0.85f, c.g * 0.85f, c.b * 0.85f, alpha);
            var fill = new Color(1f, 1f, 1f, 0.95f * alpha);
            var white = Texture2D.whiteTexture;
            GUI.DrawTexture(new Rect(r.x + 2f * k, r.y + 3f * k, r.width, r.height), white, ScaleMode.StretchToFill, true, 0f,
                new Color(0f, 0f, 0f, 0.25f * alpha), 0f, radius);
            GUI.DrawTexture(r, white, ScaleMode.StretchToFill, true, 0f, fill, 0f, radius);
            GUI.DrawTexture(r, white, ScaleMode.StretchToFill, true, 0f, edge, edgeW, radius);
            var prev = GUI.color;
            try
            {
                if (tail)
                {
                    // The edge colour first and a little larger, then the fill over it, covering the bubble's bottom edge there.
                    float tx = Mathf.Clamp(x, r.x + radius + tailW * 0.5f, r.xMax - radius - tailW * 0.5f);
                    float top = r.yMax - edgeW;
                    GUI.color = edge;
                    GUI.DrawTexture(new Rect(tx - tailW * 0.5f - edgeW, top, tailW + edgeW * 2f, tailH + edgeW * 1.6f), Tail());
                    GUI.color = fill;
                    GUI.DrawTexture(new Rect(tx - tailW * 0.5f, top, tailW, tailH), Tail());
                }
                GUI.color = new Color(1f, 1f, 1f, alpha);
                GUI.Label(new Rect(r.x + padX, r.y + padY, tw, th), said.Content, _bubbleText);
            }
            finally { GUI.color = prev; }
        }

        /// <summary>A white triangle pointing down, soft-edged (texture rows run bottom to top).</summary>
        private static Texture2D Tail()
        {
            if (_tail != null) return _tail;
            const int W = 32, H = 18;
            var px = new Color[W * H];
            for (int y = 0; y < H; y++)
            {
                float half = (y + 0.5f) / H * (W * 0.5f);
                for (int x = 0; x < W; x++)
                    px[y * W + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(half - Mathf.Abs(x + 0.5f - W * 0.5f) + 0.5f));
            }
            _tail = new Texture2D(W, H, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            _tail.SetPixels(px);
            _tail.Apply();
            return _tail;
        }

        private static Color PlayerColor(byte id)
        {
            var c = Ghosts.Colors[(id >= NetSession.MaxPlayers ? 0 : id) % Ghosts.Colors.Length];
            return Color.Lerp(new Color(c.r, c.g, c.b, 1f), Color.white, 0.35f);
        }

        private static string Hex(Color c) =>
            $"{(int)(Mathf.Clamp01(c.r) * 255):x2}{(int)(Mathf.Clamp01(c.g) * 255):x2}{(int)(Mathf.Clamp01(c.b) * 255):x2}";

        private static string EscapeName(string name) => (name ?? "").Replace("<", "‹").Replace(">", "›");

        private static void EnsureStyles()
        {
            if (_line != null && _back != null && _box?.normal.background != null) return;
            _line = new GUIStyle { fontSize = 15, richText = true, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
            _line.normal.textColor = Color.white;
            _shadow = new GUIStyle { fontSize = 15, richText = true, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
            _shadow.normal.textColor = new Color(0f, 0f, 0f, 0.85f);
            _text = new GUIStyle { fontSize = 15, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
            _text.normal.textColor = Color.white;
            _hint = new GUIStyle { fontSize = 14, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
            _hint.normal.textColor = new Color(0.8f, 0.85f, 1f, 0.8f);
            _box = new GUIStyle();
            _box.normal.background = PlayerBar.Tex(new Color(0.05f, 0.04f, 0.1f, 0.8f));
            _boxFocused = new GUIStyle();
            _boxFocused.normal.background = PlayerBar.Tex(new Color(0.12f, 0.1f, 0.25f, 0.92f));
            _back = PlayerBar.Tex(new Color(0.04f, 0.03f, 0.08f, 0.55f));
        }
    }
}
