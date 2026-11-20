using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Il2Cpp;
using Il2CppApp.KatamariSin;
using LongLostCousins.Data;
using LongLostCousins.Dressing;
using LongLostCousins.Select;
using OUAKModAPI;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LongLostCousins.Test
{
    /// <summary>
    /// Test only: <c>UserData/LongLostCousins/cousintest.flag</c> runs the in-game check once, then quits the game.
    /// The flag turns Burda on for this session only (settings.json is never written) and is deleted when read.
    /// Words in the flag: <c>style new</c> or <c>style old</c> (new by default), <c>slot N</c> (the last used slot by
    /// default), <c>stage</c> (also test a story stage), <c>results</c> (also end the stage and test the results),
    /// <c>select</c> (start with Burda not chosen and pick her on the game's select screen on the ship, then the Prince).
    /// A backup of the saves is made before the slot is loaded, and every save write is blocked for the whole session.
    /// Lines start with [CousinTest]; the last one is <c>DONE pass=N fail=M</c>. Pictures go to
    /// UserData/LongLostCousins/TestShots.
    /// </summary>
    internal static class CousinTest
    {
        private static readonly ModLog Log = LongLostCousinsMod.Log.In("Test");
        private static string Shots => Path.Combine(Settings.Folder, "TestShots");
        private const string Cousin = "burda";
        // The design's groups, and 10: the first group with the cheek (a multiply-blended overlay) for its picture.
        private static readonly int[] FaceGroups = { 0, 9, 10, 12, 15, 17, 26 };

        private static bool _running;
        private static string _style = "new";
        private static int _slot = -1;
        private static bool _stage, _results, _select;
        private static int _pass, _fail;
        private static string _setupFailure;
        private static IEnumerator<object> _script;
        private static float _resume, _deadline;
        private static Wait _wait;

        public static bool Running => _running;

        private sealed class Wait
        {
            public Func<bool> Cond;
            public float Until;
            public string What;
            public bool Fatal;
        }

        /// <summary>Wait until <paramref name="cond"/> holds. When it times out: a FAIL, and the test ends if <paramref name="fatal"/>.</summary>
        private static Wait Until(Func<bool> cond, float timeout, string what, bool fatal = true) =>
            new() { Cond = cond, Until = Time.unscaledTime + timeout, What = what, Fatal = fatal };

        private static void Msg(string s) => Log.Info("[CousinTest] " + s);

        private static void Check(string what, bool ok, string detail = "")
        {
            if (ok) _pass++; else _fail++;
            Msg($"{(ok ? "PASS" : "FAIL")} {what}{(string.IsNullOrEmpty(detail) ? "" : ": " + detail)}");
        }

        /// <summary>Reads and deletes the flag. True when the test is armed (Burda is then on in memory).</summary>
        public static bool CheckFlag()
        {
            string flag = Path.Combine(Settings.Folder, "cousintest.flag");
            if (!File.Exists(flag)) return false;
            string text = "";
            try { text = File.ReadAllText(flag); } catch { }
            try { File.Delete(flag); } catch { }
            var w = text.Split(new[] { ' ', '\n', '\r', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.ToLowerInvariant()).ToList();
            int i = w.IndexOf("style");
            if (i >= 0 && i + 1 < w.Count) _style = w[i + 1];
            i = w.IndexOf("slot");
            if (i >= 0 && i + 1 < w.Count && int.TryParse(w[i + 1], out int s)) _slot = s - 1;
            _results = w.Contains("results");
            _stage = _results || w.Contains("stage");
            _select = w.Contains("select");
            _running = true;
            _script = Script().GetEnumerator();

            var c = Catalog.Find(Cousin);
            if (c == null || c.StylePath(_style) == null)
            {
                _setupFailure = $"the mod has no {Cousin} in style '{_style}' ({Catalog.Error ?? "not in cousins.json"})";
                Settings.ChooseInMemory(null, null);
                return true;
            }
            // With select, she starts not chosen (in memory), so only the select screen can turn her on.
            Settings.ChooseInMemory(_select ? null : Cousin, _style);
            bool guard = SaveGuard.Install();
            SaveGuard.Hold = true;
            Msg($"armed: {(_select ? "nobody chosen, " + Catalog.Find(Cousin)?.StyleLabel(_style) + " to be picked on the select screen" : Settings.Describe())}, " +
                $"slot {(_slot < 0 ? "last used" : (_slot + 1).ToString())}, " +
                $"{(_select ? "ship with the select screen" : "ship")}{(_results ? ", stage and results" : _stage ? " and stage" : " only")}; every save write blocked {guard}");
            if (!guard)
            {
                _setupFailure = "the save hooks are missing; not loading a slot";
                Settings.ChooseInMemory(null, null);
            }
            try { Directory.CreateDirectory(Shots); } catch { }
            return true;
        }

        /// <summary>MelonMod.OnLateUpdate, after the dressed models are kept in step. Never throws; quits the game at the end.</summary>
        public static void Tick()
        {
            if (!_running) return;
            float now = Time.unscaledTime;
            try
            {
                if (_deadline == 0f) _deadline = now + 1500f;
                if (_setupFailure != null) { Check("the test could start", false, _setupFailure); Finish(); return; }
                if (now > _deadline) { Check("the whole test finished within 25 minutes", false, $"scene {SceneLoader.ActiveScene}"); Finish(); return; }
                if (_wait != null)
                {
                    bool ok = false;
                    try { ok = _wait.Cond(); } catch { }
                    if (!ok)
                    {
                        if (now < _wait.Until) return;
                        Check("waiting for " + _wait.What, false, $"timed out in scene {SceneLoader.ActiveScene}");
                        if (_wait.Fatal) { Finish(); return; }
                    }
                    _wait = null;
                }
                if (now < _resume) return;
                if (!_script.MoveNext()) { Finish(); return; }
                switch (_script.Current)
                {
                    case float f: _resume = now + f; break;
                    case Wait wt: _wait = wt; break;
                }
            }
            catch (Exception e)
            {
                Check("the test ran without an exception", false, $"{e.GetType().Name}: {e.Message}\n{e.StackTrace}");
                Finish();
            }
        }

        private static void Finish()
        {
            if (!_running) return;
            _running = false;
            try
            {
                Msg($"save writes blocked: {SaveGuard.Blocked}; models dressed this session: {Dresser.DressedCount}");
                Msg($"DONE pass={_pass} fail={_fail}");
            }
            catch { }
            Application.Quit();
        }

        // ---------------------------------------------------------------- the script

        private static IEnumerable<object> Script()
        {
            yield return Until(TitleReady, 180f, "the title's save slots");
            yield return 4f;
            LoadSlot();
            yield return Until(() => SceneLoader.ActiveScene == "OujiShip" && GameRefs.InGameSession, 120f, "the Prince's Ship");
            Msg("on the ship");
            yield return Until(() => Contexts.PlazaOwnerModel() != null, 60f, "the walking cousin on the ship");
            yield return 4f;
            if (_select) foreach (var x in SelectScreenTest()) yield return x;
            else
            {
                // Picked in the tab, she is worn whatever cousin the save holds: say which one, for the log.
                Msg($"the save holds cousin {Chosen.SavedCousin()}; worn now: {Chosen.On}");
                foreach (var x in Ship()) yield return x;
            }
            if (_stage)
            {
                if (_select)
                {
                    // The select part ends with the Prince picked (she is off): turn her on again, in memory, for the stage.
                    Settings.ChooseInMemory(Cousin, _style);
                    Chosen.Refresh();
                    Msg($"for the stage: {Settings.Describe()} turned on again in memory (hooks in place: {Hooks.Installed})");
                }
                foreach (var x in Stage()) yield return x;
            }
        }

        private static bool TitleReady()
        {
            var ctrl = GameRefs.SaveCtrl;
            return SceneLoader.ActiveScene == "SelectData" && GameRefs.Global != null && GameRefs.Save != null &&
                   ctrl != null && ctrl.ActionWait() && ctrl.GameData?._SystemData != null;
        }

        /// <summary>The title's continue, on the chosen slot, after a backup of the saves.</summary>
        private static void LoadSlot()
        {
            var ctrl = GameRefs.SaveCtrl;
            var data = ctrl.GameData;
            int slot = _slot >= 0 ? _slot : data._SystemData._LastUser;
            var users = data._UserData?._UserDatas;
            if (users == null || slot < 0 || slot >= users.Length || users[slot]?._PriorityData == null || users[slot]._PriorityData._GameProgression <= 0)
                throw new InvalidOperationException($"slot {slot + 1} has no game in progress");
            BackupManager.Create("Long Lost Cousins test", $"made before the Long Lost Cousins test loaded slot {slot + 1}");
            Msg($"loading slot {slot + 1} (like the title's continue)");
            ctrl.SelectLoadDataToGameData(slot);
            var save = GameRefs.Save;
            save.SetUseDataLogoMonoGetFlg();
            OujiShipSponePointList.s_SponeIndex = OujiShipSponePointList.ElevatorSpone;
            save.FrontSceneSelectData = true;
            SingletonMonoBehaviour<EncorsSaveDataChecker>.Instance.CheckEncorsContentsData();
            GameRefs.Sound?.StopBGM(0.5f);
            Il2Cpp.SceneManager.LoadSceneAsync("OujiShip", LoadSceneMode.Single, true);
        }

        private static void Shot(string name)
        {
            FrameCapture.Request(name, Shots);
            Msg("picture " + name);
        }

        private static Wait PictureTaken() => Until(() => !FrameCapture.Busy, 5f, "the picture to be taken", false);

        private static DressedModel ByContext(string context)
        {
            foreach (var m in Dresser.Models)
                if (m.Context.StartsWith(context, StringComparison.Ordinal) && m.Root != null) return m;
            return null;
        }

        private static string Why() => Dresser.LastProblem ?? "nothing logged as a problem";

        // ---------------------------------------------------------------- the Prince's Ship

        private static IEnumerable<object> Ship()
        {
            yield return Until(() => Dresser.FindFor(Contexts.PlazaOwnerModel()) != null, 20f, "the walking cousin to be dressed", false);
            var dm = Dresser.FindFor(Contexts.PlazaOwnerModel());
            Check("ship: the walking cousin is dressed as " + Settings.Describe(), dm != null, dm != null ? U.PathOf(dm.Root.transform) : Why());
            if (dm != null) CheckModel("ship walking cousin", dm, true);

            yield return Until(() => ByContext("Plaza UI") != null, 20f, "the ship's UI copy to be dressed", false);
            var ui = ByContext("Plaza UI");
            int uiObjects = 0;
            try { uiObjects = UnityEngine.Object.FindObjectsOfType<UIOuji>().Length; } catch { }
            Check("ship: the UI copy of the walking cousin is dressed", ui != null,
                  (ui != null ? U.PathOf(ui.Root.transform) + ", " : Why() + ", ") + $"{uiObjects} active UIOuji object(s)");
            if (ui != null) CheckModel("ship UI copy", ui, false);

            Shot("ship_" + _style);
            yield return PictureTaken();
            if (dm != null) foreach (var x in Faces(dm)) yield return x;
        }

        /// <summary>The checks every dressed model gets: the game hidden, her shown, bones and layer in step.</summary>
        private static void CheckModel(string name, DressedModel dm, bool mustBeSeen)
        {
            var all = dm.Root.GetComponentsInChildren<Renderer>(true);
            var on = new List<string>();
            int game = 0;
            for (int i = 0; i < all.Length; i++)
            {
                var r = all[i];
                if (r is null || dm.Own.Contains(r.Pointer)) continue;
                game++;
                if (r.enabled) on.Add(r.name);
            }
            Check($"{name}: none of the game's renderers is on", on.Count == 0,
                  on.Count == 0 ? $"{game} game renderer(s), all off" : $"{on.Count} of {game} on: {string.Join(", ", on.Take(8))}");

            var body = dm.Pieces.Where(p => p.Role == MeshRole.Body).ToList();
            // A UI copy may sit inactive while no menu shows it: then only her renderers' own switch is hers to set.
            bool rootActive = dm.Root.activeInHierarchy;
            int bodyOn = body.Count(p => p.Renderer.enabled && (!rootActive || p.Go.activeInHierarchy));
            Check($"{name}: her body pieces are on", body.Count > 0 && bodyOn == body.Count,
                  $"{bodyOn} of {body.Count}{(rootActive ? "" : " (the model is inactive now; renderers checked only)")}");
            int shown = dm.Pieces.Count(p => p.Renderer.enabled && p.Go.activeInHierarchy);
            int seen = dm.Pieces.Count(p => p.Renderer.isVisible);
            if (mustBeSeen) Check($"{name}: a camera draws her", seen > 0, $"{seen} of her {shown} shown renderers drawn");
            else Msg($"{name}: {seen} of her {shown} shown renderers drawn by a camera now");

            var data = dm.Data;
            int w = dm.Assets.FollowJoint >= 0 ? dm.Assets.FollowJoint : data.JointIndex("JNT_waist");
            if (w < 0 || dm.GameJoints[w] == null || dm.GameJoints[w].parent == null)
                Check($"{name}: her waist follows the game's waist", false, "no waist joint to compare");
            else
            {
                var g = dm.GameJoints[w];
                var h = dm.HerJoints[w];
                var offset = w == dm.Assets.FollowJoint ? dm.Assets.FollowOffset : U.V3(0f, 0f, 0f);
                var expected = g.parent.TransformPoint(U.Add(g.localPosition, offset));
                float cm = Math.Abs(dm.Model.lossyScale.y);
                if (cm <= 0f) cm = 1f;
                float off = U.Length(U.Sub(h.position, expected)) / cm;
                float fromGame = U.Length(U.Sub(h.position, g.position)) / cm;
                Check($"{name}: her waist follows the game's waist", off < 0.05f,
                      $"{U.F(off)} cm from where it belongs, {U.F(fromGame)} cm from the game's waist (offset {U.F(offset)} cm, model scale {U.F(cm)})");
            }

            // World space, not the local values the follower has just copied: this catches a wrong parent, a misplaced
            // marker or root, and a joint the follower misses.
            float worst = 0f;
            string worstName = "-";
            int compared = 0;
            for (int i = 0; i < data.Joints.Length; i++)
            {
                if (dm.GameJoints[i] == null || !data.Joints[i].CopiesRotation) continue;
                compared++;
                float a = U.Angle(dm.HerJoints[i].rotation, dm.GameJoints[i].rotation);
                if (a > worst) { worst = a; worstName = data.Joints[i].Name; }
            }
            Check($"{name}: her joints face the way the game's do (world)", compared > 0 && worst < 0.05f, $"largest difference {U.F(worst)} degrees ({worstName}) over {compared} joints");

            // Where her rig matches the game's (no waist offset), every joint must stand where the game's does.
            var fo = dm.Assets.FollowOffset;
            if (U.Length(fo) < 0.001f)
            {
                float cmScale = Math.Abs(dm.Model.lossyScale.y);
                if (cmScale <= 0f) cmScale = 1f;
                float far = 0f;
                string farName = "-";
                for (int i = 0; i < data.Joints.Length; i++)
                {
                    if (dm.GameJoints[i] == null) continue;
                    float d = U.Length(U.Sub(dm.HerJoints[i].position, dm.GameJoints[i].position)) / cmScale;
                    if (d > far) { far = d; farName = data.Joints[i].Name; }
                }
                // Logged, not checked: her rest joints are the Prince's, and the game's cousin may be built otherwise.
                Msg($"{name}: her joints stand {U.F(far)} cm at most from the game's ({farName}); the game's cousin may be built otherwise than the Prince");
            }

            int want = dm.LayerSource != null ? dm.LayerSource.layer : dm.Model.gameObject.layer;
            int bad = dm.Pieces.Count(p => p.Go.layer != want) + (dm.Marker.gameObject.layer != want ? 1 : 0);
            Msg($"{name}: layer {want} (body_m's), {bad} of her object(s) on another");
        }

        // ---------------------------------------------------------------- faces

        private static IEnumerable<object> Faces(DressedModel dm)
        {
            var face = dm.Face;
            if (face == null) { Check("faces: the walking cousin has a face component", false, "none found"); yield break; }
            int before = face.prevFaceIndex;
            foreach (int g in FaceGroups)
            {
                yield return PictureTaken();
                if (dm.Root == null) { Check("faces: the walking cousin is still there", false); yield break; }
                // SetNoFace first, so SetFace runs in full and no blink is under way.
                face.SetNoFace();
                face.SetFace(g);
                dm.ApplyFace(true);
                CheckFace(dm, g);
                Shot($"face_{_style}_{g:00}");
                yield return 0.5f;
            }
            if (dm.Root != null)
            {
                face.SetNoFace();
                face.SetFace(before >= 0 ? before : 0);
            }
        }

        private static void CheckFace(DressedModel dm, int g)
        {
            var data = dm.Data;
            var groups = data.Face.Groups;
            if (g >= groups.Length) { Check($"face {g}: the data has this group", false, $"{groups.Length} groups"); return; }
            var grp = groups[g];
            if (data.Cards)
            {
                var on = dm.Pieces.Where(p => p.Role == MeshRole.Card && p.Renderer.enabled && p.Go.activeInHierarchy).Select(p => p.Card).ToList();
                Check($"face {g}: card {grp.Card} shows, and no other card", on.Count == 1 && on[0] == grp.Card,
                      "shown: " + (on.Count == 0 ? "none" : string.Join(", ", on)));
                return;
            }
            // What should show, from where each piece was placed: a piece under a game slot shows with that slot (the group's
            // pieces); one without a slot to follow shows only if it is one of group 0's; then the group's own show and hide.
            var underSlot = new HashSet<int>(dm.Pieces.Where(p => p.UnderSlot).Select(p => p.Mesh));
            var want = new SortedSet<int>(grp.Pieces.Where(b => b != LlcmModel.None8).Select(b => (int)b).Where(underSlot.Contains));
            foreach (var p in dm.Pieces) if (p.Base && !p.UnderSlot && data.Meshes[p.Mesh].IsSlotPiece && !p.Extra) want.Add(p.Mesh);
            foreach (byte b in grp.Show) want.Add(b);
            foreach (byte b in grp.Hide) want.Remove(b);
            var shown = new SortedSet<int>(dm.Pieces.Where(p => p.Role != MeshRole.Body && p.Renderer.enabled && p.Go.activeInHierarchy).Select(p => p.Mesh));
            Check($"face {g}: {Names(data, want)} show, and no other face piece", want.SetEquals(shown), "shown: " + Names(data, shown));
        }

        private static string Names(LlcmModel data, IEnumerable<int> meshes)
        {
            var n = meshes.Select(i => data.Meshes[i].Name).ToList();
            return n.Count == 0 ? "none" : string.Join(", ", n);
        }

        // ---------------------------------------------------------------- the game's select screen (word "select")

        private static IntPtr WalkerPtr() => Contexts.PlazaOwnerModel()?.Pointer ?? IntPtr.Zero;
        private static bool _openedDirectly, _eventDirect, _changeDirect, _reopened;

        /// <summary>Puts the cursor on page <paramref name="page"/>, slot <paramref name="index"/> (ChangePage, then SetCursorIndex).</summary>
        private static void FocusSlot(SelectionMenuController menu, int page, int index)
        {
            menu._pageIndex = page;
            menu.ChangePage();
            menu._iconWindowControllers[page].SetCursorIndex(index);
        }

        /// <summary>
        /// Focuses the slot again in the same frame and confirms it as the menu's update and the dialog's yes do, inside
        /// the menu-update scope (EnterScope / LeaveScope, as SelectionMenuControllerUpdate's hook makes it): the
        /// "already selected" compare (the saved cousin as the menu reads it, against the focused id), then SetCharater,
        /// then _ischange. False (a FAIL) when the cursor is not on it or the game would refuse it, so the wrong cousin is
        /// never confirmed.
        /// </summary>
        private static bool Confirm(SelectionMenuController menu, int page, int index, int wantId, string who)
        {
            FocusSlot(menu, page, index);
            var f = SelectScreen.Focused(menu);
            int id = f != null ? (int)f._ITOKONAME : -2;
            if (id != wantId) { Check($"select: the cursor is on {who} before the confirm", false, $"focused id {id}, not confirming"); return false; }
            var save = GameRefs.Save;
            SelectScreen.EnterScope();
            try
            {
                int shown = save != null ? save.GetSelectPlayerId : -1;
                bool pickable = shown != id;
                Check($"select: the menu lets {who} be picked (not \"already selected\")", pickable,
                      $"the saved cousin as the menu reads it {shown}, focused id {id}, marker hooks {SelectScreen.ScopeInstalled}");
                if (!pickable) return false;
                Msg($"confirming {who} inside the menu-update scope (SetCharater, then _ischange = true, as the dialog's yes does)");
                menu.SetCharater();
            }
            finally { SelectScreen.LeaveScope(null); }
            menu._ischange = true;
            return true;
        }

        /// <summary>After a confirm: the counters are back to zero and the net never fired.</summary>
        private static void CheckAfterConfirm(string who)
        {
            var g = GameRefs.Global;
            int now = g != null ? g._nowCharacterID : -1;
            // Both confirms in this test store the Prince.
            Check($"select: GlobalManager's current cousin is the Prince (0) after {who}", now == 0,
                  $"_nowCharacterID {now}, _selectPlayerId {RawSavedCousin()}");
            var save = GameRefs.Save;
            int plain = save != null ? save.GetSelectPlayerId : -1;
            Check($"select: the scope is closed after {who}", SelectScreen.ScopeDepth == 0 && SelectScreen.SuspendDepth == 0 && plain == RawSavedCousin(),
                  $"scope depth {SelectScreen.ScopeDepth}, suspended {SelectScreen.SuspendDepth}, GetSelectPlayerId outside the menu {plain}");
            Check($"select: the net never fired (through {who})", SelectScreen.NetFirings == 0, $"{SelectScreen.NetFirings} firing(s)");
        }

        /// <summary>Why the game may not rebuild the walking cousin, for a FAIL line.</summary>
        private static string RebuildHint() =>
            $"menu opened {(_openedDirectly ? "directly (SetActive)" : "the game's way")}, ship menu event {(_eventDirect ? "activated directly" : "activated by the ship")}, " +
            $"cousin change {(_changeDirect ? "started directly" : "started by the ship")}; {ShipEvents()}";

        private static int RawSavedCousin()
        {
            var save = GameRefs.Save;
            return save != null ? save._selectPlayerId : -1;
        }

        private static IEnumerable<object> SelectScreenTest()
        {
            var walker = Contexts.PlazaOwnerModel();
            // Set up by CheckFlag (nobody chosen), so information, not a check.
            Msg($"at the start: settings {Settings.Describe()}, worn {Chosen.On}, dressing hooks {Hooks.Installed}, walking cousin dressed {Dresser.FindFor(walker) != null}, saved cousin {RawSavedCousin()}");
            Check("select: the select screen's hooks are in place", SelectScreen.Ready, SelectScreen.Ready ? $"{SelectScreen.Expected} hooks" : "see the log");
            if (!SelectScreen.Ready) yield break;
            Check("select: the marker hooks are out while the settings name nobody", !SelectScreen.ScopeInstalled);

            // ---- open the menu and look at her slot
            SelectionMenuController menu = null;
            foreach (var x in OpenMenu(m => menu = m)) yield return x;
            if (menu == null) yield break;
            var slot = SelectScreen.SlotOf(menu);
            var wins = menu._iconWindowControllers;
            LogSlotTree("her slot (page 4, slot 14)", slot);
            LogSlotTree("Young King's slot (page 4, slot 7)", Slot(menu, SelectScreen.Page, 6));
            CheckSlot(menu, slot);

            Msg($"to page 4, slot 14 (ChangePage to {SelectScreen.Page}, cursor {SelectScreen.Slot})");
            FocusSlot(menu, SelectScreen.Page, SelectScreen.Slot);
            yield return 1.5f;
            var focused = SelectScreen.Focused(menu);
            Check("select: the cursor is on her slot", focused != null && slot != null && focused.Pointer == slot.Pointer,
                  $"page {menu._pageIndex}, cursor {wins[SelectScreen.Page]._cursorIndex}, focused id {(focused != null ? (int)focused._ITOKONAME : -2)}");
            Shot("select_page4_" + _style);
            yield return PictureTaken();

            // ---- confirm her, as the dialog's yes does
            IntPtr before = WalkerPtr();
            if (!Confirm(menu, SelectScreen.Page, SelectScreen.Slot, SelectScreen.ModId, "Burda")) yield break;
            Check("select: the save still holds the Prince", RawSavedCousin() == 0 && Chosen.SavedCousin() == 0,
                  $"_selectPlayerId {RawSavedCousin()}, GetSelectPlayerId {Chosen.SavedCousin()}, GlobalManager._nowCharacterID {GameRefs.Global?._nowCharacterID}");
            Check("select: the settings say Burda, picked on the screen", Settings.Active && Settings.CousinId == Cousin && Settings.OnScreen && Settings.Style == _style,
                  $"{Settings.Describe()}, on the screen {Settings.OnScreen}");
            Msg($"settings kept in memory only (set up by the test): {Settings.MemoryOnly}");
            Check("select: she is worn and the dressing hooks are in place", Chosen.On && Hooks.Installed && Hooks.Count == Hooks.Expected,
                  $"worn {Chosen.On}, {Hooks.Count} of {Hooks.Expected} hooks");
            Check("select: her slot carries 100 again", slot != null && (int)slot._ITOKONAME == SelectScreen.ModId, slot != null ? "id " + (int)slot._ITOKONAME : "no slot");
            CheckAfterConfirm("the confirm of Burda");
            yield return 0.2f;   // a LateUpdate: the marker hooks follow the settings there
            Check("select: the marker hooks come in once the settings name her", SelectScreen.ScopeInstalled,
                  SelectScreen.ScopeInstalled ? "in" : SelectScreen.ScopeFailed ? "they could not be made (see the log)" : "not in");

            // ---- the ship's new walking cousin, after the game's swap
            _changeDirect = false;
            foreach (var x in AwaitRebuild(before, "the confirm")) yield return x;
            Check("select: the game rebuilt the walking cousin", WalkerPtr() != IntPtr.Zero && WalkerPtr() != before, RebuildHint());
            yield return 3f;
            foreach (var x in Ship()) yield return x;

            // ---- the Prince's slot: she turns off
            before = WalkerPtr();
            menu = null;
            foreach (var x in OpenMenu(m => menu = m)) yield return x;
            if (menu == null) yield break;
            wins = menu._iconWindowControllers;
            slot = SelectScreen.SlotOf(menu);
            if (_reopened)
            {
                // The game did not close and reset the screen, so its markers are the ones from before the confirm. The
                // screen places them in IconWindowController.SetIcon when it opens; called here directly for the two pages.
                Msg("[direct] refreshing the current-cousin markers: IconWindowController.SetIcon on pages 1 and 4 (the screen was not reset by the game)");
                wins[0].SetIcon();
                wins[SelectScreen.Page].SetIcon();
            }
            var marks = wins[SelectScreen.Page]._selectIcon;
            bool marked = marks != null && SelectScreen.Slot < marks.Length && marks[SelectScreen.Slot] != null && marks[SelectScreen.Slot].activeSelf;
            var princeMarks = wins[0]._selectIcon;
            bool princeMarked = princeMarks != null && princeMarks.Length > 0 && princeMarks[0] != null && princeMarks[0].activeSelf;
            Check("select: the current-cousin marker sits on her slot, not the Prince's", marked && !princeMarked,
                  $"hers {marked}, the Prince's {princeMarked}, marker hooks {SelectScreen.ScopeInstalled}, screen reset by the game {!_reopened}");
            LogSlotTree("her slot, reopened", slot);

            // ---- the scope and its suspension, on the save as it is now (the Prince, with her worn)
            var save = GameRefs.Save;
            int inScope, suspended;
            SelectScreen.EnterScope();
            try
            {
                inScope = save != null ? save.GetSelectPlayerId : -1;
                using (SelectScreen.Suspended()) suspended = save != null ? save.GetSelectPlayerId : -1;
            }
            finally { SelectScreen.LeaveScope(null); }
            Check("select: inside the menu's scope the saved Prince reads as her id, and as the Prince when suspended", inScope == SelectScreen.ModId && suspended == 0,
                  $"in the scope {inScope}, suspended {suspended}, depth after {SelectScreen.ScopeDepth}");

            Msg("to page 1, slot 1 (the Prince)");
            FocusSlot(menu, 0, 0);
            yield return 1f;
            if (!Confirm(menu, 0, 0, 0, "the Prince")) yield break;
            Check("select: the Prince turns her off", !Settings.Active && !Chosen.On && !Hooks.Installed,
                  $"settings {Settings.Describe()}, worn {Chosen.On}, dressing hooks {Hooks.Installed}");
            // The Prince's id is 0, so the save holding 0 here is information, not a check.
            Msg("the save after the Prince: _selectPlayerId " + RawSavedCousin());
            CheckAfterConfirm("the confirm of the Prince");
            yield return 0.2f;
            Check("select: the marker hooks go once the settings name nobody", !SelectScreen.ScopeInstalled);
            _changeDirect = false;
            foreach (var x in AwaitRebuild(before, "the Prince")) yield return x;
            yield return 3f;
            var plain = Contexts.PlazaOwnerModel();
            bool rebuilt = plain != null && plain.Pointer != before;
            Check("select: the rebuilt walking cousin is the game's own", rebuilt && Dresser.FindFor(plain) == null && !Dresser.CarriesMarker(plain),
                  !rebuilt ? "the game did not rebuild it; " + RebuildHint() : $"dressed {Dresser.FindFor(plain) != null}, marker {Dresser.CarriesMarker(plain)}");
            Check("select: the net never fired (whole run)", SelectScreen.NetFirings == 0, $"{SelectScreen.NetFirings} firing(s)");
            Shot("select_prince_" + _style);
            yield return PictureTaken();
        }

        /// <summary>Slot <paramref name="index"/> of window <paramref name="page"/>, or null.</summary>
        private static ShortcutIconData Slot(SelectionMenuController menu, int page, int index)
        {
            var wins = menu._iconWindowControllers;
            if (wins == null || page >= wins.Length || wins[page] == null) return null;
            var slots = wins[page]._shortcutIconDatas;
            return slots != null && index < slots.Length ? slots[index] : null;
        }

        /// <summary>The width an Image is drawn at in its slot's units (rect width times its own scale and its parents' up to the slot).</summary>
        private static float DrawnWidth(UnityEngine.UI.Image img, Transform slot)
        {
            float w = img.rectTransform.rect.width;
            for (var t = img.transform; t != null && t.Pointer != slot.Pointer; t = t.parent) w *= t.localScale.x;
            return w;
        }

        /// <summary>Her slot on the open menu: id 100, the found look with her sprite, her name, no New mark.</summary>
        private static void CheckSlot(SelectionMenuController menu, ShortcutIconData slot)
        {
            if (slot == null) { Check("select: page 4 has slot 14", false); return; }
            Check("select: page 4 slot 14 carries the mod's id 100", (int)slot._ITOKONAME == SelectScreen.ModId, "id " + (int)slot._ITOKONAME);
            var c = SelectScreen.Cousin;
            var want = CousinIcon.Get(c, Settings.StyleFor(c));
            var icon = slot._iconImage;
            bool back = slot._backImage != null && slot._backImage.enabled;
            bool iconOn = icon != null && icon.enabled && icon.gameObject.activeInHierarchy;
            var sprite = icon != null ? icon.sprite : null;
            bool hers = want != null && sprite != null && sprite.Pointer == want.Pointer;
            var tex = sprite != null ? sprite.texture : null;
            bool textured = tex != null && tex.width > 0;
            float alpha = icon != null ? icon.color.a : 0f;
            // Drawn at a found slot's size: Young King's icon on the same page, or the scene's 1024 x 0.2.
            var king = Slot(menu, SelectScreen.Page, 6);
            float kingWidth = king != null && king._iconImage != null ? DrawnWidth(king._iconImage, king.transform) : 0f;
            float refWidth = kingWidth > 1f ? kingWidth : 204.8f;
            float width = icon != null ? DrawnWidth(icon, slot.transform) : 0f;
            bool sized = width >= refWidth * 0.5f;
            bool blank = slot._blankImage != null && slot._blankImage.enabled;
            bool hatena = slot._hatenaImage != null && slot._hatenaImage.enabled;
            bool released = slot._oujiData != null && slot._oujiData._isReleased;
            Check("select: her slot draws her icon (icon shown with her sprite and texture, at a found slot's size, opaque)", iconOn && hers && textured && sized && alpha > 0.5f,
                  $"icon shown {iconOn}, her sprite {hers} ({(sprite != null ? sprite.name : "none")}), texture {(tex != null ? tex.name + " " + tex.width + "x" + tex.height : "none")}, " +
                  $"drawn {U.F(width)} wide (Young King's {U.F(kingWidth)}), alpha {U.F(alpha)}");
            Check("select: her slot looks found (back on, blank and question mark off, released)", back && !blank && !hatena && released,
                  $"back {back}, blank {blank}, question mark {hatena}, released {released}");
            string name = slot._oujiData != null ? slot._oujiData._name : null;
            string text = slot._text != null ? slot._text.text : null;
            Check("select: her name is Burda", name == "Burda" && text == "Burda", $"OujiData name '{name}', text '{text}'");
            var news = menu._iconWindowControllers[SelectScreen.Page]._newIcon;
            bool isNew = news != null && SelectScreen.Slot < news.Length && news[SelectScreen.Slot] != null && news[SelectScreen.Slot].activeSelf;
            Check("select: her slot has no New mark", !isNew);
        }

        /// <summary>The ship's menu event pieces and their state, for the log.</summary>
        private static string ShipEvents()
        {
            try
            {
                var omb = UnityEngine.Object.FindObjectOfType<OrderToMenuBehavior>();
                var etm = UnityEngine.Object.FindObjectOfType<EventToMenuController>();
                var etc = UnityEngine.Object.FindObjectOfType<EventToChangeCharacter>();
                var ctrl = GameRefs.SaveCtrl;
                var h = omb != null ? omb.m_UpdateHandler : null;
                var och = etm != null ? etm.m_OrderChangeChara : null;
                return $"OrderToMenuBehavior {(omb == null ? "none" : $"OrderEvent {omb.OrderEvent}, update handler {(h == null ? "none" : h.UpdateHandle.ToString())}")}, " +
                       $"save idle (ActionWait) {(ctrl != null ? ctrl.ActionWait().ToString() : "-")}, " +
                       $"EventToMenuController {(etm == null ? "none" : "active " + etm.m_ActiveEventHandle)}, " +
                       $"OrderToChangeCharacter {(och == null ? "none" : "OrderEvent " + och.OrderEvent)}, " +
                       $"EventToChangeCharacter {(etc == null ? "none" : $"active {etc.m_ActiveEventHandle}, step {etc.m_ChangeCharaEnum}")}";
            }
            catch (Exception e) { return "ship events unreadable: " + e.Message; }
        }

        /// <summary>OrderToMenuBehavior.Update's own conditions for taking up an open menu: no order yet, its update handler on, the save idle.</summary>
        private static bool MenuAllowed()
        {
            var omb = UnityEngine.Object.FindObjectOfType<OrderToMenuBehavior>();
            if (omb == null) return true;
            var h = omb.m_UpdateHandler;
            var ctrl = GameRefs.SaveCtrl;
            return !omb.OrderEvent && (h == null || h.UpdateHandle) && (ctrl == null || ctrl.ActionWait());
        }

        /// <summary>
        /// Opens The Prince &amp; Cousins on the ship by the game's own calls. Once OrderToMenuBehavior.Update would take up an
        /// open menu: the pause menu (MenuManager.ActiveMenu(0)), which OrderToMenuBehavior.Update answers by making the
        /// ship's EventToMenuController active (it runs MenuManager.MenuStateUpdate and later sees _ischange); then the Mates
        /// entry (MenuController.SelectUpdate(1)), which MenuStateUpdate turns into SelectionMenuController.SetActive(true).
        /// A step the game does not take is logged with the state and then made by a direct call, labelled [direct].
        /// A screen still open from before is used as it is (<see cref="_reopened"/>).
        /// </summary>
        private static IEnumerable<object> OpenMenu(Action<SelectionMenuController> got)
        {
            MenuManager mm = null;
            try { mm = SingletonMonoBehaviour<MenuManager>.instance; } catch { }
            if (mm == null) mm = UnityEngine.Object.FindObjectOfType<MenuManager>();
            var menu = mm != null ? mm._selectionMenuController : null;
            if (menu == null) { Check("select: the ship has the select screen", false, mm == null ? "no MenuManager" : "MenuManager has no SelectionMenuController"); yield break; }
            var mc = mm._menuController;
            var ev = UnityEngine.Object.FindObjectOfType<EventToMenuController>();
            string State() => $"_menuState {(int)mm._menuState}, _isActiveMenu {mm._isActiveMenu}, pause menu canvas {(mc != null ? mc._canvasActive.ToString() : "-")}, " +
                              $"takes input {(mc != null ? mc._onUpdate.ToString() : "-")}, select screen open {menu.ActiveSelf}, _ischange {menu._ischange}; {ShipEvents()}";
            _reopened = false;
            if (menu.ActiveSelf)
            {
                Msg("the select screen is still open (the game did not close and reset it): " + State());
                _reopened = true;
                yield return 1f;
                Check("select: the select screen is open", menu.ActiveSelf, State());
                got(menu);
                yield break;
            }
            _openedDirectly = _eventDirect = false;
            yield return Until(MenuAllowed, 30f, "the ship to take up an open menu (OrderToMenuBehavior.Update's conditions)", false);
            Msg("opening the pause menu (MenuManager.ActiveMenu(0)): " + State());
            mm.ActiveMenu(0);
            yield return Until(() => ev == null || ev.m_ActiveEventHandle, 3f, "OrderToMenuBehavior.Update to make the ship's EventToMenuController active", false);
            if (ev != null && !ev.m_ActiveEventHandle)
            {
                var omb = UnityEngine.Object.FindObjectOfType<OrderToMenuBehavior>();
                Msg("[direct] the ship did not take up the open menu (" + State() + "); calling OrderToMenuBehavior.OrderActiveEvent(), " +
                    "the activation its Update makes when the menu is open");
                _eventDirect = true;
                if (omb != null) omb.OrderActiveEvent();
                else Msg("no OrderToMenuBehavior found");
                yield return Until(() => ev.m_ActiveEventHandle, 3f, "the ship's EventToMenuController to be active", false);
            }
            yield return Until(() => mc == null || mc._onUpdate, 10f, "the pause menu to take input", false);
            Msg("choosing Mates (MenuController.SelectUpdate(1)): " + State());
            if (mc != null) mc.SelectUpdate(1);
            yield return Until(() => menu.ActiveSelf, 15f, "the select screen to open the game's way", false);
            if (!menu.ActiveSelf)
            {
                Msg("[direct] the game's way did not open it (" + State() + "); opening it directly (SelectionMenuController.SetActive(true))");
                _openedDirectly = true;
                menu.SetActive(true);
                yield return Until(() => menu.ActiveSelf, 10f, "the select screen to open", false);
            }
            yield return 2f;   // its fade-in
            Check("select: the select screen is open", menu.ActiveSelf, State());
            if (menu.ActiveSelf) got(menu);
        }

        /// <summary>
        /// After a confirm: the ship's EventToMenuController sees _ischange and starts the cousin change
        /// (OrderToChangeCharacter.OrderActiveEvent; EventToChangeCharacter then rebuilds the walker through ModelLoader).
        /// If the change has not started within 5 s, it is logged and started by that same call, directly ([direct]).
        /// </summary>
        private static IEnumerable<object> AwaitRebuild(IntPtr before, string after)
        {
            var etm = UnityEngine.Object.FindObjectOfType<EventToMenuController>();
            var etc = UnityEngine.Object.FindObjectOfType<EventToChangeCharacter>();
            bool Rebuilt() => WalkerPtr() != IntPtr.Zero && WalkerPtr() != before;
            bool Started() => Rebuilt() || (etc != null && etc.m_ActiveEventHandle);
            yield return Until(Started, 5f, "the ship to start the cousin change after " + after, false);
            if (!Started())
            {
                var och = etm != null ? etm.m_OrderChangeChara : null;
                Msg($"[direct] the ship did not start the cousin change ({ShipEvents()}); calling OrderToChangeCharacter.OrderActiveEvent(), " +
                    "what EventToMenuController.Update does when it sees _ischange");
                _changeDirect = true;
                if (och != null) och.OrderActiveEvent();
                else Msg("no OrderToChangeCharacter found");
            }
            yield return Until(Rebuilt, 60f, "the game to rebuild the walking cousin after " + after, false);
            Msg("after the change: " + ShipEvents());
        }

        /// <summary>Every child of a slot, for the log: active flags, Image, sprite and texture, colour, rect and scale.</summary>
        private static void LogSlotTree(string label, ShortcutIconData slot)
        {
            if (slot == null) { Msg(label + ": no slot"); return; }
            try
            {
                var root = slot.transform;
                Msg($"{label} ({root.name}, id {(int)slot._ITOKONAME}): active {root.gameObject.activeSelf}/{root.gameObject.activeInHierarchy}, scale {U.F(root.localScale)}");
                LogChildren(root, 1);
            }
            catch (Exception e) { Msg(label + ": unreadable: " + e.Message); }
        }

        private static void LogChildren(Transform t, int depth)
        {
            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                var go = c.gameObject;
                string img = "";
                var im = go.GetComponent<UnityEngine.UI.Image>();
                if (im != null)
                {
                    var sp = im.sprite;
                    var tex = sp != null ? sp.texture : null;
                    var col = im.color;
                    img = $", Image enabled {im.enabled}, sprite {(sp != null ? sp.name : "none")}, texture {(tex != null ? tex.name + " " + tex.width + "x" + tex.height : "none")}, " +
                          $"colour ({U.F(col.r)}, {U.F(col.g)}, {U.F(col.b)}, {U.F(col.a)})";
                }
                var rt = c.TryCast<RectTransform>();
                string rect = rt != null ? $", rect {U.F(rt.rect.width)}x{U.F(rt.rect.height)}" : "";
                Msg($"{new string(' ', depth * 2)}[{i}] {c.name}: active {go.activeSelf}/{go.activeInHierarchy}{img}{rect}, scale {U.F(c.localScale)}");
                if (depth < 3) LogChildren(c, depth + 1);
            }
        }

        // ---------------------------------------------------------------- a story stage and its results

        private static IEnumerable<object> Stage()
        {
            var entry = StageCatalog.Entries.FirstOrDefault(s => s.Kind == MapKind.Stage && s.Internal == "Stage_Big1");
            if (entry == null) { Check("stage: Stage_Big1 is in the API's stage list", false); yield break; }
            Msg("load stage: " + SceneLoader.LoadStage(entry));
            yield return Until(() => GameRefs.Main != null && GameRefs.Main.PlayerInitiated, 180f, "the stage's player");

            float t0 = Time.unscaledTime;
            bool skippedFly = false;
            while (true)
            {
                float since = Time.unscaledTime - t0;
                if (since > 150f) { Check("stage: the stage starts playing", false, $"scene {SceneLoader.ActiveScene}"); yield break; }
                var gm = GameRefs.Main;
                if (gm == null) { yield return 0.25f; continue; }
                if (gm.MainUI != null && gm.MainUI.IsTipsActive()) { gm.MainUI.DeactivateTipsUI(); yield return 1f; continue; }
                if (gm.StartMessageDone && gm.IsGameRunning && Katamari.Core != null) break;
                if (!gm.StartMessageDone)
                {
                    if (gm._isInDemoCamera && !skippedFly && since > 5f) { Msg("skipping the fly-over"); gm.EndDemoCameraWithFade(); skippedFly = true; }
                    var gui = SingletonMonoBehaviour<GlobalUIController>.Instance;
                    if (!gm._isInDemoCamera && gui != null && gui.CanSkipMessage && since > 8f) { Msg("skipping the King's talk"); gui.SkipNowMessage(); yield return 1f; continue; }
                }
                yield return 0.1f;
            }
            Msg("the stage is playing");
            yield return 2f;

            var main = GameRefs.Main;
            var ouji = main.Ouji;
            var dm = ouji != null ? Dresser.FindFor(ouji.gameObject) : null;
            Check("stage: the player is dressed", dm != null, dm != null ? U.PathOf(dm.Root.transform) : Why());
            if (dm != null)
            {
                CheckModel("stage player", dm, true);
                var inList = new HashSet<IntPtr>();
                var list = ouji.rendList;
                if (list != null)
                    for (int i = 0; i < list.Count; i++) { var r = list[i]; if (r != null) inList.Add(r.Pointer); }
                int mine = dm.Pieces.Count(p => inList.Contains(p.Renderer.Pointer));
                Check("stage: her renderers are in the player's rendList", mine == dm.Pieces.Count, $"{mine} of {dm.Pieces.Count}; the list holds {inList.Count}");
            }
            var hud = main.UIOuji;
            var hd = hud != null ? Dresser.FindFor(hud.gameObject) : null;
            Check("stage: the HUD copy is dressed", hd != null, hd != null ? U.PathOf(hd.Root.transform) : (hud == null ? "MainGameManager.UIOuji is null" : Why()));
            if (hd != null) CheckModel("stage HUD copy", hd, false);
            Shot("stage_" + _style);
            yield return PictureTaken();

            if (dm != null)
            {
                var bodyRenderer = dm.LayerSource != null ? dm.LayerSource.GetComponent<Renderer>() : null;
                ouji.SetAlpha(0.5f);
                CheckAlpha(dm, bodyRenderer, "SetAlpha(0.5)", 0.5f);
                Shot("stage_alpha_" + _style);
                yield return PictureTaken();
                ouji.SetAlpha(1f);
                CheckAlpha(dm, bodyRenderer, "SetAlpha(1)", 1f);
            }

            if (_results) foreach (var x in Results()) yield return x;
        }

        private static float AlphaOf(Renderer r)
        {
            var m = r != null ? r.sharedMaterial : null;
            if (m == null || !m.HasProperty("_BaseColor")) return -1f;
            return m.GetColor("_BaseColor").a;
        }

        private static void CheckAlpha(DressedModel dm, Renderer body, string what, float asked)
        {
            float game = AlphaOf(body);
            float lo = float.MaxValue, hi = float.MinValue;
            int n = 0;
            foreach (var p in dm.Pieces)
            {
                float a = AlphaOf(p.Renderer);
                if (a < 0f) continue;
                n++;
                lo = Math.Min(lo, a);
                hi = Math.Max(hi, a);
            }
            bool ok = game >= 0f && Math.Abs(game - asked) < 0.01f && n > 0 && Math.Abs(lo - game) < 0.01f && Math.Abs(hi - game) < 0.01f;
            Check($"stage: {what} gives her materials the body's _BaseColor alpha", ok,
                  $"body {U.F(game)}, hers {(n > 0 ? U.F(lo) + " to " + U.F(hi) : "none")} over {n} renderer(s)");
        }

        /// <summary>What the results have so far: their setters, what stands under each holder, and the hook's count.</summary>
        private static string ResultsState()
        {
            try
            {
                var parts = new List<string>();
                foreach (var s in UnityEngine.Object.FindObjectsOfType<ResultMonoSetter>()) parts.Add("ResultMonoSetter " + HolderText(s.m_PlayCharacter != null ? s.m_PlayCharacter.transform : null));
                foreach (var s in UnityEngine.Object.FindObjectsOfType<ResultAAMonoSetter>()) parts.Add("ResultAAMonoSetter " + HolderText(s.m_PlayCharacter != null ? s.m_PlayCharacter.transform : null));
                if (parts.Count == 0) parts.Add("no results setter in the scene");
                var gui = SingletonMonoBehaviour<GlobalUIController>.Instance;
                string king = gui != null ? $"; King's message {gui.IsInDemoMessage}, can skip {gui.CanSkipMessage}" : "";
                return string.Join(", ", parts) + $"; SetPlayCharacter ran {Contexts.ResultSets}x{king}";
            }
            catch (Exception e) { return "results state unreadable: " + e.Message; }
        }

        private static string HolderText(Transform h)
        {
            if (h == null) return "(no holder)";
            var names = new List<string>();
            for (int i = 0; i < h.childCount; i++) names.Add(h.GetChild(i).name);
            return $"holder {h.name} [{string.Join(", ", names)}]";
        }

        private static IEnumerable<object> Results()
        {
            Msg("finish the stage: " + StageControl.FinishStage());
            float t0 = Time.unscaledTime;
            string scene = SceneLoader.ActiveScene;
            DressedModel rd;
            float nextReport = t0 + 20f;
            int immediate = 0;
            bool answered = false;
            bool shotTaken = false;
            while ((rd = ByContext("results")) == null)
            {
                if (Time.unscaledTime - t0 > 240f)
                {
                    Check("results: the results model is dressed", false, $"not within 240 s; scene {SceneLoader.ActiveScene}; {ResultsState()}; King's message closed {immediate}x; finish asked {(answered ? "and answered" : "never")}; {Why()}");
                    yield break;
                }
                if (SceneLoader.ActiveScene != scene) { scene = SceneLoader.ActiveScene; Msg("scene " + scene); }
                if (Time.unscaledTime >= nextReport)
                {
                    nextReport += 20f;
                    Msg($"waiting for the results, {Time.unscaledTime - t0:0} s: {ResultsState()}");
                    if (!shotTaken) { shotTaken = true; Shot("results_wait_" + _style); }
                }
                var gui = SingletonMonoBehaviour<GlobalUIController>.Instance;
                if (gui != null && gui.CanSkipMessage) { gui.SkipNowMessage(); yield return 1f; continue; }
                // "Are you finished?": answered All Done the way the game does after the King's last line.
                var gm = GameRefs.Main;
                if (gm != null && gm.IsInRetrySelection && !answered && Time.unscaledTime - t0 > 5f)
                {
                    answered = true;
                    Msg("answering \"Are you finished?\" with All Done (QuitSelectionUI, GameToResult)");
                    var ui = UnityEngine.Object.FindObjectOfType<MainGameUIManager>();
                    if (ui != null) ui.QuitSelectionUI();
                    gm.GameToResult();
                    yield return 2f;
                    continue;
                }
                // The King's closing line ("let's go home") waits for a press that SkipNowMessage does not stand in for.
                if (gui != null && gui.IsInDemoMessage && Time.unscaledTime - t0 > 5f)
                {
                    immediate++;
                    Msg("closing the King's message (SkipNowMessageImmediate)");
                    gui.SkipNowMessageImmediate();
                    yield return 2f;
                    continue;
                }
                yield return 0.25f;
            }
            yield return 3f;
            Check("results: the results model is dressed", rd.Root != null, rd.Root != null ? U.PathOf(rd.Root.transform) : "it went away");
            if (rd.Root == null) yield break;
            CheckModel("results model", rd, false);
            Shot("results_" + _style);
            yield return PictureTaken();
        }
    }
}
