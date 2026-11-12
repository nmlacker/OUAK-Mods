using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Il2CppApp.KatamariSin;
using UnityEngine;
using Random = UnityEngine.Random;

namespace OUAKModAPI
{
    /// <summary>
    /// Smoke, Hoop, Dice and Special Dice (<see cref="GamePatches.UnusedFreebies"/>, part of the API). The game never hands
    /// these out (no story box holds them, the battle draws leave them out), so this only acts when a mod gives one.
    /// <para>What the game has (traced 2026-11-06): in story stages <c>ActivateItemEffect</c> plays a cosmetic effect and
    /// ends them at once (Smoke: puff particle #41 and <c>SetPlayerAlpha(0.5)</c>, which nothing ever sets back; Hoop: the
    /// rainbow ring #40; Dice: nothing). In battles they go through <c>UserMultiplayItem</c>, which needs a King Box reward
    /// type they don't have, so nothing happens and the freebie can't be used. The battle code still has their pieces:
    /// the host's collision rules (<c>MultiplayGameManager.HostGameProcess</c>) never let a katamari with
    /// <c>UserStatus.timeTransparent</c>, <c>timeInvincible</c> or <c>timeCrownPower</c> be rolled up, and one with Crown
    /// power knocks the other away; <c>StartLocalCoreTransparent</c> (never called) and <c>MultiplaySetting.bigBattleSmokeBombTime</c>
    /// (10 s) / <c>bigBattleSmokeBombTransparentRate</c> (0.4) are the unfinished Smoke bomb; <c>RequestHoopEffect</c> (never
    /// called) plays the Hoop ring.</para>
    /// <list type="bullet">
    /// <item><b>Smoke</b> (user's design: "ghosted/invincible like how you are when your Katamari (re)spawns"): the game's
    /// Smoke bomb for <see cref="SmokeTime"/> s: see-through, and no katamari can roll you up. Story stages: the ghost look for
    /// the same time, then back to normal (there's nothing to hide from).</item>
    /// <item><b>Hoop</b> (user's choice, 2026-11-07, "randomly teleport the player somewhere", from the game's leftovers: an
    /// unused random warp, <c>MainGameCore.SetRandomSpawnWarp</c> / <c>WarpRandomSpawnPoint</c>, and the unused
    /// <c>EffectManager.WaitAndCallHoopEffectCor</c> that plays the Hoop ring at a spot after <c>HoopWaitTime</c> 0.3 s): in
    /// battles you drop through the rainbow ring and come out at a random spawn point, ring at both ends. Story stages have
    /// no spawn points: the ring only.</item>
    /// <item><b>Dice / Special Dice</b> (user's design): rolls a random freebie with the game's roulette
    /// (<c>SetInventoryItem(item, isRandom: true)</c>, as battle item boxes do): Dice from the normal freebies, Special Dice from
    /// the Specials, never a Dice, Random or Sunglasses, and only ones that work where you are (<see cref="FreebieRules"/>).</item>
    /// </list>
    /// </summary>
    public static class UnusedFreebies
    {
        /// <summary>The game's Smoke bomb time (<c>MultiplaySetting.bigBattleSmokeBombTime</c>).</summary>
        public static float SmokeTime = 10f;
        /// <summary>The Smoke bomb's see-through rate (<c>MultiplaySetting.bigBattleSmokeBombTransparentRate</c>; story stages use the game's own 0.5).</summary>
        public static float SmokeAlpha = 0.4f;
        /// <summary>The game's HoopWaitTime: the second ring appears this long after the first (where you come out).</summary>
        public static float HoopWaitTime = 0.3f;

        /// <summary>How long Hoop stays on the HUD (the battle freebies' own time, Game.ONLINE_ITEM_TIME).</summary>
        public static float HoopShowTime => StoryFreebies.MushroomShowTime;

        /// <summary>Hoop warps done, where the last one started, and how far it went (measured 0.3 s later).</summary>
        public static int Warps { get; private set; }
        public static Vector3 HoopFrom { get; private set; }
        public static float LastWarpDistance { get; private set; } = -1f;

        private static float _hoopWaitUntil = -1f, _hoopMinTime, _hoopStart, _hoopSettle = -1f;
        private static int _hoopTries;
        private static Vector3 _hoopLast;
        private static int _hoopCount0;
        private static bool _hoopAimed;

        /// <summary>Hoop warps that went to a spawn point the API picked (see <see cref="Aim"/>).</summary>
        public static int AimedWarps { get; private set; }

        /// <summary>
        /// The game's "random" warp isn't random: <c>WarpRandomSpawnPoint</c> takes spawn point number
        /// <c>isReqSpawnCount % count</c> (<c>MainGameManager.GetRandomSpawnPoint(int, ...)</c>, the restart points in
        /// <c>listRestartPosition</c>), and that counter only changes when the prince respawns, so every Hoop before a
        /// respawn went to the same point, often the one you were next to (traced and measured 2026-11-09: three warps in a
        /// row to the same point 2.4 away). The API picks one of the points at least <see cref="HoopMinDistance"/> away at
        /// random and points the counter at it for this warp only (put back once the game has warped).
        /// </summary>
        private static bool Aim(MainGameManager gm, MainGameCore core)
        {
            try
            {
                var list = gm.listRestartPosition;
                int n = list?.Count ?? 0;
                if (n == 0) return false;
                var here = core.transform.position;
                var far = new List<int>();
                for (int i = 0; i < n; i++) if (Vector3.Distance(list[i], here) >= HoopMinDistance) far.Add(i);
                if (far.Count == 0) return false;
                int pick = far[Random.Range(0, far.Count)];
                int count = core.isReqSpawnCount;
                core.isReqSpawnCount = count - (((count % n) + n) % n) + pick;   // the same round of n, at the picked point
                AimedWarps++;
                return true;
            }
            catch (Exception e) { ApiCore.Log.Debug($"Hoop: couldn't pick a spawn point ({e.Message})"); return false; }
        }
        // Distance from where you went in, frame by frame ("w" while the game's warp is still pending), for the session log.
        private static readonly System.Text.StringBuilder _hoopTrace = new();

        /// <summary>A warp that ends this close to where it started (the game picked the spawn point you were at) is redone.</summary>
        public static float HoopMinDistance = 5f;

        // The ring where you come out: once the warp has moved you (the game warps on a later update), and no sooner than
        // the game's HoopWaitTime after the first ring.
        private static void HoopArrivalTick()
        {
            if (_hoopWaitUntil < 0f) return;
            try
            {
                var gm = GameRefs.Main;
                var core = gm?.GetCore();
                if (core == null || !GameRefs.IsMultiplay) { _hoopWaitUntil = -1f; return; }
                var pos = core.transform.position;
                float d = Vector3.Distance(HoopFrom, pos);
                float step = Vector3.Distance(_hoopLast, pos);
                _hoopLast = pos;
                // The game clears isWarpStartPoint where it does the warp (MainGameCore.UpdateMultiPlay).
                bool pending = false;
                try { pending = core.isWarpStartPoint; } catch { }
                if (_hoopTrace.Length < 6000) _hoopTrace.Append($"{Time.time - _hoopStart:0.00}:{d:0.0}{(pending ? "w" : "")} ");
                if (!pending && _hoopAimed) { _hoopAimed = false; try { core.isReqSpawnCount = _hoopCount0; } catch { } }
                if (Time.time < _hoopMinTime) return;
                // Arrived: the warp has been done and the katamari has stopped jumping (under 0.5 a frame) for 0.25 s;
                // or 3 s have passed.
                if (!pending && step < 0.5f) { if (_hoopSettle < 0f) _hoopSettle = Time.time + 0.25f; }
                else _hoopSettle = -1f;
                bool settled = _hoopSettle > 0f && Time.time >= _hoopSettle;
                if (!settled && Time.time < _hoopWaitUntil) return;
                if (d < HoopMinDistance && _hoopTries < 3)
                {
                    // The game's random spawn point was (next to) the one you stood on: ask for another.
                    _hoopTries++;
                    _hoopTrace.Append("| again | ");
                    _hoopAimed = Aim(gm, core);
                    core.SetRandomSpawnWarp();
                    _hoopWaitUntil = Time.time + 3f;
                    _hoopSettle = -1f;
                    return;
                }
                _hoopWaitUntil = -1f;
                LastWarpDistance = d;
                gm.RequestHoopEffect(core);
                ApiCore.Log.Debug($"Hoop: warped {d:0.0} after {_hoopTries} tr{(_hoopTries == 1 ? "y" : "ies")}, arrived {Time.time - _hoopStart:0.00} s after use; distance by frame: {_hoopTrace}");
                if (_hoopTries > 1) ApiCore.Log.Info($"Hoop: warped {d:0.0} after {_hoopTries} tries.");
            }
            catch (Exception e) { _hoopWaitUntil = -1f; ApiCore.Log.Warn($"Hoop: {e.Message}"); }
        }

        /// <summary>The freebies this patch gives an effect.</summary>
        public static readonly eInstageItemType[] Items = { eInstageItemType.Smoke, eInstageItemType.Hoop, eInstageItemType.Dice, eInstageItemType.SP_Dice };

        /// <summary>One of <see cref="Items"/> got its effect: the freebie, and what it did.</summary>
        public static event Action<eInstageItemType, string> Applied;
        /// <summary>A Dice rolled: the Dice used, and the freebie it gave.</summary>
        public static event Action<eInstageItemType, eInstageItemType> DiceRolled;

        /// <summary>The last Dice roll's result (NoItem before any).</summary>
        public static eInstageItemType LastRoll { get; private set; } = eInstageItemType.NoItem;
        public static int Rolls { get; private set; }

        private static float _smokeUntil = -1f, _hoopUntil = -1f;
        private static bool _storyAlpha;

        /// <summary>True while your Smoke runs.</summary>
        public static bool SmokeActive => _smokeUntil > 0f && Time.time < _smokeUntil;
        /// <summary>True while your Hoop runs.</summary>
        public static bool HoopActive => _hoopUntil > 0f && Time.time < _hoopUntil;

        /// <summary>King Chests you didn't take because one of these freebies was in your slot (held or running).</summary>
        public static int ChestsRefused { get; private set; }

        /// <summary>True while one of <see cref="Items"/> is in your freebie slot (held, or its countdown running).</summary>
        public static bool InYourSlot
        {
            get
            {
                try { var gm = GameRefs.Main; return gm != null && Array.IndexOf(Items, gm._nowInventoryItem) >= 0; }
                catch { return false; }
            }
        }

        private static bool BeforeObtainTreasure(MainGameCore __0)
        {
            try
            {
                if (!GameRefs.IsMultiplay || __0 == null || __0.isMultiCpu) return true;
                var me = GameRefs.Main?.GetCore();
                if (me == null || me.Pointer != __0.Pointer || !InYourSlot) return true;
                ChestsRefused++;
                return false;   // like a regular freebie: no chest until its timer is up
            }
            catch { return true; }
        }

        internal static bool Install()
        {
            var h = ApiCore.Instance?.HarmonyInstance ?? throw new InvalidOperationException("the API isn't initialised");
            h.Patch(AccessTools.Method(typeof(MainGameManager), nameof(MainGameManager.ActivateItemEffect)),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(UnusedFreebies), nameof(BeforeActivate))),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(UnusedFreebies), nameof(AfterActivate))));
            h.Patch(AccessTools.Method(typeof(MainGameManager), nameof(MainGameManager.EndItemEffect)),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(UnusedFreebies), nameof(AfterEndItemEffect))));
            // King Chests follow the regular freebies' rule (traced 2026-11-12 in the host loop that calls
            // MultiplayBigBattle.GetObtainableTreasure): a prince only takes a chest while it holds no battle reward
            // (core +0xD4) and no freebie timer runs (UserStatus.itemEffectTimer). Smoke, Hoop and Dice have no reward
            // type and never set that timer, so you could take a chest while one was held or running.
            h.Patch(AccessTools.Method(typeof(MultiplayBigBattle), nameof(MultiplayBigBattle.GetObtainableTreasure)),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(UnusedFreebies), nameof(BeforeObtainTreasure))));
            GameEvents.StageRestarted += _ => StopAll();
            GameEvents.StageLeft += StopAll;
            GameEvents.StageEnded += (_, _) => StopAll();
            GameEvents.Quit += StopAll;
            GameEvents.Update += Tick;
            GameEvents.Update += HoopArrivalTick;
            FreebieRules.Register(eInstageItemType.Smoke, $"Smoke bomb: see-through for {SmokeTime:0} s, and no katamari can roll you up", battle: true);
            FreebieRules.Register(eInstageItemType.Hoop, "Hoop: drops you through a rainbow ring to a random spot on the map", battle: true);
            FreebieRules.Register(eInstageItemType.Dice, "Rolls a random freebie", battle: true);
            FreebieRules.Register(eInstageItemType.SP_Dice, "Rolls a random Special freebie", battle: true);
            FreebieRules.Register(eInstageItemType.Dice, "Rolls a random freebie");
            FreebieRules.Register(eInstageItemType.SP_Dice, "Rolls a random Special freebie");
            return true;
        }

        /// <summary>The freebies a Dice (or Special Dice) may roll where you are now.</summary>
        public static eInstageItemType[] DiceChoices(bool special) =>
            StageControl.Freebies.Where(i => i is not (eInstageItemType.Dice or eInstageItemType.SP_Dice or eInstageItemType.RandomItem or eInstageItemType.Sunglass)
                                             && IsSpecial(i) == special && FreebieRules.WorksHere(i)).ToArray();

        /// <summary>True for the Special freebies (SP_Drink .. SP_Dice).</summary>
        public static bool IsSpecial(eInstageItemType i) => (int)i >= (int)eInstageItemType.SP_Drink && (int)i <= (int)eInstageItemType.SP_Dice;

        // Battles: the game can't use these (no reward type), so the API does it instead of the game's call.
        private static bool BeforeActivate(MainGameManager __instance, out eInstageItemType __state)
        {
            __state = eInstageItemType.NoItem;
            try
            {
                __state = __instance._nowInventoryItem;
                if (!GameRefs.IsMultiplay || Array.IndexOf(Items, __state) < 0) return true;
                var core = __instance.GetCore();
                if (core == null) return true;
                if (!CanUseNow(core))
                {
                    ApiCore.Log.Info($"{StageControl.Pretty(__state)}: can't be used right now (being rolled up {core.involvedCore != null}, watching {GameRefs.Multiplay?.isWatching}, past the goal {core.isRaceGoal})");
                    return true;
                }
                Apply(__instance, core, __state, battle: true);
                __state = eInstageItemType.NoItem;   // handled
                return false;
            }
            catch (Exception e) { ApiCore.Log.Warn($"Freebie {__state}: {e.Message}"); return true; }
        }

        // MainGameCore.IsMultiEnableItem without its first check (the prince must hold a King Box reward, which these
        // freebies don't have): not being rolled up, not watching, not past the race goal.
        private static bool CanUseNow(MainGameCore core)
        {
            if (core.involvedCore != null || core.isRaceGoal) return false;
            var mgr = GameRefs.Multiplay;
            return mgr != null && !mgr.isWatching;
        }

        // Story stages: the game played its cosmetic effect and ended the freebie; now the real one.
        private static void AfterActivate(MainGameManager __instance, eInstageItemType __state)
        {
            if (GameRefs.IsMultiplay || Array.IndexOf(Items, __state) < 0) return;
            try { Apply(__instance, __instance.GetCore(), __state, battle: false); }
            catch (Exception e) { ApiCore.Log.Warn($"Freebie {__state}: {e.Message}"); }
        }

        private static void Apply(MainGameManager gm, MainGameCore core, eInstageItemType item, bool battle)
        {
            string what;
            switch (item)
            {
                case eInstageItemType.Smoke:
                    if (battle)
                    {
                        // The game's unfinished Smoke bomb: UserStatus.timeTransparent (the host never rolls you up while
                        // it runs; it counts it down and calls FinishTransparent) and MainGameCore.StartTransparent (see-through).
                        GameRefs.Multiplay.StartLocalCoreTransparent(core, SmokeTime, true);
                        PlaySmokePuff(gm, core);
                        what = $"see-through for {SmokeTime:0} s, no katamari can roll you up";
                    }
                    else
                    {
                        _storyAlpha = true;   // the game's story branch set the player to 0.5 alpha and never sets it back
                        what = $"ghosted for {SmokeTime:0} s";
                    }
                    _smokeUntil = Time.time + SmokeTime;
                    StoryFreebies.ShowCountdown(item, SmokeTime);
                    break;
                case eInstageItemType.Hoop:
                    if (battle)
                    {
                        // The game's unused random warp: SetRandomSpawnWarp() asks the prince's next battle update to warp it
                        // to a random spawn point (MainGameCore.WarpRandomSpawnPoint). The Hoop ring where you go in and, the
                        // game's HoopWaitTime (0.3 s, EffectManager.WaitAndCallHoopEffectCor) later, where you come out.
                        gm.RequestHoopEffect(core);
                        HoopFrom = core.transform.position;
                        LastWarpDistance = -1f;   // measured again on arrival
                        _hoopTrace.Clear();
                        _hoopCount0 = core.isReqSpawnCount;
                        _hoopAimed = Aim(gm, core);
                        core.SetRandomSpawnWarp();
                        Warps++;
                        _hoopWaitUntil = Time.time + 3f;
                        _hoopMinTime = Time.time + HoopWaitTime;
                        _hoopTries = 1;
                        _hoopStart = Time.time; _hoopSettle = -1f; _hoopLast = HoopFrom;
                        what = "dropped through the rainbow ring to a random spot on the map";
                    }
                    else what = "the Hoop ring (story stages have no spawn points to warp to)";
                    _hoopUntil = Time.time + HoopShowTime;
                    StoryFreebies.ShowCountdown(item, HoopShowTime);
                    break;
                case eInstageItemType.Dice:
                case eInstageItemType.SP_Dice:
                {
                    var pool = DiceChoices(item == eInstageItemType.SP_Dice);
                    if (pool.Length == 0) { what = "nothing to roll here"; break; }
                    var pick = pool[Random.Range(0, pool.Length)];
                    if (battle) gm.EndItemEffect(true);   // used up (the story branch already did this)
                    // The game's roulette: what battle item boxes do (MainGameItemBox.Collected -> SetInventoryItem(item, true)).
                    gm.SetInventoryItem(pick, true, false);
                    LastRoll = pick;
                    Rolls++;
                    what = $"rolled {StageControl.Pretty(pick)}";
                    try { DiceRolled?.Invoke(item, pick); } catch (Exception e) { GameEvents.Report(DiceRolled, e); }
                    break;
                }
                default:
                    return;
            }
            try { Applied?.Invoke(item, what); } catch (Exception e) { GameEvents.Report(Applied, e); }
        }

        private static void PlaySmokePuff(MainGameManager gm, MainGameCore core)
        {
            // The puff the story branch plays (EfItem particle #41) at the katamari. (The 2-argument overload: the
            // 5-argument one can't be called through Il2CppInterop's Nullable<Vector3>.)
            try { gm.effect?.PlayEffectParticle((EffectManager.eParticleType)41, core.transform.position); } catch { }
        }

        private static MultiplayController.UserStatus Status(MainGameCore core) =>
            core == null ? null : GameRefs.Multiplay?.GetUserStatusFromCoreNo(core.multiCoreNo);

        private static void Tick()
        {
            if (_smokeUntil < 0f && _hoopUntil < 0f) return;
            float now = Time.time;
            if (_smokeUntil > 0f && now >= _smokeUntil) EndSmoke();
            if (_hoopUntil > 0f && now >= _hoopUntil) _hoopUntil = -1f;
            if (!GameRefs.InGameSession) StopAll();
        }

        private static void EndSmoke()
        {
            _smokeUntil = -1f;
            if (!_storyAlpha) return;
            _storyAlpha = false;
            try { GameRefs.Main?.SetPlayerAlpha(1f); } catch (Exception e) { ApiCore.Log.Warn($"Smoke: {e.Message}"); }
        }

        // The freebie's countdown ran out or something cleared it.
        private static bool _ending;
        private static void AfterEndItemEffect()
        {
            if (_ending || (!_storyAlpha && _hoopUntil < 0f && _smokeUntil < 0f)) return;
            _ending = true;
            try { if (!GameRefs.IsMultiplay) StopAll(); } finally { _ending = false; }
        }

        /// <summary>Ends your Smoke and Hoop now (battles: the see-through and the Crown-power rule too).</summary>
        public static void StopAll()
        {
            try
            {
                if (GameRefs.IsMultiplay && GameRefs.Multiplay != null)
                {
                    var core = GameRefs.Main?.GetCore();
                    if (SmokeActive && core != null) GameRefs.Multiplay.FinishTransparent(core, false);
                    // (Hoop is a one-off warp: nothing to end.)
                }
            }
            catch { }
            _hoopUntil = -1f;
            EndSmoke();
        }

        /// <summary>What's running now, e.g. "Smoke 7 s, Hoop 3 s", or "".</summary>
        public static string Describe()
        {
            float now = Time.time;
            var parts = new List<string>();
            if (_smokeUntil > now) parts.Add($"Smoke {_smokeUntil - now:0} s");
            if (_hoopUntil > now) parts.Add($"Hoop {_hoopUntil - now:0} s");
            return string.Join(", ", parts);
        }
    }
}
