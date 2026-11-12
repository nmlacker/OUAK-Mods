using System;
using System.Linq;
using ImGuiNET;
using OUAKModAPI;
using UnityEngine;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace OUAKMultiplayer
{
    /// <summary>
    /// The Online tab of the mod window, drawn with Dear ImGui: host or join a session, the session's settings and
    /// votes, who's here, and (as tabs along the top) the lobby, ghost and voice settings.
    /// </summary>
    internal sealed class OnlinePage
    {
        private readonly TextField _netName = new(16, "your name");
        private readonly TextField _netAddress = new(64, "host's IP address, e.g. 192.168.1.20");
        private readonly TextField _netPort = new(5, NetSession.DefaultPort.ToString());
        private readonly TextField _netPassword = new(24, "password (optional)");
        private bool _netFieldsLoaded;
        private int _voteStage, _voteKick;

        private const float LabelW = 300f;

        /// <summary>A tab with flags and no close button (ImGui.NET's managed BeginTabItem with flags wants a p_open).</summary>
        private static unsafe bool TabItem(string label, ImGuiTabItemFlags flags)
        {
            int n = System.Text.Encoding.UTF8.GetByteCount(label);
            byte* buf = stackalloc byte[n + 1];
            fixed (char* c = label) System.Text.Encoding.UTF8.GetBytes(c, label.Length, buf, n);
            buf[n] = 0;
            return ImGuiNative.igBeginTabItem(buf, null, flags) != 0;
        }

        /// <summary>Self-test: select the Ranking tab on the next frame; frames it was drawn.</summary>
        public static bool ShowRanking;
        public static int RankingFrames;

        public void Register() => ModMenu.AddTab("online", "Online", DrawOnline, 95, "OUAK Multiplayer");

        private void DrawOnline()
        {
            if (!_netFieldsLoaded)
            {
                _netFieldsLoaded = true;
                _netName.Text = NetSession.SavedName == "Player" && OUAKModAPI.Steam.Available ? Proto.CleanName(OUAKModAPI.Steam.MyName) : NetSession.SavedName;
                _netAddress.Text = NetSession.SavedAddress;
                _netPort.Text = NetSession.SavedPort.ToString();
            }
            if (!ImGui.BeginTabBar("online")) return;
            var mode = NetSession.Mode;
            if (ImGui.BeginTabItem(mode == NetSession.Role.Off ? "Connect" : "Session"))
            {
                if (mode == NetSession.Role.Off) DrawConnect();
                else DrawSession(mode);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Lobby")) { DrawLobbySettings(); ImGui.EndTabItem(); }
            if (TabItem("Ranking", ShowRanking ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
            {
                ShowRanking = false;
                DrawRanking();
                RankingFrames++;
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem(GhostSettings.Shown ? "Ghosts###ghosts" : "Ghosts (hidden)###ghosts")) { DrawGhostSettings(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem(Voice.Talk ? "Voice (mic on)###voice" : "Voice###voice")) { DrawVoiceSettings(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Chat")) { DrawChatSettings(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Versus")) { DrawVersus(); ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }

        // ---------------------------------------------------------------- ranking

        /// <summary>
        /// How players are placed (Online/Ranking): the session's ranking system (the host's choice is everyone's), the
        /// presets (Standard, Speedrun) and your own (a title and measures in order), with what each measure means.
        /// </summary>
        private void DrawRanking()
        {
            bool client = NetSession.Mode == NetSession.Role.Client;
            bool changed = false;
            if (client)
            {
                var cur = Ranking.Current;
                ImUi.Title($"The host ranks by \"{cur.Title}\"");
                RulesList(cur);
                ImUi.Hint("The host picks the ranking system for everyone in the session. Your own choice below is used when you host.");
                ImGui.Separator();
            }
            ImUi.Title(client ? "Your ranking system (when you host)" : "How players are placed");
            ImUi.Hint("Players are compared on the first measure; the next one only decides between players equal on everything before it. " +
                      "Equal on all of them: they share the place. Leaving the race (DQ) always comes last. " +
                      "The Results show the system's title and measures, so everyone sees how they were ranked. " +
                      "In a run, a player's place shows on their slot once they reach the win condition.");

            ImUi.Header("Ranking system");
            foreach (var s in Ranking.All.ToList())
            {
                bool on = s.Title == Ranking.ActiveTitle;
                if (ImGui.RadioButton((s.Preset ? s.Title + " (preset)" : s.Title) + "##rk_" + s.Title, on) && !on)
                {
                    Ranking.ActiveTitle = s.Title;
                    changed = true;
                }
                ImGui.Indent(ImUi.Px(28f));
                ImUi.Hint(Ranking.Describe(s));
                ImGui.Unindent(ImUi.Px(28f));
            }
            var active = Ranking.Active;
            int b = ImUi.Buttons(Ranking.Custom.Count < Ranking.MaxCustom ? $"New (from \"{active.Title}\")" : "New (16 is the most)",
                                 active.Preset ? "Delete (presets stay)" : $"Delete \"{active.Title}\"");
            if (b == 0 && Ranking.Custom.Count < Ranking.MaxCustom)
            {
                var copy = active.Copy(Ranking.FreeTitle(active.Preset ? "My ranking" : active.Title));
                Ranking.Custom.Add(copy);
                Ranking.ActiveTitle = copy.Title;
                changed = true;
            }
            if (b == 1 && !active.Preset)
            {
                Ranking.Custom.Remove(active);
                Ranking.ActiveTitle = Ranking.Standard.Title;
                changed = true;
            }

            active = Ranking.Active;
            ImUi.Header($"\"{active.Title}\": its measures, in order");
            if (active.Preset)
            {
                RulesList(active);
                ImUi.Hint("Presets can't be changed. Press New to start your own from this one.");
            }
            else
            {
                ImUi.Field("Title", ImUi.Px(80f));
                string title = active.Title;
                if (ImGui.InputTextWithHint("##rktitle", "a title everyone will see", ref title, (uint)Ranking.MaxTitle))
                {
                    string clean = Ranking.CleanTitle(title);
                    if (clean.Length > 0 && Ranking.All.All(s => s == active || s.Title != clean))
                    {
                        active.Title = clean;
                        Ranking.ActiveTitle = clean;
                        changed = true;
                    }
                }
                changed |= RulesEditor(active);
            }

            ImUi.Header("The measures");
            foreach (var k in Ranking.AllKeys)
                ImUi.Wrapped($"{Ranking.KeyName(k)}: {Ranking.KeyHelp(k)}", new Vector4(0.8f, 0.8f, 0.86f, 1f));

            if (changed)
            {
                Ranking.Save();
                if (NetSession.Mode == NetSession.Role.Host) NetSession.SettingsChanged();
            }
        }

        /// <summary>A system's measures, numbered, with their direction; hover one for what it means.</summary>
        private static void RulesList(RankingSystem s)
        {
            for (int i = 0; i < s.Rules.Count; i++)
            {
                var r = s.Rules[i];
                ImGui.TextUnformatted($"{i + 1}. {Ranking.KeyName(r.Key)}: {Ranking.Direction(r)}");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(Ranking.KeyHelp(r.Key));
            }
            if (s.Rules.Count == 0) ImGui.TextDisabled("No measures: everyone shares first place.");
        }

        /// <summary>Your own system's measures: turn round, move up / down, remove, and add one not used yet.</summary>
        private static bool RulesEditor(RankingSystem s)
        {
            bool changed = false;
            int move = -1, dir = 0, remove = -1;
            for (int i = 0; i < s.Rules.Count; i++)
            {
                var r = s.Rules[i];
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted($"{i + 1}. {Ranking.KeyName(r.Key)}: {Ranking.Direction(r)}");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(Ranking.KeyHelp(r.Key));
                float bw = ImUi.Px(64f);
                ImUi.RightAlign(bw * 4f + ImGui.GetStyle().ItemSpacing.X * 3f);
                if (ImGui.Button("Turn##rt" + i, new Vector2(bw, 0f))) { r.Reverse = !r.Reverse; s.Rules[i] = r; changed = true; }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Turn this measure round: the other end comes first.");
                ImGui.SameLine();
                ImGui.BeginDisabled(i == 0);
                if (ImGui.Button("Up##ru" + i, new Vector2(bw, 0f))) { move = i; dir = -1; }
                ImGui.EndDisabled();
                ImGui.SameLine();
                ImGui.BeginDisabled(i == s.Rules.Count - 1);
                if (ImGui.Button("Down##rd" + i, new Vector2(bw, 0f))) { move = i; dir = 1; }
                ImGui.EndDisabled();
                ImGui.SameLine();
                if (ImGui.Button("Remove##rx" + i, new Vector2(bw, 0f))) remove = i;
            }
            if (s.Rules.Count == 0) ImGui.TextDisabled("No measures yet: everyone shares first place. Add one below.");
            if (move >= 0)
            {
                (s.Rules[move], s.Rules[move + dir]) = (s.Rules[move + dir], s.Rules[move]);
                changed = true;
            }
            if (remove >= 0) { s.Rules.RemoveAt(remove); changed = true; }

            var unused = Ranking.AllKeys.Where(k => s.Rules.All(r => r.Key != k)).ToList();
            if (unused.Count > 0 && s.Rules.Count < Ranking.MaxRules)
            {
                ImGui.Spacing();
                ImUi.Field("Add a measure", ImUi.Px(140f));
                if (ImGui.BeginCombo("##rkadd", "pick one…"))
                {
                    foreach (var k in unused)
                    {
                        if (ImGui.Selectable(Ranking.KeyName(k) + "##add" + (int)k)) { s.Rules.Add(new RankRule(k)); changed = true; }
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Ranking.KeyHelp(k));
                    }
                    ImGui.EndCombo();
                }
            }
            return changed;
        }

        // ---------------------------------------------------------------- chat

        /// <summary>Versus online through the game's own networking, mod players only (Online/VersusGate).</summary>
        private void DrawVersus()
        {
            ImUi.Title("Versus online: Katamari Ball, Race, Target");
            ImUi.Hint("Versus modes online use the game's own online (the ship's terminal: create, search or join a room). " +
                      "It runs on Steam's lobbies and networking. Host / Join above is the mod's own connection, for the single-player stages only.");
            ImUi.Hint("With OUAK Multiplayer, versus is only with other OUAK Multiplayer players: rooms you create are marked as mod rooms, " +
                      "room searches only find mod rooms, players without the mod are refused when they try to join your room, " +
                      "and you don't join a room without the mod.");
            ImGui.Spacing();
            ImGui.TextDisabled(VersusGate.Active ? "Mod-only versus: on" : "Mod-only versus: NOT active (see the log)");
            ImGui.TextDisabled(VersusGate.Describe());
            if (!string.IsNullOrEmpty(VersusGate.Last)) ImGui.TextDisabled("Last: " + VersusGate.Last);
        }

        /// <summary>Quick chat (Online/QuickChat): on/off and your own lines, edited, reordered, added, removed. Saved right away.</summary>
        private void DrawChatSettings()
        {
            ImUi.Title("Chat");
            ImUi.Hint("Keyboard: T opens the chat box (Enter sends, Esc closes). Controller: D-pad down opens your quick chat lines; " +
                      "D-pad up / down choose, A sends, B closes. A mouse click or the keys 1-9 also send a line while the list is open.");
            ImGui.Spacing();
            bool on = QuickChat.Enabled;
            if (ImGui.Checkbox("D-pad down opens quick chat", ref on)) { QuickChat.Enabled = on; QuickChat.Save(); }

            ImUi.Header($"Your quick chat lines ({QuickChat.Messages.Count} of {QuickChat.MaxMessages})");
            int remove = -1, up = -1;
            bool changed = false;
            float bw = ImUi.Px(34f);
            for (int i = 0; i < QuickChat.Messages.Count; i++)
            {
                ImGui.PushID(i);
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted($"{i + 1}");
                ImGui.SameLine(ImUi.Px(26f));
                ImGui.SetNextItemWidth(-(bw * 3f + ImGui.GetStyle().ItemSpacing.X * 3f));
                string t = QuickChat.Messages[i];
                if (ImGui.InputTextWithHint("##line", "a short message", ref t, (uint)QuickChat.MaxLength)) QuickChat.Messages[i] = t;
                if (ImGui.IsItemDeactivatedAfterEdit()) changed = true;
                ImGui.SameLine();
                ImGui.BeginDisabled(i == 0);
                if (ImGui.Button("Up", new Vector2(bw, 0f))) up = i;
                ImGui.EndDisabled();
                ImGui.SameLine();
                ImGui.BeginDisabled(i == QuickChat.Messages.Count - 1);
                if (ImGui.Button("Dn", new Vector2(bw, 0f))) up = i + 1;
                ImGui.EndDisabled();
                ImGui.SameLine();
                if (ImGui.Button("X", new Vector2(bw, 0f))) remove = i;
                ImGui.PopID();
            }
            if (up > 0)
            {
                (QuickChat.Messages[up - 1], QuickChat.Messages[up]) = (QuickChat.Messages[up], QuickChat.Messages[up - 1]);
                changed = true;
            }
            if (remove >= 0)
            {
                QuickChat.Messages.RemoveAt(remove);
                changed = true;
            }
            ImGui.Spacing();
            ImGui.BeginDisabled(QuickChat.Messages.Count >= QuickChat.MaxMessages);
            if (ImGui.Button("Add a line")) { QuickChat.Messages.Add("New message"); changed = true; }
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button("Reset to the defaults")) QuickChat.ResetToDefaults();
            if (changed) QuickChat.Save();
            if (NetSession.Connected) ImGui.TextDisabled(QuickChat.Describe());
        }

        // ---------------------------------------------------------------- not in a session

        private void DrawConnect()
        {
            ImUi.Hint("Prototype: play alongside friends who also run this mod (same version). Everyone plays their own game: you see each other's " +
                      "katamaris, objects one player rolls up vanish for the others, and players follow the host from map to map. " +
                      "This is the mod's own connection, not the game's online mode.");
            ImGui.Spacing();
            ImUi.Field("Name", 110f);
            ImUi.Input("##name", _netName);
            ImUi.Field("Host IP", 110f);
            float portW = ImUi.Px(90f);
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - portW - ImUi.Px(50f));
            string addr = _netAddress.Text;
            if (ImGui.InputTextWithHint("##addr", _netAddress.Placeholder, ref addr, (uint)_netAddress.MaxLength)) _netAddress.Text = addr;
            ImGui.SameLine();
            ImGui.TextUnformatted("Port");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(-1f);
            string port = _netPort.Text;
            if (ImGui.InputTextWithHint("##port", _netPort.Placeholder, ref port, (uint)_netPort.MaxLength, ImGuiInputTextFlags.CharsDecimal)) _netPort.Text = port;
            ImUi.Field("Password", 110f);
            ImUi.Input("##password", _netPassword);
            ImGui.Spacing();

            switch (ImUi.Buttons("Host", "Join", "Test on this PC"))
            {
                case 0:
                    ModTasks.Run(() => NetSession.Host(ParsePort(), _netName.Text, _netPassword.Text, false));
                    break;
                case 1:
                    ModTasks.Run(() => NetSession.Join(_netAddress.Text, ParsePort(), _netName.Text, _netPassword.Text));
                    break;
                case 2:
                    // Hosts on 127.0.0.1 only (no firewall prompt) and adds the loopback bot as a second player.
                    ModTasks.Run(() =>
                    {
                        NetSession.Host(ParsePort(), _netName.Text, _netPassword.Text, true);
                        string started = NetSession.StartBot();
                        // Your sharing setting stays as you set it; the bot only rolls things up with it on.
                        return NetSession.SharePickups ? started : started + " (Sharing picked-up objects is off, so the bot won't roll anything up.)";
                    });
                    break;
            }
            ImGui.Spacing();

            bool changed = false;
            changed |= ImGui.Checkbox("When hosting: share picked-up objects (they vanish for everyone on the map)", ref NetSession.SharePickups);
            changed |= ImGui.Checkbox("When hosting: only let in players Steam verifies (their Steam account checked by Steam)", ref NetSession.SteamOnly);
            changed |= ImGui.Checkbox("When hosting: bring everyone into the stages I start", ref NetSession.BringToMyMap);
            changed |= ImGui.Checkbox("When joining: follow the host's map", ref NetSession.FollowHost);
            if (changed) NetSession.SavePrefs();

            ImGui.Spacing();
            ImUi.Wrapped(NetSession.Info, NetSession.InfoIsError ? ImTheme.Err : ImTheme.Ok);
            ImGui.Spacing();
            ImUi.Hint($"Hosting: friends need your IP and UDP port {ParsePortOrDefault()} open (forward it on your router, or use the same LAN or a VPN such as Tailscale or ZeroTier). " +
                      "Windows may ask to let the game through the firewall the first time you host.");
        }

        // ---------------------------------------------------------------- hosting or joined

        private void DrawSession(NetSession.Role mode)
        {
            bool host = mode == NetSession.Role.Host;
            ImUi.Wrapped(NetSession.Info, NetSession.InfoIsError ? ImTheme.Err : ImTheme.Ok);
            int hit = host ? ImUi.Buttons("Leave the session", NetSession.BotRunning ? "Stop the test bot" : "Add a test bot") : ImUi.Buttons("Leave the session");
            if (hit == 0) ModTasks.Run(() => { NetSession.Leave("Left the session."); return "Left the session."; });
            if (hit == 1) ModTasks.Run(() => { if (NetSession.BotRunning) { NetSession.StopBot(); return "Loopback bot stopped."; } return NetSession.StartBot(); });
            ImGui.TextDisabled("Lobby: " + Lobby.Describe());
            ImGui.TextDisabled(Chat.Received > 0 || Chat.LastText.Length > 0
                ? $"Chat (press T in game; Enter sends): last: {Chat.LastText}"
                : "Chat: press T in game to type a message (Enter sends, Esc closes).");
            ImGui.TextDisabled(Voice.Summary());

            // ---- players
            ImUi.Header($"Players ({NetSession.Peers.Count + 1} of {NetSession.MaxPlayers})" +
                        (mode == NetSession.Role.Client && NetSession.HostPingMs >= 0f ? $"  ·  ping {NetSession.HostPingMs:0} ms" : ""));
            if (ImGui.BeginTable("players", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthStretch, 2.2f);
                ImGui.TableSetupColumn("Where", ImGuiTableColumnFlags.WidthStretch, 2f);
                ImGui.TableSetupColumn("Ping", ImGuiTableColumnFlags.WidthFixed, ImUi.Px(70f));
                ImGui.TableHeadersRow();
                PlayerRow($"{NetSession.MyName} (you{(host ? ", host" : "")}){MyTag()}", Whereabouts.Mine(), "");
                foreach (var p in NetSession.Peers.Values)
                    PlayerRow(p.Name + Tag(p), Whereabouts.Of(p), host && p.PingMs >= 0f ? $"{p.PingMs:0} ms" : "");
                ImGui.EndTable();
            }
            if (host)
                ImGui.TextDisabled($"Objects indexed: {Ghosts.IndexedObjects}  ·  you rolled up {Ghosts.SentPickups} shared  ·  {Ghosts.HiddenByOthers} hidden by others");

            // ---- settings
            ImUi.Header("Session");
            bool changed = false;
            if (host)
            {
                // The host decides for the whole session; turning it off puts back everything others rolled up.
                changed |= ImGui.Checkbox("Share picked-up objects: what anyone rolls up vanishes for everyone (whole session)", ref NetSession.SharePickups);
                changed |= ImGui.Checkbox("Bring everyone into the stages I start, and run them together (results, standings)", ref NetSession.BringToMyMap);
                SyncRun.StartTogether = NetSession.BringToMyMap;
                changed |= ImGui.Checkbox("Race start: after the shared start, skip the King's talk to get going sooner", ref SyncRun.RaceStart);
                ImUi.Tip("Off (strict start): everyone hears the King's whole talk, so all clocks start together.");
                changed |= ImGui.Checkbox("Only let in players Steam verifies (new joins)", ref NetSession.SteamOnly);
            }
            else
            {
                ImGui.TextDisabled(NetSession.PickupsShared
                    ? "Shared picked-up objects: on (the host's setting) - what anyone rolls up vanishes for everyone."
                    : "Shared picked-up objects: off (the host's setting) - everyone keeps their own stage.");
                changed |= ImGui.Checkbox("Join the stages the host starts", ref NetSession.FollowHost);
            }
            changed |= ImGui.Checkbox("Player bar in two rows of four (off: one row, two groups of four)", ref PlayerBar.TwoRows);
            if (changed) NetSession.SavePrefs();
            if (mode == NetSession.Role.Client && NetSession.Connected && SyncRun.BestRttMs >= 0f)
                ImGui.TextDisabled($"Clock synced to the host (best round trip {SyncRun.BestRttMs:0} ms).");

            // The host's say over restarts and the Plaza (HostAuthority), and free roam (StageGate, PlazaFlow).
            ImUi.Hint(host
                ? "Pause menu Retry / back to the Plaza: Force (everyone), Vote, or Only me (Retry). Everyone follows your restarts and returns."
                : "Pause menu Retry / back to the Plaza: Vote (everyone) or Only me. The host's restarts and returns bring you along.");
            ImUi.Hint("This stage: " + VoteState(VoteKind.Restart, "restart vote") + "  ·  " + VoteState(VoteKind.ReturnToPlaza, "Plaza vote"));
            ImUi.Hint(host
                ? "Free roam: walk the plazas and ship freely. Pick a stage (a fan's Go, or the Levels tab): go now with everyone, or vote."
                : "Free roam: walk the plazas and ship freely. Picking a stage (a fan's Go, or the Levels tab) starts a vote.");
            ImUi.Hint(PlazaFlow.Now != PlazaFlow.Phase.Idle
                ? "Getting ready: " + PlazaFlow.Describe()
                : "A fan's stage: everyone meets at its stage screen to pick music; it starts when all are ready (or 30 s).");

            // ---- votes: anyone can start one; it passes with more than half of all players.
            float cooldown = Votes.CooldownLeft;
            ImUi.Header(Votes.Open ? $"Vote running: {Votes.Describe(Votes.Active.Kind, Votes.Active.Arg)}"
                : cooldown > 0f ? $"Votes (your last one didn't pass: you can start another in {Mathf.CeilToInt(cooldown)} s)" : "Votes");
            ImGui.BeginDisabled(!NetSession.Connected || Votes.Open || cooldown > 0f);
            float bw = ImUi.Px(130f);
            // Single-player stages only: Katamari Ball, Race and Target use the game's own networking, not the mod's sessions.
            var maps = StageCatalog.Entries.Where(e => e.Kind == MapKind.Stage).ToList();
            if (ImGui.Button("Random stage", new Vector2(bw, 0f))) ModTasks.Run(() => Votes.Propose(VoteKind.RandomStage, 0));
            if (maps.Count > 0)
            {
                _voteStage = Mathf.Clamp(_voteStage, 0, maps.Count - 1);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - bw - ImGui.GetStyle().ItemSpacing.X);
                if (ImGui.BeginCombo("##stage", maps[_voteStage].Name))
                {
                    for (int i = 0; i < maps.Count; i++)
                        if (ImGui.Selectable(maps[i].Name + "##" + i, i == _voteStage)) _voteStage = i;
                    ImGui.EndCombo();
                }
                ImGui.SameLine();
                var pick = maps[_voteStage];
                if (ImGui.Button("Vote: play", new Vector2(bw, 0f))) ModTasks.Run(() => Votes.Propose(VoteKind.GoToStage, Ghosts.KeyFor(pick)));
            }
            var others = NetSession.Peers.Values.Where(p => p.Id != 0 || host).ToList();
            if (others.Count > 0)
            {
                _voteKick = Mathf.Clamp(_voteKick, 0, others.Count - 1);
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - bw - ImGui.GetStyle().ItemSpacing.X);
                if (ImGui.BeginCombo("##kick", others[_voteKick].Name))
                {
                    for (int i = 0; i < others.Count; i++)
                        if (ImGui.Selectable(others[i].Name + "##" + i, i == _voteKick)) _voteKick = i;
                    ImGui.EndCombo();
                }
                ImGui.SameLine();
                byte kickId = others[_voteKick].Id;
                if (ImGui.Button("Vote: remove", new Vector2(bw, 0f))) ModTasks.Run(() => Votes.Propose(VoteKind.Kick, kickId));
            }
            ImGui.EndDisabled();
        }

        // ---------------------------------------------------------------- lobby

        /// <summary>
        /// The lobby settings (Online/Lobby): the host's choices, which everyone in the session plays by. The host
        /// edits them (saved, and sent to everyone right away); a player who joined sees the host's.
        /// </summary>
        private void DrawLobbySettings()
        {
            bool host = NetSession.Mode != NetSession.Role.Client;
            int before = Lobby.Hash();
            ImUi.Title(host ? "Everyone in your session plays by these" : "The host's settings, for everyone");
            if (host)
            {
                ImUi.Hint("Saved for next time, and sent to everyone the moment you change them.");
                ImGui.Spacing();
                ImGui.Checkbox("Sudden Death at the stage's end", ref Lobby.SuddenDeath);
                ImUi.Hint(Lobby.SuddenDeath
                    ? "Once the stage has ended for the first player (their time ran out, or the goal ended it), anyone still playing " +
                      "(they paused, restarted or started late) must reach their next growth size in time, again and again, or their run ends (CUT)."
                    : "Off: everyone plays the stage out like single-player. A run ends when that player's own time runs out, " +
                      "when the goal ends the stage, or when they leave it. The results wait for everyone.");
                ImGui.BeginDisabled(!Lobby.SuddenDeath);
                ValueRow("Time to reach each growth size", Lobby.SuddenDeathTimes, ref Lobby.SuddenDeathTime, " s");
                ImGui.EndDisabled();
                IndexRow("Opening fly-over", Lobby.SkipNames, ref Lobby.SkipIntro);
                ValueRow("Time to pick music at a fan's stage screen", Lobby.ReadyTimes, ref Lobby.ReadyTime, " s");
                ValueRow("Wait after starting a vote that didn't pass", Lobby.CooldownTimes, ref Lobby.VoteCooldown, " s");
                IndexRow("Freebies", Lobby.FreebieNames, ref Lobby.FreebieRule);
                if (Lobby.FreebieRule == Lobby.FreebiesSame) ItemRow("Everyone starts with", ref Lobby.FreebieItem);
                if (Lobby.FreebieRule != Lobby.FreebiesNone)
                {
                    IndexRow("A freebie that isn't ticked is", Lobby.OtherFreebieNames, ref Lobby.OtherFreebies);
                    FreebieGrid();
                    ImUi.Hint("Shoes, Crown and Mushroom (and their Specials) do nothing in story stages in the game itself: the API reimplements them. " +
                              "Ticked ones work for everyone in the session, unticked ones are off for everyone.");
                }
            }
            else
            {
                var text = new Vector4(0.92f, 0.92f, 0.96f, 1f);
                ImUi.Wrapped(Lobby.SuddenDeathOn
                    ? $"Sudden Death: on, at the stage's end. Once the stage has ended for the first player, anyone still playing has {Lobby.SuddenDeathSeconds:0} s to reach each growth size."
                    : "Sudden Death: off. Everyone plays the stage out like single-player.", text);
                ImUi.Wrapped("Freebies: " + (Lobby.Freebies switch
                {
                    Lobby.FreebiesNone => "none at all",
                    Lobby.FreebiesSame => "everyone starts with " + StageControl.Pretty(Lobby.SameFreebie) + "; after that only " + Lobby.AllowedText(),
                    _ => "only " + Lobby.AllowedText(),
                }) + (Lobby.Freebies != Lobby.FreebiesNone && Lobby.Allowed != Lobby.AllFreebies
                    ? $" (others are {Lobby.OtherFreebieNames[Lobby.Others].ToLowerInvariant()})" : "") + ".", text);
                ImUi.Wrapped("Reimplemented freebies: " + Lobby.StoryFreebieState + ".", text);
                ImUi.Wrapped("Opening fly-over: " + Lobby.SkipNames[Lobby.FlyOver].ToLowerInvariant() + ".", text);
                ImUi.Wrapped($"{Lobby.ReadySeconds:0} s to pick music at a fan's stage screen; {Lobby.CooldownSeconds:0} s wait after a vote that didn't pass.", text);
            }
            if (host && Lobby.Hash() != before)
            {
                Lobby.Save();
                NetSession.SettingsChanged();
            }
        }

        /// <summary>The host's list of freebies that may be given or picked up in the session's stages: one tick each, All / None.</summary>
        private void FreebieGrid()
        {
            var items = Lobby.Items;
            int on = items.Count(i => (Lobby.AllowedMask & (1u << (int)i)) != 0);
            ImGui.Spacing();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted($"Freebies allowed: {on} of {items.Length}");
            float bw = ImUi.Px(70f);
            ImUi.RightAlign(bw * 2f + ImGui.GetStyle().ItemSpacing.X);
            if (ImGui.Button("All", new Vector2(bw, 0f))) Lobby.AllowedMask = Lobby.AllFreebies;
            ImGui.SameLine();
            if (ImGui.Button("None", new Vector2(bw, 0f))) Lobby.AllowedMask = 0u;
            if (!ImGui.BeginTable("allowed", 3, ImGuiTableFlags.SizingStretchSame)) return;
            foreach (var item in items)
            {
                ImGui.TableNextColumn();
                bool itemOn = (Lobby.AllowedMask & (1u << (int)item)) != 0;
                if (ImGui.Checkbox(StageControl.Pretty(item) + "##" + (int)item, ref itemOn)) Lobby.SetAllowed(item, itemOn);
            }
            ImGui.EndTable();
        }

        private static void IndexRow(string label, string[] names, ref int value)
        {
            ImUi.Field(label, LabelW);
            value = Mathf.Clamp(value, 0, names.Length - 1);
            ImGui.Combo("##" + label, ref value, names, names.Length);
        }

        private static void ValueRow(string label, int[] values, ref int value, string unit)
        {
            ImUi.Field(label, LabelW);
            int idx = Math.Max(0, Array.IndexOf(values, value));
            string Name(int v) => v == 0 && unit == " s" ? "none" : v + unit;
            if (ImGui.BeginCombo("##" + label, Name(values[idx])))
            {
                for (int i = 0; i < values.Length; i++)
                    if (ImGui.Selectable(Name(values[i]) + "##" + i, i == idx)) value = values[i];
                ImGui.EndCombo();
            }
        }

        private static void ItemRow(string label, ref Il2CppApp.KatamariSin.eInstageItemType item)
        {
            ImUi.Field(label, LabelW);
            var items = Lobby.Items;
            if (ImGui.BeginCombo("##" + label, StageControl.Pretty(item)))
            {
                foreach (var it in items)
                    if (ImGui.Selectable(StageControl.Pretty(it) + "##" + (int)it, it == item)) item = it;
                ImGui.EndCombo();
            }
        }

        // ---------------------------------------------------------------- ghosts

        /// <summary>
        /// How other players' katamaris show on your screen (Online/GhostSettings): on / off, when they vanish
        /// (near you, far away, blocking your view, only the nearest), and which parts. Saved right away.
        /// </summary>
        private void DrawGhostSettings()
        {
            int before = GhostSettings.Hash();
            ImUi.Title("Other players' katamaris on your screen");
            ImUi.Hint("Only what you see changes: the player bar, races, results and shared pickups work the same. " +
                      "Distances are in your own katamari's size, so a setting works the same at 10 cm and at 100 m.");
            ImGui.Spacing();
            ImGui.Checkbox("Show other players' katamaris", ref GhostSettings.Shown);
            ImGui.BeginDisabled(!GhostSettings.Shown);
            IndexRow("Vanish when near you", GhostSettings.NearNames, ref GhostSettings.Near);
            IndexRow("Vanish when far away", GhostSettings.FarNames, ref GhostSettings.Far);
            IndexRow("Show at most", GhostSettings.MaxNames, ref GhostSettings.Max);
            ImGui.Checkbox("Vanish while one is between the camera and your katamari", ref GhostSettings.HideBlocking);
            ImGui.Checkbox("Show their cousins", ref GhostSettings.Cousins);
            ImGui.Checkbox("Show what they've rolled up", ref GhostSettings.Stuck);
            ImGui.Checkbox("Show name tags above them", ref GhostSettings.Tags);
            ImGui.Checkbox("Show chat bubbles above them (what they say in the chat)", ref GhostSettings.Bubbles);
            ImGui.EndDisabled();
            if (NetSession.Connected && Ghosts.CurrentMapKey != 0)
                ImGui.TextDisabled($"Right now: {Ghosts.ShownGhosts} of {Ghosts.PlacedGhosts} katamari(s) on this map shown.");
            if (GhostSettings.Hash() != before) GhostSettings.Save();
        }

        // ---------------------------------------------------------------- voice

        /// <summary>
        /// Voice chat (Online/Voice): hearing the others and their volume; your microphone, how you talk (a held key
        /// or open mic) and its volume, with a level meter; hearing yourself as a test; muting players. Yours only,
        /// saved right away (mutes and the self-listen last for the session).
        /// </summary>
        private void DrawVoiceSettings()
        {
            int before = Voice.Hash();
            ImUi.Title("Talk to the players in your session");
            ImUi.Hint("Inside Unity: your microphone is read through Unity's own Microphone (the game's code left that class out; MelonLoader rebuilds it " +
                      "over the engine's microphone code) and voices play through the game's own audio. Nothing is sent until you hold the talk key " +
                      "(or, with open mic, speak).");
            ImGui.Spacing();
            ImGui.Checkbox("Hear other players' voices", ref Voice.Listen);
            ImGui.BeginDisabled(!Voice.Listen);
            IndexRow("Their voices' volume", Voice.VolumeNames, ref Voice.Volume);
            ImGui.EndDisabled();
            ImGui.Checkbox(Voice.Talk ? "Use my microphone (it's open while you're in a session)" : "Use my microphone", ref Voice.Talk);
            ImGui.BeginDisabled(!Voice.Talk);
            int mode = Voice.OpenMic ? 1 : 0;
            IndexRow("Talk by", Voice.ModeNames, ref mode);
            Voice.OpenMic = mode == 1;
            if (Voice.OpenMic) IndexRow("Open mic sensitivity", Voice.SensitivityNames, ref Voice.Sensitivity);
            else IndexRow("Talk key (hold it while you speak)", Voice.KeyNames, ref Voice.Key);
            IndexRow("Microphone volume", Voice.GainNames, ref Voice.Gain);
            // Which microphone: Windows' default, or one Unity lists (a saved one that's gone shows as the default).
            var mics = Voice.Devices();
            var micNames = new string[mics.Length + 1];
            micNames[0] = "Windows' default recording device";
            Array.Copy(mics, 0, micNames, 1, mics.Length);
            int mic = Voice.Device.Length == 0 ? 0 : Array.IndexOf(mics, Voice.Device) + 1, micWas = mic;
            IndexRow("Microphone", micNames, ref mic);
            if (mic != micWas)
            {
                Voice.Device = mic == 0 ? "" : mics[mic - 1];
                Voice.RestartMic();
            }

            // The microphone's level, so you can see it hears you (green while your voice is going out).
            ImUi.Field("Microphone level", LabelW);
            ImGui.PushStyleColor(ImGuiCol.PlotHistogram, Voice.Transmitting ? new Vector4(0.35f, 0.95f, 0.4f, 0.95f) : new Vector4(0.7f, 0.75f, 0.9f, 0.8f));
            ImGui.ProgressBar(Voice.MicOn ? Mathf.Clamp01(Voice.Level) : 0f, new Vector2(-1f, ImUi.Px(14f)), "");
            ImGui.PopStyleColor();
            ImGui.Checkbox("Hear yourself (a test: your voice as the others hear it; use headphones)", ref Voice.HearSelf);
            ImGui.EndDisabled();
            string micText = Voice.Talk
                ? (NetSession.Connected ? $"Microphone: {Voice.MicStatus}{(Voice.Transmitting ? "  ·  TALKING" : "")}" : "Microphone: opens when you're in a session")
                : "Microphone: off";
            ImGui.TextDisabled($"{micText}  ·  {mics.Length} microphone(s) seen by Unity");
            if (NetSession.BotRunning) ImGui.TextDisabled("The test bot says back what it hears a second after you stop talking.");

            if (NetSession.Connected && NetSession.Peers.Count > 0)
            {
                ImUi.Header("Mute players (just for you, this session)");
                foreach (var p in NetSession.Peers.Values)
                {
                    bool muted = Voice.Muted.Contains(p.Id);
                    if (ImGui.Checkbox($"Mute {p.Name}{(Voice.IsTalking(p.Id) ? "  (talking)" : "")}##{p.Id}", ref muted))
                    {
                        if (muted) Voice.Muted.Add(p.Id);
                        else Voice.Muted.Remove(p.Id);
                    }
                }
            }
            if (Voice.Hash() != before) Voice.Save();
        }

        // ---------------------------------------------------------------- bits

        private static void PlayerRow(string name, string map, string ping)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(name);
            ImGui.TableNextColumn();
            ImGui.TextDisabled(map);
            ImGui.TableNextColumn();
            ImGui.TextDisabled(ping);
        }

        /// <summary>"  · Steam verified" / "  · no Steam"...: what the host knows about who they are.</summary>
        private static string Tag(Peer p) =>
            p.Verified ? "  · Steam verified" : string.IsNullOrEmpty(p.VerifyNote) ? "" : "  · " + p.VerifyNote;

        private static string MyTag() => NetSession.MyIdentity.IsSteam ? "  · Steam" : "  · no Steam";

        private static string VoteState(VoteKind kind, string what) =>
            HostAuthority.CanCall(kind, NetSession.MyId, out string why, out _) ? $"you can call a {what}" : why.TrimEnd('.');

        private int ParsePort()
        {
            if (!int.TryParse(_netPort.Text.Trim(), out int port) || port < 1024 || port > 65535)
                throw new InvalidOperationException("Port must be a number from 1024 to 65535.");
            return port;
        }

        private int ParsePortOrDefault() =>
            int.TryParse(_netPort.Text.Trim(), out int port) && port is >= 1024 and <= 65535 ? port : NetSession.DefaultPort;
    }
}
