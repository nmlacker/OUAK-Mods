using System;
using Il2CppApp.KatamariSin;
using UnityEngine;

namespace OUAKModAPI
{
    /// <summary>
    /// Your katamari, prince and stage clock in the current stage, read straight from the game. Everything is
    /// null-safe: outside a stage the values are 0 / empty and <see cref="Exists"/> is false.
    /// Sizes are in the game's unit, tenths of a millimetre (12994 = 1299.4 mm, shown 1m29cm9mm), like stage goals.
    /// </summary>
    public static class Katamari
    {
        /// <summary>The player's katamari (MainGameManager.GetCore()).</summary>
        public static MainGameCore Core
        {
            get
            {
                try { var c = GameRefs.Main?.GetCore(); return c != null ? c : null; }
                catch { return null; }
            }
        }

        /// <summary>The player's prince (MainGameManager.GetOuji()).</summary>
        public static MainGameOuji Prince
        {
            get
            {
                try { var o = GameRefs.Main?.GetOuji(); return o != null ? o : null; }
                catch { return null; }
            }
        }

        public static bool Exists => Core != null;

        /// <summary>Size in tenths of a millimetre (MainGameManager.KatamariSize).</summary>
        public static int Size => GameRefs.Main?.KatamariSize ?? 0;

        /// <summary>Size in metres.</summary>
        public static float SizeMeters => Size / 10000f;

        /// <summary>The size the way the game writes it, e.g. "1m29cm9mm".</summary>
        public static string SizeText => FormatSize(Size);

        /// <summary>World position of the katamari's centre.</summary>
        public static Vector3 Position
        {
            get { var c = Core; return c != null ? c.transform.position : Vector3.zero; }
        }

        /// <summary>The katamari's radius in world units (MainGameCore.KatamariRadius).</summary>
        public static float Radius => Core?.KatamariRadius ?? 0f;

        /// <summary>1 = time attack (counts up), 0 = time limit (counts down), -1 = no clock (MainGameManager.CheckIsTimerRule).</summary>
        public static int ClockRule
        {
            get { try { return GameRefs.Main != null ? Mathf.Clamp(GameRefs.Main.CheckIsTimerRule(), -1, 1) : -1; } catch { return -1; } }
        }

        /// <summary>Seconds played this attempt (MainGameManager._playTime).</summary>
        public static float Elapsed => GameRefs.Main?._playTime ?? 0f;

        /// <summary>Seconds left on a time-limit clock (MainGameManager._nowTime).</summary>
        public static float TimeLeft => GameRefs.Main?._nowTime ?? 0f;

        /// <summary>The stage's time limit in seconds (MainGameManager._startTime). Settable at the start of a stage.</summary>
        public static float TimeLimit
        {
            get => GameRefs.Main?._startTime ?? 0f;
            set
            {
                var gm = GameRefs.Main;
                if (gm == null) return;
                gm._startTime = Mathf.Max(1f, value);
                gm._nowTime = Mathf.Max(0f, gm._startTime - gm._playTime);
            }
        }

        /// <summary>The freebie in the slot.</summary>
        public static eInstageItemType Freebie => GameRefs.Main?._nowInventoryItem ?? eInstageItemType.NoItem;

        /// <summary>True while the player controls the katamari (not paused, no cutscene, not ended).</summary>
        public static bool Playing
        {
            get
            {
                var gm = GameRefs.Main;
                return gm != null && gm.PlayerInitiated && gm.IsGameRunning && !gm._isInClearDemo && !gm._isInGameOver;
            }
        }

        /// <summary>True once the stage's goal was reached (MainGameManager._hasCleared).</summary>
        public static bool Cleared => GameRefs.Main?._hasCleared ?? false;

        /// <summary>Formats a size in tenths of a millimetre like the game: "1m29cm9mm".</summary>
        public static string FormatSize(int tenthsMm)
        {
            if (tenthsMm <= 0) return "0mm";
            int mm = tenthsMm / 10;
            int m = mm / 1000, cm = mm / 10 % 100, rest = mm % 10;
            string s = "";
            if (m > 0) s += $"{m}m";
            if (cm > 0 || m > 0) s += $"{cm}cm";
            s += $"{rest}mm";
            return s;
        }

        /// <summary>Stops the katamari from taking input for <paramref name="seconds"/> (MainGameCore.FreezeInput).</summary>
        public static void FreezeInput(float seconds)
        {
            try { Core?.FreezeInput(seconds); }
            catch (Exception e) { ApiCore.Log.Warn($"FreezeInput: {e.Message}"); }
        }
    }
}
