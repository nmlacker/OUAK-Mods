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
    /// Prince (0) and the mod's settings say Burda, picked on the screen.
    /// <para>Two sets of hooks. The slot set is always in place, because this screen is where she is picked: the slot
    /// loop when the menu opens, the confirm, the two mode-1 writers of the saved cousin, and a net on the save itself,
    /// which turns a stray 100 into the Prince before the save is converted to bytes. The scope set exists only while the
    /// settings name her: two short scopes (the menu's update and the slot icons' refresh) in which a saved Prince reads
    /// as 100, so the current-cousin marker sits on her slot and the Prince's slot can be picked, and the guide's name.
    /// The scope set comes and goes only in OnLateUpdate, when no game method is on the stack. Every handler catches
    /// everything; none lets an exception reach the game.</para>
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
        /// <summary>The slot set: window and slot Initialize, CheckNewFlag, SetCharater, EndShortCutMove, EndSelectionMove, SetSaveAction.</summary>
        public const int Expected = 7;
        /// <summary>The scope set: the saved-cousin getter, the two scopes, SetIconNameText.</summary>
        public const int ScopeExpected = 4;
        /// <summary>The scale of a found slot's icon (Ouji_7's Icon in the OujiShip scene); a free slot's icon is 0.001.</summary>
        private const float FoundIconScale = 0.2f;

        private static bool _complete, _scopeFailed;
        private static HookSet _scope;
        private static int _depth, _suspend;
        private static readonly List<ShortcutIconData> _swapped = new();
        private static OujiModelCreate _swappedModel;

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
        /// <summary>Inside the menu's update or the icons' refresh, and not suspended: a saved Prince reads as 100 here.</summary>
        public static bool InScope => _depth > 0 && _suspend == 0;
        /// <summary>The scope and suspension counters, for the test.</summary>
        public static int ScopeDepth => _depth;
        public static int SuspendDepth => _suspend;

        public static CousinEntry Cousin => Catalog.Find(CousinId);

        /// <summary>Makes the slot set, all or none: with one missing, the mod could store 100 or break the menu.</summary>
        public static bool Install()
        {
            if (_complete) return true;
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
                    & Set.Prefix(typeof(SaveDataController), nameof(SaveDataController.SetSaveAction), typeof(SelectScreen), nameof(BeforeSetSaveAction),
                                 new[] { typeof(int), typeof(bool) });
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
        /// MelonMod.OnLateUpdate, first thing: no game method is on the stack, so the counters go back to zero and the
        /// scope set follows the settings (in while they name her, out when they stop). Never throws.
        /// </summary>
        public static void LateTick()
        {
            _depth = 0;
            _suspend = 0;
            if (!_complete) return;
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
                               nameof(EnterScope), null, nameof(LeaveScope), Type.EmptyTypes)
                    & set.Hook(typeof(IconWindowController), nameof(IconWindowController.SetIcon), typeof(SelectScreen),
                               nameof(EnterScope), null, nameof(LeaveScope), Type.EmptyTypes)
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

        /// <summary>Her 100 becomes 0 in a slot the game is about to read, remembered for <see cref="PutBack"/>.</summary>
        private static void Swap(ShortcutIconData s)
        {
            if (s == null || (int)s._ITOKONAME != ModId) return;
            foreach (var x in _swapped) if (x.Pointer == s.Pointer) return;
            s._ITOKONAME = (ItokoSpawnData.ITOKONAME)0;
            _swapped.Add(s);
        }

        private static void PutBack()
        {
            if (_swapped.Count > 0)
            {
                var list = _swapped.ToArray();
                _swapped.Clear();
                foreach (var s in list)
                    try { if (s != null) s._ITOKONAME = (ItokoSpawnData.ITOKONAME)ModId; }
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
        /// fix is counted in <see cref="NetFirings"/> and logged as an error. Reads the field, not the getter.
        /// </summary>
        private static void Net(string where)
        {
            try
            {
                var save = GameRefs.Save;
                if (save != null && save._selectPlayerId == ModId)
                {
                    save._selectPlayerId = 0;
                    NetFirings++;
                    Log.Error($"the save held the mod's id 100 at {where}; set back to the Prince (net firing {NetFirings})");
                }
                var g = GameRefs.Global;
                if (g != null && g._nowCharacterID == ModId)
                {
                    g._nowCharacterID = 0;
                    NetFirings++;
                    Log.Error($"GlobalManager held the mod's id 100 at {where}; set back to the Prince (net firing {NetFirings})");
                }
            }
            catch (Exception e) { Failed("net at " + where, e); }
        }

        /// <summary>
        /// SelectionMenuController.SetCharater, before it: her 100 becomes 0 (the Prince) in her slot and in the focused
        /// slot, focused or not, for the game to store, and the settings say Burda, picked on the screen, when she is the
        /// one focused. Any other slot turns her off. The scope is suspended (SetCharater reads the saved cousin for
        /// GlobalManager).
        /// </summary>
        public static void BeforeSetCharater(SelectionMenuController __instance)
        {
            _suspend++;
            ShortcutIconData focused = null;
            bool pickedHer = false;
            // The swap first and on its own: nothing after it may leave 100 in a slot the game is about to store.
            try
            {
                PutBack();
                focused = Focused(__instance);
                pickedHer = focused != null && (int)focused._ITOKONAME == ModId;
                Swap(focused);
                Swap(SlotOf(__instance));
            }
            catch (Exception e) { Failed("SetCharater (slot)", e); }
            try
            {
                if (focused == null) return;
                var c = Cousin;
                if (pickedHer && c != null)
                {
                    Settings.Choose(c.Id, Settings.StyleFor(c), onScreen: true);
                    Log.Info($"{c.Name} picked on the select screen; the game stores the Prince");
                }
                else if (Settings.Active)
                {
                    Log.Info($"cousin {(int)focused._ITOKONAME} picked on the select screen; {Settings.Describe()} is turned off");
                    Settings.Choose(null, null);
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

        /// <summary>SetCharater, always last: the slot and the scope are restored even when the game's method threw.</summary>
        public static Exception FinallySetCharater(Exception __exception)
        {
            try { PutBack(); }
            catch (Exception e) { Failed("SetCharater (finally)", e); }
            Net("SetCharater");
            if (_suspend > 0) _suspend--;
            return __exception;
        }

        /// <summary>
        /// CharacterSelectionManager.EndShortCutMove, before it: it stores its menu's focused slot id straight into the save
        /// (+0x19C) and then GlobalManager. A focused 100 (or her slot) becomes 0 for the call.
        /// </summary>
        public static void BeforeEndShortCutMove(CharacterSelectionManager __instance)
        {
            try
            {
                PutBack();
                var menu = __instance._selectionMenuController;
                Swap(Focused(menu));
                Swap(SlotOf(menu));
            }
            catch (Exception e) { Failed("EndShortCutMove", e); }
        }

        /// <summary>
        /// CharacterSelectionManager.EndSelectionMove, before it: it stores the focused 3D model's TOKONAME
        /// (_createtChecks[_scrollSelection._pageIndex]._modelCreates[_cursorIndex]) straight into the save. A 100 there
        /// becomes 0 for the call.
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
                m.TOKONAME = (ItokoSpawnData.ITOKONAME)0;
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

        /// <summary>SaveDataController.SetSaveAction, before it: the save is converted to bytes inside, so a stray 100 is caught here first.</summary>
        public static void BeforeSetSaveAction() => Net("SetSaveAction");

        // ---------------------------------------------------------------- the scope

        /// <summary>SelectionMenuControllerUpdate and IconWindowController.SetIcon, before them: into the scope.</summary>
        public static void EnterScope() => _depth++;

        /// <summary>Their finalizer: out of the scope, also when the game's method threw.</summary>
        public static Exception LeaveScope(Exception __exception)
        {
            if (_depth > 0) _depth--;
            return __exception;
        }

        /// <summary>GlobalSaveData.GetSelectPlayerId, after it: in the scope, a saved Prince reads as her id while she is worn.</summary>
        public static void AfterGetSelectPlayerId(ref int __result)
        {
            if (_depth == 0 || _suspend != 0 || __result != 0) return;
            try
            {
                if (Settings.Active && Settings.CousinId == CousinId) __result = ModId;
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
