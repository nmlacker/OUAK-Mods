using System;
using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using OUAKModAPI;
using Vector2 = System.Numerics.Vector2;

namespace OUAKSongSelector
{
    /// <summary>The "Songs" page of the mod window: what's playing, the playlists, where they play, your music folder.</summary>
    internal static class SongsTab
    {
        private static int _sel;
        private static readonly TextField _name = new(60, "Playlist name (Enter renames)");
        private static readonly TextField _filter = new(60, "Filter songs");
        private static string _nameFor;
        private static int _addTab;
        private static int _stagePick;
        private static string _msg = "";
        private static bool _confirmDelete;
        private static Playlist _shownActive;

        private static readonly string[] RepeatLabels = { "Repeat all", "Repeat one", "Play once" };

        public static void Draw()
        {
            ImUi.Title("Song Selector");
            ImUi.Hint("Playlists of the game's own songs and your own music files. Choose where each plays: everywhere, " +
                      "all story stages, the Plaza, or single stages. Changes are saved at once.");
            if (Store.LastError != "") ImUi.Err(Store.LastError);
            if (!Player.HooksOk) ImUi.Err("Some of the mod's hooks couldn't be installed; playlists may not play. See the log.");

            NowPlaying();
            ImGui.Spacing();
            if (ImGui.CollapsingHeader("Playlists", ImGuiTreeNodeFlags.DefaultOpen)) Playlists();
            if (ImGui.CollapsingHeader("Where they play", ImGuiTreeNodeFlags.DefaultOpen)) WhereTheyPlay();
            if (ImGui.CollapsingHeader("Your music folder")) MusicFolder();
            if (ImGui.CollapsingHeader("Options")) Options();
        }

        // ------------------------------------------------------------------ now playing

        private static void NowPlaying()
        {
            ImUi.Header("Now playing");
            string place = Player.Where switch
            {
                Place.Stage => "Story stage: " + StageName(Player.StageKey),
                Place.Plaza => "The Plaza",
                _ => "Not in a story stage or the Plaza (playlists play there).",
            };
            ImGui.TextUnformatted(place);
            if (Player.Where != Place.None)
            {
                if (Player.Active == null) ImGui.TextDisabled("The game's own music." + (Player.Status != "" ? " " + Player.Status : ""));
                else
                {
                    ImGui.TextUnformatted($"Playlist: {Player.Active.Name}  ({Player.ActiveFrom})");
                    if (Player.Current != null)
                    {
                        bool mine = Player.CurrentIsCustom;
                        ImGui.TextUnformatted((mine ? "Your file: " : "Game song: ") + Catalog.Describe(Player.Current));
                        float frac = Player.Length > 0 ? (float)Math.Clamp(Player.Position / Player.Length, 0, 1) : 0f;
                        ImGui.ProgressBar(frac, new Vector2(-1f, ImUi.Px(18f)), $"{Clock(Player.Position)} / {(Player.Length > 0 ? Clock(Player.Length) : "?")}");
                    }
                    if (Player.Status != "") ImUi.Hint(Player.Status);
                }
            }
            bool can = Player.CanControl;
            int hit = Buttons(can, "<< Previous", "Next >>", "Stop playlist here");
            if (hit == 0) ModTasks.Run(Player.Previous);
            if (hit == 1) ModTasks.Run(Player.Next);
            if (hit == 2) ModTasks.Run(Player.StopHere);
        }

        private static int Buttons(bool enabled, params string[] labels)
        {
            ImGui.BeginDisabled(!enabled);
            int hit = ImUi.Buttons(labels);
            ImGui.EndDisabled();
            return enabled ? hit : -1;
        }

        private static string Clock(double s) => s < 0 ? "?" : $"{(int)(s / 60)}:{(int)(s % 60):00}";

        // ------------------------------------------------------------------ playlists

        private static void Playlists()
        {
            var lists = Store.Settings.Playlists;
            int b = ImUi.Buttons("New playlist", "Duplicate", "Delete");
            if (b == 0) { lists.Add(new Playlist { Name = Store.FreeName("My playlist") }); Store.Save(); _sel = lists.Count - 1; _confirmDelete = false; }
            if (b == 1 && _sel < lists.Count)
            {
                var src = lists[_sel];
                lists.Add(new Playlist { Name = Store.FreeName(src.Name + " copy"), Tracks = src.Tracks.ToList(), Shuffle = src.Shuffle, Repeat = src.Repeat });
                Store.Save(); _sel = lists.Count - 1;
            }
            if (b == 2 && _sel < lists.Count) _confirmDelete = true;
            if (_confirmDelete && _sel < lists.Count)
            {
                ImUi.Err($"Delete \"{lists[_sel].Name}\"? Places that use it go back to the next choice.");
                int c = ImUi.Buttons("Yes, delete it", "Keep it");
                if (c == 0) { Store.Delete(lists[_sel]); _sel = Math.Max(0, _sel - 1); _confirmDelete = false; }
                if (c == 1) _confirmDelete = false;
            }
            if (lists.Count == 0) { ImUi.Hint("No playlists yet: press New playlist."); return; }
            // Show the playlist that plays here whenever that changes.
            if (Player.Active != _shownActive) { _shownActive = Player.Active; int ai = lists.IndexOf(Player.Active); if (ai >= 0) { _sel = ai; _confirmDelete = false; } }
            _sel = Math.Clamp(_sel, 0, lists.Count - 1);

            int pick = ImUi.ChoiceGrid("lists", lists.Count, 3, i => $"{lists[i].Name} ({lists[i].Tracks.Count})", _sel, cellHeight: 30f);
            if (pick >= 0) { _sel = pick; _confirmDelete = false; }
            var p = lists[_sel];

            if (_nameFor != p.Name) { _name.Text = p.Name; _nameFor = p.Name; }
            ImUi.Field("Name");
            if (ImUi.Input("##plname", _name) && !string.IsNullOrWhiteSpace(_name.Text) && _name.Text.Trim() != p.Name) { Store.Rename(p, _name.Text); _nameFor = p.Name; _name.Text = p.Name; }

            bool sh = p.Shuffle;
            if (ImGui.Checkbox("Shuffle", ref sh)) { p.Shuffle = sh; Store.Save(); }
            ImGui.SameLine();
            int rep = (int)p.Repeat;
            ImGui.SetNextItemWidth(ImUi.Px(150f));
            if (ImGui.BeginCombo("##repeat", RepeatLabels[rep]))
            {
                for (int i = 0; i < RepeatLabels.Length; i++)
                    if (ImGui.Selectable(RepeatLabels[i], i == rep)) { p.Repeat = (RepeatMode)i; Store.Save(); }
                ImGui.EndCombo();
            }
            ImUi.Tip("Repeat all: start over after the last song. Repeat one: the same song again. Play once: after the last song the game's own music comes back.");
            if (Player.Where != Place.None)
            {
                ImGui.SameLine();
                if (ImGui.Button("Play here now")) { var pl = p; ModTasks.Run(() => Player.PlayNow(pl)); }
                ImUi.Tip("Plays this playlist here until you leave this stage or the Plaza.");
            }

            ImGui.Spacing();
            ImGui.TextUnformatted($"Songs ({p.Tracks.Count})");
            float h = ImUi.Px(Math.Clamp(p.Tracks.Count, 3, 10) * 28f + 8f);
            if (ImGui.BeginChild("tracks", new Vector2(-1f, h), ImGuiChildFlags.Borders))
            {
                int move = -1, dir = 0, remove = -1;
                for (int i = 0; i < p.Tracks.Count; i++)
                {
                    string r = p.Tracks[i];
                    ImGui.PushID(i);
                    bool ok = Catalog.Playable(r, out string why);
                    bool playing = Player.Active == p && Player.Current == r;
                    string kind = TrackRef.IsFile(r, out _) ? "[file] " : "";
                    ImGui.AlignTextToFramePadding();
                    if (ok) ImGui.TextUnformatted($"{(playing ? "> " : "")}{i + 1}. {kind}{Catalog.Describe(r)}");
                    else ImGui.TextDisabled($"{i + 1}. {kind}{Catalog.Describe(r)}  ({why})");
                    ImUi.RightAlign(ImUi.Px(4 * 46f));
                    if (ImGui.SmallButton("Play") && ok) { var pl = p; string tr = r; ModTasks.Run(() => Player.PlayNow(pl, tr)); }
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Up") && i > 0) { move = i; dir = -1; }
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Down") && i < p.Tracks.Count - 1) { move = i; dir = 1; }
                    ImGui.SameLine();
                    if (ImGui.SmallButton("X")) remove = i;
                    ImGui.PopID();
                }
                if (move >= 0) { (p.Tracks[move], p.Tracks[move + dir]) = (p.Tracks[move + dir], p.Tracks[move]); Store.Save(); }
                if (remove >= 0) { p.Tracks.RemoveAt(remove); Store.Save(); }
                if (p.Tracks.Count == 0) ImGui.TextDisabled("Empty: add songs below.");
            }
            ImGui.EndChild();

            ImGui.Spacing();
            ImUi.Segments("addtab", new[] { "Add game songs", "Add your files" }, ref _addTab);
            ImUi.Input("##filter", _filter);
            string f = (_filter.Text ?? "").Trim();
            if (ImGui.BeginChild("add", new Vector2(-1f, ImUi.Px(240f)), ImGuiChildFlags.Borders))
            {
                if (_addTab == 0) AddGameSongs(p, f);
                else AddFiles(p, f);
            }
            ImGui.EndChild();
        }

        private static void AddGameSongs(Playlist p, string f)
        {
            var songs = Catalog.GameSongs;
            if (songs.Count == 0) { ImGui.TextDisabled("The game's song list loads with the game; open this again in a moment."); return; }
            int album = -1;
            foreach (var s in songs)
            {
                if (f != "" && s.Label.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0 && s.FirstRelease.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (s.Album != album) { album = s.Album; ImGui.SeparatorText(Catalog.AlbumNames[Math.Clamp(album, 0, Catalog.AlbumNames.Length - 1)]); }
                bool ok = Catalog.Available(s, out string why);
                ImGui.PushID(s.Index);
                if (ImGui.SmallButton("Add")) { p.Tracks.Add(s.Ref); Store.Save(); }
                ImGui.SameLine();
                if (ok) ImGui.TextUnformatted(s.Label);
                else ImGui.TextDisabled($"{s.Label}  ({why})");
                if (s.FirstRelease != "") ImUi.Tip("First released in: " + s.FirstRelease);
                ImGui.PopID();
            }
        }

        private static void AddFiles(Playlist p, string f)
        {
            var files = Catalog.CustomSongs;
            if (files.Count == 0) { ImUi.Hint($"No music files yet. Put them in {Catalog.MusicFolder} and press Rescan (under \"Your music folder\")."); return; }
            for (int i = 0; i < files.Count; i++)
            {
                var c = files[i];
                if (f != "" && c.RelPath.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0) continue;
                ImGui.PushID(i);
                if (ImGui.SmallButton("Add")) { p.Tracks.Add(c.Ref); Store.Save(); }
                ImGui.SameLine();
                ImGui.TextUnformatted($"{c.RelPath}  ({c.Bytes / 1048576.0:0.0} MB)");
                ImGui.PopID();
            }
        }

        // ------------------------------------------------------------------ assignments

        private static void WhereTheyPlay()
        {
            var u = Store.Settings.Use;
            ImUi.Hint("The most specific choice wins: a single stage, then All story stages / The Plaza, then Everywhere. " +
                      "Nothing chosen anywhere = the game's own music (including the song you pick in a fan's talk).");
            string v;
            v = u.Global; if (PlaylistCombo("Everywhere", ref v, "(nothing: the game's music)", false)) { u.Global = v; Store.Save(); }
            v = u.Stages; if (PlaylistCombo("All story stages", ref v, "(same as Everywhere)", false)) { u.Stages = v; Store.Save(); }
            v = u.Plaza; if (PlaylistCombo("The Plaza", ref v, "(same as Everywhere)", false)) { u.Plaza = v; Store.Save(); }

            ImGui.Spacing();
            ImGui.TextUnformatted("Single stages");
            string remove = null;
            foreach (var kv in u.PerStage.OrderBy(k => StageName(k.Key)).ToList())
            {
                ImGui.PushID(kv.Key);
                string val = kv.Value;
                if (PlaylistCombo(StageName(kv.Key), ref val, "(remove)", true)) { if (string.IsNullOrEmpty(val)) remove = kv.Key; else { u.PerStage[kv.Key] = val; Store.Save(); } }
                ImGui.PopID();
            }
            if (remove != null) { u.PerStage.Remove(remove); Store.Save(); }

            var stages = StageCatalog.Entries.Where(e => e.Kind == MapKind.Stage).ToList();
            if (stages.Count > 0)
            {
                _stagePick = Math.Clamp(_stagePick, 0, stages.Count - 1);
                if (Player.Where == Place.Stage)
                {
                    int here = stages.FindIndex(s => s.Stage.ToString() == Player.StageKey);
                    if (here >= 0 && ImGui.SmallButton("This stage")) _stagePick = here;
                    ImGui.SameLine();
                }
                ImGui.SetNextItemWidth(ImUi.Px(320f));
                if (ImGui.BeginCombo("##stagepick", stages[_stagePick].Name))
                {
                    for (int i = 0; i < stages.Count; i++)
                        if (ImGui.Selectable($"{stages[i].Name}##{i}", i == _stagePick)) _stagePick = i;
                    ImGui.EndCombo();
                }
                ImGui.SameLine();
                string key = stages[_stagePick].Stage.ToString();
                if (ImUi.Button("Add a choice for this stage", !u.PerStage.ContainsKey(key)))
                {
                    u.PerStage[key] = Store.Settings.Playlists.FirstOrDefault()?.Name ?? Player.GameMusic;
                    Store.Save();
                }
            }
        }

        /// <summary>A combo of playlists. Returns true when changed. Single stages also offer "the game's own music".</summary>
        private static bool PlaylistCombo(string label, ref string value, string emptyLabel, bool perStage)
        {
            string shown = string.IsNullOrEmpty(value) ? emptyLabel : value == Player.GameMusic ? "(the game's own music)" : value;
            if (!string.IsNullOrEmpty(value) && value != Player.GameMusic && Store.Find(value) == null) shown = value + " (missing)";
            bool changed = false;
            ImUi.Field(label, 230f);
            if (ImGui.BeginCombo("##" + label, shown))
            {
                if (ImGui.Selectable(emptyLabel, string.IsNullOrEmpty(value))) { value = ""; changed = true; }
                if (perStage && ImGui.Selectable("(the game's own music)", value == Player.GameMusic)) { value = Player.GameMusic; changed = true; }
                foreach (var p in Store.Settings.Playlists)
                    if (ImGui.Selectable(p.Name, string.Equals(p.Name, value, StringComparison.OrdinalIgnoreCase))) { value = p.Name; changed = true; }
                ImGui.EndCombo();
            }
            return changed;
        }

        private static string StageName(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            var e = StageCatalog.Entries.FirstOrDefault(s => s.Kind == MapKind.Stage && s.Stage.ToString() == key);
            return e != null ? e.Name : StageCatalog.Pretty(key);
        }

        // ------------------------------------------------------------------ files and options

        private static void MusicFolder()
        {
            ImGui.TextWrapped(Catalog.MusicFolder);
            ImUi.Hint("Types that play on this PC: " + string.Join(" ", Catalog.PlayableTypes) + ". Subfolders are fine. " +
                      "WAV is read by the mod; the others are decoded by Windows itself (Media Foundation), so nothing extra is needed. " +
                      "The game plays its own music with CRI, which can't read your files, so they play through Unity's audio next to it.");
            if (ImUi.Button("Rescan the folder")) _msg = Catalog.RescanMusic();
            ImGui.TextDisabled(_msg != "" ? _msg : $"{Catalog.CustomSongs.Count} music file(s).");
            if (Catalog.UnsupportedFiles.Count > 0)
                ImUi.Err($"Windows here can't decode: {string.Join(", ", Catalog.UnsupportedFiles.Take(6))}{(Catalog.UnsupportedFiles.Count > 6 ? " ..." : "")}. " +
                         "For OGG / Opus files, Microsoft's free \"Web Media Extensions\" adds them; or convert them (e.g. to MP3, FLAC or WAV).");
        }

        private static void Options()
        {
            var s = Store.Settings;
            float vol = s.CustomVolume * 100f;
            ImUi.Field("Your files' volume");
            if (ImGui.SliderFloat("##cvol", ref vol, 0f, 200f, "%.0f %%")) s.CustomVolume = vol / 100f;
            if (ImGui.IsItemDeactivatedAfterEdit()) Store.Save();
            ImUi.Tip("Next to the game's music volume (Options > BGM and master), which also applies to your files.");
            bool show = s.ShowNowPlaying;
            if (ImGui.Checkbox("Show the song's name when a playlist song starts", ref show)) { s.ShowNowPlaying = show; Store.Save(); }
            bool locked = s.AllowLockedSongs;
            if (ImGui.Checkbox("Allow base-game songs this save hasn't unlocked yet", ref locked)) { s.AllowLockedSongs = locked; Store.Save(); }
            ImUi.Tip("DLC songs always need their pack.");
            ImGui.TextDisabled(AcbInfo.Report);
        }
    }
}
