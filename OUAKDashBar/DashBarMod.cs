using System;
using ImGuiNET;
using MelonLoader;
using OUAKModAPI;

[assembly: MelonInfo(typeof(OUAKDashBar.DashBarMod), "OUAK Dash Bar", "1.0.0", "OUAK mods")]
[assembly: MelonGame("Bandai Namco Entertainment Inc.", "Once Upon A KATAMARI")]
[assembly: MelonAdditionalDependencies("OUAKModAPI")]

namespace OUAKDashBar
{
    /// <summary>
    /// OUAK Dash Bar: the prince's dash stamina on the game's own unused dash gauge (<c>UIMultiDashGauge</c>), in story
    /// stages and multiplayer modes (Katamari Ball battles, Race, Target, versus online), with a countdown to when the dash
    /// starts regenerating. Never on the ship, the Plaza or menus.
    /// </summary>
    public sealed class DashBarMod : MelonMod
    {
        internal static readonly ModLog Log = ModLog.For("Dash Bar");

        private static MelonPreferences_Entry<bool> _enabled, _multi, _timer, _timerWhileDashing, _hideWhenFull;

        /// <summary>Show the bar in story stages.</summary>
        public static bool Enabled => _enabled?.Value ?? true;
        /// <summary>Also show it in multiplayer modes (Katamari Ball, Race, Target, versus online).</summary>
        public static bool InMultiplayer => _multi?.Value ?? true;

        /// <summary>Where the bar can show: a story stage, or a multiplayer map with the player's katamari in it.</summary>
        public static bool InPlace(out bool multi)
        {
            multi = false;
            if (GameRefs.InStage) return true;
            var gm = GameRefs.Main;
            if (gm == null || !GameRefs.IsMultiplay || GameRefs.Multiplay == null) return false;
            bool player;
            try { player = gm.PlayerInitiated; } catch { return false; }
            multi = true;
            return player;
        }
        /// <summary>Show the countdown until the stamina starts coming back.</summary>
        public static bool ShowTimer => _timer?.Value ?? true;
        /// <summary>Also count down during a dash (regeneration starts when it ends).</summary>
        public static bool TimerWhileDashing => _timerWhileDashing?.Value ?? true;
        /// <summary>Hide the bar while the stamina is full and nothing is happening.</summary>
        public static bool HideWhenFull => _hideWhenFull?.Value ?? false;

        /// <summary>The state shown this frame (for other mods and the test).</summary>
        public static DashState Current { get; private set; }

        public override void OnInitializeMelon()
        {
            var cat = ApiCore.Category("OUAKDashBar", "OUAK Dash Bar");
            _enabled = cat.CreateEntry("Enabled", true, "Show the dash bar in story stages");
            _multi = cat.CreateEntry("InMultiplayer", true, "Also show it in multiplayer modes (Katamari Ball, Race, Target, versus online)");
            _timer = cat.CreateEntry("ShowTimer", true, "Count down until the dash starts regenerating");
            _timerWhileDashing = cat.CreateEntry("TimerWhileDashing", true, "Also count down during a dash");
            _hideWhenFull = cat.CreateEntry("HideWhenFull", false, "Hide the bar while the stamina is full");

            ModMenu.AddTab("dashbar", "Dash Bar", DrawPage, order: 600, owner: "OUAK Dash Bar");
            GameEvents.LateUpdate += Tick;
            GameEvents.Update += DashBarTest.Tick;
            GameEvents.StageLeft += GaugeView.Release;
            GameEvents.Quit += GaugeView.Release;
            FollowHudSpace();
            DashBarTest.CheckFlag();
            Log.Info("Dash Bar loaded.");
        }

        /// <summary>The name the bar's own space would be claimed under (it claims none; it keeps clear of the others).</summary>
        public const string HudOwner = "OUAK Dash Bar";

        // Keeps the gauge clear of other mods' HUD at the bottom centre (e.g. OUAK Multiplayer's player bar, however
        // many rows it stacks), through the API's HudSpace. An older API without it: the gauge stays where the game put it.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void FollowHudSpace()
        {
            try { GaugeView.OtherHud = () => HudSpace.Claims; _ = HudSpace.Claims; }
            catch (Exception e) { Log.Warn("This OUAK Modding API has no HudSpace; the bar won't move out of other HUD's way (" + e.Message + ")."); }
        }

        // After the game's own Update (the dash timers run there), so the bar shows this frame's stamina.
        private static void Tick()
        {
            try
            {
                if (DashBarTest.ProbeOnly) return;
                bool show = Enabled && InPlace(out bool multi) && (!multi || InMultiplayer) && Katamari.Core != null;
                Current = show ? DashState.Read(Katamari.Core) : default;
                if (show && Current.Valid) GaugeView.Show(Current);
                else GaugeView.Hide();
            }
            catch (Exception e) { Log.Warn("Tick: " + e.Message); }
        }

        private static void DrawPage()
        {
            ImUi.Title("OUAK Dash Bar");
            ImUi.Hint("Shows how much dash you have left on the game's own (unused) dash bar, in story stages and multiplayer " +
                      "modes, with a countdown to when the dash starts coming back.");
            Bool(_enabled, "Show the dash bar");
            if (Enabled) Bool(_multi, "Also in multiplayer modes (Katamari Ball, Race, Target, versus online)");
            Bool(_timer, "Count down until the dash starts regenerating");
            if (ShowTimer) Bool(_timerWhileDashing, "Also count down during a dash (regeneration starts when the dash ends)");
            Bool(_hideWhenFull, "Hide the bar while the dash is full");

            ImUi.Header("Now");
            var s = Current;
            if (!InPlace(out _)) ImGui.TextUnformatted("Not in a stage or a multiplayer match.");
            else if (!s.Valid) ImGui.TextUnformatted("No katamari yet.");
            else
            {
                ImGui.TextUnformatted($"{Describe(s.Phase)}: {s.Fill * 100f:0}% left ({Math.Max(0f, s.Max - s.Used):0.0} of {s.Max:0.0} s of dash)");
                if (s.Phase == DashPhase.Exhausted) ImGui.TextUnformatted($"Full again in {s.RegenIn:0.0} s");
                else if (s.Phase == DashPhase.Dashing) ImGui.TextUnformatted($"Regenerating starts in {s.RegenIn:0.0} s{(s.RefillsAtOnce ? " (this dash will run out)" : "")}");
                else if (s.Phase == DashPhase.Regenerating) ImGui.TextUnformatted($"Full in {s.FullIn:0} s");
                else if (s.Phase == DashPhase.Unlimited && s.UnlimitedLeft >= 0f) ImGui.TextUnformatted($"Unlimited dash: {s.UnlimitedLeft:0.0} s left");
            }

            ImUi.Header("How the game's dash works");
            ImUi.Hint("Each dash start costs 0.5 s of stamina, and a full dash uses up to 1.5 s more while it runs (3 s in all). " +
                      "After a dash the stamina comes back slowly (0.1 s per second) right away. Run out completely and you can't " +
                      "dash for 8 s; then it's full again at once. Shoes (and in battles the Unlimited dash King Chance) give unlimited dash while they last.");
        }

        private static void Bool(MelonPreferences_Entry<bool> e, string label)
        {
            bool v = e.Value;
            if (ImGui.Checkbox(label, ref v)) { e.Value = v; MelonPreferences.Save(); }
        }

        internal static string Describe(DashPhase p) => p switch
        {
            DashPhase.Full => "Full",
            DashPhase.Dashing => "Dashing",
            DashPhase.Exhausted => "Out of dash",
            DashPhase.Regenerating => "Regenerating",
            DashPhase.Unlimited => "Unlimited",
            _ => "-",
        };
    }
}
