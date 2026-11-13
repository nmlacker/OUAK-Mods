using System;
using System.Collections.Generic;
using System.Linq;
using MelonLoader;
using UnityEngine;
using OUAKModAPI;

namespace OUAKMultiplayer
{
    /// <summary>
    /// The player list: bottom centre, one compact slot per player (up to 8), in rank order left to right:
    /// players who have finished first (in their finishing order), then everyone else by current size.
    /// Slots glide to their new place when the order changes. The top three finishers wear the game's own
    /// pictures: the Crown and the wash tin (freebie icons game_item14 / game_item06) and the biscuit
    /// (the stage object BISCUIT_A, rendered). Layout: one row in two groups of four, or two rows of four.
    /// </summary>
    internal static class PlayerBar
    {
        public static bool TwoRows;

        private const float SlotW = 224f, SlotH = 80f, Gap = 6f, GroupGap = 30f, Bottom = 14f;
        private static readonly Dictionary<byte, Vector2> Shown = new();
        private static GUIStyle _name, _size, _time, _where, _timers, _badge, _place, _tagClear, _tagFail, _tagCut;
        private static Texture2D _panel, _mine, _iconBg;

        private sealed class Entry
        {
            public byte Id;
            public string Name;
            public Texture Icon;
            public KatamariState State;
            public string Time;
            /// <summary>Where they are, short (Whereabouts): "on the ship", "Edo plaza", "getting ready", "in …", "spectating"…</summary>
            public string Where = "";
            public Activity Activity;
            public bool Me, HasState, SameMap;
            public int Place;
            /// <summary>Place from the run going on (not a final result yet).</summary>
            public bool Live;
            public RunResult Result;
        }

        /// <summary>Draws the bar; returns its top edge (in GUI units) so popups can sit above it.</summary>
        public static float Draw()
        {
            EnsureStyles();
            float scale = Votes.GuiScale();
            float sw = Screen.width / scale, sh = Screen.height / scale;
            var entries = Collect();
            int n = entries.Count;
            if (n == 0) return sh;
            bool two = TwoRows && n > 4;
            float top = sh - Bottom - SlotH * (two ? 2 : 1) - (two ? Gap : 0f);

            float k = 1f - Mathf.Exp(-10f * Time.unscaledDeltaTime);
            Teleport.BeginBar(scale);
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                var e = entries[i];
                var target = SlotPos(i, n, two, sw, sh);
                if (!Shown.TryGetValue(e.Id, out var cur) || (cur - target).sqrMagnitude > 4000000f) cur = target;
                cur = Vector2.Lerp(cur, target, k);
                Shown[e.Id] = cur;
                var rect = new Rect(cur.x, cur.y, SlotW, SlotH);
                Teleport.RecordSlot(e.Id, rect);
                DrawSlot(rect, e);
                minX = Mathf.Min(minX, rect.xMin); minY = Mathf.Min(minY, rect.yMin);
                maxX = Mathf.Max(maxX, rect.xMax); maxY = Mathf.Max(maxY, rect.yMax);
            }
            // The screen space the bar takes (one row or two, as it glides), for other mods' HUD (the API's HudSpace:
            // the Dash Bar moves up out of its way). In screen pixels, IMGUI coordinates; it lapses when not drawn.
            try { HudSpace.Claim("OUAK Multiplayer/PlayerBar", new Rect(minX * scale, minY * scale, (maxX - minX) * scale, (maxY - minY) * scale)); }
            catch { }
            foreach (byte id in Shown.Keys.Where(id => entries.All(e => e.Id != id)).ToList()) Shown.Remove(id);
            return top;
        }

        private static Vector2 SlotPos(int i, int n, bool two, float sw, float sh)
        {
            if (two)
            {
                int row = i / 4, col = i % 4;
                int inRow = row == 0 ? Math.Min(n, 4) : n - 4;
                float width = inRow * SlotW + (inRow - 1) * Gap;
                float y = sh - Bottom - SlotH - (row == 0 ? SlotH + Gap : 0f);
                return new Vector2((sw - width) / 2f + col * (SlotW + Gap), y);
            }
            float total = n * SlotW + (n - 1) * Gap + (n > 4 ? GroupGap : 0f);
            float x = (sw - total) / 2f + i * (SlotW + Gap) + (i >= 4 ? GroupGap : 0f);
            return new Vector2(x, sh - Bottom - SlotH);
        }

        private static List<Entry> Collect()
        {
            var list = new List<Entry>();
            Ghosts.TryLocalState(out var me);
            var mine = Whereabouts.Local();
            list.Add(new Entry
            {
                Id = NetSession.MyId, Name = NetSession.MyName, State = me, Time = me.TimeText(), Me = true,
                Icon = PortraitStudio.For(NetSession.MyLook), Activity = mine,
                HasState = me.Map != 0, SameMap = true, Where = Whereabouts.Short(mine, me.Map, HubPresence.HubKey),
            });
            float now = Time.unscaledTime;
            foreach (var p in NetSession.Peers.Values)
            {
                bool fresh = p.HasState && now - p.StateAt < 3f;
                bool inHub = p.HubKey != 0 && p.HubAt > 0f && now - p.HubAt < 3f;
                var act = fresh ? p.Activity : Activity.Unknown;
                list.Add(new Entry
                {
                    Id = p.Id, Name = p.Name, Icon = PortraitStudio.For(p.Look), State = fresh ? p.State : default,
                    Time = SyncRun.EstimateTime(p), HasState = fresh && p.MapKey != 0, Activity = act,
                    SameMap = fresh && p.MapKey == Ghosts.CurrentMapKey && p.MapKey != 0,
                    Where = fresh || inHub ? Whereabouts.Short(act, fresh ? p.MapKey : 0, inHub ? p.HubKey : 0) : "no news",
                });
            }

            // Places by the session's ranking system (Ranking): finished players by their results; in the run, the
            // live place of each player once they've reached the win condition. Then the rest by size (largest first).
            var standings = SyncRun.Standings;
            var live = Ranking.LivePlaces();
            foreach (var e in list)
            {
                int idx = standings.FindIndex(r => r.Id == e.Id);
                if (idx >= 0) e.Result = standings[idx];
                bool placed = e.Result != null || (e.HasState && (e.SameMap || e.Me) && e.State.ReachedWin);
                if (placed && live.TryGetValue(e.Id, out int place)) { e.Place = place; e.Live = e.Result == null; }
                else if (idx >= 0) e.Place = idx + 1;
            }
            return list.OrderBy(e => e.Place == 0 ? 1 : 0).ThenBy(e => e.Place)
                       .ThenByDescending(e => e.HasState ? e.State.Size : -1).ThenBy(e => e.Id).ToList();
        }

        /// <summary>
        /// One player: their cousin's portrait on a tile in their colour (and a place picture once they've
        /// finished), then three lines: their name in full; their size with the rank badge and status; and their
        /// clock, result time or where they are.
        /// </summary>
        private static void DrawSlot(Rect r, Entry e)
        {
            GUI.DrawTexture(r, e.Me ? _mine : _panel);
            var c = Ghosts.Colors[(e.Id == 255 ? 0 : e.Id) % Ghosts.Colors.Length];
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, 3f), Solid(new Color(c.r, c.g, c.b, 1f)));
            // Under the mouse: an outline, and "click: teleport" when they're on the ship or in a plaza.
            bool hover = !e.Me && Teleport.HoverId == e.Id;
            if (hover) GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, new Color(1f, 1f, 1f, 0.8f), 1.5f, 3f);

            float side = Mathf.Min(r.height - 14f, 52f);
            var icon = new Rect(r.x + 6f, r.y + 8f + (r.height - 14f - side) / 2f, side, side);
            // A light tile in their colour, so a dark cousin still stands out.
            GUI.DrawTexture(icon, Solid(Color.Lerp(new Color(c.r, c.g, c.b, 1f), Color.white, 0.55f), 0.5f));
            if (e.Icon != null) GUI.DrawTexture(icon, e.Icon);
            else GUI.Label(icon, e.Name.Length > 0 ? e.Name.Substring(0, 1).ToUpperInvariant() : "?", _badge);
            // Talking on voice chat: a green ring round the portrait, and the speaker mark on its corner.
            if (Voice.IsTalking(e.Id))
            {
                GUI.DrawTexture(icon, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, new Color(0.4f, 1f, 0.45f, 0.95f), 2.5f, 5f);
                Voice.DrawTalking(new Rect(icon.xMax - 13f, icon.yMax - 13f, 17f, 17f));
            }

            if (e.Place >= 1)
            {
                var mark = new Rect(icon.x - 6f, icon.y - 10f, 26f, 26f);
                if (e.Place > 3 || !RankIcons.Draw(mark, e.Place)) GUI.Label(mark, e.Place.ToString(), _place);
            }

            float tx = icon.xMax + 7f, tw = r.xMax - tx - 6f;
            // 1: the name, the whole width.
            GUI.Label(new Rect(tx, r.y + 4f, tw, 18f), e.Me ? e.Name + " (you)" : e.Name, _name);

            // Status: CLEAR (beat the stage), FAIL (didn't), CUT (ended by Sudden Death); HURRY while Sudden Death is on them.
            GUIStyle tag = null;
            string tagText = null;
            if (e.Result != null) { tagText = e.Result.Status; tag = e.Result.Cleared ? _tagClear : e.Result.Forced ? _tagCut : _tagFail; }
            else if (SyncRun.Hurried.Contains(e.Id) && !SyncRun.StandingsFinal) { tagText = "HURRY"; tag = _tagCut; }

            string size, line3, timers = null;
            if (e.Result != null)
            {
                // Finished: the game's own result values, the run's timers and when they reached the win condition.
                var res = e.Result;
                size = KatamariState.SizeText(res.Size);
                timers = TimersLine(res.Igt, res.RealTime, res.TimeAttack ? res.Time : -1f, res.TimeAttack, tw: TextWidth(r));
                line3 = res.ClearIgt >= 0f || res.SizeTime >= 0f ? WinLine(res.ClearIgt, res.ClearRta, res.SizeTime, TextWidth(r))
                      : res.TimeAttack ? "time " + KatamariState.ClockText(res.RaceTime) + (res.Predicted ? "*" : "") : e.Where;
            }
            else if (e.HasState)
            {
                // In your stage: their size, three timers and win-condition moment (or that they're watching); elsewhere: where.
                size = KatamariState.SizeText(e.State.Size);
                bool here = e.SameMap || e.Me;
                if (here && e.Activity != Activity.Spectating)
                    timers = TimersLine(e.State.Igt, RunRta(), e.State.TimerRule == 1 ? e.State.Elapsed : e.State.TimeLeft,
                                        e.State.TimerRule == 1, e.State.TimerRule < 0, TextWidth(r));
                line3 = e.Activity == Activity.Spectating ? "spectating"
                      : here ? (e.State.ReachedWin ? WinLine(e.State.ClearIgt, e.State.ClearRta, e.State.ClearStage, TextWidth(r)) : "win condition: not yet")
                      : e.Where;
            }
            else
            {
                size = "";
                line3 = string.IsNullOrEmpty(e.Where) ? "not in a stage" : e.Where;
            }
            if (hover && Teleport.CanOffer(e.Id)) line3 = "click: teleport";

            // 2: size, with the rank badge and status on the right.
            float right = r.xMax - 6f;
            if (tag != null)
            {
                GUI.Label(new Rect(right - 44f, r.y + 24f, 44f, 15f), tagText, tag);
                right -= 48f;
            }
            if (e.Result != null && StageRank.Draw(new Rect(right - 18f, r.y + 22f, 18f, 18f), e.Result)) right -= 21f;
            // Live: once past the win condition, the rank badge their stage HUD shows right now.
            else if (e.Result == null && e.HasState && e.State.ReachedWin && e.State.HudRank != 255
                     && StageRank.DrawHud(new Rect(right - 18f, r.y + 22f, 18f, 18f), e.State.HudRank)) right -= 21f;
            // 3: the three timers; 4: when they reached the win condition, or where they are.
            if (size.Length > 0)
            {
                GUI.Label(new Rect(tx, r.y + 22f, right - tx, 18f), size, _size);
                if (timers != null)
                {
                    GUI.Label(new Rect(tx, r.y + 40f, tw, 17f), timers, _timers);
                    GUI.Label(new Rect(tx, r.y + 58f, tw, 17f), line3, _where);
                }
                else GUI.Label(new Rect(tx, r.y + 40f, tw, 17f), line3, _where);
            }
            else GUI.Label(new Rect(tx, r.y + 22f, right - tx, 18f), line3, _where);
        }

        /// <summary>RTA of the run in progress: real time since the shared GO (-1 when no run is going).</summary>
        private static float RunRta() =>
            SyncRun.StartAt > 0f && SyncRun.Phase == RunPhase.Running ? SyncRun.HostNow - SyncRun.StartAt : -1f;

        /// <summary>
        /// "IGT 1:23.4  RTA 1:25.0  2:35 left" (time attack: the stage clock counting up), shortened to fit the slot:
        /// without "left", then without the stage clock.
        /// </summary>
        private static string TimersLine(float igt, float rta, float stage, bool countsUp, bool noClock = false, float tw = 999f)
        {
            string both = $"IGT {RunClocks.Short(igt)}  RTA {RunClocks.Short(rta)}";
            if (noClock || stage < 0f || stage > 90000f) return both;
            string clock = ShortClock(countsUp ? stage : Mathf.Max(0f, stage));
            return Fit(_timers, tw, countsUp ? $"{both}  stage {clock}" : $"{both}  {clock} left", $"{both}  {clock}", both);
        }

        /// <summary>When the win condition was reached: "win: IGT 0:52.3 · RTA 0:53.0", shortened to fit the slot.</summary>
        private static string WinLine(float igt, float rta, float stage, float tw = 999f) =>
            igt >= 0f ? Fit(_where, tw, $"win: IGT {RunClocks.Short(igt)} · RTA {RunClocks.Short(rta)}",
                                        $"win IGT {RunClocks.Short(igt)} RTA {RunClocks.Short(rta)}",
                                        $"win {RunClocks.Short(igt)} / {RunClocks.Short(rta)}")
                      : stage >= 0f ? "win at stage clock " + ShortClock(stage) : "win condition: not reached";

        /// <summary>The text column's width in a slot (right of the portrait).</summary>
        private static float TextWidth(Rect r) => r.xMax - 6f - (r.x + 6f + Mathf.Min(r.height - 14f, 52f) + 7f);

        private static GUIStyle _wrapTimers, _wrapWhere;
        private static float _lineTimers, _lineWhere;

        /// <summary>
        /// The first of the texts that fits on one line of <paramref name="width"/> in this style (the last one otherwise).
        /// Measured with GUIStyle.CalcHeight on a word-wrapping copy (two lines tall = too long); CalcHeight is the
        /// measuring call the chat bubbles already use.
        /// </summary>
        private static string Fit(GUIStyle style, float width, params string[] texts)
        {
            try
            {
                bool timers = style == _timers;
                ref GUIStyle wrap = ref timers ? ref _wrapTimers : ref _wrapWhere;
                if (wrap == null)
                {
                    wrap = Style(style.fontSize, Color.white, style.fontStyle, TextAnchor.UpperLeft);
                    wrap.wordWrap = true;
                    wrap.clipping = TextClipping.Overflow;
                    float one = wrap.CalcHeight(new GUIContent("0:00.0"), 1000f);
                    if (timers) _lineTimers = one; else _lineWhere = one;
                }
                float line = timers ? _lineTimers : _lineWhere;
                foreach (var t in texts)
                    if (wrap.CalcHeight(new GUIContent(t), width) < line * 1.5f) return t;
            }
            catch { }
            return texts[texts.Length - 1];
        }

        /// <summary>The game's result-screen clock: "3:05".</summary>
        private static string ShortClock(float seconds)
        {
            int t = Mathf.Max(0, (int)seconds);
            return $"{t / 60}:{t % 60:00}";
        }

        private static readonly Dictionary<int, Texture2D> SolidCache = new();

        private static Texture2D Solid(Color c, float alpha = 1f)
        {
            int a = Mathf.Clamp((int)(alpha * 15f), 0, 15);
            int key = (a << 24) | ((int)(c.r * 255) << 16) | ((int)(c.g * 255) << 8) | (int)(c.b * 255);
            if (SolidCache.TryGetValue(key, out var t) && t != null) return t;
            return SolidCache[key] = Tex(new Color(c.r, c.g, c.b, alpha));
        }

        private static void EnsureStyles()
        {
            if (_name != null && _panel != null && _mine != null && _iconBg != null && _tagClear?.normal.background != null) return;
            _name = Style(14, Color.white, FontStyle.Bold, TextAnchor.MiddleLeft);
            _size = Style(13, new Color(1f, 0.88f, 0.45f), FontStyle.Bold, TextAnchor.MiddleLeft);
            _time = Style(12, new Color(0.8f, 0.9f, 1f), FontStyle.Normal, TextAnchor.MiddleRight);
            _where = Style(12, new Color(0.8f, 0.9f, 1f), FontStyle.Normal, TextAnchor.MiddleLeft);
            _timers = Style(11, new Color(0.92f, 0.95f, 1f), FontStyle.Normal, TextAnchor.MiddleLeft);
            _badge = Style(18, Color.white, FontStyle.Bold, TextAnchor.MiddleCenter);
            _place = Style(16, new Color(1f, 0.85f, 0.25f), FontStyle.Bold, TextAnchor.MiddleCenter);
            _place.normal.background = Tex(new Color(0.15f, 0.1f, 0.25f, 0.95f));
            _panel = Tex(new Color(0.06f, 0.05f, 0.1f, 0.72f));
            _mine = Tex(new Color(0.12f, 0.09f, 0.2f, 0.82f));
            _iconBg = Tex(new Color(1f, 1f, 1f, 0.12f));
            _tagClear = SyncRun.Tag(new Color(0.2f, 0.65f, 0.35f, 1f));
            _tagFail = SyncRun.Tag(new Color(0.7f, 0.22f, 0.22f, 1f));
            _tagCut = SyncRun.Tag(new Color(0.85f, 0.5f, 0.15f, 1f));
            _tagClear.fontSize = _tagFail.fontSize = _tagCut.fontSize = 10;
            SolidCache.Clear();
        }

        private static GUIStyle Style(int size, Color color, FontStyle font, TextAnchor align)
        {
            var s = new GUIStyle();
            s.fontSize = size;
            s.fontStyle = font;
            s.alignment = align;
            s.normal.textColor = color;
            s.clipping = TextClipping.Clip;
            return s;
        }

        public static Texture2D Tex(Color c)
        {
            var t = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixels(new[] { c });
            t.Apply();
            return t;
        }
    }

    /// <summary>
    /// The pictures for 1st, 2nd and 3rd: the game's Crown and wash tin (Tarai) freebie icons (game_item14 and
    /// game_item06, found among the loaded sprites/textures once the game has them), and the biscuit stage
    /// object (BISCUIT_A), rendered into an icon. Looked up a few times at most; a numbered badge stands in.
    /// </summary>
    internal static class RankIcons
    {
        private static Texture _crown, _tin, _biscuit;
        private static float _nextTry;
        private static int _tries;

        /// <summary>Drawing only (OnGUI): the icons are looked up and cropped in <see cref="Tick"/>.</summary>
        public static bool Draw(Rect r, int place)
        {
            var tex = place switch { 1 => _crown, 2 => _biscuit, 3 => _tin, _ => null };
            if (tex == null) return false;
            GUI.DrawTexture(r, tex, ScaleMode.ScaleToFit);
            Drawn++;
            return true;
        }

        /// <summary>The picture for a place (1-3), or null (Dear ImGui draws it with ImUi.Image).</summary>
        public static Texture Icon(int place) => place switch { 1 => _crown, 2 => _biscuit, 3 => _tin, _ => null };

        /// <summary>Place icons drawn so far (for the self-test).</summary>
        public static int Drawn;

        public static string Describe() => $"crown={_crown != null} biscuit={_biscuit != null} wash tin={_tin != null} drawn={Drawn}";

        /// <summary>Per frame (Update): finds the icons once someone has finished.</summary>
        public static void Tick()
        {
            if (SyncRun.Standings.Count > 0) Resolve();
        }

        private static void Resolve()
        {
            if ((_crown != null && _tin != null && _biscuit != null) || _tries >= 8 || Time.unscaledTime < _nextTry) return;
            _nextTry = Time.unscaledTime + 5f;
            _tries++;
            try
            {
                if (_crown == null || _tin == null)
                {
                    foreach (var s in Resources.FindObjectsOfTypeAll<Sprite>())
                    {
                        if (s == null) continue;
                        string n = s.name;
                        if (_crown == null && n == "game_item14") _crown = SpriteCrop.Of(s);
                        else if (_tin == null && n == "game_item06") _tin = SpriteCrop.Of(s);
                    }
                }
                if (_crown == null || _tin == null)
                {
                    foreach (var t in Resources.FindObjectsOfTypeAll<Texture2D>())
                    {
                        if (t == null) continue;
                        string n = t.name;
                        if (_crown == null && n == "game_item14") _crown = t;
                        else if (_tin == null && n == "game_item06") _tin = t;
                    }
                }
                if (_biscuit == null) _biscuit = RenderBiscuit();
                if (_tries == 1 || (_crown != null && _tin != null && _biscuit != null))
                    MelonLogger.Msg($"[Online] Place icons: crown={_crown != null} biscuit={_biscuit != null} wash tin={_tin != null}.");
            }
            catch (Exception e)
            {
                _tries = 99;
                MelonLogger.Warning($"[Online] Place icons unavailable: {e.Message}");
            }
        }

        /// <summary>The biscuit's mesh (from the loaded stage, or the asset) drawn into a small texture.</summary>
        private static Texture RenderBiscuit()
        {
            Mesh mesh = null;
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Material> mats = null;
            foreach (var mf in Resources.FindObjectsOfTypeAll<MeshFilter>())
            {
                if (mf == null || mf.sharedMesh == null || !mf.sharedMesh.name.Contains("BISCUIT")) continue;
                var mr = mf.GetComponent<MeshRenderer>();
                if (mr == null) continue;
                mesh = mf.sharedMesh;
                mats = mr.sharedMaterials;
                break;
            }
            if (mesh == null) return null;
            var go = new GameObject("OUAK biscuit icon");
            try
            {
                go.transform.position = new Vector3(0f, -5000f, 0f);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterials = mats;
                return Portrait.TakeObject(go);
            }
            finally { UnityEngine.Object.Destroy(go); }
        }
    }

    /// <summary>
    /// A sprite's own picture as a standalone texture, for GUI.DrawTexture. Sprites usually sit in an atlas,
    /// and the IMGUI call that draws part of a texture (GUI.DrawTextureWithTexCoords) is stripped from this
    /// game: MelonLoader rebuilds it with raw engine calls, and an engine error inside it crashes the game.
    /// So the sprite's rectangle is copied once (Graphics.Blit with scale/offset, outside OnGUI).
    /// </summary>
    internal static class SpriteCrop
    {
        public static Texture Of(Sprite s, int maxSize = 256)
        {
            if (s == null) return null;
            var tex = s.texture;
            if (tex == null || tex.width <= 0 || tex.height <= 0) return null;
            var rect = s.textureRect;
            if (rect.width < 1f || rect.height < 1f) return null;
            if (rect.width >= tex.width - 0.5f && rect.height >= tex.height - 0.5f) return tex;
            float k = Mathf.Min(1f, maxSize / Mathf.Max(rect.width, rect.height));
            var rt = new RenderTexture(Mathf.Max(1, Mathf.RoundToInt(rect.width * k)), Mathf.Max(1, Mathf.RoundToInt(rect.height * k)), 0)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            var prev = RenderTexture.active;
            try
            {
                Graphics.Blit(tex, rt, new Vector2(rect.width / tex.width, rect.height / tex.height), new Vector2(rect.x / tex.width, rect.y / tex.height));
            }
            finally { RenderTexture.active = prev; }
            return rt;
        }
    }
}
