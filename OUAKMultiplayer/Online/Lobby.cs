using System;
using System.Linq;
using Il2CppApp.KatamariSin;
using MelonLoader;
using UnityEngine;
using OUAKModAPI;

namespace OUAKMultiplayer
{
    /// <summary>
    /// The session's lobby settings: the host's choices (saved in its preferences), sent to everyone with the
    /// other session settings (Msg.Settings, every 2 s and on joining). Everyone plays by the host's values:
    /// - Sudden Death: only at the stage's end. Once the stage has ended for the first player (time up), anyone still
    ///   playing must keep
    ///   growing: reach the next growth size within N seconds, again and again, or their run ends (SyncRun).
    /// - Freebies in shared runs: as the game gives them, none at all, or everyone starts with the same one.
    /// - The opening fly-over: a vote, always skipped, or never skipped.
    /// - A fan's stage screen (PlazaFlow): how long everyone has to pick music once they're all there.
    /// - How long whoever started a vote that didn't pass waits before starting another.
    /// </summary>
    internal static class Lobby
    {
        public const int FreebiesAllowed = 0, FreebiesNone = 1, FreebiesSame = 2;
        public const int SkipVote = 0, SkipAlways = 1, SkipNever = 2;
        public const int WhenFirst = 0, WhenHalf = 1, WhenOneLeft = 2;

        public static readonly int[] SuddenDeathTimes = { 10, 15, 20, 30, 45, 60, 90, 120 };
        /// <summary>
        /// No longer used (2026-11-07: Sudden Death starts only at the stage's end). The setting is still saved and sent
        /// (its byte stays in the Settings packet) so the protocol doesn't change.
        /// </summary>
        public static readonly string[] SuddenDeathWhenNames = { "The first player finishes", "Half the players have finished", "Only one player is left" };
        public static readonly string[] FreebieNames = { "Only the ones ticked below", "None at all", "Everyone starts with the same one" };
        public static readonly string[] OtherFreebieNames = { "Swapped for one that's allowed", "Taken away" };
        /// <summary>Every freebie allowed (bit n = eInstageItemType n).</summary>
        public const uint AllFreebies = 0x1FFFFFFEu;
        public static readonly string[] SkipNames = { "Vote at each stage start", "Always skip it", "Never skip it" };
        public static readonly int[] ReadyTimes = { 15, 20, 30, 45, 60, 90 };
        public static readonly int[] CooldownTimes = { 0, 10, 15, 30, 60, 120 };

        /// <summary>
        /// The freebies a session can allow: the ones that do something in story stages (FreebieRules), plus the API's
        /// reimplemented ones (StoryFreebies: Shoes, Crown, Mushroom and their Specials), which the host's list switches
        /// on or off for everyone in the session.
        /// </summary>
        public static eInstageItemType[] Items =>
            FreebieRules.Usable().Concat(StoryFreebies.Items).Distinct().OrderBy(i => (int)i).ToArray();

        /// <summary>True for a freebie whose effect in story stages is the API's (GamePatches.StoryFreebies), not the game's.</summary>
        public static bool IsReimplemented(eInstageItemType item) => Array.IndexOf(StoryFreebies.Items, item) >= 0;

        // The host's choices (saved). Clients don't use these: they use the host's (Received*).
        public static bool SuddenDeath = true;
        public static int SuddenDeathTime = 30, SuddenDeathWhen = WhenHalf, FreebieRule = FreebiesAllowed, SkipIntro = SkipVote, ReadyTime = 30, VoteCooldown = 30;
        public static eInstageItemType FreebieItem = eInstageItemType.RandomItem;
        /// <summary>Which freebies may be given or picked up (bit n = eInstageItemType n).</summary>
        public static uint AllowedMask = AllFreebies;
        /// <summary>A freebie that isn't allowed: 0 = swapped for a random allowed one, 1 = taken away.</summary>
        public static int OtherFreebies;

        // What a client got from its host.
        private static bool _rSuddenDeath = true;
        private static int _rTime = 30, _rWhen = WhenHalf, _rFreebies, _rSkip, _rReady = 30, _rCooldown = 30, _rOther;
        private static eInstageItemType _rItem = eInstageItemType.RandomItem;
        private static uint _rMask = AllFreebies;

        private static MelonPreferences_Entry<bool> _sdPref;
        private static MelonPreferences_Entry<int> _sdTimePref, _sdWhenPref, _freebiePref, _skipPref, _readyPref, _cooldownPref, _otherPref;
        private static MelonPreferences_Entry<string> _itemPref, _allowedPref;

        private static bool Host => NetSession.Mode != NetSession.Role.Client;

        // The values in force: the host's own, or what the host sent.
        public static bool SuddenDeathOn => Host ? SuddenDeath : _rSuddenDeath;
        public static float SuddenDeathSeconds => Host ? SuddenDeathTime : _rTime;
        public static int SuddenDeathStarts => Host ? SuddenDeathWhen : _rWhen;
        public static int Freebies => Host ? FreebieRule : _rFreebies;
        public static eInstageItemType SameFreebie => Host ? FreebieItem : _rItem;
        public static int FlyOver => Host ? SkipIntro : _rSkip;
        public static float ReadySeconds => Host ? ReadyTime : _rReady;
        public static float CooldownSeconds => Host ? VoteCooldown : _rCooldown;
        public static uint Allowed => Host ? AllowedMask : _rMask;
        public static int Others => Host ? OtherFreebies : _rOther;

        public static bool IsAllowed(eInstageItemType item) => item == eInstageItemType.NoItem || (Allowed & (1u << (int)item)) != 0;

        public static void SetAllowed(eInstageItemType item, bool on)
        {
            uint bit = 1u << (int)item;
            AllowedMask = on ? AllowedMask | bit : AllowedMask & ~bit;
        }

        /// <summary>The allowed freebies' names, for the lobby page and logs.</summary>
        public static string AllowedText()
        {
            var on = Items.Where(i => i != eInstageItemType.NoItem && (Allowed & (1u << (int)i)) != 0).ToList();
            if (on.Count == Items.Length) return "all of them";
            if (on.Count == 0) return "none";
            return string.Join(", ", on.Select(StageControl.Pretty));
        }

        public static void Init(MelonPreferences_Category cat)
        {
            _sdPref = cat.CreateEntry("SuddenDeath", true, "Sudden Death", "Hosting: once the stage has ended for the first player (time up), anyone still playing must keep growing or their run ends. Off: everyone plays the stage out like single-player.");
            _sdTimePref = cat.CreateEntry("SuddenDeathTime", 30, "Sudden Death time", "Seconds to reach the next growth size (each time).");
            _sdWhenPref = cat.CreateEntry("SuddenDeathWhen", WhenHalf, "Sudden Death starts", "No longer used: Sudden Death starts only at the stage's end.");
            _freebiePref = cat.CreateEntry("SessionFreebies", FreebiesAllowed, "Freebies", "Shared runs: 0 = allowed, 1 = none, 2 = everyone starts with the same one.");
            _itemPref = cat.CreateEntry("SessionFreebieItem", "RandomItem", "Same freebie", "eInstageItemType name for \"everyone starts with the same one\".");
            _skipPref = cat.CreateEntry("SkipFlyOver", SkipVote, "Opening fly-over", "0 = vote, 1 = always skip, 2 = never skip.");
            _readyPref = cat.CreateEntry("ReadyTime", 30, "Ready time", "Seconds to pick music at a fan's stage screen once everyone is there.");
            _cooldownPref = cat.CreateEntry("VoteCooldown", 30, "Lost-vote cooldown", "Seconds before whoever started a vote that didn't pass can start another.");
            _allowedPref = cat.CreateEntry("AllowedFreebies", "", "Allowed freebies", "Comma-separated eInstageItemType names that may be given or picked up; empty = all of them.");
            _otherPref = cat.CreateEntry("OtherFreebies", 0, "Other freebies", "A freebie that isn't allowed: 0 = swapped for an allowed one, 1 = taken away.");
            SuddenDeath = _sdPref.Value;
            SuddenDeathTime = Pick(SuddenDeathTimes, _sdTimePref.Value, 30);
            SuddenDeathWhen = Mathf.Clamp(_sdWhenPref.Value, 0, 2);
            FreebieRule = Mathf.Clamp(_freebiePref.Value, 0, 2);
            if (Enum.TryParse(_itemPref.Value, out eInstageItemType item) && Items.Contains(item)) FreebieItem = item;
            SkipIntro = Mathf.Clamp(_skipPref.Value, 0, 2);
            ReadyTime = Pick(ReadyTimes, _readyPref.Value, 30);
            VoteCooldown = Pick(CooldownTimes, _cooldownPref.Value, 30);
            OtherFreebies = Mathf.Clamp(_otherPref.Value, 0, 1);
            AllowedMask = AllFreebies;
            if (!string.IsNullOrWhiteSpace(_allowedPref.Value))
            {
                AllowedMask = 0u;
                foreach (var name in _allowedPref.Value.Split(','))
                    if (Enum.TryParse(name.Trim(), out eInstageItemType t) && Items.Contains(t)) AllowedMask |= 1u << (int)t;
            }
        }

        private static int Pick(int[] allowed, int value, int fallback) => allowed.Contains(value) ? value : fallback;

        public static void Save()
        {
            if (_sdPref == null) return;
            _sdPref.Value = SuddenDeath;
            _sdTimePref.Value = SuddenDeathTime;
            _sdWhenPref.Value = SuddenDeathWhen;
            _freebiePref.Value = FreebieRule;
            _itemPref.Value = FreebieItem.ToString();
            _skipPref.Value = SkipIntro;
            _readyPref.Value = ReadyTime;
            _cooldownPref.Value = VoteCooldown;
            _otherPref.Value = OtherFreebies;
            _allowedPref.Value = AllowedMask == AllFreebies ? ""
                : string.Join(",", Items.Where(i => (AllowedMask & (1u << (int)i)) != 0).Select(i => i.ToString()));
        }

        public static int Hash() => HashCode.Combine(HashCode.Combine(SuddenDeath, SuddenDeathTime, SuddenDeathWhen, FreebieRule, FreebieItem, SkipIntro, ReadyTime, VoteCooldown),
                                                     AllowedMask, OtherFreebies);

        // ---------------------------------------------------------------- the Settings packet

        /// <summary>Host: the lobby part of the Settings packet (after its flags byte; flag 2 = Sudden Death).</summary>
        public static byte Flags => (byte)(SuddenDeath ? 2 : 0);

        public static PacketWriter Write(PacketWriter w) =>
            w.U8((byte)Mathf.Clamp(SuddenDeathTime, 0, 255)).U8((byte)SuddenDeathWhen).U8((byte)FreebieRule).U8((byte)FreebieItem)
             .U8((byte)SkipIntro).U8((byte)Mathf.Clamp(ReadyTime, 0, 255)).U8((byte)Mathf.Clamp(VoteCooldown, 0, 255))
             .U32(AllowedMask).U8((byte)OtherFreebies)
             .Then(Ranking.Write);

        /// <summary>Client: the host's lobby settings.</summary>
        public static void Read(byte flags, PacketReader r)
        {
            bool sd = (flags & 2) != 0;
            int time = r.U8(), when = r.U8(), freebies = r.U8(), item = r.U8(), skip = r.U8(), ready = r.U8(), cooldown = r.U8();
            uint mask = r.U32() & AllFreebies;
            int other = Mathf.Clamp(r.U8(), 0, 1);
            Ranking.Read(r);
            bool changed = sd != _rSuddenDeath || time != _rTime || when != _rWhen || freebies != _rFreebies || skip != _rSkip || mask != _rMask || other != _rOther;
            _rMask = mask;
            _rOther = other;
            _rSuddenDeath = sd;
            _rTime = Mathf.Clamp(time, 5, 255);
            _rWhen = Mathf.Clamp(when, 0, 2);
            _rFreebies = Mathf.Clamp(freebies, 0, 2);
            _rItem = Enum.IsDefined(typeof(eInstageItemType), item) && Items.Contains((eInstageItemType)item) ? (eInstageItemType)item : eInstageItemType.RandomItem;
            _rSkip = Mathf.Clamp(skip, 0, 2);
            _rReady = Mathf.Clamp(ready, 5, 255);
            _rCooldown = Mathf.Clamp(cooldown, 0, 255);
            if (changed) MelonLogger.Msg("[Online] The host's lobby settings: " + Describe());
        }

        public static string Describe()
        {
            string sd = SuddenDeathOn ? $"Sudden Death {SuddenDeathSeconds:0} s per growth (for anyone still playing once the stage has ended for the first player)"
                                      : "no Sudden Death (everyone plays the stage out like single-player)";
            string fb = Freebies switch
            {
                FreebiesNone => "no freebies",
                FreebiesSame => $"everyone starts with {StageControl.Pretty(SameFreebie)}" + (Allowed == AllFreebies ? "" : $" (then only: {AllowedText()})"),
                _ => Allowed == AllFreebies ? "all freebies" : $"freebies: {AllowedText()}",
            };
            string fly = FlyOver switch { SkipAlways => "fly-over always skipped", SkipNever => "fly-over never skipped", _ => "fly-over: vote" };
            return $"{sd} · {fb} · {fly} · {ReadySeconds:0} s to get ready · lost-vote wait {CooldownSeconds:0} s";
        }

        // ---------------------------------------------------------------- freebies

        private static IntPtr _givenFor;
        private static float _runningSince = -1f, _nextClear, _nextCheck;
        private static bool _giving, _swapPending, _patched;
        private static readonly System.Random Rng = new();
        public static int Cleared { get; private set; }
        public static int Blocked { get; private set; }
        public static int Swapped { get; private set; }

        /// <summary>
        /// The freebie rules hold in any story stage while you're in a session with others: the stage's own
        /// start-of-stage freebie, item boxes, a Random freebie's roll and the mod's cheats all go through
        /// MainGameManager.SetInventoryItem, which is checked against the host's list.
        /// </summary>
        public static bool RulesApply => NetSession.Connected && NetSession.Peers.Count > 0 && GameRefs.Main != null && !GameRefs.IsMultiplay;

        public static void Patch(HarmonyLib.Harmony harmony)
        {
            try
            {
                harmony.Patch(HarmonyLib.AccessTools.Method(typeof(MainGameManager), nameof(MainGameManager.SetInventoryItem)),
                    prefix: new HarmonyLib.HarmonyMethod(HarmonyLib.AccessTools.Method(typeof(Lobby), nameof(BeforeSetItem))));
                _patched = true;
            }
            catch (Exception e) { MelonLogger.Warning($"[Online] Freebie list unavailable: {e.Message}"); }
        }

        /// <summary>
        /// A freebie about to go into your slot: one that isn't allowed is held back (and, unless the host says
        /// they're taken away, swapped for an allowed one on the next frame). Clearing the slot always goes through.
        /// </summary>
        private static bool BeforeSetItem(eInstageItemType __0)
        {
            if (_giving || __0 == eInstageItemType.NoItem) return true;
            try
            {
                if (!RulesApply) return true;
                if (Freebies != FreebiesNone && IsAllowed(__0)) return true;
                Blocked++;
                if (Freebies != FreebiesNone && Others == 0 && AnyAllowed()) _swapPending = true;
                return false;
            }
            catch { return true; }
        }

        private static bool AnyAllowed() => (Allowed & AllFreebies) != 0;

        /// <summary>A random allowed freebie (Random itself only when it's the only one).</summary>
        private static eInstageItemType RandomAllowed()
        {
            var pool = Items.Where(i => i != eInstageItemType.RandomItem && IsAllowed(i)).ToList();
            if (pool.Count == 0 && IsAllowed(eInstageItemType.RandomItem)) return eInstageItemType.RandomItem;
            return pool.Count == 0 ? eInstageItemType.NoItem : pool[Rng.Next(pool.Count)];
        }

        /// <summary>Gives a freebie the rules allow (it passes the check: it's ours).</summary>
        private static void Give(MainGameManager gm, eInstageItemType item, bool noSound)
        {
            _giving = true;
            try { gm.SetInventoryItem(item, false, noSound); }
            finally { _giving = false; }
        }

        /// <summary>
        /// Per frame, after the mod's own cheats. Swaps a held-back freebie; checks the slot twice a second (a
        /// freebie that got in some other way, or one you had before the rules arrived); in a shared run, also
        /// "none at all" and "everyone starts with the same one".
        /// </summary>
        public static void Tick()
        {
            SyncStoryFreebies();
            if (!RulesApply) { _runningSince = -1f; _swapPending = false; return; }
            var gm = GameRefs.Main;
            try
            {
                if (!gm.IsGameRunning || gm._isInClearDemo || gm._isInGameOver) return;
                float now = Time.unscaledTime;
                if (_swapPending)
                {
                    _swapPending = false;
                    var swap = RandomAllowed();
                    if (swap != eInstageItemType.NoItem)
                    {
                        Give(gm, swap, false);
                        Swapped++;
                        MelonLogger.Msg($"[Online] That freebie isn't allowed in this session (the host's list); you got {StageControl.Pretty(swap)} instead.");
                    }
                }
                bool inRun = SyncRun.Phase == RunPhase.Running && SyncRun.MyResult == null && Ghosts.CurrentMapKey == SyncRun.RunMap && SyncRun.RunMap != 0;
                switch (Freebies)
                {
                    case FreebiesNone:
                        if (now < _nextClear) return;
                        if (gm._nowInventoryItem == eInstageItemType.NoItem && gm._itemEffectTimer < 0f) return;
                        _nextClear = now + 0.25f;
                        if (gm._itemEffectTimer >= 0f) gm.EndItemEffect(true);
                        Give(gm, eInstageItemType.NoItem, true);
                        Cleared++;
                        if (Cleared == 1 || Cleared % 10 == 0) MelonLogger.Msg("[Online] No freebies in this session (the host's setting): took one away.");
                        return;
                    case FreebiesSame when inRun && gm.Pointer != _givenFor:
                    {
                        // Once per stage, a moment after control returns (the game's own start-of-stage items first).
                        if (_runningSince < 0f) { _runningSince = now; return; }
                        if (now - _runningSince < 0.4f) return;
                        _givenFor = gm.Pointer;
                        var item = SameFreebie;
                        if (gm._itemEffectTimer >= 0f) gm.EndItemEffect(true);
                        Give(gm, item, true);
                        ModTasks.Toast($"Everyone starts with: {StageControl.Pretty(gm._nowInventoryItem)}");
                        MelonLogger.Msg($"[Online] Everyone starts with the same freebie (the host's setting): {StageControl.Pretty(gm._nowInventoryItem)}.");
                        return;
                    }
                }
                // The slot itself: anything not on the list (the start-of-stage "same one" is the host's own pick).
                if (now < _nextCheck) return;
                _nextCheck = now + 0.5f;
                var held = gm._nowInventoryItem;
                if (held == eInstageItemType.NoItem || IsAllowed(held)) return;
                if (Freebies == FreebiesSame && held == SameFreebie) return;
                var instead = Others == 0 ? RandomAllowed() : eInstageItemType.NoItem;
                Give(gm, instead, true);
                if (instead == eInstageItemType.NoItem) Cleared++;
                else Swapped++;
                MelonLogger.Msg($"[Online] {StageControl.Pretty(held)} isn't allowed in this session: " +
                                (instead == eInstageItemType.NoItem ? "taken away." : $"swapped for {StageControl.Pretty(instead)}."));
            }
            catch (Exception e)
            {
                _nextClear = _nextCheck = Time.unscaledTime + 5f;
                MelonLogger.Warning($"[Online] Freebie rule: {e.Message}");
            }
        }

        // ---------------------------------------------------------------- the API's reimplemented freebies

        private const string ModName = "OUAK Multiplayer";
        private static bool _sfOn;
        private static float _nextSf;
        private static readonly System.Collections.Generic.Dictionary<eInstageItemType, bool> SfBefore = new();
        public static string StoryFreebieState { get; private set; } = "off (not in a session with others)";

        /// <summary>
        /// The host's choice of the API's reimplemented freebies (Shoes, Crown, Mushroom and their Specials in story
        /// stages): while you're in a session with others, GamePatches.StoryFreebies is on with each of those freebies
        /// on exactly when the host's list allows it (none of them with "no freebies"). Your own setting (another mod
        /// may have switched some on or off) is put back when you leave the session.
        /// </summary>
        private static void SyncStoryFreebies()
        {
            float now = Time.unscaledTime;
            if (now < _nextSf) return;
            _nextSf = now + 0.5f;
            try
            {
                bool want = NetSession.Connected && NetSession.Peers.Count > 0;
                if (want)
                {
                    if (!_sfOn)
                    {
                        SfBefore.Clear();
                        foreach (var i in StoryFreebies.Items) SfBefore[i] = StoryFreebies.IsOn(i);
                        if (!GamePatches.StoryFreebies.Ready)
                        {
                            StoryFreebieState = "unavailable: " + GamePatches.StoryFreebies.Error;
                            _nextSf = now + 10f;
                            return;
                        }
                        _sfOn = true;
                    }
                    var on = new System.Collections.Generic.List<string>();
                    foreach (var i in StoryFreebies.Items)
                    {
                        bool want1 = Freebies != FreebiesNone && IsAllowed(i);
                        if (StoryFreebies.IsOn(i) != want1) StoryFreebies.SetItem(i, want1);
                        if (want1) on.Add(StageControl.Pretty(i));
                    }
                    string state = on.Count == 0 ? "none of them (the host's list)" : string.Join(", ", on) + " (the host's list)";
                    if (state != StoryFreebieState) MelonLogger.Msg($"[Online] Reimplemented freebies in this session: {state}.");
                    StoryFreebieState = state;
                }
                else if (_sfOn)
                {
                    foreach (var kv in SfBefore) if (StoryFreebies.IsOn(kv.Key) != kv.Value) StoryFreebies.SetItem(kv.Key, kv.Value);
                    _sfOn = false;
                    StoryFreebieState = "off (not in a session with others)";
                    MelonLogger.Msg("[Online] Left the session: the reimplemented freebies are back to your own setting.");
                }
            }
            catch (Exception e)
            {
                _nextSf = now + 5f;
                MelonLogger.Warning($"[Online] Reimplemented freebies: {e.Message}");
            }
        }

        public static string DescribeFreebies() =>
            $"list hook {(_patched ? "on" : "off")}, held back {Blocked}, swapped {Swapped}, taken away {Cleared}";
    }
}
