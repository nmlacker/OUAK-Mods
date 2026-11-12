using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Il2CppApp.KatamariSin;
using UnityEngine;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace OUAKModAPI
{
    /// <summary>
    /// The freebie boxes of story stages (<c>MainGameItemBox</c>): what each one gives and shows
    /// (<see cref="GamePatches.ItemBoxes"/>, part of the API; nothing changes until a mod sets a rule).
    /// <para>How the game does it (traced 2026-11-06): a story box holds a bitmask of the five story freebies
    /// (<c>ContainItem</c>: Drink, Magnet, Pickel, Sonar, Timer) and shows a model of its freebie placed in the stage
    /// (<c>ItemParent</c> → "ItemObject" → e.g. "MAGNET"). Rolling it up (<c>Collected</c>) plays its "get" animation, picks
    /// one freebie of the mask at random, flies the model onto the katamari (<c>RecieveOnTopItemObj</c>) and puts the freebie
    /// in the slot (<c>SetInventoryItem</c>). Battle boxes draw from the battle pool instead and show no model: see
    /// <see cref="BattleItemPools"/>.</para>
    /// <para>Rules (any mod; they stack): <see cref="SetEnabled"/> takes a freebie out of every box (a box left with
    /// nothing disappears), <see cref="SetSwap"/> turns one freebie into another in every box, <see cref="SetBox"/> sets one box's
    /// contents. A changed box gives its new freebie and shows its model (<see cref="FreebieModels"/>), including on top of
    /// the katamari. <see cref="Reset"/> puts every box back.</para>
    /// </summary>
    public static class ItemBoxes
    {
        private static readonly HashSet<eInstageItemType> _off = new();
        private static readonly Dictionary<eInstageItemType, eInstageItemType> _swap = new();
        private static readonly Dictionary<int, eInstageItemType[]> _boxes = new();   // by instance id
        private static readonly Dictionary<int, BoxLook> _looks = new();
        private static readonly HashSet<int> _hidden = new();
        private static float _nextRefresh;

        private sealed class BoxLook { public MainGameItemBox Box; public GameObject Original; public GameObject Shown; public eInstageItemType Item; }

        /// <summary>A rule changed (refresh menus).</summary>
        public static event Action Changed;
        /// <summary>A changed box was rolled up: the box, and the freebie it gave.</summary>
        public static event Action<MainGameItemBox, eInstageItemType> Gave;

        /// <summary>The story freebies a box's mask can hold, in bit order (MainGameItemBox.itemName).</summary>
        public static readonly eInstageItemType[] MaskItems =
            { eInstageItemType.Drink, eInstageItemType.Magnet, eInstageItemType.Pickel, eInstageItemType.Sonar, eInstageItemType.Timer };

        internal static bool Install()
        {
            var h = ApiCore.Instance?.HarmonyInstance ?? throw new InvalidOperationException("the API isn't initialised");
            h.Patch(AccessTools.Method(typeof(MainGameItemBox), nameof(MainGameItemBox.Collected)),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(ItemBoxes), nameof(BeforeCollected))));
            GameEvents.StagePlaying += _ => Refresh();
            GameEvents.StageRestarted += _ => ModTasks.Later(Refresh, 0.5f);
            GameEvents.Update += FreebieModels.Tick;
            GameEvents.Update += () =>
            {
                // Boxes appear with the stage (and come back on a restart): keep them in line while a rule is set.
                if ((!Active && _looks.Count == 0 && _hidden.Count == 0) || Time.unscaledTime < _nextRefresh) return;
                _nextRefresh = Time.unscaledTime + 1f;
                Refresh();
            };
            return true;
        }

        // ------------------------------------------------------------------ rules

        /// <summary>True when some rule is set.</summary>
        public static bool Active => _off.Count > 0 || _swap.Count > 0 || _boxes.Count > 0;

        /// <summary>Lets <paramref name="item"/> come out of boxes (default) or not.</summary>
        public static void SetEnabled(eInstageItemType item, bool on)
        {
            if (on ? _off.Remove(item) : _off.Add(item)) OnChanged();
        }

        public static bool IsEnabled(eInstageItemType item) => !_off.Contains(item);

        /// <summary>Boxes that would give <paramref name="from"/> give <paramref name="to"/> instead (NoItem or the same = no swap).</summary>
        public static void SetSwap(eInstageItemType from, eInstageItemType to)
        {
            if (to == eInstageItemType.NoItem || to == from) { if (_swap.Remove(from)) OnChanged(); return; }
            _swap[from] = to;
            OnChanged();
        }

        /// <summary>What boxes give instead of <paramref name="from"/> (from itself when not swapped).</summary>
        public static eInstageItemType SwapOf(eInstageItemType from) => _swap.TryGetValue(from, out var t) ? t : from;

        /// <summary>Sets one box's contents (several = one of them at random, with the roulette); null = the game's own again.</summary>
        public static void SetBox(MainGameItemBox box, IEnumerable<eInstageItemType> items)
        {
            if (box == null) return;
            int id = box.GetInstanceID();
            if (items == null) _boxes.Remove(id);
            else _boxes[id] = items.Where(i => i != eInstageItemType.NoItem && i < eInstageItemType.InstageItemTypeMax).Distinct().ToArray();
            OnChanged();
        }

        /// <summary>Removes every rule and puts every box back as the game made it.</summary>
        public static void Reset()
        {
            _off.Clear(); _swap.Clear(); _boxes.Clear();
            OnChanged();
        }

        private static void OnChanged()
        {
            Refresh();
            try { Changed?.Invoke(); } catch (Exception e) { GameEvents.Report(Changed, e); }
        }

        // ------------------------------------------------------------------ boxes

        /// <summary>The story boxes in the loaded stage (battle boxes are left out: <see cref="BattleItemPools"/>).</summary>
        public static List<MainGameItemBox> All() =>
            Resources.FindObjectsOfTypeAll<MainGameItemBox>().Where(b => b != null && b.gameObject.scene.isLoaded && !b.IsMultiplayBox).ToList();

        /// <summary>What the game put in a box (its mask).</summary>
        public static eInstageItemType[] GameContents(MainGameItemBox box)
        {
            var list = new List<eInstageItemType>();
            if (box == null) return list.ToArray();
            for (int i = 0; i < MaskItems.Length; i++) if ((box.ContainItem & (1 << i)) != 0) list.Add(MaskItems[i]);
            return list.ToArray();
        }

        /// <summary>What a box gives with the rules applied (empty = nothing: the box is hidden).</summary>
        public static eInstageItemType[] Contents(MainGameItemBox box)
        {
            if (box == null) return Array.Empty<eInstageItemType>();
            if (_boxes.TryGetValue(box.GetInstanceID(), out var own)) return own;
            return GameContents(box).Select(SwapOf).Where(i => !_off.Contains(i)).Distinct().ToArray();
        }

        /// <summary>True when the rules change what <paramref name="box"/> gives.</summary>
        public static bool Changes(MainGameItemBox box) => !Contents(box).SequenceEqual(GameContents(box));

        /// <summary>Applies the rules to every box in the stage: models swapped, emptied boxes hidden, the rest put back.</summary>
        public static void Refresh()
        {
            if (!GameRefs.InStage || GameRefs.IsMultiplay) { if (!GameRefs.InGameSession) { _looks.Clear(); _hidden.Clear(); } return; }
            try
            {
                foreach (var box in All())
                {
                    var game = GameContents(box);
                    if (game.Length == 1) FreebieModels.Harvest(game[0], OriginalModel(box));
                    var now = Contents(box);
                    int id = box.GetInstanceID();
                    if (box.enabled) continue;   // being taken (its "get" animation)
                    if (now.Length == 0)
                    {
                        // Nothing left to give: the box goes away (back when the rules allow something again).
                        if (box.gameObject.activeSelf) { box.gameObject.SetActive(false); _hidden.Add(id); }
                        continue;
                    }
                    if (_hidden.Remove(id)) box.gameObject.SetActive(true);
                    Show(box, now.Length == 1 && (game.Length != 1 || now[0] != game[0]) ? now[0] : eInstageItemType.NoItem);
                }
            }
            catch (Exception e) { ApiCore.Log.Warn($"Item boxes: {e.Message}"); }
        }

        /// <summary>The box's own model (ItemParent → ItemObject → first child).</summary>
        private static GameObject OriginalModel(MainGameItemBox box)
        {
            try
            {
                if (_looks.TryGetValue(box.GetInstanceID(), out var l) && l.Original != null) return l.Original;
                var parent = box.ItemParent;
                if (parent == null || parent.transform.childCount == 0) return null;
                var obj = parent.transform.GetChild(0);
                return obj.childCount > 0 ? obj.GetChild(0).gameObject : obj.gameObject;
            }
            catch { return null; }
        }

        /// <summary>Shows <paramref name="item"/>'s model in the box (NoItem = the box's own model).</summary>
        private static void Show(MainGameItemBox box, eInstageItemType item)
        {
            int id = box.GetInstanceID();
            _looks.TryGetValue(id, out var look);
            var original = look?.Original ?? OriginalModel(box);
            if (original == null) return;
            if (look == null) _looks[id] = look = new BoxLook { Box = box, Original = original };
            if (look.Item == item && (item == eInstageItemType.NoItem || look.Shown != null)) return;
            if (look.Shown != null) { Object.Destroy(look.Shown); look.Shown = null; }
            look.Item = item;
            if (item == eInstageItemType.NoItem) { original.SetActive(true); return; }
            float size = 0f;
            try
            {
                var rs = original.GetComponentsInChildren<Renderer>(true);
                if (rs.Length > 0) { var b = rs[0].bounds; foreach (var r in rs) b.Encapsulate(r.bounds); size = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z)); }
            }
            catch { }
            Material mat = null;
            try { mat = original.GetComponentInChildren<Renderer>(true)?.sharedMaterial; } catch { }
            var shown = FreebieModels.Create(item, original.transform.parent, size > 0f ? size : 0.5f, mat);
            if (shown == null) { original.SetActive(true); look.Item = eInstageItemType.NoItem; return; }   // icon not loaded yet: try on the next refresh
            shown.transform.localPosition = original.transform.localPosition;
            shown.transform.localRotation = original.transform.localRotation;
            original.SetActive(false);
            look.Shown = shown;
        }

        // The game's Collected(), for a box the rules change (the same steps, with the rules' pick).
        private static bool BeforeCollected(MainGameItemBox __instance)
        {
            try
            {
                var box = __instance;
                if (box == null || box.IsMultiplayBox || !Active || !Changes(box)) return true;
                if (box.enabled) return false;   // already taken (the game returns too)
                var items = Contents(box);
                var gm = GameRefs.Main;
                if (gm == null) return true;
                var anim = box._anim;
                if (anim == null) box.gameObject.SetActive(false);
                else { anim.Play(box.ANIM_STATENAME_GET); box.enabled = true; }
                if (items.Length == 0) return false;
                var pick = items[Random.Range(0, items.Length)];
                if (box.ItemParent != null) gm.RecieveOnTopItemObj(box.ItemParent);
                gm.SetInventoryItem(pick, items.Length > 1, false);
                try { Gave?.Invoke(box, pick); } catch (Exception e) { GameEvents.Report(Gave, e); }
                return false;
            }
            catch (Exception e) { ApiCore.Log.Warn($"Item box: {e.Message}"); return true; }
        }

        /// <summary>One line per box, for logs and menus: "ItemMagnet: Magnet -> Shoes (model: game: SHOES01_B)".</summary>
        public static string Describe(MainGameItemBox box)
        {
            var game = GameContents(box);
            var now = Contents(box);
            string g = string.Join("/", game.Select(StageControl.Pretty));
            if (!Changes(box)) return $"{box.name}: {g}";
            string n = now.Length == 0 ? "nothing (hidden)" : string.Join("/", now.Select(StageControl.Pretty));
            _looks.TryGetValue(box.GetInstanceID(), out var look);
            return $"{box.name}: {g} -> {n}{(look?.Shown != null ? $" (model: {FreebieModels.Source(look.Item)})" : "")}";
        }
    }
}
