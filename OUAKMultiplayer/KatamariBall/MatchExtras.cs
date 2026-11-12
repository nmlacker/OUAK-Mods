using System;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using Il2CppApp.KatamariSin;
using MelonLoader;
using UnityEngine;
using OUAKModAPI;
using Chance = Il2CppApp.KatamariSin.MultiplaySetting.KingChanceType;

namespace OUAKMultiplayer
{
    /// <summary>
    /// What this mod turns on in Katamari Ball (the game's Sports Katamari) through the API's patches:
    /// <list type="bullet">
    /// <item><b>King Chances</b> in offline matches (<see cref="GamePatches.KingChances"/>). The game draws 4 different ones per
    /// match (MultiplayBigBattle.kingChanceTable) and its tip says the King acts "depending on your time remaining", but it
    /// never starts one. Here the match's 4 fire on time, one in each quarter of the time limit, evenly spaced: at 1/8, 3/8,
    /// 5/8 and 7/8 of it (with no time limit, of the map's own limit). Each goes out once no King Chance is still waiting to
    /// show, through <see cref="KingChances.FireNext"/> (the game's KingChanceProcess and nowKingChangeNo).</item>
    /// <item><b>Target points</b> (<see cref="GamePatches.TargetPoints"/>): in the Target round every crane counted by the
    /// game's own target counter gives <see cref="TargetRound.PointsPerTarget"/> (10) points, for you and the CPU princes.</item>
    /// <item><b>Race and Target</b> maps (<see cref="GamePatches.Race"/> / <see cref="GamePatches.Target"/>, plus
    /// <see cref="GamePatches.TargetCpus"/> so the Target round's CPU princes can roll things up): turned on when this
    /// mod starts one of those maps, since the game's own path for them is broken.</item>
    /// </list>
    /// Settings: [OUAKModMenu_Match] KingChances, TargetPoints (the Katamari Ball tab).
    /// </summary>
    internal static class MatchExtras
    {
        public const string ModName = "OUAK Multiplayer";

        public static bool KingChancesOn = true, TargetPointsOn = true;

        /// <summary>Tests that fire King Chances themselves turn the timed ones off for their launch.</summary>
        public static bool Suspended;

        private static MelonPreferences_Entry<bool> _kcPref, _tpPref;

        // This match: which MultiplayBigBattle it is, and when each King Chance went out (round clock, s).
        private static IntPtr _match;
        public static readonly List<string> FiredThisMatch = new();

        public static void Init(MelonPreferences_Category cat)
        {
            _kcPref = cat.CreateEntry("KingChances", true, "King Chances",
                "Offline matches: the match's 4 King Chances fire one per quarter of the time limit, evenly spaced.");
            _tpPref = cat.CreateEntry("TargetPoints", true, "Target points", "Target round: points for every crane rolled up.");
            KingChancesOn = _kcPref.Value;
            TargetPointsOn = _tpPref.Value;
            KingChances.AddFilter(ModName, Filter);
            Apply();
        }

        public static void Save()
        {
            if (_kcPref == null) return;
            _kcPref.Value = KingChancesOn;
            _tpPref.Value = TargetPointsOn;
            MelonPreferences.Save();
            Apply();
        }

        /// <summary>
        /// The switches. The API's patches are always there and act only in their own context (the user: "Mods don't
        /// switch patches on or off; they just use the features"): King Chances happen only when this mod fires them
        /// (MatchExtras.Tick), and crane points are TargetRound.PointsPerTarget (0 = none).
        /// </summary>
        public static void Apply()
        {
            TargetRound.PointsPerTarget = TargetPointsOn ? 10 : 0;
        }

        /// <summary>Starts a multiplayer map as an offline round; Race and Target need the API's fixes for those rounds.</summary>
        public static string Launch(MapEntry e)
        {
            if (e == null) throw new ArgumentNullException(nameof(e));
            // Race and Target run on the API's fixes for those rounds (always part of the API, acting only on those maps).
            if (e.Internal == "Stage_ONRace1" && !GamePatches.Race.Ready) throw new InvalidOperationException("Race mode: " + GamePatches.Race.Error);
            if (e.Internal == "Stage_ONTarget1" && !GamePatches.Target.Ready) throw new InvalidOperationException("Target mode: " + GamePatches.Target.Error);
            return SceneLoader.Load(e);
        }

        // ---------------------------------------------------------------- King Chances on time

        /// <summary>The round's length for the schedule: its time limit, or the map's own limit when there's none.</summary>
        public static float RoundLength()
        {
            var gm = GameRefs.Main;
            float limit = gm != null ? gm._startTime : 0f;
            if (limit <= 0f || limit > 90000f) limit = CustomMatch.GameDefaultTime > 0f ? CustomMatch.GameDefaultTime : 180f;
            return limit;
        }

        /// <summary>When (round clock, seconds) King Chance <paramref name="i"/> (0-3) is due: the middle of quarter i.</summary>
        public static float DueAt(int i, float length) => length * (2 * i + 1) / 8f;

        public static void Tick()
        {
            if (!KingChancesOn || Suspended || !GamePatches.KingChances.Enabled || !KingChances.InOfflineMatch) return;
            var mgr = GameRefs.Multiplay;
            var bb = mgr?.multiplayBigBattle;
            if (bb == null) return;
            if (bb.Pointer != _match)
            {
                _match = bb.Pointer;
                FiredThisMatch.Clear();
            }
            // Only while the round runs (not the countdown, the end or the results), one at a time.
            if (TrainingTrace.StateName(mgr.stateMachine) != "GameState" || KingChances.Pending) return;
            int done = KingChances.Next;
            var table = KingChances.Table;
            if (done >= Math.Min(4, table.Length)) return;
            float t = mgr.playTime, length = RoundLength();
            if (t < DueAt(done, length) || t >= length) return;
            var fired = KingChances.FireNext();
            string line = $"{done + 1} of 4 at {Clock(t)} ({fired})";
            FiredThisMatch.Add(line);
            MelonLogger.Msg($"[Matches] King Chance {line}; due at {Clock(DueAt(done, length))} of a {Clock(length)} round");
        }

        /// <summary>
        /// The match's rules applied to a King Chance before it fires: with the custom rules leaving no freebie, the King
        /// Chest and freebie drops become object drops; a second object drop (its objects already out) is "... Nothing happened!".
        /// </summary>
        private static Chance Filter(Chance t)
        {
            if (!KingChances.InOfflineMatch) return t;
            if (t == Chance.kingBox && CustomMatch.NoKingBoxRewards) t = Chance.monoDrop;
            if (t == Chance.itemDrop && CustomMatch.NoBoxFreebies) t = Chance.monoDrop;
            // The game's "... Nothing happened!" (SYS_270) is the message table's last entry, KingChanceType.max
            // ("none" would show an empty line, measured by the API session).
            if (t == Chance.monoDrop && AddMonoShown()) t = KingChances.Nothing;
            return t;
        }

        private static bool AddMonoShown()
        {
            try
            {
                var c = GameRefs.Main?._inStageData?.transform.Find("mono/AddMono");
                return c != null && c.gameObject.activeSelf;
            }
            catch { return false; }
        }

        public static string Clock(float s)
        {
            int t = Mathf.Max(0, (int)s);
            return $"{t / 60}:{t % 60:00}";
        }

        /// <summary>For the tab: this match's King Chances, which went out, and when the next one is due.</summary>
        public static string Describe()
        {
            if (!KingChances.InOfflineMatch) return null;
            var mgr = GameRefs.Multiplay;
            var table = KingChances.Table;
            int done = KingChances.Next;
            float length = RoundLength();
            string next = !KingChancesOn ? "off" : done >= Math.Min(4, table.Length) ? "all 4 done"
                : $"next at {Clock(DueAt(done, length))} (clock {Clock(mgr?.playTime ?? 0f)})";
            return $"This match's King Chances: {string.Join(", ", table)}; {done} fired, {next}.";
        }
    }
}
