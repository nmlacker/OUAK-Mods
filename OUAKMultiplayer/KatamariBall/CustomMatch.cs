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

namespace OUAKMultiplayer
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
    /// - Freebies: what King Boxes and item boxes may give, as two pools handed to the API (BattleItemPools) while the
    ///   rules apply. The old per-freebie list (DisabledFreebies, the MatchFreebie table) is only read to make the pools
    ///   the first time.
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

            _kingPoolPref = cat.CreateEntry("KingBoxPool", "", "King Box rewards",
                "Comma-separated KingBoxRewardType names King Boxes may give in custom matches; none = nothing; empty = taken from DisabledFreebies.");
            _kingFreebiePref = cat.CreateEntry("KingBoxFreebies", "", "King Box freebies",
                "Comma-separated freebies (eInstageItemType) King Boxes may also give in custom matches: Smoke, Hoop, Dice, SP_Dice; empty or none = none.");
            _itemPoolPref = cat.CreateEntry("ItemBoxPool", "", "Item box freebies",
                "Comma-separated eInstageItemType names item boxes may give in custom matches; none = nothing; empty = taken from DisabledFreebies.");
            LoadPools();
            // King Boxes and item boxes are the API's (BattleItemPools): no hooks of this mod's own.
            PatchActive = true;
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
            FreebiesOn = which != "off";
            KingBoxPool.Clear();
            KingBoxFreebiePool.Clear();
            ItemBoxPool.Clear();
            if (which == "kbfree")
            {
                // King Boxes give only the freebies with no reward type (Smoke, Hoop, Dice, Special Dice); item boxes the game's own.
                foreach (var t in BattleItemPools.KingBoxFreebies) KingBoxFreebiePool.Add(t);
                foreach (var t in BattleItemPools.GameItemBoxItems) ItemBoxPool.Add(t);
            }
            else if (which == "some")
            {
                // A swap in each pool: King Boxes only Washtub or Rocket, item boxes only Crown.
                KingBoxPool.Add(Reward.basin);
                KingBoxPool.Add(Reward.rocket);
                ItemBoxPool.Add(eInstageItemType.Crown);
            }
            else
            {
                foreach (var r in GameKingBox()) KingBoxPool.Add(r);
                foreach (var t in BattleItemPools.GameItemBoxItems) ItemBoxPool.Add(t);
            }
            MelonLogger.Msg($"[TrainingTest] custom rules for this launch: freebies {(FreebiesOn ? "on" : "off")}, King Boxes " +
                            string.Join(", ", KingBoxPool.Select(RewardName).Concat(KingBoxFreebiePool.Select(ItemName))) +
                            "; item boxes " + string.Join(", ", ItemBoxPool.Select(ItemName)));
        }

        public static void SavePrefs()
        {
            _enabledPref.Value = Enabled;
            _freebiesPref.Value = FreebiesOn;
            _mapPref.Value = Maps[MapIndex].ToString();
            _timePref.Value = TimeChoices[TimeIndex];
            _cpuPref.Value = Cpu.ToString();
            _disabledPref.Value = string.Join(",", Disabled.Select(r => r.ToString()));
            SavePools();
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
            ApplyPools();
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

        // ---------------------------------------------------------------- freebies: the API's battle item pools

        /// <summary>What King Boxes may give in a custom match (the King's chests). Empty: nothing.</summary>
        public static readonly HashSet<Reward> KingBoxPool = new();
        /// <summary>
        /// Freebies King Boxes may also give in a custom match: the ones with no King Box reward type (the API's
        /// BattleItemPools.KingBoxFreebies: Smoke, Hoop, Dice, Special Dice). They go to your freebie slot with the
        /// roulette; a CPU prince gets one of the pool's rewards instead (or nothing).
        /// </summary>
        public static readonly HashSet<eInstageItemType> KingBoxFreebiePool = new();
        /// <summary>What item boxes may give in a custom match. Empty: nothing.</summary>
        public static readonly HashSet<eInstageItemType> ItemBoxPool = new();

        private static MelonPreferences_Entry<string> _kingPoolPref, _itemPoolPref, _kingFreebiePref;

        /// <summary>Everything a King Box can be set to give: the rewards and the freebies with no reward type.</summary>
        public static int KingBoxTotal => BattleItemPools.KingBoxRewards.Length + BattleItemPools.KingBoxFreebies.Length;
        public static int KingBoxCount => KingBoxPool.Count + KingBoxFreebiePool.Count;
        private static bool _poolsSet;
        private static int _poolsHash;

        /// <summary>A King Box reward's name, in the game's words where it has them.</summary>
        public static string RewardName(Reward r) => r switch
        {
            Reward.brother => "Buddy", Reward.basin => "Washtub", Reward.spiderThread => "Spiderweb", Reward.bubble => "Bubble",
            Reward.mushroom => "Mushroom", Reward.hand => "Hand", Reward.shoes => "Shoes", Reward.crown => "Crown",
            Reward.basinPlus => "Special Washtub", Reward.spiderThreadPlus => "Special Spiderweb", Reward.bubblePlus => "Special Bubble",
            Reward.mushroomPlus => "Special Mushroom", Reward.crownPlus => "Special Crown", Reward.magnet => "Magnet",
            Reward.magnetPlus => "Special Magnet", Reward.rocket => "Rocket", Reward.rocketPlus => "Special Rocket",
            _ => r.ToString(),
        };

        public static string ItemName(eInstageItemType i) => StageControl.Pretty(i);

        /// <summary>The game's own King Box rewards (falls back to the measured list before the game has its table).</summary>
        private static Reward[] GameKingBox()
        {
            try
            {
                var g = BattleItemPools.GameKingBoxRewards;
                if (g.Length > 0) return g;
            }
            catch { }
            return new[] { Reward.brother, Reward.basin, Reward.mushroom, Reward.magnet, Reward.rocket,
                           Reward.basinPlus, Reward.mushroomPlus, Reward.magnetPlus, Reward.rocketPlus };
        }

        /// <summary>
        /// Loads both pools. The first time (no pool saved yet) they come from the old per-freebie list: the game's own
        /// King Box rewards and item box freebies minus the ones switched off there.
        /// </summary>
        private static void LoadPools()
        {
            KingBoxPool.Clear();
            KingBoxFreebiePool.Clear();
            ItemBoxPool.Clear();
            foreach (var n in (_kingFreebiePref.Value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (Enum.TryParse(n, out eInstageItemType f) && Array.IndexOf(BattleItemPools.KingBoxFreebies, f) >= 0) KingBoxFreebiePool.Add(f);
            string k = _kingPoolPref.Value ?? "", i = _itemPoolPref.Value ?? "";
            if (k.Length > 0)
            {
                foreach (var n in k.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (Enum.TryParse(n, out Reward r) && Array.IndexOf(BattleItemPools.KingBoxRewards, r) >= 0) KingBoxPool.Add(r);
            }
            else
                foreach (var r in GameKingBox()) if (!Disabled.Contains(r)) KingBoxPool.Add(r);
            if (i.Length > 0)
            {
                foreach (var n in i.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (Enum.TryParse(n, out eInstageItemType t) && Array.IndexOf(BattleItemPools.ItemBoxItems, t) >= 0) ItemBoxPool.Add(t);
            }
            else
                foreach (var t in BattleItemPools.GameItemBoxItems)
                {
                    var f = Freebies.FirstOrDefault(x => x.BoxItem == t);
                    if (f == null || !Disabled.Contains(f.Reward)) ItemBoxPool.Add(t);
                }
        }

        private static string PoolText(IEnumerable<string> names)
        {
            var list = names.ToList();
            return list.Count == 0 ? "none" : string.Join(",", list);
        }

        private static void SavePools()
        {
            if (_kingPoolPref == null) return;
            _kingPoolPref.Value = PoolText(KingBoxPool.OrderBy(r => (int)r).Select(r => r.ToString()));
            _itemPoolPref.Value = PoolText(ItemBoxPool.OrderBy(t => (int)t).Select(t => t.ToString()));
            _kingFreebiePref.Value = PoolText(KingBoxFreebiePool.OrderBy(t => (int)t).Select(t => t.ToString()));
        }

        public static bool InKingBoxPool(Reward r) => KingBoxPool.Contains(r);
        public static bool InItemBoxPool(eInstageItemType t) => ItemBoxPool.Contains(t);
        public static bool InKingBoxFreebies(eInstageItemType t) => KingBoxFreebiePool.Contains(t);

        public static void ToggleKingBoxFreebie(eInstageItemType t)
        {
            if (!KingBoxFreebiePool.Remove(t)) KingBoxFreebiePool.Add(t);
            SavePrefs();
        }

        public static void ToggleKingBox(Reward r)
        {
            if (!KingBoxPool.Remove(r)) KingBoxPool.Add(r);
            SavePrefs();
        }

        public static void ToggleItemBox(eInstageItemType t)
        {
            if (!ItemBoxPool.Remove(t)) ItemBoxPool.Add(t);
            SavePrefs();
        }

        /// <summary>0 = the game's own (no freebies: the game's King Boxes never give them), 1 = everything, 2 = nothing.</summary>
        public static void SetKingBoxPool(int which)
        {
            KingBoxPool.Clear();
            KingBoxFreebiePool.Clear();
            foreach (var r in which == 0 ? GameKingBox() : which == 1 ? BattleItemPools.KingBoxRewards : Array.Empty<Reward>()) KingBoxPool.Add(r);
            if (which == 1) foreach (var t in BattleItemPools.KingBoxFreebies) KingBoxFreebiePool.Add(t);
            SavePrefs();
        }

        public static void SetItemBoxPool(int which)
        {
            ItemBoxPool.Clear();
            foreach (var t in which == 0 ? BattleItemPools.GameItemBoxItems : which == 1 ? BattleItemPools.ItemBoxItems : Array.Empty<eInstageItemType>()) ItemBoxPool.Add(t);
            SavePrefs();
        }

        /// <summary>True while custom rules apply and King Boxes give nothing.</summary>
        public static bool NoKingBoxRewards => Applies && (!FreebiesOn || KingBoxCount == 0);

        /// <summary>True while custom rules apply and item boxes give nothing.</summary>
        public static bool NoBoxFreebies => Applies && (!FreebiesOn || ItemBoxPool.Count == 0);

        /// <summary>
        /// The pools go to the API (BattleItemPools) while custom rules apply to an offline match; otherwise the game's
        /// own draw. The API swaps a reward or freebie outside the pool for one inside it, gives nothing for an empty
        /// pool (the box is still taken) and keeps the results' buddy count true.
        /// </summary>
        private static void ApplyPools()
        {
            if (!Applies)
            {
                if (_poolsSet)
                {
                    BattleItemPools.Reset();
                    _poolsSet = false;
                    _poolsHash = 0;
                }
                return;
            }
            var king = FreebiesOn ? KingBoxPool.ToArray() : Array.Empty<Reward>();
            var kingFreebies = FreebiesOn ? KingBoxFreebiePool.ToArray() : Array.Empty<eInstageItemType>();
            var items = FreebiesOn ? ItemBoxPool.ToArray() : Array.Empty<eInstageItemType>();
            int hash = HashCode.Combine(FreebiesOn, string.Join(",", king.OrderBy(r => (int)r)), string.Join(",", kingFreebies.OrderBy(t => (int)t)),
                                        string.Join(",", items.OrderBy(t => (int)t)));
            if (_poolsSet && hash == _poolsHash) return;
            BattleItemPools.SetKingBox(king, kingFreebies);
            BattleItemPools.SetItemBox(items);
            _poolsSet = true;
            _poolsHash = hash;
            var kingNames = king.Select(RewardName).Concat(kingFreebies.Select(ItemName)).ToList();
            MelonLogger.Msg($"Custom match: King Boxes give {(kingNames.Count == 0 ? "nothing" : string.Join(", ", kingNames))}; " +
                            $"item boxes give {(items.Length == 0 ? "nothing" : string.Join(", ", items.Select(ItemName)))}.");
        }
    }
}
