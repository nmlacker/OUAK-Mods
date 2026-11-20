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
    /// <c>select</c> (start with Burda not chosen and pick her on the game's select screen on the ship, then the test's
    /// cousin's own slot), <c>probe</c> (also run slice three's in-game probes and log them on [CousinProbe] lines:
    /// CousinTest.Probe.cs), <c>cousin N</c> (the cousin the save holds for the run, 6 by default), <c>creator</c> (the
    /// save holds the Cousin Creator's 999 with the custom index it already has), <c>forever</c> (use the Katamari
    /// Forever folder already in settings.json and check the old style's Forever motions: CousinTest.Forever.cs; without
    /// the word the folder is treated as unset).
    /// A backup of the saves is made before the slot is loaded, and every save write is blocked for the whole session.
    /// Right after the slot is read, the save's cousin (and GlobalManager's run-time copy) is set to the test's cousin in
    /// memory, so the Prince under her is a real change; the fields are put back and compared before DONE.
    /// Lines start with [CousinTest]; the last one is <c>DONE pass=N fail=M</c>. Pictures go to
    /// UserData/LongLostCousins/TestShots.
    /// </summary>
    internal static partial class CousinTest
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
        // The save's cousin for the run (word "cousin N" or "creator"), and the fields as the slot had them.
        private static int _testCousin = 6, _testCustom = int.MinValue;
        private static bool _saveSet;
        private static int _s0 = int.MinValue, _c0 = int.MinValue, _d0 = int.MinValue;
        private static int[] _w0;
        // The select run ended on the Prince (the test cousin's slot was not on the screen): the save then holds 0.
        private static bool _selectStoredPrince;
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
            _probe = w.Contains("probe");
            _forever = w.Contains("forever");
            i = w.IndexOf("cousin");
            if (i >= 0 && i + 1 < w.Count && int.TryParse(w[i + 1], out int tc) && Chosen.IsStorableId(tc)) _testCousin = tc;
            if (w.Contains("creator")) _testCousin = Chosen.CreatorId;
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
                $"{(_select ? "ship with the select screen" : "ship")}{(_results ? ", stage and results" : _stage ? " and stage" : " only")}{(_probe ? ", with the probes" : "")}{(_forever ? ", with Katamari Forever's motions from the folder in settings.json" : ", without Katamari Forever's motions")}; " +
                $"the save holds {(_testCousin == Chosen.CreatorId ? "the Cousin Creator's 999 (its own custom index)" : "cousin " + _testCousin)} in memory; every save write blocked {guard}");
            if (!guard)
            {
                _setupFailure = "the save hooks are missing; not loading a slot";
                Settings.ChooseInMemory(null, null);
            }
            else if (_probe) ArmProbe();
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
                if (_probe) ProbeTick(now);
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
                if (_probe) ProbeEnd();
            }
            catch { }
            try { RestoreTestSave(); }
            catch (Exception e) { Check("save: the save's fields in memory are put back", false, $"{e.GetType().Name}: {e.Message}"); }
            try
            {
                Msg($"save writes blocked: {SaveGuard.Blocked}; models dressed this session: {Dresser.DressedCount}; " +
                    $"Prince-base loader calls {PrinceBase.LoaderCalls}, getter reads turned to 0 {PrinceBase.Rewrites}, run-time id set to 0 {PrinceBase.NowIdZeroed}x, " +
                    $"models undressed by a Plaza swap {PrinceBase.Undressed}, confirm write-backs {SelectScreen.WriteBacks}, net firings {SelectScreen.NetFirings}");
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
            if (_probe) ProbeBegin();
            yield return Until(() => Contexts.PlazaOwnerModel() != null, 60f, "the walking cousin on the ship");
            yield return 4f;
            if (_select) foreach (var x in SelectScreenTest()) yield return x;
            else
            {
                // Chosen by the flag with nothing recorded: the first save check in a game session (Chosen.CheckSave) records the save's cousin.
                Msg($"the save holds {Chosen.Describe(RawSavedCousin(), GameRefs.Save?._customPlayerIndex ?? -1)}; worn now: {Chosen.On}; the settings record {Settings.DescribeRecord()}");
                Check("ship: the settings record the save's cousin", Settings.HasRecord && Settings.Saved == _testCousin && Settings.Custom == _testCustom,
                      $"recorded {Settings.DescribeRecord()} (custom index {Settings.Custom}), the test's {Chosen.Describe(_testCousin, _testCustom)}");
                foreach (var x in Ship()) yield return x;
                if (_forever) foreach (var x in Fenced("the Forever ship checks", ForeverChecks("ship", Dresser.FindFor(Contexts.PlazaOwnerModel())))) yield return x;
                foreach (var x in OffAndOn()) yield return x;
            }
            if (_probe) foreach (var x in Guarded("the ship probes", ShipProbes())) yield return x;
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
            SetTestSave();
            var save = GameRefs.Save;
            save.SetUseDataLogoMonoGetFlg();
            OujiShipSponePointList.s_SponeIndex = OujiShipSponePointList.ElevatorSpone;
            save.FrontSceneSelectData = true;
            SingletonMonoBehaviour<EncorsSaveDataChecker>.Instance.CheckEncorsContentsData();
            GameRefs.Sound?.StopBGM(0.5f);
            Il2Cpp.SceneManager.LoadSceneAsync("OujiShip", LoadSceneMode.Single, true);
        }

        /// <summary>
        /// Right after the slot is read: the start values are kept, and the save's cousin and GlobalManager's run-time id
        /// become the test's cousin, in memory (every save write stays blocked).
        /// </summary>
        private static void SetTestSave()
        {
            var save = GameRefs.Save;
            var g = GameRefs.Global;
            if (save == null) { Msg("no save object after the slot was read; the save's cousin is left alone"); return; }
            _s0 = save._selectPlayerId;
            _c0 = save._customPlayerIndex;
            _d0 = g != null ? g._nowCharacterID : int.MinValue;
            _w0 = Wearing();
            _testCustom = _c0;
            save._selectPlayerId = _testCousin;
            if (g != null) g._nowCharacterID = _testCousin;
            _saveSet = true;
            Msg($"the save in memory: _selectPlayerId {_s0} -> {_testCousin}, _customPlayerIndex {_c0} kept, GlobalManager._nowCharacterID {_d0} -> {_testCousin}, " +
                $"_wearingPresent [{string.Join(", ", _w0)}]; put back before DONE");
        }

        /// <summary>From Finish: the save's fields go back to the slot's own values, and are compared with them.</summary>
        private static void RestoreTestSave()
        {
            if (!_saveSet) return;
            var save = GameRefs.Save;
            var g = GameRefs.Global;
            if (save == null) { Check("save: the save's fields in memory are put back", false, "no save object at the end"); return; }
            CheckSaveKept("at the end of the run");
            save._selectPlayerId = _s0;
            save._customPlayerIndex = _c0;
            if (g != null && _d0 != int.MinValue) g._nowCharacterID = _d0;
            var w = Wearing();
            bool same = save._selectPlayerId == _s0 && save._customPlayerIndex == _c0 && _w0 != null && w.SequenceEqual(_w0) &&
                        (g == null || _d0 == int.MinValue || g._nowCharacterID == _d0);
            Check("save: the save's fields in memory are back as the slot had them", same,
                  $"_selectPlayerId {save._selectPlayerId} (start {_s0}), _customPlayerIndex {save._customPlayerIndex} (start {_c0}), " +
                  $"_nowCharacterID {(g != null ? g._nowCharacterID : -1)} (start {_d0}), _wearingPresent [{string.Join(", ", w)}] (start [{string.Join(", ", _w0 ?? new int[0])}])");
        }

        /// <summary>The save still holds the test's cousin and its custom index: picking, wearing and dropping her changed nothing there.</summary>
        private static void CheckSaveKept(string when)
        {
            var save = GameRefs.Save;
            if (!_saveSet || save == null) { Check($"save: the test's cousin is still in the save {when}", false, "the test could not set it"); return; }
            if (_selectStoredPrince) { Msg($"the save {when} holds {save._selectPlayerId}: the select run stored the Prince, so it is not compared with the test's cousin"); return; }
            Check($"save: the save still holds {Chosen.Describe(_testCousin, _testCustom)} {when}", save._selectPlayerId == _testCousin && save._customPlayerIndex == _testCustom,
                  $"_selectPlayerId {save._selectPlayerId}, _customPlayerIndex {save._customPlayerIndex}, _nowCharacterID {GameRefs.Global?._nowCharacterID}; " +
                  $"confirm write-backs {SelectScreen.WriteBacks}, net firings {SelectScreen.NetFirings}");
        }

        /// <summary>The model under her is the Prince: OujiPresent._oujiID 0 and the Prince's prefab for this place (the model or one of its children).</summary>
        private static void CheckBase(string what, GameObject go, string want)
        {
            int id = PresentId(go);
            var names = new List<string>();
            if (go != null)
            {
                names.Add(go.name);
                var op = go.GetComponentInChildren<OujiPresent>(true);
                if (op != null && op.gameObject.Pointer != go.Pointer) names.Add(op.gameObject.name);
                for (int i = 0; i < go.transform.childCount && i < 8; i++) names.Add(go.transform.GetChild(i).name);
            }
            bool named = names.Any(n => n.StartsWith(want, StringComparison.Ordinal));
            Check($"{what}: the hidden base is the Prince ({want}, OujiPresent._oujiID 0)", go != null && id == 0 && named,
                  $"{(go != null ? U.PathOf(go.transform) : "no model")}, _oujiID {id}, names [{string.Join(", ", names)}]; save {RawSavedCousin()}, " +
                  $"_nowCharacterID {GameRefs.Global?._nowCharacterID}; loader calls {PrinceBase.LoaderCalls}, getter reads turned to 0 {PrinceBase.Rewrites}, run-time id set to 0 {PrinceBase.NowIdZeroed}x");
        }

        /// <summary>
        /// Every cousin model in the scene (each CharacterAnimation) that carries her, by the dresser's list or by her marker
        /// in the model, sits under one of the player's own objects (the walking cousin, a UIOuji copy, the stage's player
        /// or HUD copy, the ending's player, a results holder) and stands on the Prince. Read from the scene, not from the
        /// dresser's contexts.
        /// </summary>
        private static void CheckOnlyPlayers(string where)
        {
            var players = new List<Transform>();
            void Add(Component c) { if (c != null) players.Add(c.transform); }
            var walker = Contexts.PlazaOwnerModel();
            if (walker != null) players.Add(walker.transform);
            try { foreach (var u in UnityEngine.Object.FindObjectsOfType<UIOuji>()) Add(u); } catch { }
            var main = GameRefs.Main;
            if (main != null) { Add(main.Ouji); Add(main.UIOuji); }
            Add(GameRefs.Global?.EndGameMan?.Ouji);
            try
            {
                foreach (var s in UnityEngine.Object.FindObjectsOfType<ResultMonoSetter>()) if (s.m_PlayCharacter != null) players.Add(s.m_PlayCharacter.transform);
                foreach (var s in UnityEngine.Object.FindObjectsOfType<ResultAAMonoSetter>()) if (s.m_PlayCharacter != null) players.Add(s.m_PlayCharacter.transform);
            }
            catch { }
            var bad = new List<string>();
            int models = 0, hers = 0;
            foreach (var ca in UnityEngine.Object.FindObjectsOfType<CharacterAnimation>())
            {
                if (ca == null) continue;
                models++;
                var go = ca.gameObject;
                if (Dresser.Find(go) == null && !Dresser.CarriesMarker(go)) continue;
                hers++;
                var t = go.transform;
                bool mine = players.Any(p => IsUnder(t, p));
                int id = PresentId(go);
                if (!mine || id != 0) bad.Add($"{U.PathOf(t)} (_oujiID {id}, under one of the player's objects {mine})");
            }
            Check($"{where}: she is only on the player's own models, each on the Prince", hers > 0 && bad.Count == 0,
                  (bad.Count > 0 ? string.Join("; ", bad) + "; " : "") + $"{hers} of the {models} cousin model(s) in the scene carry her");
        }

        private static bool IsUnder(Transform t, Transform root)
        {
            for (int i = 0; t != null && i < 16; i++, t = t.parent) if (t.Pointer == root.Pointer) return true;
            return false;
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
            CheckBase("ship walking cousin", Contexts.PlazaOwnerModel(), "OujiPrefab_001");

            yield return Until(() => ByContext("Plaza UI") != null, 20f, "the ship's UI copy to be dressed", false);
            var ui = ByContext("Plaza UI");
            int uiObjects = 0;
            try { uiObjects = UnityEngine.Object.FindObjectsOfType<UIOuji>().Length; } catch { }
            Check("ship: the UI copy of the walking cousin is dressed", ui != null,
                  (ui != null ? U.PathOf(ui.Root.transform) + ", " : Why() + ", ") + $"{uiObjects} active UIOuji object(s)");
            if (ui != null) CheckModel("ship UI copy", ui, false);
            CheckBase("ship UI copy", ui != null ? ui.Root : FirstUiCopy(), "OujiPrefab_001");
            CheckSaveKept("on the ship");
            CheckOnlyPlayers("ship");

            Shot("ship_" + _style);
            yield return PictureTaken();

            // Slice three, stage 3: the presents she wears, on the walker and its UI copy, and her antenna.
            if (dm != null) foreach (var x in CheckPresents("ship walking cousin", dm, false)) yield return x;
            if (ui != null && ui.Root != null) foreach (var x in CheckPresents("ship UI copy", ui, false)) yield return x;
            Shot("present_ship_" + _style);
            yield return PictureTaken();
            if (dm != null && dm.Root != null) foreach (var x in AntennaFlip(dm)) yield return x;
            if (dm != null) foreach (var x in Faces(dm)) yield return x;
        }

        /// <summary>The first UIOuji in the scene (the ship's picture of the walking cousin), for a FAIL line when none is dressed.</summary>
        private static GameObject FirstUiCopy()
        {
            try
            {
                var all = UnityEngine.Object.FindObjectsOfType<UIOuji>();
                return all.Length > 0 ? all[0].gameObject : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// The tab run's off and on: the tab turns her off (the save and the run-time id keep the test's cousin), a ship
        /// rebuild builds that cousin again, undressed; the tab turns her on, and a rebuild builds the Prince under her.
        /// </summary>
        private static IEnumerable<object> OffAndOn()
        {
            Msg("turning her off in the tab (CousinsTab.Choose(null))");
            CousinsTab.Choose(null, null);
            Check("off: the tab turns her off", !Settings.Active && !Chosen.On, $"settings {Settings.Describe()}, worn {Chosen.On}");
            CheckSaveKept("after turning her off");
            yield return 0.2f;   // a LateUpdate: the run-time id is put back there if the mod had set it to 0
            var g = GameRefs.Global;
            Check("off: GlobalManager's run-time cousin id is the save's cousin", g != null && g._nowCharacterID == _testCousin,
                  $"_nowCharacterID {(g != null ? g._nowCharacterID : -1)}, the run-time id still the mod's 0 {PrinceBase.Zeroed}");
            bool rebuilt = false;
            foreach (var x in DirectRebuild("with her off", 0, ok => rebuilt = ok)) yield return x;
            yield return 2f;
            CheckOwnCousin("off: the rebuilt walking cousin", Contexts.PlazaOwnerModel(), rebuilt);
            CheckSaveKept("after the rebuild with her off");
            Shot("off_" + _style);
            yield return PictureTaken();

            Msg("turning her on again in the tab (CousinsTab.Choose)");
            CousinsTab.Choose(Cousin, _style);
            Check("on again: she is worn, with the save's cousin recorded", Chosen.On && Settings.HasRecord && Settings.Saved == _testCousin,
                  $"worn {Chosen.On}, recorded {Settings.DescribeRecord()}");
            yield return 0.2f;
            foreach (var x in DirectRebuild("with her on again", _testCousin != 0 ? 1 : 0, ok => rebuilt = ok)) yield return x;
            yield return Until(() => Dresser.FindFor(Contexts.PlazaOwnerModel()) != null, 20f, "the rebuilt walking cousin to be dressed", false);
            var walker = Contexts.PlazaOwnerModel();
            Check("on again: the rebuilt walking cousin is dressed", rebuilt && Dresser.FindFor(walker) != null, rebuilt ? Why() : "the ship did not rebuild it");
            CheckBase("on again: ship walking cousin", walker, "OujiPrefab_001");
            CheckSaveKept("after turning her on again");
            CheckOnlyPlayers("on again");
        }

        /// <summary>[direct] A ship rebuild of the walking cousin by OrderToChangeCharacter.OrderActiveEvent, what the ship does after a cousin change.</summary>
        private static IEnumerable<object> DirectRebuild(string why, int wantRewrites, Action<bool> done)
        {
            IntPtr before = WalkerPtr();
            int calls = PrinceBase.LoaderCalls, rewrites = PrinceBase.LoaderRewrites;
            var etm = UnityEngine.Object.FindObjectOfType<EventToMenuController>();
            var och = etm != null ? etm.m_OrderChangeChara : null;
            if (och == null) och = UnityEngine.Object.FindObjectOfType<OrderToChangeCharacter>();
            if (och == null) { Check($"ship rebuild {why}: the ship has an OrderToChangeCharacter", false); done(false); yield break; }
            Msg($"[direct] ship rebuild {why}: OrderToChangeCharacter.OrderActiveEvent(); save {RawSavedCousin()}, _nowCharacterID {GameRefs.Global?._nowCharacterID}");
            och.OrderActiveEvent();
            yield return Until(() => WalkerPtr() != IntPtr.Zero && WalkerPtr() != before, 60f, "the ship to rebuild the walking cousin " + why, false);
            bool ok = WalkerPtr() != IntPtr.Zero && WalkerPtr() != before;
            CheckLoaderRewrites($"ship rebuild {why}", wantRewrites, calls, rewrites);
            done(ok);
        }

        /// <summary>
        /// The getter answered 0 inside the model loader exactly <paramref name="want"/> times since the counts were taken
        /// (one read per loader call on the ship, so 1 for a rebuild with her worn and 0 with her off).
        /// </summary>
        private static void CheckLoaderRewrites(string what, int want, int callsBefore, int rewritesBefore)
        {
            int calls = PrinceBase.LoaderCalls - callsBefore, rewrites = PrinceBase.LoaderRewrites - rewritesBefore;
            Check($"{what}: the saved cousin read as the Prince inside the loader {want}x", rewrites == want,
                  $"{rewrites} read(s) turned to 0 in {calls} loader call(s) (Prince-base hooks in {PrinceBase.Installed}, {PrinceBase.Count} of {PrinceBase.Expected}); " +
                  $"OujiPresent.Start reads turned to 0 this session {PrinceBase.PresentRewrites}");
        }

        /// <summary>The walking cousin is the test's cousin itself (its OujiPresent id; for the Cousin Creator's 999 a ModelSetup
        /// or a non-Prince "_D_Custom" prefab), not dressed.</summary>
        private static void CheckOwnCousin(string what, GameObject walker, bool rebuilt)
        {
            string address;
            try { address = Il2CppApp.KatamariSin.Game.GetItokoPrefabAddressFromID(_testCousin) ?? "null"; }
            catch (Exception e) { address = "unreadable: " + e.Message; }
            Msg($"[direct] Game.GetItokoPrefabAddressFromID({_testCousin}) = {address}");
            int id = PresentId(walker);
            bool setup = walker != null && walker.GetComponentInChildren<ModelSetup>(true) != null;
            // On the ship the game builds a designed cousin from its base cousin's "_D_Custom" prefab (slice one's runs on
            // the real slot-3 save, 999 with custom index 9, built OUJI06_D_Custom too); elsewhere it may carry a ModelSetup.
            bool customPrefab = walker != null && walker.name.Contains("_D_Custom") && id != 0;
            bool own = _testCousin == Chosen.CreatorId ? setup || customPrefab : id == _testCousin;
            Check($"{what} is {Chosen.Describe(_testCousin, _testCustom)} again, not dressed", rebuilt && walker != null && own && Dresser.FindFor(walker) == null && !Dresser.CarriesMarker(walker),
                  !rebuilt ? "the ship did not rebuild it" : $"{(walker != null ? walker.name : "no model")}, _oujiID {id}, ModelSetup {setup}, dressed {Dresser.FindFor(walker) != null}, marker {Dresser.CarriesMarker(walker)}");
        }

        /// <summary>The checks every dressed model gets: the game hidden, her shown, bones and layer in step.</summary>
        private static void CheckModel(string name, DressedModel dm, bool mustBeSeen)
        {
            var all = dm.Root.GetComponentsInChildren<Renderer>(true);
            var on = new List<string>();
            int game = 0, worn = 0;
            for (int i = 0; i < all.Length; i++)
            {
                var r = all[i];
                if (r is null || dm.Own.Contains(r.Pointer)) continue;
                // A present she wears (under one of her attach nodes) is hers to show; CheckPresents checks those.
                if (Presents.IsWorn(dm, r)) { worn++; continue; }
                game++;
                if (r.enabled) on.Add(r.name);
            }
            Check($"{name}: none of the game's renderers is on", on.Count == 0,
                  (on.Count == 0 ? $"{game} game renderer(s), all off" : $"{on.Count} of {game} on: {string.Join(", ", on.Take(8))}") +
                  $"; {worn} renderer(s) of presents she wears left out");

            // Her antenna follows the game's antenna object (a hat may hide it), so it counts only while the game's is on.
            var gameAntenna = dm.Op != null ? dm.Op._antennaObject : null;
            bool antennaOff = dm.Antenna != null && gameAntenna != null && !gameAntenna.activeSelf;
            var body = dm.Pieces.Where(p => p.Role == MeshRole.Body && !(antennaOff && p == dm.Antenna)).ToList();
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
            if (dm.Clip != null)
            {
                // Katamari Forever's clip plays on this state (word forever): her joints follow the bake, not the game's.
                Msg($"{name}: on a Forever state ({StateOf(dm)}): her joints are checked against the bake instead of the game's");
                CheckBakeMatch(name, dm);
                int lw = dm.LayerSource != null ? dm.LayerSource.layer : dm.Model.gameObject.layer;
                Msg($"{name}: layer {lw} (body_m's), {dm.Pieces.Count(p => p.Go.layer != lw) + (dm.Marker.gameObject.layer != lw ? 1 : 0)} of her object(s) on another");
                return;
            }
            int w = dm.Assets.FollowJoint >= 0 ? dm.Assets.FollowJoint : data.JointIndex("JNT_waist");
            if (w < 0 || dm.GameJoints[w] == null || dm.GameJoints[w].parent == null)
                Check($"{name}: her waist follows the game's waist", false, "no waist joint to compare");
            else
            {
                var g = dm.GameJoints[w];
                var h = dm.HerJoints[w];
                var offset = w == dm.Assets.FollowJoint ? dm.Assets.FollowOffset : U.V3(0f, 0f, 0f);
                var expected = g.parent.TransformPoint(U.Add(g.localPosition, offset));
                // The stage HUD copy scales and moves her marker (cousins.json "hud_scale"/"hud_offset"): there her waist
                // stands where the game's would stand under the marker's scale and offset.
                if (dm.Marker.localScale.x != 1f || U.Length(dm.Marker.localPosition) > 0f)
                    expected = dm.Marker.TransformPoint(dm.Model.InverseTransformPoint(expected));
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
                // On a Forever state whose own face group is g, Forever's expression picks the card (word forever only).
                int card = grp.Card;
                string why = "";
                if (dm.Clip != null && (dm.RowFace == ForeverPlayer.NoRowFace || dm.RowFace == g))
                {
                    card = DressedModel.ExpressionCard(data.Face, dm.Clip.Expression);
                    why = $" (Forever's expression {dm.Clip.Expression} on {StateOf(dm)}, whose own group is {g})";
                }
                var on = dm.Pieces.Where(p => p.Role == MeshRole.Card && p.Renderer.enabled && p.Go.activeInHierarchy).Select(p => p.Card).ToList();
                Check($"face {g}: card {card} shows, and no other card{why}", on.Count == 1 && on[0] == card,
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
        private static void CheckAfterConfirm(string who, int want)
        {
            var g = GameRefs.Global;
            int now = g != null ? g._nowCharacterID : -1;
            // SetCharater sets the run-time id from the save, which keeps the player's own cousin.
            Check($"select: GlobalManager's current cousin is {want} after {who}", now == want,
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
            CheckSaveKept("after confirming her");
            Check("select: the settings record the save's cousin", Settings.HasRecord && Settings.Saved == _testCousin && Settings.Custom == _testCustom,
                  $"recorded {Settings.DescribeRecord()} (custom index {Settings.Custom}); GetSelectPlayerId {Chosen.SavedCousin()}, confirm write-backs {SelectScreen.WriteBacks}");
            Check("select: the settings say Burda, picked on the screen", Settings.Active && Settings.CousinId == Cousin && Settings.OnScreen && Settings.Style == _style,
                  $"{Settings.Describe()}, on the screen {Settings.OnScreen}");
            Msg($"settings kept in memory only (set up by the test): {Settings.MemoryOnly}");
            Check("select: she is worn and the dressing hooks are in place", Chosen.On && Hooks.Installed && Hooks.Count == Hooks.Expected,
                  $"worn {Chosen.On}, {Hooks.Count} of {Hooks.Expected} hooks");
            Check("select: her slot carries 100 again", slot != null && (int)slot._ITOKONAME == SelectScreen.ModId, slot != null ? "id " + (int)slot._ITOKONAME : "no slot");
            CheckAfterConfirm("the confirm of Burda", _testCousin);
            yield return 0.2f;   // a LateUpdate: the marker hooks follow the settings there
            Check("select: the marker hooks come in once the settings name her", SelectScreen.ScopeInstalled,
                  SelectScreen.ScopeInstalled ? "in" : SelectScreen.ScopeFailed ? "they could not be made (see the log)" : "not in");

            // ---- the ship's new walking cousin, after the game's swap
            _changeDirect = false;
            int calls0 = PrinceBase.LoaderCalls, rewrites0 = PrinceBase.LoaderRewrites;
            foreach (var x in AwaitRebuild(before, "the confirm")) yield return x;
            Check("select: the game rebuilt the walking cousin", WalkerPtr() != IntPtr.Zero && WalkerPtr() != before, RebuildHint());
            CheckLoaderRewrites("select: the rebuild after her confirm", _testCousin != 0 ? 1 : 0, calls0, rewrites0);
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
            bool found = FindSlot(menu, _testCousin, out int ownPage, out int ownIndex);
            if (_reopened && found && ownPage != 0 && ownPage != SelectScreen.Page)
            {
                Msg($"[direct] refreshing the current-cousin markers: IconWindowController.SetIcon on page {ownPage + 1} too");
                wins[ownPage].SetIcon();
            }
            bool marked = Marked(menu, SelectScreen.Page, SelectScreen.Slot);
            bool ownMarked = found && Marked(menu, ownPage, ownIndex);
            Check($"select: the current-cousin marker sits on her slot, not on {Chosen.Describe(_testCousin, _testCustom)}'s", marked && !ownMarked,
                  $"hers {marked}, the save's cousin's {(found ? ownMarked.ToString() : "(no slot on the screen)")}, marker hooks {SelectScreen.ScopeInstalled}, screen reset by the game {!_reopened}");
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
            Check("select: inside the menu's scope the save's cousin reads as her id, and as itself when suspended", inScope == SelectScreen.ModId && suspended == _testCousin,
                  $"in the scope {inScope}, suspended {suspended}, depth after {SelectScreen.ScopeDepth}");

            // Off: the save's own cousin picked again (the save then still holds it); the Prince when it has no slot here.
            bool ownPickable = found && Slot(menu, ownPage, ownIndex)?._oujiData != null && Slot(menu, ownPage, ownIndex)._oujiData._isReleased;
            int offPage = ownPickable ? ownPage : 0, offIndex = ownPickable ? ownIndex : 0, offId = ownPickable ? _testCousin : 0;
            string offWho = ownPickable ? Chosen.Describe(_testCousin, _testCustom) : "the Prince";
            if (!ownPickable)
            {
                _selectStoredPrince = true;
                Msg($"{Chosen.Describe(_testCousin, _testCustom)} has no found slot on this screen (slot found {found}); turning her off with the Prince instead, so the save then holds 0");
            }
            Msg($"to page {offPage + 1}, slot {offIndex + 1} ({offWho})");
            FocusSlot(menu, offPage, offIndex);
            yield return 1f;
            if (!Confirm(menu, offPage, offIndex, offId, offWho)) yield break;
            Check($"select: {offWho} turns her off", !Settings.Active && !Chosen.On && !Hooks.Installed,
                  $"settings {Settings.Describe()}, worn {Chosen.On}, dressing hooks {Hooks.Installed}");
            if (ownPickable) CheckSaveKept("after turning her off");
            else
            {
                var sv = GameRefs.Save;
                Msg("the save after the Prince: _selectPlayerId " + RawSavedCousin());
                Check("select: the Prince's confirm left the custom index alone", sv != null && sv._customPlayerIndex == _testCustom,
                      $"_customPlayerIndex {(sv != null ? sv._customPlayerIndex : -1)}, the test's {_testCustom}");
            }
            CheckAfterConfirm("the confirm of " + offWho, offId);
            yield return 0.2f;
            Check("select: the marker hooks go once the settings name nobody", !SelectScreen.ScopeInstalled);
            _changeDirect = false;
            calls0 = PrinceBase.LoaderCalls;
            rewrites0 = PrinceBase.LoaderRewrites;
            foreach (var x in AwaitRebuild(before, offWho)) yield return x;
            CheckLoaderRewrites("select: the rebuild after " + offWho, 0, calls0, rewrites0);
            yield return 3f;
            var plain = Contexts.PlazaOwnerModel();
            bool rebuilt = plain != null && plain.Pointer != before;
            if (ownPickable) CheckOwnCousin("select: the rebuilt walking cousin", plain, rebuilt);
            else Check("select: the rebuilt walking cousin is the game's own", rebuilt && Dresser.FindFor(plain) == null && !Dresser.CarriesMarker(plain),
                       !rebuilt ? "the game did not rebuild it; " + RebuildHint() : $"dressed {Dresser.FindFor(plain) != null}, marker {Dresser.CarriesMarker(plain)}");
            Check("select: the net never fired (whole run)", SelectScreen.NetFirings == 0, $"{SelectScreen.NetFirings} firing(s)");
            Shot("select_off_" + _style);
            yield return PictureTaken();
            if (_selectStoredPrince)
            {
                // The rest of the run (the stage, the end) is about the test's cousin: it goes back into the save in memory.
                var sv = GameRefs.Save;
                var g = GameRefs.Global;
                if (sv != null) sv._selectPlayerId = _testCousin;
                if (g != null) g._nowCharacterID = _testCousin;
                _selectStoredPrince = false;
                Msg($"the save in memory: _selectPlayerId back to the test's {_testCousin} (and GlobalManager._nowCharacterID) after the Prince's confirm");
            }
        }

        /// <summary>The page and slot whose id is <paramref name="id"/> on this menu (her page left out), or false.</summary>
        private static bool FindSlot(SelectionMenuController menu, int id, out int page, out int index)
        {
            page = index = -1;
            var wins = menu != null ? menu._iconWindowControllers : null;
            if (wins == null) return false;
            for (int p = 0; p < wins.Length; p++)
            {
                var slots = wins[p] != null ? wins[p]._shortcutIconDatas : null;
                if (slots == null) continue;
                for (int i = 0; i < slots.Length; i++)
                    if (slots[i] != null && (int)slots[i]._ITOKONAME == id && !(p == SelectScreen.Page && i == SelectScreen.Slot)) { page = p; index = i; return true; }
            }
            return false;
        }

        /// <summary>The current-cousin marker of window <paramref name="page"/>, slot <paramref name="index"/> is on.</summary>
        private static bool Marked(SelectionMenuController menu, int page, int index)
        {
            var wins = menu._iconWindowControllers;
            if (wins == null || page < 0 || page >= wins.Length || wins[page] == null) return false;
            var marks = wins[page]._selectIcon;
            return marks != null && index >= 0 && index < marks.Length && marks[index] != null && marks[index].activeSelf;
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

        /// <summary>
        /// After a stage load: waits for the player, skips the tips, the fly-over and the King's talk, and waits until the
        /// stage plays, then 2 s more. <paramref name="done"/> gets false (after a FAIL) when it does not start within 150 s.
        /// </summary>
        private static IEnumerable<object> UntilPlaying(Action<bool> done)
        {
            yield return Until(() => GameRefs.Main != null && GameRefs.Main.PlayerInitiated, 180f, "the stage's player");

            float t0 = Time.unscaledTime;
            bool skippedFly = false;
            while (true)
            {
                float since = Time.unscaledTime - t0;
                if (since > 150f) { Check("stage: the stage starts playing", false, $"scene {SceneLoader.ActiveScene}"); done(false); yield break; }
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
            done(true);
        }

        private static IEnumerable<object> Stage()
        {
            var entry = StageCatalog.Entries.FirstOrDefault(s => s.Kind == MapKind.Stage && s.Internal == "Stage_Big1");
            if (entry == null) { Check("stage: Stage_Big1 is in the API's stage list", false); yield break; }
            Msg("load stage: " + SceneLoader.LoadStage(entry));
            if (_probe) ProbeAfterLoadStage("first");
            bool playing = false;
            foreach (var x in UntilPlaying(ok => playing = ok)) yield return x;
            if (!playing) yield break;

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
            CheckBase("stage player", ouji != null ? ouji.gameObject : null, "OUJI01_D");
            CheckBase("stage HUD copy", hud != null ? hud.gameObject : null, "OUJI01_D");
            CheckSaveKept("after the stage load");
            CheckOnlyPlayers("stage");
            Shot("stage_" + _style);
            yield return PictureTaken();
            if (_forever)
            {
                foreach (var x in Fenced("the Forever stage checks", ForeverChecks("stage", dm))) yield return x;
                // The HUD copy plays its own Animator (one frame behind the player): checked when it is on a Forever state.
                if (_style == "old" && hd != null && hd.Root != null)
                {
                    yield return Until(() => hd.Root == null || hd.Clip != null, 5f, "the stage HUD copy on a Forever state", false);
                    if (hd.Root != null && hd.Clip != null) CheckBakeMatch("forever stage HUD copy, state " + StateOf(hd), hd);
                }
            }

            // Slice three, stages 3 and 4: the presents on the player (with the fade list) and on the HUD copy, and the HUD's framing.
            if (dm != null) foreach (var x in CheckPresents("stage player", dm, true)) yield return x;
            if (hd != null && hd.Root != null) foreach (var x in CheckPresents("stage HUD copy", hd, false)) yield return x;
            Shot("present_stage_" + _style);
            yield return PictureTaken();
            if (hd != null && hd.Root != null)
            {
                CheckHud(main, hd);
                foreach (var x in HudPictures(main, hd)) yield return x;
            }

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

            if (_probe) foreach (var x in Guarded("the stage probes", StageProbes())) yield return x;
            if (_results) foreach (var x in Results()) yield return x;
            // Last, because it loads the stage again: the HUD picture with the worn HEAD present at 0 in memory (not after the results).
            if (_probe && !_results) foreach (var x in Guarded("the HEAD-at-0 stage", HeadlessStage(entry))) yield return x;
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
