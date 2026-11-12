using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Il2CppApp.KatamariSin;
using Reward = Il2CppApp.KatamariSin.MultiplaySetting.KingBoxRewardType;
using UserStatus = Il2CppApp.KatamariSin.MultiplayController.UserStatus;

namespace OUAKModAPI
{
    /// <summary>
    /// What King Boxes (the King's chests) and item boxes (the freebie boxes) may give in battles
    /// (<see cref="GamePatches.BattleItemPools"/>, part of the API). By default both are the game's own draw; a mod
    /// changes them with <see cref="SetKingBox"/> / <see cref="SetItemBox"/> and puts them back with <see cref="Reset"/>.
    /// <para>King Boxes: rolling one up takes it off the map (<c>MultiplayBigBattle.GetTreasureProcess</c>), then
    /// <c>MultiplayGetKingBox</c> draws a reward from the game's list (<see cref="GameKingBoxRewards"/>: Buddy, Washtub,
    /// Mushroom, Magnet, Rocket and Specials, weighted by the phase of the round and your rank) and gives it
    /// (<c>MainGameCore.GetKingBox</c>, which also queues the reward display). With a pool set, a reward outside it is
    /// swapped for a random one of the pool, which may hold anything the battle code can use
    /// (<see cref="KingBoxRewards"/>, e.g. Hand, Spiderweb, Bubble, Shoes, Crown); an empty pool gives nothing (the box is
    /// still taken). The results' buddy count follows what was really given.</para>
    /// <para>Item boxes: <c>MainGameItemBox.GetRandomItemMulti</c> draws from Washtub, Spiderweb, Bubble, Mushroom, Hand,
    /// Shoes, Crown and their Specials (<see cref="GameItemBoxItems"/>). With a pool set, a draw outside it is swapped for a
    /// random freebie of the pool (any of <see cref="ItemBoxItems"/>); an empty pool gives nothing.</para>
    /// <para>Smoke, Hoop and both Dice have no battle code of the game's own; <see cref="UnusedFreebies"/> gives them one, so
    /// item boxes may hand them out, and King Boxes too (<see cref="KingBoxFreebies"/>: they have no reward type, so they go
    /// to your slot with the roulette). Sunglasses stay out.</para>
    /// </summary>
    public static class BattleItemPools
    {
        /// <summary>Every King Box reward the battle code can use.</summary>
        public static readonly Reward[] KingBoxRewards =
        {
            Reward.brother, Reward.basin, Reward.spiderThread, Reward.bubble, Reward.mushroom, Reward.hand, Reward.shoes, Reward.crown,
            Reward.basinPlus, Reward.spiderThreadPlus, Reward.bubblePlus, Reward.mushroomPlus, Reward.crownPlus,
            Reward.magnet, Reward.magnetPlus, Reward.rocket, Reward.rocketPlus,
        };

        /// <summary>Every freebie an item box can hand out in battles (the ones with battle code, plus Smoke, Hoop and both Dice through <see cref="UnusedFreebies"/>).</summary>
        public static readonly eInstageItemType[] ItemBoxItems =
        {
            eInstageItemType.Tarai, eInstageItemType.Spider, eInstageItemType.Bubble, eInstageItemType.Mushroom, eInstageItemType.Hand,
            eInstageItemType.Shoes, eInstageItemType.Crown, eInstageItemType.SP_Tarai, eInstageItemType.SP_Spider, eInstageItemType.SP_Bubble,
            eInstageItemType.SP_Mushroom, eInstageItemType.SP_Crown, eInstageItemType.Magnet, eInstageItemType.SP_Magnet,
            eInstageItemType.Drink, eInstageItemType.SP_Drink,
            eInstageItemType.Smoke, eInstageItemType.Hoop, eInstageItemType.Dice, eInstageItemType.SP_Dice,
        };

        /// <summary>The game's own item box draw (read from MainGameItemBox.GetRandomItemMulti).</summary>
        public static readonly eInstageItemType[] GameItemBoxItems =
        {
            eInstageItemType.Tarai, eInstageItemType.Spider, eInstageItemType.Bubble, eInstageItemType.Mushroom, eInstageItemType.Hand,
            eInstageItemType.Shoes, eInstageItemType.Crown, eInstageItemType.SP_Tarai, eInstageItemType.SP_Spider, eInstageItemType.SP_Bubble,
            eInstageItemType.SP_Mushroom, eInstageItemType.SP_Crown,
        };

        /// <summary>The game's own King Box list (MultiplaySetting.kingBoxItemList).</summary>
        public static Reward[] GameKingBoxRewards
        {
            get
            {
                var list = MultiplaySetting.kingBoxItemList;
                if (list == null) return Array.Empty<Reward>();
                var r = new Reward[list.Length];
                for (int i = 0; i < list.Length; i++) r[i] = list[i];
                return r.Distinct().ToArray();
            }
        }

        /// <summary>
        /// Freebies a King Box can give besides its rewards: the ones with no King Box reward type (Smoke, Hoop, Dice,
        /// Special Dice, made to work by <see cref="UnusedFreebies"/>). They go to your freebie slot with the roulette, as
        /// item boxes give them; CPU princes can't use them, so a CPU gets a reward from the pool instead (or nothing).
        /// </summary>
        public static readonly eInstageItemType[] KingBoxFreebies =
            { eInstageItemType.Smoke, eInstageItemType.Hoop, eInstageItemType.Dice, eInstageItemType.SP_Dice };

        private static HashSet<eInstageItemType> _kingBoxFreebies = new();

        /// <summary>The King Box freebies in the pool (empty when none).</summary>
        public static IReadOnlyCollection<eInstageItemType> KingBoxFreebiePool => _kingBoxFreebies;

        /// <summary>A King Box gave a freebie (no reward type): the prince, the freebie.</summary>
        public static event Action<MainGameCore, eInstageItemType> KingBoxGaveFreebie;
        public static int KingBoxFreebiesGiven { get; private set; }

        private static HashSet<Reward> _kingBox;
        private static HashSet<eInstageItemType> _itemBox;

        /// <summary>The King Box pool, or null for the game's own draw.</summary>
        public static IReadOnlyCollection<Reward> KingBox => _kingBox;

        /// <summary>The item box pool, or null for the game's own draw.</summary>
        public static IReadOnlyCollection<eInstageItemType> ItemBox => _itemBox;

        /// <summary>King Boxes give only these rewards (empty: nothing). Null: the game's own draw. Clears the King Box freebies.</summary>
        public static void SetKingBox(IEnumerable<Reward> allowed) => SetKingBox(allowed, null);

        /// <summary>
        /// King Boxes give only these rewards and freebies (<see cref="KingBoxFreebies"/>); both empty: nothing. Null rewards:
        /// the game's own list (plus the freebies, if any). Every entry is equally likely.
        /// </summary>
        public static void SetKingBox(IEnumerable<Reward> allowed, IEnumerable<eInstageItemType> freebies)
        {
            _kingBoxFreebies = new HashSet<eInstageItemType>((freebies ?? Array.Empty<eInstageItemType>()).Where(i => Array.IndexOf(KingBoxFreebies, i) >= 0));
            _kingBox = allowed == null
                ? (_kingBoxFreebies.Count > 0 ? new HashSet<Reward>(GameKingBoxRewards) : null)
                : new HashSet<Reward>(allowed.Where(r => Array.IndexOf(KingBoxRewards, r) >= 0));
        }

        /// <summary>Item boxes give only these (empty: nothing). Null: the game's own draw.</summary>
        public static void SetItemBox(IEnumerable<eInstageItemType> allowed) =>
            _itemBox = allowed == null ? null : new HashSet<eInstageItemType>(allowed.Where(i => Array.IndexOf(ItemBoxItems, i) >= 0));

        /// <summary>Both back to the game's own draw.</summary>
        public static void Reset() { _kingBox = null; _itemBox = null; _kingBoxFreebies = new HashSet<eInstageItemType>(); }

        /// <summary>A battle is running (offline or online).</summary>
        public static bool InBattle => GameRefs.IsMultiplay && GameRefs.Multiplay != null;

        public static int EmptyKingBoxes { get; private set; }
        public static int KingBoxSwaps { get; private set; }
        public static int ItemBoxSwaps { get; private set; }

        /// <summary>A reward was swapped: the prince, what the game drew, what was given instead.</summary>
        public static event Action<MainGameCore, Reward, Reward> KingBoxSwapped;

        internal static bool Install()
        {
            var h = ApiCore.Instance?.HarmonyInstance ?? throw new InvalidOperationException("the API isn't initialised");
            h.Patch(AccessTools.Method(typeof(MultiplayBigBattle), nameof(MultiplayBigBattle.MultiplayGetKingBox)),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(BattleItemPools), nameof(BeforeMultiplayGetKingBox))),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(BattleItemPools), nameof(AfterMultiplayGetKingBox))));
            h.Patch(AccessTools.Method(typeof(MainGameCore), nameof(MainGameCore.GetKingBox)),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(BattleItemPools), nameof(BeforeGetKingBox))));
            h.Patch(AccessTools.Method(typeof(MainGameItemBox), nameof(MainGameItemBox.GetRandomItemMulti)),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(BattleItemPools), nameof(AfterRandomItemMulti))));
            return true;
        }

        // ------------------------------------------------------------------ King Boxes

        private static MainGameCore _kbCore;
        private static UserStatus _kbUser;
        private static int _kbBrothersBefore, _kbBrothersWant = -1;

        // The reward step of rolling up a King Box. An empty pool skips it: the box is taken, nothing is given.
        private static bool BeforeMultiplayGetKingBox(MainGameCore core)
        {
            _kbCore = null; _kbUser = null; _kbBrothersWant = -1;
            var pool = _kingBox;
            if (pool == null || !InBattle) return true;
            try
            {
                if (pool.Count == 0 && _kingBoxFreebies.Count == 0) { EmptyKingBoxes++; return false; }
                // A freebie (no reward type) instead of a reward, for you only: CPU princes can't use them.
                if (_kingBoxFreebies.Count > 0 && core != null && !core.isMultiCpu && GameRefs.Main != null && core == GameRefs.Main.GetCore()
                    && UnityEngine.Random.Range(0, pool.Count + _kingBoxFreebies.Count) >= pool.Count)
                {
                    var list = _kingBoxFreebies.ToList();
                    var item = list[UnityEngine.Random.Range(0, list.Count)];
                    var gm = GameRefs.Main;
                    if (gm._itemEffectTimer >= 0f) gm.EndItemEffect(true);
                    gm.SetInventoryItem(item, true, false);   // the roulette, as item boxes give freebies
                    KingBoxFreebiesGiven++;
                    try { KingBoxGaveFreebie?.Invoke(core, item); } catch (Exception e) { GameEvents.Report(KingBoxGaveFreebie, e); }
                    return false;
                }
                if (pool.Count == 0) { EmptyKingBoxes++; return false; }
                _kbCore = core;
                _kbUser = core != null ? GameRefs.Multiplay?.GetUserStatusFromCoreNo(core.multiCoreNo) : null;
                _kbBrothersBefore = _kbUser?.getBrotherNum ?? 0;
            }
            catch (Exception e) { ApiCore.Log.Warn($"King Box pool: {e.Message}"); }
            return true;
        }

        // Keeps the results' buddy count true to what was given when a swap took a buddy in or out.
        private static void AfterMultiplayGetKingBox()
        {
            try { if (_kbUser != null && _kbBrothersWant >= 0) _kbUser.getBrotherNum = _kbBrothersWant; }
            catch (Exception e) { ApiCore.Log.Warn($"King Box pool: {e.Message}"); }
            _kbCore = null; _kbUser = null; _kbBrothersWant = -1;
        }

        private static bool BeforeGetKingBox(MainGameCore __instance, ref Reward type, ref int num)
        {
            var pool = _kingBox;
            if (pool == null || type == Reward.none || !InBattle) return true;
            try
            {
                if (pool.Count == 0) return false;   // another path (network event, debug) with nothing allowed
                if (pool.Contains(type)) return true;
                var was = type;
                var list = pool.ToList();
                type = list[UnityEngine.Random.Range(0, list.Count)];
                KingBoxSwaps++;
                if (_kbUser != null)
                {
                    // MultiplayGetKingBox counts a buddy (before this call) only when it drew one, and draws buddy #num
                    // (0-39) for it; a buddy swapped in gets a random one the same way.
                    if (was == Reward.brother) _kbBrothersWant = _kbBrothersBefore;
                    if (type == Reward.brother) { _kbBrothersWant = _kbBrothersBefore + 1; if (num == 0) num = UnityEngine.Random.Range(0, 40); }
                }
                try { KingBoxSwapped?.Invoke(__instance, was, type); } catch (Exception e) { GameEvents.Report(KingBoxSwapped, e); }
            }
            catch (Exception e) { ApiCore.Log.Warn($"King Box pool: {e.Message}"); }
            return true;
        }

        // ------------------------------------------------------------------ item boxes

        // GetRandomItemMulti returns (eInstageItemType - 1), or -1 for nothing.
        private static void AfterRandomItemMulti(ref int __result)
        {
            var pool = _itemBox;
            if (pool == null || __result < 0 || !InBattle) return;
            try
            {
                var item = (eInstageItemType)(__result + 1);
                if (pool.Contains(item)) return;
                var list = pool.ToList();
                __result = list.Count == 0 ? -1 : (int)list[UnityEngine.Random.Range(0, list.Count)] - 1;
                ItemBoxSwaps++;
            }
            catch (Exception e) { ApiCore.Log.Warn($"Item box pool: {e.Message}"); }
        }
    }
}
