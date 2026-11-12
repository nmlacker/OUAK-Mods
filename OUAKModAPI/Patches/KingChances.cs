using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using Il2CppApp.KatamariSin;
using Chance = Il2CppApp.KatamariSin.MultiplaySetting.KingChanceType;
using Reward = Il2CppApp.KatamariSin.MultiplaySetting.KingBoxRewardType;

namespace OUAKModAPI
{
    /// <summary>
    /// King Chances in battles (Sports Katamari). Needs <see cref="GamePatches.KingChances"/> enabled.
    /// <para>What the game has: <c>MultiplayBigBattle.SetupBigBattle</c> draws each match's
    /// <c>kingChanceTable</c> (4 different types out of 6); texts SYS_264..270; a tip saying the King acts
    /// "depending on your time remaining"; and the handler: <c>MultiplayGameManager.KingChanceProcess(type)</c>
    /// sets a request, <c>GameState.Update</c> passes it to <c>SetKingChanceBonus</c> once no King Box reward or
    /// message is on screen, and that queues the message ("King Chance" + the type's text) and does the stage part:
    /// monoDrop shows the stage data's <c>mono/AddMono</c>, itemDrop <c>mono/AddItems</c>, bonusArea hides
    /// <c>KingChance/BonusAreaWall</c>. But nothing ever calls KingChanceProcess (and <c>nowKingChangeNo</c> is
    /// only ever reset), and three types only get their message.</para>
    /// <para>With the patch on: <see cref="Fire"/> / <see cref="FireNext"/> request one the game's way, and those
    /// three get what their texts say, through the game's own helpers:
    /// kingBox "Received a King Chest!" = every prince gets a King Chest reward (<c>MultiplayBigBattle.MultiplayGetKingBox</c>,
    /// the reward step of rolling one up); itemConfiscation "Confiscated a freebie?!" = every prince's held freebie goes
    /// (<c>MultiplaySetItem(core, none)</c>); unexploded "Unlimited dash for a set time!" = every prince gets the game's
    /// unused 60 s unlimited dash (<c>MultiplayInfinityDash</c>; the game's <c>HostGameProcess</c> ends it).
    /// The King Chest choice reads the message text: the game has no code for it.</para>
    /// <para>When to fire them is up to the enabling mod (e.g. at fixed points of the time left, as the tip says).</para>
    /// </summary>
    public static class KingChances
    {
        /// <summary>
        /// The game's "... Nothing happened!" King Chance (message SYS_270 at index 7 of <c>bigBattleKingChanceMessage</c>,
        /// measured; no effect). <see cref="Chance.none"/> (index 0) shows "King Chance" with an empty line instead.
        /// </summary>
        public const Chance Nothing = Chance.max;

        /// <summary>
        /// Changes a type before it's fired, e.g. a mod's rules without freebies turning kingBox / itemDrop into monoDrop
        /// or <see cref="Nothing"/>. Prefer <see cref="AddFilter"/>, which several mods can use side by side; this single
        /// delegate runs after those.
        /// </summary>
        public static Func<Chance, Chance> Filter;

        private static readonly List<(string owner, Func<Chance, Chance> f)> _filters = new();

        /// <summary>
        /// Adds a filter that can change a type before it's fired. Filters run in the order they were added, each
        /// getting the previous one's result. Dispose the returned handle to remove it.
        /// </summary>
        public static IDisposable AddFilter(string owner, Func<Chance, Chance> filter)
        {
            if (filter == null) throw new ArgumentNullException(nameof(filter));
            var entry = (owner ?? "mod", filter);
            _filters.Add(entry);
            return new ModMenu.Remover(() => _filters.Remove(entry));
        }

        /// <summary>The mods that have a filter in (in order).</summary>
        public static IEnumerable<string> FilterOwners => _filters.ConvertAll(f => f.owner);

        /// <summary>A King Chance was requested (the type after <see cref="Filter"/>).</summary>
        public static event Action<Chance> Fired;

        /// <summary>A King Chance happened: the game showed its message and applied it.</summary>
        public static event Action<Chance> Happened;

        public static int FiredCount { get; private set; }
        public static int HappenedCount { get; private set; }

        /// <summary>A battle (Big Battle rule) is running.</summary>
        public static bool InBattle
        {
            get
            {
                if (!GameRefs.IsMultiplay) return false;
                var m = GameRefs.Multiplay;
                return m != null && m.nowRule == NetworkBattleRule.bigBattle && m.multiplayBigBattle != null;
            }
        }

        /// <summary>An offline battle (Sports Katamari / training against CPU princes) is running.</summary>
        public static bool InOfflineMatch => InBattle && GameRefs.Multiplay.isSinglePlay;

        /// <summary>This match's King Chances, in order (empty outside battles).</summary>
        public static Chance[] Table
        {
            get
            {
                var t = GameRefs.Multiplay?.multiplayBigBattle?.kingChanceTable;
                if (t == null) return Array.Empty<Chance>();
                var r = new Chance[t.Length];
                for (int i = 0; i < t.Length; i++) r[i] = t[i];
                return r;
            }
        }

        /// <summary>How many of the table's King Chances have been fired with <see cref="FireNext"/> (the game's nowKingChangeNo).</summary>
        public static int Next => GameRefs.Multiplay?.multiplayBigBattle?.nowKingChangeNo ?? 0;

        /// <summary>True while a requested King Chance hasn't been shown yet.</summary>
        public static bool Pending => GameRefs.Multiplay?.isRequestKingChanceType ?? false;

        /// <summary>
        /// Fires the table's next King Chance (wrapping round after the 4th) and advances <see cref="Next"/>.
        /// Returns the type fired, or null outside battles.
        /// </summary>
        public static Chance? FireNext()
        {
            Require();
            var bb = GameRefs.Multiplay?.multiplayBigBattle;
            var table = bb?.kingChanceTable;
            if (bb == null || table == null || table.Length == 0) return null;
            int n = bb.nowKingChangeNo;
            var type = table[((n % table.Length) + table.Length) % table.Length];
            bb.nowKingChangeNo = n + 1;
            return Fire(type);
        }

        /// <summary>
        /// Requests a King Chance of <paramref name="type"/> the game's way. Returns the type after <see cref="Filter"/>
        /// (<paramref name="filter"/> false skips it).
        /// </summary>
        public static Chance Fire(Chance type, bool filter = true)
        {
            Require();
            var mgr = GameRefs.Multiplay ?? throw new InvalidOperationException("There's no battle running.");
            if (filter && TestOnlyBlockOthers)
            {
                // Another mod's King Chance (e.g. the Multiplayer mod's quarterly ones) during an API test: even the
                // "Nothing happened!" one shows the King's message and freezes the round, which upsets the test's timing.
                ApiCore.Log.Info($"King Chance {type} not fired: an API test is running.");
                return Nothing;
            }
            var real = type;
            if (filter)
            {
                foreach (var (owner, f) in _filters.ToArray())
                {
                    try { real = f(real); } catch (Exception e) { ApiCore.Log.Warn($"King Chance filter ({owner}): {e.Message}"); }
                }
                if (Filter != null)
                {
                    try { real = Filter(real); } catch (Exception e) { ApiCore.Log.Warn($"King Chance filter: {e.Message}"); }
                }
            }
            // SetKingChanceBonus reads bigBattleKingChanceMessage[type]; past its end it would throw inside the game.
            var ids = MultiplaySetting.bigBattleKingChanceMessage;
            if ((int)real < 0 || ids == null || (int)real >= ids.Length)
                throw new InvalidOperationException($"The game has no King Chance {real}.");
            // One at a time: a request made while another King Chance is still being shown would be lost, so it waits.
            if (Pending || _queue.Count > 0)
            {
                _queue.Enqueue(real);
                ApiCore.Log.Info($"King Chance {real} queued (another one is being shown).");
                return real;
            }
            Start(mgr, real, type);
            return real;
        }

        private static readonly Queue<Chance> _queue = new();

        /// <summary>Test only (<see cref="PatchTest"/>): requests that go through the filters (other mods') are turned away.</summary>
        internal static bool TestOnlyBlockOthers;

        /// <summary>King Chances waiting for the one being shown to finish.</summary>
        public static int Queued => _queue.Count;

        private static void Start(MultiplayGameManager mgr, Chance real, Chance asked)
        {
            mgr.KingChanceProcess(real);
            FiredCount++;
            ApiCore.Log.Info(real == asked ? $"King Chance: {real}" : $"King Chance: {asked} -> {real}");
            try { Fired?.Invoke(real); } catch (Exception e) { GameEvents.Report(Fired, e); }
        }

        private static void DrainQueue()
        {
            if (_queue.Count == 0) return;
            try
            {
                var mgr = GameRefs.Multiplay;
                if (mgr == null || !InBattle) { _queue.Clear(); return; }
                if (Pending) return;
                var next = _queue.Dequeue();
                Start(mgr, next, next);
            }
            catch (Exception e) { _queue.Clear(); ApiCore.Log.Warn($"King Chance queue: {e.Message}"); }
        }

        /// <summary>The message the game shows for a type (the King's "King Chance" line + the type's text), or null.</summary>
        public static string MessageFor(Chance type)
        {
            try
            {
                var ids = MultiplaySetting.bigBattleKingChanceMessage;
                var table = GameRefs.Global?.SystemTable;
                int i = (int)type;
                if (ids == null || table == null || i < 0 || i >= ids.Length) return null;
                return table.GetText(ids[i]);
            }
            catch { return null; }
        }

        private static void Require()
        {
            if (!GamePatches.KingChances.Ready)
                throw new InvalidOperationException("The King Chance patch isn't available: " + GamePatches.KingChances.Error);
        }

        internal static bool Install()
        {
            var harmony = ApiCore.Instance?.HarmonyInstance ?? throw new InvalidOperationException("the API isn't initialised");
            harmony.Patch(AccessTools.Method(typeof(MultiplayGameManager), nameof(MultiplayGameManager.SetKingChanceBonus)),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(KingChances), nameof(AfterSetKingChanceBonus))));
            GameEvents.Update += DrainQueue;
            return true;
        }

        // The game's handler ran (message queued, stage part done): add the effects it leaves out.
        private static void AfterSetKingChanceBonus(MultiplayGameManager __instance, Chance type)
        {
            try
            {
                var mgr = __instance;
                if (mgr?.dicPlayerCore == null) return;
                int n = 0;
                switch (type)
                {
                    case Chance.unexploded:
                        foreach (var core in Cores(mgr)) { mgr.MultiplayInfinityDash(core); n++; }
                        ApiCore.Log.Info($"King Chance effect: unlimited dash for {n} prince(s).");
                        break;
                    case Chance.itemConfiscation:
                        foreach (var core in Cores(mgr)) n += Confiscate(mgr, core);
                        ApiCore.Log.Info($"King Chance effect: {n} freebie(s) confiscated (held and in use).");
                        break;
                    case Chance.kingBox:
                        var bb = mgr.multiplayBigBattle;
                        if (bb == null) break;
                        foreach (var core in Cores(mgr)) { bb.MultiplayGetKingBox(core); n++; }
                        ApiCore.Log.Info($"King Chance effect: a King Chest for {n} prince(s).");
                        break;
                }
                HappenedCount++;
            }
            catch (Exception e) { ApiCore.Log.Warn($"King Chance effect ({type}): {e.Message}"); }
            try { Happened?.Invoke(type); } catch (Exception e) { GameEvents.Report(Happened, e); }
        }

        /// <summary>
        /// Takes a prince's freebies: the one held (its King Box reward, and your HUD slot) and the ones in use (Crown power,
        /// Rocket, Magnet, unlimited dash, the Smoke bomb, Hoop). Returns how many were taken.
        /// </summary>
        public static int Confiscate(MultiplayGameManager mgr, MainGameCore core)
        {
            int n = 0;
            if (mgr == null || core == null) return 0;
            try
            {
                var st = mgr.GetUserStatusFromCoreNo(core.multiCoreNo);
                if (core.multiItemType != Reward.none) { mgr.MultiplaySetItem(core, Reward.none); n++; }
                var gm = GameRefs.Main;
                if (gm != null && core == gm.GetCore())
                {
                    if (gm._nowInventoryItem != eInstageItemType.NoItem || gm._itemEffectTimer >= 0f) { if (gm._itemEffectTimer < 0f) n++; }
                    if (gm._itemEffectTimer >= 0f) gm.EndItemEffect(true);
                    gm.SetInventoryItem(eInstageItemType.NoItem, false, true);
                    UnusedFreebies.StopAll();
                }
                if (st != null && st.timeCrownPower > 0f) { mgr.FinishCrown(core); n++; }                    // Crown, Hoop (the host's side)
                if (core.isMultiCrownPower)
                {
                    // The prince's own Crown (speed, glow, gauge): UpdateMultiPlay only honours the finish request once the
                    // Crown gauge is full, so fill it; the game ends the Crown its own way on the next frame.
                    core.multiItemGaugeTime = core.multiItemGaugeTimeMax;
                    core.RequestFinishCrownPower();
                }
                if (st != null && st.timeTransparent > 0f) { mgr.FinishTransparent(core, false); n++; }      // Smoke bomb
                if (BattleDash.Has(core) || (st != null && st.infinityDashTime > 0f))                       // Shoes, the King Chance's dash
                {
                    if (st != null) st.infinityDashTime = 0f;
                    mgr.SetInfinityDashOff(core);
                    n++;
                }
                if (core.isMultiRocket) { mgr.FinishRocket(core); n++; }
                if (core.isMultiEffectMagnet) { mgr.FinishMagnet(core); n++; }
            }
            catch (Exception e) { ApiCore.Log.Warn($"Confiscation: {e.Message}"); }
            return n;
        }

        internal static List<MainGameCore> Cores(MultiplayGameManager mgr)
        {
            var list = new List<MainGameCore>();
            var it = mgr.dicPlayerCore.GetEnumerator();
            while (it.MoveNext()) if (it.Current.Value != null) list.Add(it.Current.Value);
            return list;
        }
    }
}
