using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppApp.KatamariSin;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using LongLostCousins.Data;
using LongLostCousins.Dressing;
using OUAKModAPI;
using UnityEngine;

namespace LongLostCousins.Select
{
    /// <summary>
    /// Burda as one more cousin on the game's "The Prince &amp; Cousins" screen (the ship, the Plaza and OujiStar: every
    /// SelectionMenuController whose scene mode is not 1). She takes page 4's first free slot (window 3, slot 13, the
    /// one after id 74) under the mod's own id 100, which never reaches the game's save: confirming her stores the
    /// cousin the save already holds (its id, and its custom index for the Cousin Creator's 999), so the save is
    /// unchanged, and the mod's settings say Burda, picked on the screen, with that saved cousin recorded.
    /// <para>Two sets of hooks. The slot set is always in place, because this screen is where she is picked: the slot
    /// loop when the menu opens, the confirm, the two mode-1 writers of the saved cousin, and SetSaveAction, where a net
    /// turns a stray 100 into a cousin the save held before the save is converted to bytes; and the suspensions of the
    /// other save paths (DataToConvert.MonoDataToSaveData and both StartSceneLoad steps), so they never depend on the
    /// Prince-base hooks. The scope set exists only while
    /// the settings name her: the saved-cousin getter's one postfix, two short scopes (the menu's update and the slot
    /// icons' refresh) in which the recorded cousin reads as 100, so the current-cousin marker sits on her slot and the
    /// player's own cousin can be picked again, and the guide's name. The same postfix answers 0 inside the Prince-base
    /// loader scope (PrinceBase.cs). Inside a loader scope 0 wins, inside the menu scope 100 wins, and a suspension (the
    /// save paths, the mod's own reads) beats both. The scope set comes and goes only in OnLateUpdate, when no game method
    /// is on the stack. Every handler catches everything; none lets an exception reach the game.</para>
    /// </summary>
    internal static class SelectScreen
    {
        private static readonly ModLog Log = LongLostCousinsMod.Log.In("Select screen");
        private static readonly HookSet Set = new("LongLostCousins.SelectScreen");
        private static readonly HashSet<string> Once = new(StringComparer.Ordinal);

        /// <summary>The mod's id for Burda in the screen's slot (never written to the save).</summary>
        public const int ModId = 100;
        /// <summary>Her slot: page 4 (window index 3), array index 13 (Ouji_14).</summary>
        public const int Page = 3, Slot = 13;
        /// <summary>The cousin of cousins.json the slot shows.</summary>
        public const string CousinId = "burda";
        /// <summary>
        /// The slot set: window and slot Initialize, CheckNewFlag, SetCharater, EndShortCutMove, EndSelectionMove, SetSaveAction,
        /// MonoDataToSaveData, and the stage's and the ending's StartSceneLoad steps.
        /// </summary>
        public const int Expected = 10;
        /// <summary>The scope set: the saved-cousin getter, the two scopes, SetIconNameText.</summary>
        public const int ScopeExpected = 4;
        /// <summary>The scale of a found slot's icon (Ouji_7's Icon in the OujiShip scene); a free slot's icon is 0.001.</summary>
        private const float FoundIconScale = 0.2f;

        private static bool _complete, _scopeFailed;
        private static HookSet _scope;
        private static int _depth, _suspend, _mainThread = -1;
        private struct Swapped { public ShortcutIconData Slot; public int Custom; }
        private static readonly List<Swapped> _swapped = new();
        private static OujiModelCreate _swappedModel;
        // A confirm under way: the save's cousin and custom index read first thing in SetCharater's prefix (_snap: read and
        // storable). The net puts them back over a stray 100; her confirm (_writeBack) also puts them back if the game changed them.
        private static bool _snap, _writeBack;
        private static int _snapId, _snapCustom;
        // The last storable cousin and custom index LateTick read from the save's fields in this game session: the net's
        // fallback outside a confirm (or when the confirm's read failed).
        private static bool _hasGood;
        private static int _goodId, _goodCustom;

        /// <summary>The slot set is in place (the slot is only added then).</summary>
        public static bool Ready => _complete;
        /// <summary>The scope set is in place (only while the settings name her).</summary>
        public static bool ScopeInstalled => _scope != null;
        /// <summary>The scope set could not be made; the marker and the Prince's slot stay as the game has them.</summary>
        public static bool ScopeFailed => _scopeFailed;
        /// <summary>How many times her slot was filled this session (for the test and the tab).</summary>
        public static int SlotsFilled { get; private set; }
        /// <summary>How many times the net found the mod's id in the save or in GlobalManager this session (should stay 0).</summary>
        public static int NetFirings { get; private set; }
        /// <summary>Inside the menu's update or the icons' refresh, and not suspended: the recorded cousin reads as 100 here while she is worn.</summary>
        public static bool InScope => _depth > 0 && _suspend == 0;
        /// <summary>The scope and suspension counters, for the test.</summary>
        public static int ScopeDepth => _depth;
        public static int SuspendDepth => _suspend;
        /// <summary>How many times her confirm had to put the save's cousin back (should stay 0: the game stores what the save held).</summary>
        public static int WriteBacks { get; private set; }

        public static CousinEntry Cousin => Catalog.Find(CousinId);

        /// <summary>Makes the slot set, all or none: with one missing, the mod could store 100 or break the menu.</summary>
        public static bool Install()
        {
            if (_complete) return true;
            _mainThread = Environment.CurrentManagedThreadId;
            bool ok = Set.Prefix(typeof(IconWindowController), nameof(IconWindowController.Initialize), typeof(SelectScreen), nameof(BeforeWindowInitialize), Type.EmptyTypes)
                    & Set.Postfix(typeof(ShortcutIconData), nameof(ShortcutIconData.Initialize), typeof(SelectScreen), nameof(AfterSlotInitialize), Type.EmptyTypes)
                    & Set.Prefix(typeof(GlobalSaveData), nameof(GlobalSaveData.CheckNewFlag), typeof(SelectScreen), nameof(BeforeCheckNewFlag),
                                 new[] { typeof(Il2CppStructArray<bool>), typeof(Il2CppStructArray<bool>), typeof(int) })
                    & Set.Hook(typeof(SelectionMenuController), nameof(SelectionMenuController.SetCharater), typeof(SelectScreen),
                               nameof(BeforeSetCharater), nameof(AfterSetCharater), nameof(FinallySetCharater), Type.EmptyTypes)
                    & Set.Hook(typeof(CharacterSelectionManager), nameof(CharacterSelectionManager.EndShortCutMove), typeof(SelectScreen),
                               nameof(BeforeEndShortCutMove), null, nameof(FinallyEndMove), Type.EmptyTypes)
                    & Set.Hook(typeof(CharacterSelectionManager), nameof(CharacterSelectionManager.EndSelectionMove), typeof(SelectScreen),
                               nameof(BeforeEndSelectionMove), null, nameof(FinallyEndMove), Type.EmptyTypes)
                    & Set.Hook(typeof(SaveDataController), nameof(SaveDataController.SetSaveAction), typeof(SelectScreen),
                               nameof(BeforeSetSaveAction), null, nameof(FinallySetSaveAction), new[] { typeof(int), typeof(bool) })
                    & Set.Hook(typeof(DataToConvert), nameof(DataToConvert.MonoDataToSaveData), typeof(SelectScreen),
                               nameof(BeforeSaveConvert), null, nameof(LeaveSuspension), new[] { typeof(SaveInfoMonoData) })
                    & Set.Hook(typeof(MainGameManager._StartSceneLoad_d__229), nameof(MainGameManager._StartSceneLoad_d__229.MoveNext), typeof(SelectScreen),
                               nameof(BeforeStageLoadStep), null, nameof(LeaveSuspension), Type.EmptyTypes)
                    & Set.Hook(typeof(EndingGameManager._StartSceneLoad_d__112), nameof(EndingGameManager._StartSceneLoad_d__112.MoveNext), typeof(SelectScreen),
                               nameof(BeforeEndingLoadStep), null, nameof(LeaveSuspension), Type.EmptyTypes);
            if (!ok || Set.Count != Expected)
            {
                Log.Error($"only {Set.Count} of the select screen's {Expected} hooks could be made; Burda is not added to the screen (pick her in the tab)");
                Set.UnpatchAll();
                return false;
            }
            _complete = true;
            Log.Info($"{Set.Count} select screen hooks in place: Burda is on page 4 of The Prince & Cousins");
            return true;
        }

        /// <summary>
        /// MelonMod.OnLateUpdate, first thing: no game method is on the stack, so the counters go back to zero, no confirm
        /// is under way, the save's cousin is noted for the net, and the scope set follows the settings (in while they
        /// name her, out when they stop). Never throws.
        /// </summary>
        public static void LateTick()
        {
            _depth = 0;
            _suspend = 0;
            _writeBack = false;
            _snap = false;
            if (!_complete) return;
            NoteSave();
            try
            {
                bool want = Settings.Active && Settings.CousinId == CousinId;
                if (want && _scope == null && !_scopeFailed) InstallScope();
                else if (!want && _scope != null) UninstallScope();
            }
            catch (Exception e) { Failed("scope hooks", e); }
        }

        private static void InstallScope()
        {
            var set = new HookSet("LongLostCousins.SelectScreen.Scope");
            bool ok = set.Postfix(typeof(GlobalSaveData), "get_GetSelectPlayerId", typeof(SelectScreen), nameof(AfterGetSelectPlayerId), Type.EmptyTypes)
                    & set.Hook(typeof(SelectionMenuController), nameof(SelectionMenuController.SelectionMenuControllerUpdate), typeof(SelectScreen),
                               nameof(EnterMenuScope), null, nameof(LeaveMarkedScope), Type.EmptyTypes)
                    & set.Hook(typeof(IconWindowController), nameof(IconWindowController.SetIcon), typeof(SelectScreen),
                               nameof(EnterIconScope), null, nameof(LeaveMarkedScope), Type.EmptyTypes)
                    & set.Hook(typeof(UIGuideObjectBehavior), nameof(UIGuideObjectBehavior.SetIconNameText), typeof(SelectScreen),
                               nameof(BeforeIconNameText), nameof(AfterIconNameText), nameof(FinallyIconNameText), new[] { typeof(bool) });
            if (!ok || set.Count != ScopeExpected)
            {
                Log.Error($"only {set.Count} of the select screen's {ScopeExpected} marker hooks could be made; the current-cousin marker stays on the Prince");
                set.UnpatchAll();
                _scopeFailed = true;
                return;
            }
            _scope = set;
            Log.Info($"{set.Count} marker hooks in place (the settings name {CousinId})");
        }

        private static void UninstallScope()
        {
            _scope.UnpatchAll();
            _scope = null;
            Log.Info("marker hooks removed (the settings no longer name " + CousinId + ")");
        }

        /// <summary>
        /// Every frame from LateTick: the save's cousin and custom index, read from the fields, kept as the net's fallback
        /// when the id is one the save may hold (not 100, not negative). Forgotten outside a game session, since another
        /// slot may be loaded next.
        /// </summary>
        private static void NoteSave()
        {
            try
            {
                if (!GameRefs.InGameSession) { _hasGood = false; return; }
                var save = GameRefs.Save;
                if (save == null) return;
                int id = save._selectPlayerId;
                if (!Chosen.IsStorableId(id)) return;
                _goodId = id;
                _goodCustom = save._customPlayerIndex;
                _hasGood = true;
            }
            catch (Exception e) { Failed("noting the save's cousin", e); }
        }

        /// <summary>A save path begins: no rewrite of the saved cousin until <see cref="SuspendLeave"/>.</summary>
        public static void SuspendEnter() => _suspend++;
        public static void SuspendLeave() { if (_suspend > 0) _suspend--; }

        /// <summary>For the mod's own reads of the saved cousin: no 100 in there, whatever is on the stack.</summary>
        public static Suspension Suspended()
        {
            _suspend++;
            return default;
        }

        public readonly struct Suspension : IDisposable
        {
            public void Dispose() { if (_suspend > 0) _suspend--; }
        }

        private static void Failed(string where, Exception e)
        {
            try { if (Once.Add(where + e.GetType().Name)) Log.Warn($"{where}: {e.GetType().Name}: {e.Message}"); } catch { }
        }

        /// <summary>The slot under the menu's cursor, the one SetCharater stores: window [_pageIndex], slot [_cursorIndex].</summary>
        public static ShortcutIconData Focused(SelectionMenuController menu)
        {
            var wins = menu != null ? menu._iconWindowControllers : null;
            int page = menu != null ? menu._pageIndex : -1;
            if (wins == null || page < 0 || page >= wins.Length) return null;
            var w = wins[page];
            var slots = w != null ? w._shortcutIconDatas : null;
            int i = w != null ? w._cursorIndex : -1;
            if (slots == null || i < 0 || i >= slots.Length) return null;
            return slots[i];
        }

        /// <summary>Her slot on this menu, or null (no such window or slot).</summary>
        public static ShortcutIconData SlotOf(SelectionMenuController menu)
        {
            var wins = menu != null ? menu._iconWindowControllers : null;
            if (wins == null || Page >= wins.Length || wins[Page] == null) return null;
            var slots = wins[Page]._shortcutIconDatas;
            return slots != null && Slot < slots.Length ? slots[Slot] : null;
        }

        // ---------------------------------------------------------------- the slot

        /// <summary>
        /// IconWindowController.Initialize, before it: page 4's free slot 13 gets id 100. Never on the mode-1 screen (the
        /// Cousin Creator's selection), whose own writers store a slot's id straight into the save: not when the menu says
        /// mode 1, and not while a CharacterSelectionManager exists in the scene.
        /// </summary>
        public static void BeforeWindowInitialize(IconWindowController __instance)
        {
            if (!_complete) return;
            try
            {
                var menu = __instance._selectionMenuController;
                if (menu == null || menu._sceneMode == SelectionMenuController.SceneMode.CHARACTERSELECTION) return;
                var wins = menu._iconWindowControllers;
                if (wins == null || Page >= wins.Length || wins[Page] == null || wins[Page].Pointer != __instance.Pointer) return;
                var slots = __instance._shortcutIconDatas;
                if (slots == null || Slot >= slots.Length || slots[Slot] == null) return;
                var s = slots[Slot];
                int id = (int)s._ITOKONAME;
                if (id == ModId) return;
                if (id != -1)
                {
                    if (Once.Add("taken" + id)) Log.Warn($"page 4 slot 14 already holds cousin {id}; Burda is not added there");
                    return;
                }
                if (UnityEngine.Object.FindObjectOfType<CharacterSelectionManager>() != null)
                {
                    if (Once.Add("charselect")) Log.Info("a CharacterSelectionManager is in the scene; Burda's slot is not filled here");
                    return;
                }
                if (Cousin == null) { if (Once.Add("nocousin")) Log.Warn("cousins.json has no " + CousinId + "; the slot stays empty"); return; }
                s._ITOKONAME = (ItokoSpawnData.ITOKONAME)ModId;
                SlotsFilled++;
                if (Once.Add("filled")) Log.Info("Burda's slot filled on page 4 (mode " + (int)menu._sceneMode + ")");
            }
            catch (Exception e) { Failed("IconWindowController.Initialize", e); }
        }

        /// <summary>
        /// ShortcutIconData.Initialize, after it: her slot looks found, with her icon and name. A free slot's Icon object is
        /// scaled to 0.001 in the scene (a found slot's is 0.2), so her icon gets a found sibling's scale.
        /// </summary>
        public static void AfterSlotInitialize(ShortcutIconData __instance)
        {
            try
            {
                if ((int)__instance._ITOKONAME != ModId) return;
                var c = Cousin;
                string name = c?.Name ?? "Burda";
                var data = __instance._oujiData;
                if (data != null)
                {
                    data._isReleased = true;
                    data._name = name;
                }
                if (__instance._backImage != null) __instance._backImage.enabled = true;
                var icon = __instance._iconImage;
                if (icon != null)
                {
                    icon.enabled = true;
                    var sprite = CousinIcon.Get(c, Settings.StyleFor(c));
                    if (sprite != null) icon.sprite = sprite;
                    if (!icon.gameObject.activeSelf) icon.gameObject.SetActive(true);
                    var rt = icon.rectTransform;
                    var want = FoundScale(__instance);
                    if (Mathf.Abs(rt.localScale.x - want.x) > 0.0001f || Mathf.Abs(rt.localScale.y - want.y) > 0.0001f)
                    {
                        if (Once.Add("iconscale")) Log.Info($"her icon was scaled {U.F(rt.localScale)}; set to a found slot's {U.F(want)}");
                        rt.localScale = want;
                    }
                }
                if (__instance._blankImage != null) __instance._blankImage.enabled = false;
                if (__instance._hatenaImage != null) __instance._hatenaImage.enabled = false;
                if (__instance._text != null) __instance._text.text = name;
            }
            catch (Exception e) { Failed("ShortcutIconData.Initialize", e); }
        }

        /// <summary>The icon scale of another slot in the same window whose icon is shown at a normal size, or the scene's 0.2.</summary>
        private static Vector3 FoundScale(ShortcutIconData slot)
        {
            try
            {
                var parent = slot.transform.parent;
                if (parent != null)
                    for (int i = 0; i < parent.childCount; i++)
                    {
                        var sib = parent.GetChild(i).GetComponent<ShortcutIconData>();
                        if (sib == null || sib.Pointer == slot.Pointer || sib._iconImage == null) continue;
                        var s = sib._iconImage.rectTransform.localScale;
                        if (s.x > 0.01f && s.y > 0.01f) return s;
                    }
            }
            catch (Exception e) { Failed("icon scale", e); }
            return U.V3(FoundIconScale, FoundIconScale, 1f);
        }

        /// <summary>GlobalSaveData.CheckNewFlag, before it: an id outside either array has no New mark (the original would throw).</summary>
        public static bool BeforeCheckNewFlag(Il2CppStructArray<bool> newFlags, Il2CppStructArray<bool> releasableFlags, int index, ref bool __result)
        {
            try
            {
                if (index < 0 || newFlags == null || releasableFlags == null || index >= newFlags.Length || index >= releasableFlags.Length)
                {
                    __result = false;
                    return false;
                }
            }
            catch (Exception e) { Failed("CheckNewFlag", e); }
            return true;
        }

        // ---------------------------------------------------------------- the confirm

        /// <summary>
        /// The save's cousin and custom index now, read from the fields: what her slot stands for while the game reads it.
        /// False, and logged, when there is no save object or it holds an id no slot stands for (100 or -1; the net keeps
        /// 100 out, so never seen): her slot is then left as it is and the net puts a cousin the save held back. Nothing
        /// is recorded from here.
        /// </summary>
        private static bool SaveNow(out int id, out int custom)
        {
            var save = GameRefs.Save;
            id = save != null ? save._selectPlayerId : -1;
            custom = save != null ? save._customPlayerIndex : -1;
            if (save != null && Chosen.IsStorableId(id)) return true;
            if (Once.Add("unstorable" + id))
                Log.Warn(save == null ? "no save object to read the cousin from; her slot is left as it is in this call"
                                      : $"the save holds {id}, which no slot stands for; her slot is left as it is in this call and the net puts a cousin the save held back");
            return false;
        }

        /// <summary>SetCharater's prefix, first thing: the save as this confirm finds it, or no snapshot (never a made-up 0).</summary>
        private static void TakeSnapshot()
        {
            _snap = false;
            try { _snap = SaveNow(out _snapId, out _snapCustom); }
            catch (Exception e) { Failed("SetCharater (reading the save)", e); }
        }

        /// <summary>
        /// The cousin the net puts back: the save as the confirm under way found it, else the last one <see cref="NoteSave"/>
        /// read from the save. False when neither is known.
        /// </summary>
        private static bool Known(out int id, out int custom, out string from)
        {
            if (_snap) { id = _snapId; custom = _snapCustom; from = "the save's cousin when this confirm began"; return true; }
            if (_hasGood) { id = _goodId; custom = _goodCustom; from = "the last cousin seen in the save"; return true; }
            id = custom = -1;
            from = null;
            return false;
        }

        /// <summary>
        /// Her 100 becomes the save's own cousin (<paramref name="id"/>, and <paramref name="custom"/> in the slot's
        /// _customId, which SetCharater stores for 999) in a slot the game is about to read, remembered for <see cref="PutBack"/>.
        /// </summary>
        private static void Swap(ShortcutIconData s, int id, int custom)
        {
            if (s == null || (int)s._ITOKONAME != ModId) return;
            foreach (var x in _swapped) if (x.Slot.Pointer == s.Pointer) return;
            _swapped.Add(new Swapped { Slot = s, Custom = s._customId });
            s._ITOKONAME = (ItokoSpawnData.ITOKONAME)id;
            s._customId = custom;
        }

        private static void PutBack()
        {
            if (_swapped.Count > 0)
            {
                var list = _swapped.ToArray();
                _swapped.Clear();
                foreach (var x in list)
                    try
                    {
                        if (x.Slot == null) continue;
                        x.Slot._ITOKONAME = (ItokoSpawnData.ITOKONAME)ModId;
                        x.Slot._customId = x.Custom;
                    }
                    catch (Exception e) { Failed("put back", e); }
            }
            var m = _swappedModel;
            _swappedModel = null;
            if (m != null)
                try { m.TOKONAME = (ItokoSpawnData.ITOKONAME)ModId; }
                catch (Exception e) { Failed("put back (model)", e); }
        }

        /// <summary>
        /// The net: the save and GlobalManager must never hold the mod's id. Each is checked and fixed on its own, and each
        /// fix is counted in <see cref="NetFirings"/> and logged as an error. A 100 in the save becomes a cousin the save
        /// itself held (<see cref="Known"/>: the confirm's snapshot, else the last one seen; with its custom index for 999);
        /// a 100 in GlobalManager becomes the save's cousin, or that same known one. With nothing known, the field is left
        /// as it is (never a made-up 0). Reads the fields, not the getter.
        /// </summary>
        private static void Net(string where)
        {
            try
            {
                var save = GameRefs.Save;
                if (save != null && save._selectPlayerId == ModId)
                {
                    NetFirings++;
                    if (Known(out int id, out int custom, out string from))
                    {
                        save._selectPlayerId = id;
                        if (id == Chosen.CreatorId) save._customPlayerIndex = custom;
                        Log.Error($"the save held the mod's id 100 at {where}; set back to {Chosen.Describe(id, custom)}, {from} (net firing {NetFirings})");
                    }
                    else Log.Error($"the save held the mod's id 100 at {where} and no cousin the save held is known; left as it is (net firing {NetFirings})");
                }
                var g = GameRefs.Global;
                if (g != null && g._nowCharacterID == ModId)
                {
                    NetFirings++;
                    int id;
                    string from;
                    if (save != null && Chosen.IsStorableId(save._selectPlayerId)) { id = save._selectPlayerId; from = "the save's cousin"; }
                    else if (!Known(out id, out _, out from))
                    {
                        Log.Error($"GlobalManager held the mod's id 100 at {where} and no cousin the save held is known; left as it is (net firing {NetFirings})");
                        return;
                    }
                    g._nowCharacterID = id;
                    Log.Error($"GlobalManager held the mod's id 100 at {where}; set back to {id}, {from} (net firing {NetFirings})");
                }
            }
            catch (Exception e) { Failed("net at " + where, e); }
        }

        /// <summary>
        /// Inside her confirm: the save's cousin and custom index go back to what they were when the confirm began, if the
        /// game changed them. Counted in <see cref="WriteBacks"/>.
        /// </summary>
        private static void WriteBack(string where)
        {
            if (!_writeBack || !_snap) return;
            try
            {
                var save = GameRefs.Save;
                if (save == null || (save._selectPlayerId == _snapId && save._customPlayerIndex == _snapCustom)) return;
                Log.Warn($"her confirm left the save at cousin {save._selectPlayerId} (custom index {save._customPlayerIndex}) at {where}; put back to {_snapId} ({_snapCustom})");
                save._selectPlayerId = _snapId;
                save._customPlayerIndex = _snapCustom;
                WriteBacks++;
            }
            catch (Exception e) { Failed("write back at " + where, e); }
        }

        /// <summary>
        /// SelectionMenuController.SetCharater, before it: her 100 becomes the save's own cousin (and its custom index) in
        /// her slot and in the focused slot, focused or not, so the game stores what the save already holds; and the
        /// settings say Burda, picked on the screen, with that cousin recorded, when she is the one focused. Any other slot
        /// turns her off. The rewrites are suspended (SetCharater reads the saved cousin for GlobalManager, and its save
        /// write, SetSaveAction, runs inside it). The save is read first of all, into the snapshot the swap, the record
        /// and the net use; without one (unreadable, or no storable id) her slot keeps 100, the net puts a cousin the save
        /// held back, and the record waits for the next check.
        /// </summary>
        public static void BeforeSetCharater(SelectionMenuController __instance)
        {
            TakeSnapshot();
            _suspend++;
            _writeBack = false;
            ShortcutIconData focused = null;
            bool pickedHer = false;
            // The swap first and on its own: nothing after it may leave 100 in a slot the game is about to store.
            try
            {
                PutBack();
                focused = Focused(__instance);
                pickedHer = focused != null && (int)focused._ITOKONAME == ModId;
                if (_snap)
                {
                    Swap(focused, _snapId, _snapCustom);
                    Swap(SlotOf(__instance), _snapId, _snapCustom);
                    _writeBack = pickedHer;
                }
            }
            catch (Exception e) { Failed("SetCharater (slot)", e); }
            try
            {
                if (focused == null) return;
                var c = Cousin;
                if (pickedHer && c != null)
                {
                    if (_snap)
                    {
                        Settings.Choose(c.Id, Settings.StyleFor(c), onScreen: true, saved: _snapId, custom: _snapCustom);
                        Log.Info($"{c.Name} picked on the select screen; the save keeps {Chosen.Describe(_snapId, _snapCustom)}");
                    }
                    else
                    {
                        Settings.Choose(c.Id, Settings.StyleFor(c), onScreen: true);
                        Chosen.CheckSoon();
                        Log.Info($"{c.Name} picked on the select screen; the save's cousin could not be read here, so it is recorded at the next check");
                    }
                }
                else if (Settings.Active)
                {
                    Log.Info($"cousin {(int)focused._ITOKONAME} picked on the select screen; {Settings.Describe()} is turned off");
                    Settings.Choose(null, null);
                    // SetCharater sets GlobalManager's run-time id from the save itself.
                    PrinceBase.Zeroed = false;
                }
            }
            catch (Exception e) { Failed("SetCharater (settings)", e); }
        }

        /// <summary>SetCharater, after it: 100 back in her slot, and the dressing hooks follow the choice.</summary>
        public static void AfterSetCharater()
        {
            try { PutBack(); }
            catch (Exception e) { Failed("SetCharater (put back)", e); }
            try
            {
                bool was = Chosen.On;
                Chosen.Refresh();
                // The cousin shown now is about to be rebuilt by the game: the new one gets dressed, not this one.
                if (!was && Chosen.On) Contexts.LeaveCurrentOwnerAlone();
            }
            catch (Exception e) { Failed("SetCharater (hooks)", e); }
        }

        /// <summary>SetCharater, always last: the slot, the save and the suspension are restored even when the game's method threw.</summary>
        public static Exception FinallySetCharater(Exception __exception)
        {
            try { PutBack(); }
            catch (Exception e) { Failed("SetCharater (finally)", e); }
            Net("SetCharater");
            WriteBack("the end of SetCharater");
            _writeBack = false;
            _snap = false;
            if (_suspend > 0) _suspend--;
            return __exception;
        }

        /// <summary>
        /// CharacterSelectionManager.EndShortCutMove, before it: it stores its menu's focused slot id straight into the save
        /// (+0x19C) and then GlobalManager. A focused 100 (or her slot) becomes the save's own cousin for the call.
        /// </summary>
        public static void BeforeEndShortCutMove(CharacterSelectionManager __instance)
        {
            try
            {
                PutBack();
                if (!SaveNow(out int id, out int custom)) return;
                var menu = __instance._selectionMenuController;
                Swap(Focused(menu), id, custom);
                Swap(SlotOf(menu), id, custom);
            }
            catch (Exception e) { Failed("EndShortCutMove", e); }
        }

        /// <summary>
        /// CharacterSelectionManager.EndSelectionMove, before it: it stores the focused 3D model's TOKONAME
        /// (_createtChecks[_scrollSelection._pageIndex]._modelCreates[_cursorIndex]) straight into the save. A 100 there
        /// becomes the save's own cousin for the call.
        /// </summary>
        public static void BeforeEndSelectionMove(CharacterSelectionManager __instance)
        {
            try
            {
                PutBack();
                var checks = __instance._createtChecks;
                var scroll = __instance._scrollSelection;
                if (checks == null || scroll == null) return;
                int page = scroll._pageIndex, i = __instance._cursorIndex;
                if (page < 0 || page >= checks.Length || checks[page] == null) return;
                var models = checks[page]._modelCreates;
                if (models == null || i < 0 || i >= models.Length || models[i] == null) return;
                var m = models[i];
                if ((int)m.TOKONAME != ModId) return;
                if (!SaveNow(out int id, out _)) return;
                m.TOKONAME = (ItokoSpawnData.ITOKONAME)id;
                _swappedModel = m;
            }
            catch (Exception e) { Failed("EndSelectionMove", e); }
        }

        /// <summary>EndShortCutMove and EndSelectionMove, always last: the ids go back, and the net checks the save.</summary>
        public static Exception FinallyEndMove(Exception __exception)
        {
            try { PutBack(); }
            catch (Exception e) { Failed("EndMove (finally)", e); }
            Net("the mode-1 selection end");
            return __exception;
        }

        /// <summary>
        /// SaveDataController.SetSaveAction, before it: the save is converted to bytes inside, so a stray 100 is caught here
        /// first, her confirm's save is checked against what it held before, and the getter's rewrites are suspended.
        /// </summary>
        public static void BeforeSetSaveAction()
        {
            _suspend++;
            Net("SetSaveAction");
            WriteBack("SetSaveAction");
        }

        /// <summary>SetSaveAction, always last: the suspension ends.</summary>
        public static Exception FinallySetSaveAction(Exception __exception)
        {
            if (_suspend > 0) _suspend--;
            return __exception;
        }

        // ---------------------------------------------------------------- the other save paths

        /// <summary>DataToConvert.MonoDataToSaveData copies the saved cousin into the save's bytes: no rewrite in there.</summary>
        public static void BeforeSaveConvert() => _suspend++;

        /// <summary>
        /// A StartSceneLoad step of the stage or the ending, before it: the getter's rewrites are suspended for the step
        /// (stage 20 keeps the getter's answer and copies it back to the save later), and, while the Prince-base hooks are
        /// in, PrinceBase sets the run-time cousin id for the step (<see cref="PrinceBase.ZeroForLoad"/>).
        /// </summary>
        public static void BeforeStageLoadStep()
        {
            _suspend++;
            LoadStep("stage");
        }

        public static void BeforeEndingLoadStep()
        {
            _suspend++;
            LoadStep("ending");
        }

        private static void LoadStep(string what)
        {
            try { if (PrinceBase.Installed) PrinceBase.ZeroForLoad(what); }
            catch (Exception e) { Failed(what + " load", e); }
        }

        /// <summary>MonoDataToSaveData and the StartSceneLoad steps, always last: the suspension ends, also when the game's method threw.</summary>
        public static Exception LeaveSuspension(Exception __exception)
        {
            if (_suspend > 0) _suspend--;
            return __exception;
        }

        // ---------------------------------------------------------------- the scope

        /// <summary>Into the scope (the test's own confirm uses this one directly).</summary>
        public static void EnterScope() => _depth++;

        /// <summary>Out of the scope, also when the game's method threw.</summary>
        public static Exception LeaveScope(Exception __exception)
        {
            if (_depth > 0) _depth--;
            return __exception;
        }

        /// <summary>
        /// A menu in the Cousin Creator's selection (mode 1) never opens the scope: her slot is not there, and the Creator's
        /// own writers of the saved cousin run in that scene.
        /// </summary>
        private static bool ScopeMenu(SelectionMenuController menu)
        {
            try { return menu != null && menu._sceneMode != SelectionMenuController.SceneMode.CHARACTERSELECTION; }
            catch { return false; }
        }

        /// <summary>SelectionMenuControllerUpdate, before it: into the scope, except on the mode-1 screen.</summary>
        public static void EnterMenuScope(SelectionMenuController __instance, out bool __state)
        {
            __state = ScopeMenu(__instance);
            if (__state) _depth++;
        }

        /// <summary>IconWindowController.SetIcon, before it: into the scope, except on the mode-1 screen.</summary>
        public static void EnterIconScope(IconWindowController __instance, out bool __state)
        {
            __state = false;
            try { __state = __instance != null && ScopeMenu(__instance._selectionMenuController); }
            catch { }
            if (__state) _depth++;
        }

        /// <summary>Their finalizer: out of the scope if they opened it, also when the game's method threw.</summary>
        public static Exception LeaveMarkedScope(bool __state, Exception __exception)
        {
            if (__state && _depth > 0) _depth--;
            return __exception;
        }

        /// <summary>
        /// GlobalSaveData.GetSelectPlayerId, after it: the getter's one postfix (about 62 call sites, so the first line
        /// returns when no scope is open). While she is worn (the answer is the cousin recorded when she was picked), it
        /// answers 0 inside the Prince-base loader scope and her id 100 inside the menu scope; the loader scope wins when
        /// both are open, and a suspension (save paths, the mod's own reads) wins over both. Main thread only.
        /// </summary>
        public static void AfterGetSelectPlayerId(ref int __result)
        {
            if ((_depth == 0 && PrinceBase.Depth == 0) || _suspend != 0) return;
            try
            {
                if (Environment.CurrentManagedThreadId != _mainThread || Settings.CousinId != CousinId || !GameRefs.InGameSession) return;
                var save = GameRefs.Save;
                // A compare only: a scoped read (stage 20's 0 in a loader scope, say) never becomes the record.
                if (save == null || !Chosen.IsRecord(__result, save._customPlayerIndex)) return;
                if (PrinceBase.Depth > 0)
                {
                    if (__result != 0) { __result = 0; PrinceBase.CountRewrite(); }
                }
                else __result = ModId;
            }
            catch (Exception e) { Failed("GetSelectPlayerId", e); }
        }

        /// <summary>UIGuideObjectBehavior.SetIconNameText, before it: the scope is suspended (the guide must read the real cousin).</summary>
        public static void BeforeIconNameText() => _suspend++;

        /// <summary>SetIconNameText, after it: with the cousin's name asked for and her worn, the guide says her name.</summary>
        public static void AfterIconNameText(UIGuideObjectBehavior __instance, bool oujiName)
        {
            try
            {
                if (!oujiName || !Chosen.Now) return;
                var setter = __instance.m_TextSetter;
                var c = Settings.Cousin;
                if (setter != null && c != null) setter.SetText(c.Name);
            }
            catch (Exception e) { Failed("SetIconNameText", e); }
        }

        public static Exception FinallyIconNameText(Exception __exception)
        {
            if (_suspend > 0) _suspend--;
            return __exception;
        }
    }
}
