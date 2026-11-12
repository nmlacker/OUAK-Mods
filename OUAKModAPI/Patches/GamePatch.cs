using System;
using System.Collections.Generic;
using System.Linq;

namespace OUAKModAPI
{
    /// <summary>
    /// A fix for a broken or unfinished part of the game. Every patch is part of the API: it's installed when the API
    /// starts and only ever acts in its own context (crane points only in the Target mode, story-freebie effects only
    /// when a battle freebie is used in a story stage, King Chance effects only when a mod fires one...). Those contexts
    /// only exist through mods, so the unmodded game plays exactly as before. Mods don't switch patches on or off: they
    /// use the features (<see cref="KingChances"/>, <see cref="TargetRound"/>, <see cref="StoryFreebies"/>,
    /// <see cref="BattleItemPools"/>, the Race / Target <see cref="GameModes"/>...).
    /// </summary>
    public sealed class GamePatch
    {
        private readonly Func<bool> _install;
        private bool _tried;

        internal GamePatch(string id, string name, string context, Func<bool> install)
        {
            Id = id;
            Name = name;
            Description = context;
            _install = install;
        }

        /// <summary>Short id, e.g. "target".</summary>
        public string Id { get; }
        public string Name { get; }
        /// <summary>What it fixes and where it acts.</summary>
        public string Description { get; }

        /// <summary>Its hooks are in place.</summary>
        public bool Ready { get; private set; }

        /// <summary>Why it couldn't be installed, or null.</summary>
        public string Error { get; private set; }

        internal void Install()
        {
            if (_tried) return;
            _tried = true;
            try { Ready = _install == null || _install(); }
            catch (Exception e) { Ready = false; Error = e.Message; }
            if (!Ready)
            {
                Error ??= "its hooks couldn't be installed (see the log)";
                ApiCore.Log.Warn($"Patch {Name}: {Error}");
            }
        }

        /// <summary>Obsolete: patches are always part of the API and act only in their own context. Returns <see cref="Ready"/>.</summary>
        [Obsolete("Patches are always part of the API and act only in their own context: there's nothing to switch on.")]
        public bool Enable(string mod) => Ready;

        /// <summary>Obsolete: does nothing.</summary>
        [Obsolete("Patches are always part of the API and act only in their own context: there's nothing to switch off.")]
        public void Disable(string mod) { }

        /// <summary>Obsolete: same as <see cref="Ready"/>.</summary>
        [Obsolete("Use Ready.")]
        public bool Enabled => Ready;

        public override string ToString() => $"{Name}: {(Ready ? "ready" : _tried ? "unavailable: " + Error : "not installed")}";
    }

    /// <summary>
    /// The API's patches for broken or unused game features. All of them are installed when the API starts and act only
    /// in their own context. See the README's "Patches" section.
    /// </summary>
    public static class GamePatches
    {
        /// <summary>
        /// The online Race round, offline against CPU racers (Stage_ONRace1, which the game never offers offline): race
        /// rule, course loaded before the racers spawn, restart points, no hard-point arrow. Mode: <see cref="GameModes.RaceMode"/>.
        /// </summary>
        public static readonly GamePatch Race = new("race", "Race mode",
            "Race map offline: race rule, course loaded before the CPU racers spawn, restart points, no hard-point arrow.",
            TrainingRules.Install);

        /// <summary>
        /// The online Target ("collect") round, offline against CPU princes (Stage_ONTarget1): collect rule, restart points,
        /// no hard-point arrow. Mode: <see cref="GameModes.TargetMode"/>.
        /// </summary>
        public static readonly GamePatch Target = new("target", "Target mode",
            "Target map offline: collect rule, restart points, no hard-point arrow.",
            TrainingRules.Install);

        /// <summary>Target mode only: <see cref="TargetRound.PointsPerTarget"/> points for every target (crane) rolled up.</summary>
        public static readonly GamePatch TargetPoints = new("target-points", "Target points",
            "Target mode only: points for every crane rolled up (the game's own crane count), for you and the CPU princes.",
            TargetRound.Install);

        /// <summary>Target mode only: the CPU princes roll up what their katamari touches, so they collect cranes and score.</summary>
        public static readonly GamePatch TargetCpus = new("target-cpus", "Target CPU princes",
            "Target mode only: CPU princes roll up what their katamari touches (the game never lets them).",
            TargetRound.InstallCpus);

        /// <summary>Battles: King Chances fired by a mod (<see cref="OUAKModAPI.KingChances.Fire"/>) get the effects the game leaves out.</summary>
        public static readonly GamePatch KingChances = new("king-chances", "King Chances",
            "Battles, when a mod fires one: King Chest, confiscation (held freebies and ones in use) and unlimited dash get their effect (the game only shows their message).",
            OUAKModAPI.KingChances.Install);

        /// <summary>Story stages: Shoes, Crown and Mushroom (and Specials) do what their texts say when a mod gives them to you.</summary>
        public static readonly GamePatch StoryFreebies = new("story-freebies", "Battle freebies in story stages",
            "Story stages: Shoes = unlimited dash 10 s, Crown / Special Crown = 1.5x speed 15 / 30 s, Mushroom / Special Mushroom = grow at once.",
            OUAKModAPI.StoryFreebies.Install);

        /// <summary>The Random freebie never hands out one that does nothing where you are.</summary>
        public static readonly GamePatch RandomFreebie = new("random-freebie", "Random freebie fix",
            "Random never hands out a freebie that does nothing where you are.",
            OUAKModAPI.RandomFreebie.Install);

        /// <summary>Battles: what King Boxes and item boxes may give, when a mod sets <see cref="OUAKModAPI.BattleItemPools"/>.</summary>
        public static readonly GamePatch BattleItemPools = new("battle-item-pools", "Battle item pools",
            "Battles, when a mod sets them: which rewards King Boxes and which freebies item boxes may give (none = empty boxes).",
            OUAKModAPI.BattleItemPools.Install);

        /// <summary>Smoke, Hoop, Dice and Special Dice, when a mod gives you one: the effects the game never finished (<see cref="OUAKModAPI.UnusedFreebies"/>).</summary>
        public static readonly GamePatch UnusedFreebies = new("unused-freebies", "Smoke, Hoop and Dice",
            "When a mod gives you one: Smoke = see-through, can't be rolled up (10 s); Hoop = drops you through a rainbow ring to a random spot (battles); Dice / Special Dice = roll a random freebie. Like other freebies, no King Chest while one is in your slot.",
            OUAKModAPI.UnusedFreebies.Install);

        /// <summary>Story stages: what freebie boxes give and show, when a mod sets a rule (<see cref="OUAKModAPI.ItemBoxes"/>, models: <see cref="FreebieModels"/>).</summary>
        public static readonly GamePatch ItemBoxes = new("item-boxes", "Story freebie boxes",
            "Story stages, when a mod sets a rule: which freebies boxes give (off, swapped, or set per box), with the new freebie's model shown.",
            OUAKModAPI.ItemBoxes.Install);

        /// <summary>Unlimited dash really works: Shoes (story and battles) and the King Chance (<see cref="OUAKModAPI.BattleDash"/>).</summary>
        public static readonly GamePatch BattleDash = new("battle-dash", "Unlimited dash",
            "Shoes and the Unlimited dash King Chance: a running dash keeps going until you brake, hit a wall or the time runs out, and dashing costs no stamina " +
            "(battle Shoes never started it, the dash timer never read the flag, and even the game's switch only stopped the drain during a dash).",
            OUAKModAPI.BattleDash.Install);

        /// <summary>Battles: Hand gives you what it stole, and every freebie's countdown ring shows (<see cref="OUAKModAPI.BattleFreebies"/>).</summary>
        public static readonly GamePatch BattleFreebies = new("battle-freebies", "Battle freebie fixes",
            "Battles: Hand puts the stolen freebie in your slot (the game never did), and the HUD ring shows for Spiderweb, Bubble, Hand, Shoes, Crown and the Specials.",
            OUAKModAPI.BattleFreebies.Install);

        /// <summary>Story stages: the Timer keeps the music paused, even after the pause menu (<see cref="OUAKModAPI.TimerMusic"/>).</summary>
        public static readonly GamePatch TimerMusic = new("timer-music", "Timer keeps the music paused",
            "Story stages: closing the pause menu (or a King's message) while the Timer runs no longer restarts the music while the clock is still frozen; it resumes when the Timer ends.",
            OUAKModAPI.TimerMusic.Install);

        public static IReadOnlyList<GamePatch> All { get; } =
            new[] { Race, Target, TargetPoints, TargetCpus, KingChances, StoryFreebies, RandomFreebie, BattleItemPools, UnusedFreebies, ItemBoxes, BattleDash, BattleFreebies, TimerMusic };

        public static GamePatch Find(string id) =>
            All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

        internal static void Init()
        {
            foreach (var p in All) p.Install();
            if (Race.Ready) GameModes.Register(GameModes.RaceMode);
            if (Target.Ready) GameModes.Register(GameModes.TargetMode);
            ApiCore.Log.Info("Patches: " + string.Join("; ", All.Select(p => p.ToString())));
        }
    }
}
