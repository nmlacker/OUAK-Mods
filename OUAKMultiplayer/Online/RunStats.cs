using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Il2CppApp.KatamariSin;
using MelonLoader;
using UnityEngine;
using OUAKModAPI;

namespace OUAKMultiplayer
{
    /// <summary>
    /// Your after-action report for a run, read from your own game: dashes (MainGameCore.ExecuteOujiDash, which
    /// CheckDash calls once per dash), quick turns (each time MainGameCore.InOujiTurn comes on: _specialAction
    /// OujiTurn or OujiDashWaitTurn; ExecuteOujiTurn itself runs every frame of a turn, so it can't be counted),
    /// freebies used (the item effect starting), objects rolled up, distance rolled, and the stage time at which
    /// you reached each growth size (the size steps where the camera pulls back: GetNextCameraDataSize). Also the
    /// stage rank and "Size Time" the game's own result screen shows (ReadRank).
    /// </summary>
    internal static class RunStats
    {
        private const int MaxSteps = 16;

        public static int Dashes, Turns, Freebies, Objects;
        public static float Distance;
        public static readonly List<(int size, float time)> Steps = new();

        private static bool _active, _hasPos, _itemActive, _turning;
        private static Vector3 _lastPos;
        private static int _nextStep;

        public static void Init(HarmonyLib.Harmony harmony)
        {
            try
            {
                harmony.Patch(AccessTools.Method(typeof(MainGameCore), nameof(MainGameCore.ExecuteOujiDash)),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(RunStats), nameof(AfterDash))));
            }
            catch (Exception e) { MelonLogger.Warning($"[Online] Run stats patches failed: {e.Message}"); }
        }

        /// <summary>Starts counting for a new run (the shared start).</summary>
        public static void Begin()
        {
            Dashes = Turns = Freebies = Objects = 0;
            Distance = 0f;
            Steps.Clear();
            _hasPos = _itemActive = _turning = false;
            _active = true;
            _nextStep = NextStepSize();
            // The stage HUD's rank badges are loaded now; keep them for the results.
            StageRank.Resolve();
        }

        public static void Stop() => _active = false;

        public static void Tick()
        {
            if (!_active) return;
            try
            {
                var gm = GameRefs.Main;
                var core = gm?.GetCore();
                if (core == null) return;
                var p = core.transform.position;
                if (_hasPos)
                {
                    var d = p - _lastPos;
                    d.y = 0f;
                    float m = d.magnitude;
                    if (m < 50f) Distance += m;
                }
                _lastPos = p;
                _hasPos = true;

                bool item = gm._itemEffectTimer >= 0f;
                if (item && !_itemActive) Freebies++;
                _itemActive = item;

                // A quick turn lasts OujiTurnTime (0.4 s): one count when it begins.
                bool turning = core.InOujiTurn;
                if (turning && !_turning) Turns++;
                _turning = turning;

                int size = gm.KatamariSize;
                if (_nextStep > 0 && size >= _nextStep && Steps.Count < MaxSteps)
                {
                    Steps.Add((_nextStep, gm._playTime));
                    int next = NextStepSize();
                    _nextStep = next > _nextStep ? next : 0;
                }
            }
            catch { }
        }

        public static void OnLocalPickup()
        {
            if (_active) Objects++;
        }

        private static void AfterDash(MainGameCore __instance)
        {
            if (_active && IsMine(__instance)) Dashes++;
        }

        private static bool IsMine(MainGameCore core)
        {
            try
            {
                var mine = GameRefs.Main?.GetCore();
                return mine != null && core != null && core.Pointer == mine.Pointer;
            }
            catch { return false; }
        }

        /// <summary>The next growth size (camera step), or 0 if there isn't one.</summary>
        public static int NextStepSize()
        {
            try
            {
                var gm = GameRefs.Main;
                return gm != null ? gm.GetNextCameraDataSize() : 0;
            }
            catch { return 0; }
        }

        public static void Fill(RunResult r)
        {
            r.Dashes = Dashes;
            r.Turns = Turns;
            r.Freebies = Freebies;
            r.Objects = Objects;
            r.Distance = Distance;
            r.Steps.Clear();
            r.Steps.AddRange(Steps);
        }

        // ---------------------------------------------------------------- the game's own result values

        /// <summary>
        /// Fills in what the game's own result screen shows: the stage rank (MainGameManager.CheckClearRate(),
        /// which StoreResultData saves as LastStageResultData.ClearedRate and the result screen draws as
        /// _RateSprite[rank]), the HUD's rank badge (GetNowClearRateIcon()), and "Size Time" (_clearTime: the
        /// stage clock when the goal was reached, set by MakeStageCleared). Size is the result's Size.
        /// </summary>
        public static void ReadRank(RunResult r, bool log = false)
        {
            try
            {
                var gm = GameRefs.Main;
                if (gm == null || GameRefs.IsMultiplay) return;
                r.Rank = (sbyte)Mathf.Clamp(gm.CheckClearRate(), 0, 15);
                r.RankIcon = (byte)Mathf.Clamp(gm.GetNowClearRateIcon(), 0, 255);
                r.SizeTime = gm._hasCleared && gm._clearTime > 0f ? gm._clearTime : -1f;
                StageRank.Resolve();
                if (log) LogRating(gm, r);
            }
            catch (Exception e) { MelonLogger.Warning($"[Online] Reading the stage rank failed: {e.Message}"); }
        }

        /// <summary>
        /// What the stage rates (its rule and the five targets). CheckClearRate counts the targets reached
        /// (CheckClearRate_CoreSize: thresholds at or below your size) and returns 5 - count (0-1 targets: 4), so
        /// 0 is best; stages with other rules (time, count, price, exact size…) rate their own measure.
        /// </summary>
        private static void LogRating(MainGameManager gm, RunResult r)
        {
            string targets = "?";
            try
            {
                var list = gm.AllStageClearData?.list;
                int idx = gm._stageIdx;
                if (list != null && idx >= 0 && idx < list.Count && list[idx]?.RateNumber != null)
                {
                    var nums = list[idx].RateNumber;
                    var parts = new List<string>();
                    bool sizes = gm.CheckIsSizeRateRule();
                    for (int i = 0; i < nums.Length; i++) parts.Add(sizes ? KatamariState.SizeText(nums[i]) : nums[i].ToString());
                    targets = string.Join("/", parts) + $" (rule {list[idx].RuleID})";
                }
            }
            catch { }
            MelonLogger.Msg($"[Online] Stage rating: rule {gm._inStageRuleID}, size rule {gm.CheckIsSizeRateRule()}, clock {gm.CheckIsTimerRule()}, " +
                            $"targets {targets}; size {KatamariState.SizeText(gm.KatamariSize)}, cleared {gm._hasCleared} → rank {r.Rank} (badge {r.RankIcon}).");
        }
    }

    /// <summary>
    /// The game's rank badges, drawn from its own sprites: the result screen's (ResultScoreRateController
    /// _RateSprite, indexed by the rank) and the stage HUD's (UIRankTargetText.RankSprites, indexed by
    /// GetNowClearRateIcon). Found while they're loaded and kept; a "Rank n" text stands in otherwise.
    /// </summary>
    internal static class StageRank
    {
        private struct Icon
        {
            /// <summary>The badge cropped out of its atlas (SpriteCrop), for GUI.DrawTexture.</summary>
            public Texture Tex;
            public string Name;
        }

        private static Icon[] _result, _hud;
        private static float _nextTry;
        private static int _tries;

        /// <summary>Update only (it copies textures): at the run's start and when the run ends.</summary>
        public static void Resolve()
        {
            if ((_result != null && _hud != null) || _tries >= 60 || Time.unscaledTime < _nextTry) return;
            _nextTry = Time.unscaledTime + 3f;
            _tries++;
            try
            {
                if (_hud == null)
                    foreach (var ui in Resources.FindObjectsOfTypeAll<UIRankTargetText>())
                        if (ui != null && (_hud = Take(ui.RankSprites, "stage HUD")) != null) break;
                if (_result == null)
                    foreach (var rc in Resources.FindObjectsOfTypeAll<ResultScoreRateController>())
                        if (rc != null && (_result = Take(rc._RateSprite, "result screen")) != null) break;
            }
            catch (Exception e)
            {
                _tries = 99;
                MelonLogger.Warning($"[Online] Rank badges unavailable: {e.Message}");
            }
        }

        private static Icon[] Take(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Sprite> sprites, string where)
        {
            if (sprites == null || sprites.Length == 0) return null;
            var icons = new Icon[sprites.Length];
            var names = new List<string>();
            for (int i = 0; i < sprites.Length; i++)
            {
                var s = sprites[i];
                if (s == null) { names.Add("-"); continue; }
                Texture tex = null;
                try { tex = SpriteCrop.Of(s, 128); }
                catch (Exception e) { MelonLogger.Warning($"[Online] Rank badge {s.name}: {e.Message}"); }
                icons[i] = new Icon { Tex = tex, Name = s.name };
                names.Add(s.name);
            }
            MelonLogger.Msg($"[Online] Rank badges from the {where}: {string.Join(", ", names)}.");
            return icons;
        }

        private static Icon? Find(RunResult r)
        {
            if (r == null || r.Rank < 0) return null;
            if (_result != null && r.Rank < _result.Length && _result[r.Rank].Tex != null) return _result[r.Rank];
            if (_hud != null && r.RankIcon < _hud.Length && _hud[r.RankIcon].Tex != null) return _hud[r.RankIcon];
            return null;
        }

        /// <summary>Draws the player's rank badge (OnGUI); false if there's no picture (draw <see cref="Text"/> instead).</summary>
        public static bool Draw(Rect rect, RunResult r)
        {
            var icon = Find(r);
            if (icon == null) return false;
            GUI.DrawTexture(rect, icon.Value.Tex, ScaleMode.ScaleToFit);
            Drawn++;
            return true;
        }

        /// <summary>The player's rank badge picture, or null (Dear ImGui draws it with ImUi.Image).</summary>
        public static Texture Tex(RunResult r) => Find(r)?.Tex;

        /// <summary>Draws the stage HUD's rank badge number <paramref name="hudIndex"/> (GetNowClearRateIcon); false if there's no picture.</summary>
        public static bool DrawHud(Rect rect, byte hudIndex)
        {
            if (_hud == null || hudIndex >= _hud.Length || _hud[hudIndex].Tex == null) return false;
            GUI.DrawTexture(rect, _hud[hudIndex].Tex, ScaleMode.ScaleToFit);
            Drawn++;
            return true;
        }

        /// <summary>Badges drawn so far (for the self-test).</summary>
        public static int Drawn;

        public static string Describe() =>
            $"result badges={(_result == null ? "none" : _result.Count(i => i.Tex != null) + "/" + _result.Length)} " +
            $"HUD badges={(_hud == null ? "none" : _hud.Count(i => i.Tex != null) + "/" + _hud.Length)} drawn={Drawn}";

        /// <summary>The badge as a score: 5/5 is the game's top badge (common_rank01, Rank 0), 1/5 its lowest.</summary>
        public static int Score(RunResult r) => r == null || r.Rank < 0 ? 0 : Mathf.Clamp(5 - r.Rank, 1, 5);

        public static string Text(RunResult r) => r == null || r.Rank < 0 ? "-" : $"Rank {Score(r)}/5";

        /// <summary>For the match log: 5 of 5 (top badge) to 1 of 5.</summary>
        public static string LogText(RunResult r) => r == null || r.Rank < 0 ? "-" : $"{Score(r)} of 5";
    }
}
