using System;
using System.Linq;
using HarmonyLib;
using Il2Cpp;
using Il2CppApp.KatamariSin;
using MelonLoader;
using UnityEngine;
using OUAKModAPI;

namespace OUAKMultiplayer
{
    /// <summary>
    /// Picking a stage while in a session. Everyone roams freely (HubPresence); a stage is played together:
    /// - A player who picks one (Go on a fan's stage screen in a plaza, or the Levels / Match tabs) starts a vote
    ///   instead of going alone. If it passes: a fan's stage gathers everyone at that fan's stage screen first
    ///   (music, ready: PlazaFlow), a menu pick is loaded by the host and everyone follows (Votes.Apply).
    /// - The host who picks one chooses: go now (everyone comes along), or start a vote.
    /// The King: SelectHirobaKingController.KingTalkSwich(99) is the stage start (it closes the curtain, sets
    /// GlobalManager.launcherStage = _stages[_selectIndex] - 1 and loads MainGame). PlazaFlow skips his talk to it
    /// once a gathering says go. One that wasn't picked through a fan's screen is held there as before: "go now"
    /// lets it run; a vote or cancel ends the talk the way the plaza backs out of one (OusaDemoBack, then
    /// SelectHirobaController.Cancel, which returns the plaza to walking), and reloads the plaza if that didn't
    /// take. The mod's own loads that follow a session (passed votes, following the host) pass through Approved.
    /// </summary>
    internal static class StageGate
    {
        private sealed class Choice
        {
            public MapEntry Entry;
            public SelectHirobaKingController King;
            public bool FromKing;
            /// <summary>Picked with a fan in a plaza (PlazaSpot.Pack), or 0.</summary>
            public int Spot;
            public float Opened;
        }

        private static int _approved;
        private static bool _patched;
        private static Choice _choice;
        /// <summary>"Go now" with the King: his stage start takes a few frames (the curtain closes first), so it runs until then.</summary>
        private static float _kingGoUntil, _watchUntil;
        private static GUIStyle _title, _hint, _btn;
        private static Texture2D _panel, _btnTex;

        public static bool ChoiceOpen => _choice != null;

        public static void Init(HarmonyLib.Harmony harmony)
        {
            try
            {
                harmony.Patch(AccessTools.Method(typeof(SelectHirobaKingController), nameof(SelectHirobaKingController.KingTalkSwich)),
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(StageGate), nameof(BeforeKingTalk))));
                _patched = true;
            }
            catch (Exception e) { MelonLogger.Warning($"[Online] Stage votes from the King unavailable: {e.Message}"); }
        }

        /// <summary>Runs a load the session asked for (a passed vote, following the host) past the gate.</summary>
        public static T Approved<T>(Func<T> load)
        {
            _approved++;
            try { return load(); }
            finally { _approved--; }
        }

        /// <summary>
        /// From SceneLoader.Load: in a session, a stage you pick yourself becomes a vote (the host chooses go now or
        /// vote). Returns true when it took the load over. Multiplayer maps (Katamari Ball, Race, Target) are never
        /// taken over: the mod's own sessions are for the single-player stages only (the user, 2026-11-06).
        /// </summary>
        public static bool Intercept(MapEntry e, out string message)
        {
            message = null;
            if (_approved > 0 || !NetSession.Connected || e == null || e.Kind != MapKind.Stage) return false;
            if (NetSession.Mode == NetSession.Role.Host)
            {
                Open(e, null);
                message = $"{e.Name}: go now with everyone, or start a vote? (the popup above the player bar)";
                return true;
            }
            message = Votes.Propose(VoteKind.GoToStage, Ghosts.KeyFor(e));
            return true;
        }

        // ---------------------------------------------------------------- the King

        private static bool BeforeKingTalk(SelectHirobaKingController __instance, int __0)
        {
            try
            {
                if (!NetSession.Connected) return true;
                // A gathering's go: his talk goes straight to the stage start.
                var ours = PlazaFlow.KingTalk(__instance, __0);
                if (ours.HasValue) return ours.Value;
                if (__0 != 99) return true;
                if (Time.unscaledTime < _kingGoUntil) return true;
                // Still talking: the game waits for that too before it starts anything.
                var text = __instance.GetKingTextController;
                if (text != null && text.GetKingText) return true;
                var stages = __instance._stages;
                int idx = __instance._selectIndex;
                if (stages == null || stages.Length == 0 || idx < 0 || idx >= stages.Length || stages[0] == 999) return true;
                int stage = stages[idx];
                var entry = StageCatalog.Entries.FirstOrDefault(x => x.Kind == MapKind.Stage && (int)x.Stage == stage);
                if (entry == null) return true;
                int spot = PlazaFlow.SpotNow(stage);
                if (NetSession.Mode == NetSession.Role.Host)
                {
                    // Held here (the King waits) until you choose.
                    if (_choice == null) Open(entry, __instance, spot);
                    return false;
                }
                EndKingTalk(__instance);
                int key = Ghosts.KeyFor(entry);
                ModTasks.Run(() => Votes.Propose(VoteKind.GoToStage, key, spot), toast: true);
                return false;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[Online] Stage pick: {e.Message}");
                return true;
            }
        }

        /// <summary>Back to walking around the plaza, the way it backs out of a talk.</summary>
        private static void EndKingTalk(SelectHirobaKingController king)
        {
            try { king.OusaDemoBack(); } catch (Exception e) { MelonLogger.Warning($"[Online] Ending the King's talk: {e.Message}"); }
            try { king._talkState = 0; } catch { }
            try { king.SelectHirobaIsActive = false; } catch { }
            try
            {
                var plaza = SingletonMonoBehaviour<SelectHirobaManager>.instance;
                plaza?.GetSelectHirobaController?.Cancel();
            }
            catch (Exception e) { MelonLogger.Warning($"[Online] Back to the plaza: {e.Message}"); }
            _watchUntil = Time.unscaledTime + 2.5f;
        }

        // ---------------------------------------------------------------- the host's choice

        private static void Open(MapEntry entry, SelectHirobaKingController king, int spot = 0)
        {
            _choice = new Choice { Entry = entry, King = king, FromKing = king != null, Spot = spot, Opened = Time.unscaledTime };
            MelonLogger.Msg($"[Online] You picked {entry.Name}: go now with everyone, or start a vote?");
        }

        /// <summary>Host: Go on a fan's stage screen (PlazaFlow held it): go now with everyone, or start a vote?</summary>
        public static void OpenFromPlaza(MapEntry entry, PlazaSpot spot)
        {
            if (_choice != null) return;
            Open(entry, null, spot.Pack());
        }

        public enum Pick { GoNow, Vote, Cancel }

        public static void Choose(Pick pick)
        {
            var c = _choice;
            if (c == null) return;
            _choice = null;
            var entry = c.Entry;
            int key = Ghosts.KeyFor(entry);
            switch (pick)
            {
                case Pick.GoNow:
                    MelonLogger.Msg($"[Online] Going to {entry.Name} with everyone.");
                    if (c.FromKing) _kingGoUntil = Time.unscaledTime + 20f;   // the King's own stage start runs from his next update
                    else if (PlazaSpot.TryUnpack(c.Spot, out var spot)) ModTasks.Run(() => PlazaFlow.HostBeginGather(entry, spot), toast: true);
                    else ModTasks.Run(() => Approved(() => SceneLoader.Load(entry)), toast: true);
                    break;
                case Pick.Vote:
                    if (c.FromKing && c.King != null) EndKingTalk(c.King);
                    int arg2 = c.Spot;
                    ModTasks.Run(() => Votes.Propose(VoteKind.GoToStage, key, arg2), toast: true);
                    break;
                default:
                    if (c.FromKing && c.King != null) EndKingTalk(c.King);
                    MelonLogger.Msg($"[Online] Didn't go to {entry.Name}.");
                    break;
            }
        }

        public static void Tick()
        {
            float now = Time.unscaledTime;
            var c = _choice;
            if (c != null)
            {
                // Left the plaza (or the session) meanwhile: nothing to choose any more.
                if (!NetSession.Connected || (c.FromKing && c.King == null) || now - c.Opened > 60f) { Choose(Pick.Cancel); return; }
                if (!TextInput.IsTyping) ReadInput();
            }
            if (_watchUntil > 0f && now > _watchUntil)
            {
                _watchUntil = 0f;
                CheckPlazaBack();
            }
        }

        /// <summary>The plaza should be back to walking (SELECTHIROBA); if not, reload it so nobody is stuck.</summary>
        private static void CheckPlazaBack()
        {
            try
            {
                var plaza = SingletonMonoBehaviour<SelectHirobaManager>.instance;
                var ctrl = plaza?.GetSelectHirobaController;
                if (ctrl == null || ctrl._gameState == SelectHirobaController.GameState.SELECTHIROBA) return;
                MelonLogger.Warning($"[Online] The plaza didn't return to walking (state {ctrl._gameState}); reloading it.");
                int idx = GameRefs.Save != null ? GameRefs.Save._stageIndex : 0;
                var entry = StageCatalog.Entries.FirstOrDefault(e => e.Kind == MapKind.Plaza && !string.IsNullOrEmpty(e.PlazaKey) && SelectHirobaManager.GetStageIndex(e.PlazaKey) == idx);
                if (entry != null) ModTasks.Run(() => SceneLoader.LoadPlaza(entry), toast: true);
            }
            catch (Exception e) { MelonLogger.Warning($"[Online] Checking the plaza: {e.Message}"); }
        }

        /// <summary>G go now, V vote, Esc cancel; the D-pad: up go now, down vote, left cancel.</summary>
        private static void ReadInput()
        {
            try
            {
                if (Input.GetKeyDown(KeyCode.G)) { Choose(Pick.GoNow); return; }
                if (Input.GetKeyDown(KeyCode.V)) { Choose(Pick.Vote); return; }
                if (Input.GetKeyDown(KeyCode.Escape)) { Choose(Pick.Cancel); return; }
            }
            catch { }
            try
            {
                var pad = UnityEngine.InputSystem.Gamepad.current;
                if (pad == null) return;
                if (pad.dpad.up.wasPressedThisFrame) Choose(Pick.GoNow);
                else if (pad.dpad.down.wasPressedThisFrame) Choose(Pick.Vote);
                else if (pad.dpad.left.wasPressedThisFrame) Choose(Pick.Cancel);
            }
            catch { }
        }

        /// <summary>Above the player bar (where vote popups go), while the host chooses.</summary>
        public static void OnGUI(float barTop)
        {
            var c = _choice;
            if (c == null || Votes.Open) return;
            if (_title == null || _panel == null)
            {
                _title = Style(16, Color.white, FontStyle.Bold);
                _hint = Style(13, new Color(0.85f, 0.9f, 1f), FontStyle.Normal);
                _btn = Style(15, Color.white, FontStyle.Bold);
                _panel = PlayerBar.Tex(new Color(0.08f, 0.06f, 0.14f, 0.92f));
                _btnTex = PlayerBar.Tex(new Color(0.3f, 0.25f, 0.45f, 1f));
                _btn.normal.background = _btnTex;
            }
            float w = 600f, h = 96f;
            float x = (Screen.width / Votes.GuiScale() - w) / 2f, y = barTop - h - 10f;
            GUI.DrawTexture(new Rect(x, y, w, h), _panel);
            GUI.Label(new Rect(x, y + 6f, w, 22f), $"You picked {c.Entry.Name}", _title);
            float bw = 180f, gap = 10f, bx = x + (w - 3 * bw - 2 * gap) / 2f;
            if (GUI.Button(new Rect(bx, y + 32f, bw, 30f), GUIContent.Temp("Go now (everyone)"), _btn)) Choose(Pick.GoNow);
            if (GUI.Button(new Rect(bx + bw + gap, y + 32f, bw, 30f), GUIContent.Temp("Start a vote"), _btn)) Choose(Pick.Vote);
            if (GUI.Button(new Rect(bx + 2 * (bw + gap), y + 32f, bw, 30f), GUIContent.Temp("Cancel"), _btn)) Choose(Pick.Cancel);
            GUI.Label(new Rect(x, y + 68f, w, 20f), "G or D-pad up = go now  ·  V or D-pad down = vote  ·  Esc or D-pad left = cancel", _hint);
        }

        private static GUIStyle Style(int size, Color color, FontStyle font)
        {
            var s = new GUIStyle();
            s.fontSize = size;
            s.fontStyle = font;
            s.alignment = TextAnchor.MiddleCenter;
            s.normal.textColor = color;
            return s;
        }

        public static string Describe() => $"King hook {(_patched ? "on" : "off")}, choice {(_choice != null ? "open for " + _choice.Entry.Name : "closed")}";
    }
}
