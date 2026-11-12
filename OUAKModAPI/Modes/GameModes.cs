using System;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using Il2CppApp.KatamariSin;
using eStage = Il2CppApp.KatamariSin.MainGameManager.eStage;

namespace OUAKModAPI
{
    /// <summary>
    /// A way to play: what to load and what rules apply while it runs. Subclass it and register it with
    /// <see cref="GameModes.Register"/>; the Mods tab lists every mode with a Start button.
    /// <para>Lifecycle: <see cref="Start"/> (load your map; return a status message) → the API activates the mode,
    /// then calls <see cref="OnStageStarted"/> once the stage is up, <see cref="OnPlaying"/> each time the player
    /// gets control (start and every restart), <see cref="OnUpdate"/> every frame, <see cref="OnStageEnded"/>
    /// when it ends, and <see cref="OnStop"/> when the player leaves the stage or another mode starts.</para>
    /// </summary>
    public abstract class GameMode
    {
        /// <summary>Short unique id, e.g. "race".</summary>
        public abstract string Id { get; }
        public abstract string Name { get; }
        public virtual string Description => "";
        /// <summary>Who made it.</summary>
        public virtual string Author => "";
        /// <summary>Why it can't start right now, or null when it can.</summary>
        public virtual string Unavailable => GameRefs.InGameSession ? null : "Load a save file first.";

        public bool Active => GameModes.Active == this;

        /// <summary>Loads what the mode plays on. Return a status message; throw InvalidOperationException to refuse.</summary>
        public abstract string Start();

        /// <summary>The mode's stage (or match) is up: set your rules here.</summary>
        public virtual void OnStageStarted(MainGameManager gm) { }
        /// <summary>The player got control (start and each restart).</summary>
        public virtual void OnPlaying(MainGameManager gm) { }
        public virtual void OnUpdate() { }
        public virtual void OnStageEnded(MainGameManager gm, bool cleared) { }
        /// <summary>Drawn over the game while the mode is active.</summary>
        public virtual void OnGUI() { }
        /// <summary>The mode ended (left its stage, or another mode started).</summary>
        public virtual void OnStop() { }

        /// <summary>
        /// Does this stage belong to the mode? Called for each stage that loads after <see cref="Start"/>; by default
        /// the first stage that loads within 60 seconds counts (and any after it, until the player leaves for a hub).
        /// </summary>
        public virtual bool Owns(MainGameManager gm) => true;
    }

    /// <summary>
    /// The game modes every mod can start. The API has two of its own, <b>Race</b> and <b>Target</b> (the game's
    /// unfinished online rounds, offline against CPU princes with the API's fixes, <see cref="TrainingRules"/>); they
    /// are only listed while a mod has enabled <see cref="GamePatches.Race"/> / <see cref="GamePatches.Target"/>.
    /// </summary>
    public static class GameModes
    {
        private static readonly List<GameMode> _all = new();
        private static GameMode _pending;
        private static float _pendingUntil;
        private static bool _staged;

        public static IReadOnlyList<GameMode> All => _all;

        /// <summary>The mode being played now, or null.</summary>
        public static GameMode Active { get; private set; }

        /// <summary>Fired when a mode becomes active (or null when it stops).</summary>
        public static event Action<GameMode> ActiveChanged;

        public static void Register(GameMode mode)
        {
            if (mode == null) return;
            _all.RemoveAll(m => m.Id == mode.Id);
            _all.Add(mode);
        }

        public static void Unregister(GameMode mode)
        {
            if (mode == null) return;
            if (Active == mode) Stop();
            _all.Remove(mode);
        }

        public static GameMode Find(string id) => _all.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

        /// <summary>Starts a mode by id (stopping the active one). Returns its status message.</summary>
        public static string Start(string id)
        {
            var mode = Find(id) ?? throw new InvalidOperationException($"There's no game mode \"{id}\".");
            return Start(mode);
        }

        public static string Start(GameMode mode)
        {
            string why = mode.Unavailable;
            if (why != null) throw new InvalidOperationException(why);
            Stop();
            string msg = mode.Start();
            _pending = mode;
            _pendingUntil = UnityEngine.Time.unscaledTime + 60f;
            return msg;
        }

        /// <summary>Stops the active mode (the stage itself carries on).</summary>
        public static void Stop()
        {
            _pending = null;
            var m = Active;
            if (m == null) return;
            Active = null;
            _staged = false;
            try { m.OnStop(); } catch (Exception e) { ApiCore.Log.Warn($"{m.Name}.OnStop failed: {e.Message}"); }
            Fire(null);
        }

        /// <summary>The API's Race mode (registered while <see cref="GamePatches.Race"/> is on).</summary>
        public static readonly TrainingMode RaceMode = new("race", "Race", eStage.Stage_ONRace1,
            "The game's unfinished online Race round, offline against CPU racers: follow the course, first to the goal wins. " +
            "The API loads the race course before the racers spawn and gives the map restart points, which the game left out.");

        /// <summary>The API's Target mode (registered while <see cref="GamePatches.Target"/> is on).</summary>
        public static readonly TrainingMode TargetMode = new("target", "Target", eStage.Stage_ONTarget1,
            "The game's unfinished online Target (collect) round, offline against CPU princes: roll up the target objects. " +
            "The API runs it under its own collect rule instead of the battle rule the game would use.");

        internal static void Init()
        {
            GameEvents.StageLoaded += OnStageLoaded;
            GameEvents.StagePlaying += gm => Call(m => m.OnPlaying(gm), "OnPlaying");
            GameEvents.StageEnded += (gm, cleared) => Call(m => m.OnStageEnded(gm, cleared), "OnStageEnded");
            GameEvents.PlazaEntered += _ => { if (Active != null) Stop(); };
            GameEvents.ShipEntered += () => { if (Active != null && _staged) Stop(); };
            ModGui.AddOverlay(() => Call(m => m.OnGUI(), "OnGUI"), 50, "game modes");
        }

        private static void OnStageLoaded(MainGameManager gm)
        {
            if (_pending != null && UnityEngine.Time.unscaledTime <= _pendingUntil)
            {
                var m = _pending;
                bool owns;
                try { owns = m.Owns(gm); } catch { owns = false; }
                if (!owns) return;
                _pending = null;
                Active = m;
                _staged = true;
                ApiCore.Log.Info($"Game mode {m.Name} is on.");
                Fire(m);
            }
            Call(m => m.OnStageStarted(gm), "OnStageStarted");
        }

        internal static void Tick()
        {
            if (_pending != null && UnityEngine.Time.unscaledTime > _pendingUntil) _pending = null;
            Call(m => m.OnUpdate(), "OnUpdate");
        }

        private static readonly HashSet<string> _reported = new();

        private static void Call(Action<GameMode> f, string what)
        {
            var m = Active;
            if (m == null) return;
            try { f(m); }
            catch (Exception e)
            {
                if (_reported.Add(m.Id + what)) ApiCore.Log.Warn($"Game mode {m.Name}: {what} failed: {e.Message}");
            }
        }

        private static void Fire(GameMode m)
        {
            try { ActiveChanged?.Invoke(m); } catch (Exception e) { GameEvents.Report(ActiveChanged, e); }
        }

        // ---------------------------------------------------------------- built-in modes

        /// <summary>A multiplayer map as an offline training round, with the API's rule fixes (Race, Target).</summary>
        public sealed class TrainingMode : GameMode
        {
            private readonly string _id, _name, _desc;
            private readonly eStage _stage;

            public TrainingMode(string id, string name, eStage stage, string description)
            {
                _id = id;
                _name = name;
                _stage = stage;
                _desc = description;
            }

            public override string Id => _id;
            public override string Name => _name;
            public override string Description => _desc;
            public override string Author => "OUAK Modding API";
            public eStage Stage => _stage;

            public override string Unavailable
            {
                get
                {
                    if (!GameRefs.InGameSession) return "Load a save file first.";
                    var rule = TrainingRules.RuleFor((GI_MI)((int)_stage - 1));
                    return rule != null && !TrainingRules.PatchActive ? "The API couldn't hook the game's training rules (see the log)." : null;
                }
            }

            public override string Start()
            {
                var entry = StageCatalog.Entries.FirstOrDefault(e => e.Kind == MapKind.Multiplayer && e.Stage == _stage)
                            ?? new MapEntry { Kind = MapKind.Multiplayer, Stage = _stage, Name = _name };
                return SceneLoader.LoadMultiplayerMap(entry);
            }

            public override bool Owns(MainGameManager gm) => GameRefs.IsMultiplay;
        }

    }
}
