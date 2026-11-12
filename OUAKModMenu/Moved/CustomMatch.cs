using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Il2Cpp;
using Il2CppApp.KatamariSin;
using MelonLoader;
using UnityEngine;
using Chance = Il2CppApp.KatamariSin.MultiplaySetting.KingChanceType;
using Reward = Il2CppApp.KatamariSin.MultiplaySetting.KingBoxRewardType;
using UserStatus = Il2CppApp.KatamariSin.MultiplayController.UserStatus;
using OUAKModAPI;

namespace OUAKModMenu
{
    internal enum CpuMode { Normal, Easy, Hard, Idle }

    internal sealed class MatchFreebie
    {
        public Reward Reward;
        /// <summary>What a multiplayer item box hands out for this freebie, or NoItem if only the King Box gives it.</summary>
        public eInstageItemType BoxItem;
        public string Name;
    }

    /// <summary>
    /// Custom rules for offline matches: the game's offline training / Sports Katamari, which always runs the
    /// Big Battle rule against three CPU princes. Nothing here touches online play.
    /// - Time limit: MainGameManager.Update ends the round when MultiplayGameManager.playTime reaches
    ///   MainGameManager._startTime (MultiPlayCountTimer shows _startTime - playTime), so _startTime is the limit.
    /// - Freebies: item boxes pick from MainGameItemBox.GetRandomItemMulti (returns eInstageItemType - 1, or -1 for
    ///   nothing); King Boxes go through MainGameCore.GetKingBox and MultiplayGameManager.StartKingBoxProcess.
    /// - CPUs: MultiplayCPUBigBattle.Update skips everything (moving and its catch-up growth) while its AI wait
    ///   time is above zero; the catch-up growth rate is MultiplaySetting.dicBattleCpuAddCoreSize[map].
    /// </summary>
    internal static class CustomMatch
    {
        public static readonly GI_MI[] Maps =
        {
            GI_MI.GI_MI_ONLINE_TRAINING, GI_MI.GI_MI_ONLINE_BATTLE1, GI_MI.GI_MI_ONLINE_EDO, GI_MI.GI_MI_ONLINE_GOLD,
            GI_MI.GI_MI_ONLINE_SKULL, GI_MI.GI_MI_ONLINE_PYRAMID, GI_MI.GI_MI_ONLINE_CAVE,
        };

        /// <summary>Seconds; 0 = the game's own limit, -1 = no limit.</summary>
        public static readonly int[] TimeChoices = { 0, 60, 120, 180, 240, 300, 420, 600, 900, -1 };

        public static readonly MatchFreebie[] Freebies =
        {
            new() { Reward = Reward.basin, BoxItem = eInstageItemType.Tarai, Name = "Basin" },
            new() { Reward = Reward.spiderThread, BoxItem = eInstageItemType.Spider, Name = "Spider Thread" },
            new() { Reward = Reward.bubble, BoxItem = eInstageItemType.Bubble, Name = "Bubble" },
            new() { Reward = Reward.mushroom, BoxItem = eInstageItemType.Mushroom, Name = "Mushroom" },
            new() { Reward = Reward.hand, BoxItem = eInstageItemType.Hand, Name = "Hand" },
            new() { Reward = Reward.shoes, BoxItem = eInstageItemType.Shoes, Name = "Shoes" },
            new() { Reward = Reward.crown, BoxItem = eInstageItemType.Crown, Name = "Crown" },
            new() { Reward = Reward.basinPlus, BoxItem = eInstageItemType.SP_Tarai, Name = "Basin+" },
            new() { Reward = Reward.spiderThreadPlus, BoxItem = eInstageItemType.SP_Spider, Name = "Spider Thread+" },
            new() { Reward = Reward.bubblePlus, BoxItem = eInstageItemType.SP_Bubble, Name = "Bubble+" },
            new() { Reward = Reward.mushroomPlus, BoxItem = eInstageItemType.SP_Mushroom, Name = "Mushroom+" },
            new() { Reward = Reward.crownPlus, BoxItem = eInstageItemType.SP_Crown, Name = "Crown+" },
            new() { Reward = Reward.brother, BoxItem = eInstageItemType.NoItem, Name = "Brother" },
            new() { Reward = Reward.sunglass, BoxItem = eInstageItemType.NoItem, Name = "Sunglasses" },
            new() { Reward = Reward.drink, BoxItem = eInstageItemType.NoItem, Name = "Drink" },
            new() { Reward = Reward.magnet, BoxItem = eInstageItemType.NoItem, Name = "Magnet" },
            new() { Reward = Reward.magnetPlus, BoxItem = eInstageItemType.NoItem, Name = "Magnet+" },
            new() { Reward = Reward.rocket, BoxItem = eInstageItemType.NoItem, Name = "Rocket" },
            new() { Reward = Reward.rocketPlus, BoxItem = eInstageItemType.NoItem, Name = "Rocket+" },
        };

        private const float NoLimitSeconds = 99999f;

        public static bool Enabled;
        public static bool FreebiesOn = true;
        public static int MapIndex;
        public static int TimeIndex;
        public static CpuMode Cpu;
        public static readonly HashSet<Reward> Disabled = new();

        public static bool PatchActive { get; private set; }
        public static float GameDefaultTime { get; private set; } = -1f;

        private static MelonPreferences_Entry<bool> _enabledPref, _freebiesPref;
        private static MelonPreferences_Entry<string> _mapPref, _cpuPref, _disabledPref;
        private static MelonPreferences_Entry<int> _timePref;

        // Per-match state, reset when a new MainGameManager shows up.
        private static IntPtr _matchGm;
        private static bool _idleLogged;
        private static float _timeSet = -1f;
        private static readonly Dictionary<GI_MI, float> CpuGrowthBackup = new();

        public static void Init(MelonPreferences_Category cat, HarmonyLib.Harmony harmony)
        {
            _enabledPref = cat.CreateEntry("CustomRules", false, "Custom match rules", "Apply the Match tab's rules to offline matches (training / Sports Katamari).");
            _mapPref = cat.CreateEntry("Map", Maps[0].ToString(), "Map", "GI_MI name of the map the Start button uses.");
            _timePref = cat.CreateEntry("TimeLimit", 0, "Time limit", "Seconds. 0 = the game's own limit, -1 = no limit.");
            _cpuPref = cat.CreateEntry("Cpu", CpuMode.Normal.ToString(), "CPU princes", "Normal, Easy, Hard or Idle.");
            _freebiesPref = cat.CreateEntry("Freebies", true, "Freebies", "false = no item boxes or King Box freebies at all.");
            _disabledPref = cat.CreateEntry("DisabledFreebies", "", "Disabled freebies", "Comma-separated KingBoxRewardType names, e.g. rocket,rocketPlus.");

            Enabled = _enabledPref.Value;
            FreebiesOn = _freebiesPref.Value;
            MapIndex = Math.Max(0, Array.FindIndex(Maps, m => m.ToString() == _mapPref.Value));
            TimeIndex = Math.Max(0, Array.IndexOf(TimeChoices, _timePref.Value));
            Cpu = Enum.TryParse(_cpuPref.Value, out CpuMode cpu) ? cpu : CpuMode.Normal;
            foreach (var name in _disabledPref.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (Enum.TryParse(name, out Reward r)) Disabled.Add(r);

            try
            {
                harmony.Patch(AccessTools.Method(typeof(MainGameItemBox), nameof(MainGameItemBox.GetRandomItemMulti)),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(CustomMatch), nameof(AfterRandomItemMulti))));
                harmony.Patch(AccessTools.Method(typeof(MultiplayBigBattle), nameof(MultiplayBigBattle.MultiplayGetKingBox)),
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(CustomMatch), nameof(BeforeMultiplayGetKingBox))),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(CustomMatch), nameof(AfterMultiplayGetKingBox))));
                harmony.Patch(AccessTools.Method(typeof(MainGameCore), nameof(MainGameCore.GetKingBox)),
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(CustomMatch), nameof(BeforeGetKingBox))));
                harmony.Patch(AccessTools.Method(typeof(MultiplayGameManager), nameof(MultiplayGameManager.StartKingBoxProcess)),
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(CustomMatch), nameof(BeforeStartKingBoxProcess))));
                PatchActive = true;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"Custom match freebie patches failed: {e.Message}");
            }
        }

        /// <summary>Test launches only: rules for this launch, kept in memory (SavePrefs isn't called).</summary>
        public static void UseTestRules(string which)
        {
            if (which == "none")
            {
                Enabled = false;
                MelonLogger.Msg("[TrainingTest] custom rules off for this launch");
                return;
            }
            Enabled = true;
            TimeIndex = 0;
            Cpu = CpuMode.Normal;
            Disabled.Clear();
            FreebiesOn = which != "off";
            // King Boxes draw from MultiplaySetting.kingBoxItemList = brother, basin, mushroom, magnet, rocket and the
            // Plus versions (measured), so "some" keeps two of those plus Crown (item boxes only).
            if (which == "some")
                foreach (var f in Freebies)
                    if (f.Reward != Reward.basin && f.Reward != Reward.rocket && f.Reward != Reward.crown) Disabled.Add(f.Reward);
            MelonLogger.Msg($"[TrainingTest] custom rules for this launch: freebies {(FreebiesOn ? "on" : "off")}, allowed: " +
                            string.Join(", ", Freebies.Where(IsOn).Select(f => f.Name)));
        }

        public static void SavePrefs()
        {
            _enabledPref.Value = Enabled;
            _freebiesPref.Value = FreebiesOn;
            _mapPref.Value = Maps[MapIndex].ToString();
            _timePref.Value = TimeChoices[TimeIndex];
            _cpuPref.Value = Cpu.ToString();
            _disabledPref.Value = string.Join(",", Disabled.Select(r => r.ToString()));
            MelonPreferences.Save();
        }

        // ---------------------------------------------------------------- menu helpers

        public static GI_MI Map => Maps[MapIndex];
        public static int TimeLimit => TimeChoices[TimeIndex];

        public static string MapName(GI_MI map)
        {
            var entry = EntryFor(map);
            return entry != null ? entry.Name : map.ToString().Replace("GI_MI_ONLINE_", "");
        }

        public static string TimeName(int seconds) => seconds switch
        {
            0 => "Game default",
            < 0 => "No limit",
            _ => $"{seconds / 60}:{seconds % 60:00}",
        };

        public static string CpuName(CpuMode mode) => mode switch
        {
            CpuMode.Easy => "Easy (half catch-up growth)",
            CpuMode.Hard => "Hard (double catch-up growth)",
            CpuMode.Idle => "Idle (they stand still)",
            _ => "Normal",
        };

        public static bool IsOn(MatchFreebie f) => FreebiesOn && !Disabled.Contains(f.Reward);
        public static int OnCount => FreebiesOn ? Freebies.Count(f => !Disabled.Contains(f.Reward)) : 0;

        public static void Toggle(MatchFreebie f)
        {
            if (!Disabled.Remove(f.Reward)) Disabled.Add(f.Reward);
            SavePrefs();
        }

        public static void SetAll(bool on)
        {
            Disabled.Clear();
            if (!on) foreach (var f in Freebies) Disabled.Add(f.Reward);
            SavePrefs();
        }

        public static void Cycle(ref int index, int count, int dir) => index = ((index + dir) % count + count) % count;

        /// <summary>Starts an offline match on the chosen map (the same training start the Levels tab uses).</summary>
        public static string Start()
        {
            var entry = EntryFor(Map) ?? throw new InvalidOperationException($"{Map} isn't in the map list.");
            if (!Enabled)
            {
                Enabled = true;
                SavePrefs();
            }
            return SceneLoader.Load(entry);
        }

        private static MapEntry EntryFor(GI_MI map) =>
            StageCatalog.Entries.FirstOrDefault(e => e.Kind == MapKind.Multiplayer && (int)e.Stage - 1 == (int)map);

        // ---------------------------------------------------------------- live match

        public static MultiplayGameManager Mgm => GameRefs.Multiplay;

        /// <summary>True while an offline Big Battle match is running and custom rules are on.</summary>
        public static bool Applies
        {
            get
            {
                if (!Enabled || !GameRefs.IsMultiplay) return false;
                var m = Mgm;
                return m != null && m.isSinglePlay && m.nowRule == NetworkBattleRule.bigBattle;
            }
        }

        public static void Tick()
        {
            var gm = GameRefs.Main;
            bool applies = gm != null && Applies;
            if (!applies)
            {
                if (_matchGm != IntPtr.Zero)
                {
                    // Rules switched off mid-match: hand the clock back.
                    if (gm != null && gm.Pointer == _matchGm) RestoreTime(gm, Mgm);
                    EndMatch();
                }
                return;
            }

            var mgm = Mgm;
            if (gm.Pointer != _matchGm)
            {
                EndMatch();
                _matchGm = gm.Pointer;
                GameDefaultTime = -1f;
                _timeSet = -1f;
            }
            if (GameDefaultTime < 0f && gm._startTime > 0f) GameDefaultTime = gm._startTime;

            ApplyTime(gm, mgm);
            ApplyCpu(mgm);
        }

        /// <summary>
        /// Only moves the limit while it still holds the game's own value or the one set here, so anything else
        /// that sets the clock (the training self-test cutting a round short, a later cheat) is left alone.
        /// </summary>
        private static void ApplyTime(MainGameManager gm, MultiplayGameManager mgm)
        {
            int limit = TimeLimit;
            if (limit == 0)
            {
                RestoreTime(gm, mgm);
                return;
            }
            float want = limit < 0 ? NoLimitSeconds : limit;
            float now = gm._startTime;
            if (Mathf.Approximately(now, want)) { _timeSet = want; return; }
            bool ours = Mathf.Approximately(now, GameDefaultTime) || (_timeSet > 0f && Mathf.Approximately(now, _timeSet));
            // Never bring a round that has already run out back to life.
            if (!ours || mgm.playTime >= Mathf.Min(now, want)) return;
            gm._startTime = want;
            _timeSet = want;
            MelonLogger.Msg($"Custom match: time limit {TimeName(limit)} (game default {GameDefaultTime:0} s).");
        }

        /// <summary>Puts the game's own limit back if the clock still holds the one set here.</summary>
        private static void RestoreTime(MainGameManager gm, MultiplayGameManager mgm)
        {
            if (_timeSet > 0f && GameDefaultTime > 0f && mgm != null && Mathf.Approximately(gm._startTime, _timeSet)
                && mgm.playTime < GameDefaultTime)
                gm._startTime = GameDefaultTime;
            _timeSet = -1f;
        }

        private static void ApplyCpu(MultiplayGameManager mgm)
        {
            float scale = Cpu switch { CpuMode.Easy => 0.5f, CpuMode.Hard => 2f, _ => 1f };
            SetCpuGrowth(mgm, scale);
            if (Cpu != CpuMode.Idle) return;

            var cores = mgm.dicPlayerCore;
            if (cores == null) return;
            int idled = 0;
            var it = cores.GetEnumerator();
            while (it.MoveNext())
            {
                var core = it.Current.Value;
                if (core == null || !core.isMultiCpu || core.multiplayCPU == null) continue;
                core.multiplayCPU.SetAIWaitTime(0.5f);
                idled++;
            }
            if (idled > 0 && !_idleLogged)
            {
                _idleLogged = true;
                MelonLogger.Msg($"Custom match: {idled} CPU prince(s) idle.");
            }
        }

        private static void SetCpuGrowth(MultiplayGameManager mgm, float scale)
        {
            var table = MultiplaySetting.dicBattleCpuAddCoreSize;
            if (table == null) return;
            GI_MI map = mgm.gimiSingleStage;
            if (!table.ContainsKey(map)) return;
            if (!CpuGrowthBackup.TryGetValue(map, out float original))
            {
                original = table[map];
                CpuGrowthBackup[map] = original;
            }
            float want = original * scale;
            if (Mathf.Approximately(table[map], want)) return;
            table[map] = want;
            MelonLogger.Msg($"Custom match: CPU catch-up growth on {map}: {original:0.###} -> {want:0.###}.");
        }

        private static void EndMatch()
        {
            _matchGm = IntPtr.Zero;
            _idleLogged = false;
            RestoreCpuGrowth();
        }

        private static void RestoreCpuGrowth()
        {
            if (CpuGrowthBackup.Count == 0) return;
            try
            {
                var table = MultiplaySetting.dicBattleCpuAddCoreSize;
                if (table != null)
                    foreach (var kv in CpuGrowthBackup) table[kv.Key] = kv.Value;
            }
            catch (Exception e) { MelonLogger.Warning($"Restoring CPU growth failed: {e.Message}"); }
            CpuGrowthBackup.Clear();
        }

        // ---------------------------------------------------------------- freebie filter (Harmony)

        private static List<eInstageItemType> BoxPool() =>
            Freebies.Where(f => f.BoxItem != eInstageItemType.NoItem && IsOn(f)).Select(f => f.BoxItem).ToList();

        private static List<Reward> KingBoxPool()
        {
            var pool = new List<Reward>();
            var list = MultiplaySetting.kingBoxItemList;
            if (list == null) return pool;
            for (int i = 0; i < list.Length; i++)
            {
                var f = Freebies.FirstOrDefault(x => x.Reward == list[i]);
                if (f != null && IsOn(f) && !pool.Contains(f.Reward)) pool.Add(f.Reward);
            }
            return pool;
        }

        private static void AfterRandomItemMulti(ref int __result)
        {
            try
            {
                if (__result < 0 || !Applies) return;
                var item = (eInstageItemType)(__result + 1);
                var f = Freebies.FirstOrDefault(x => x.BoxItem == item);
                if (f == null || IsOn(f)) return;
                var pool = BoxPool();
                __result = pool.Count == 0 ? -1 : (int)pool[UnityEngine.Random.Range(0, pool.Count)] - 1;
                MelonLogger.Msg($"Custom match: item box {item} -> {(__result < 0 ? "nothing" : ((eInstageItemType)(__result + 1)).ToString())}.");
            }
            catch (Exception e) { MelonLogger.Warning($"Item box filter: {e.Message}"); }
        }

        /// <summary>King Boxes opened with every King Box freebie off (they gave nothing).</summary>
        public static int EmptyKingBoxes { get; private set; }

        /// <summary>True while custom rules apply and none of the King Box rewards is allowed.</summary>
        public static bool NoKingBoxRewards => Applies && KingBoxPool().Count == 0;

        /// <summary>True while custom rules apply and item boxes can hand out nothing.</summary>
        public static bool NoBoxFreebies => Applies && BoxPool().Count == 0;

        // The MultiplayGetKingBox call in progress: its user's buddy count before it ran, and the count to leave when a
        // remap swapped a buddy in or out (-1 = leave the game's count).
        private static UserStatus _kbUser;
        private static int _kbBrothersBefore, _kbBrothersWant = -1;

        /// <summary>
        /// Rolling up a King Box (MultiplayBigBattle.GetTreasureProcess) first takes the box off the map (GetTreasure +
        /// network event 36), then calls MultiplayGetKingBox, which is only the reward: it draws one by the player's
        /// rank and the phase of the round, counts a buddy (UserStatus.getBrotherNum, a results stat), gives it
        /// (MainGameCore.GetKingBox: the held item, or a buddy; the player's own reward also queues the King Box
        /// display, StartKingBoxProcess) and sends event 8. With no King Box freebie allowed, skipping it leaves the
        /// box taken and nothing given.
        /// </summary>
        private static bool BeforeMultiplayGetKingBox(MainGameCore core)
        {
            _kbUser = null;
            _kbBrothersWant = -1;
            try
            {
                if (!Applies) return true;
                if (KingBoxPool().Count == 0)
                {
                    EmptyKingBoxes++;
                    bool mine = core != null && core.Pointer == (GameRefs.Main?.GetCore()?.Pointer ?? IntPtr.Zero);
                    MelonLogger.Msg($"Custom match: King Box opened by {(mine ? "you" : "a CPU prince")}: no reward (every King Box freebie is off).");
                    if (mine) ModTasks.Toast("King Box: empty (freebies are off in this match)");
                    return false;
                }
                var mgm = Mgm;
                if (core != null && mgm != null)
                {
                    _kbUser = mgm.GetUserStatusFromCoreNo(core.multiCoreNo);
                    _kbBrothersBefore = _kbUser?.getBrotherNum ?? 0;
                }
            }
            catch (Exception e) { MelonLogger.Warning($"King Box filter: {e.Message}"); }
            return true;
        }

        /// <summary>Keeps the results' buddy count true to what was given when a remap swapped a buddy in or out.</summary>
        private static void AfterMultiplayGetKingBox()
        {
            try
            {
                if (_kbUser != null && _kbBrothersWant >= 0) _kbUser.getBrotherNum = _kbBrothersWant;
            }
            catch (Exception e) { MelonLogger.Warning($"King Box filter: {e.Message}"); }
            _kbUser = null;
            _kbBrothersWant = -1;
        }

        private static bool BeforeGetKingBox(ref Reward type, ref int num)
        {
            if (type != Reward.none && NoKingBoxRewards) return false; // a path other than MultiplayGetKingBox (network event, debug)
            var was = type;
            type = Remap(type);
            if (type != was && _kbUser != null)
            {
                // MultiplayGetKingBox counts a buddy (before this call) only when it drew one, and draws buddy #num
                // (0-39) for it; a buddy swapped in gets a random one the same way.
                if (was == Reward.brother) _kbBrothersWant = _kbBrothersBefore;
                if (type == Reward.brother)
                {
                    _kbBrothersWant = _kbBrothersBefore + 1;
                    if (num == 0) num = UnityEngine.Random.Range(0, 40);
                }
            }
            return true;
        }

        private static bool BeforeStartKingBoxProcess(ref Reward getType)
        {
            if (getType != Reward.none && NoKingBoxRewards) return false;
            getType = Remap(getType);
            return true;
        }

        /// <summary>
        /// A disabled King Box reward becomes one of the enabled King Box rewards, picked by its type number. The
        /// choice is deterministic so GetKingBox and the reward display (StartKingBoxProcess) always agree.
        /// </summary>
        private static Reward Remap(Reward type)
        {
            try
            {
                if (type == Reward.none || !Applies) return type;
                var f = Freebies.FirstOrDefault(x => x.Reward == type);
                if (f == null || IsOn(f)) return type;
                var pool = KingBoxPool();
                if (pool.Count == 0) return type;
                var swapped = pool[((int)type % pool.Count + pool.Count) % pool.Count];
                MelonLogger.Msg($"Custom match: King Box {type} -> {swapped}.");
                return swapped;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"King Box filter: {e.Message}");
                return type;
            }
        }
    }
}
