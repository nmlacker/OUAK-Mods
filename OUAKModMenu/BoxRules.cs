using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppApp.KatamariSin;
using MelonLoader;
using OUAKModAPI;

namespace OUAKModMenu
{
    /// <summary>
    /// The Item boxes tab's rules for story stages' freebie boxes, saved under [OUAKModMenu] and applied through the
    /// API's <see cref="ItemBoxes"/> (which swaps what a box gives and the model it shows; its rules last a session).
    /// A story box can only hold Drink, Magnet, Pickel, Sonar or Timer (ItemBoxes.MaskItems): each can be switched off
    /// (a box left with nothing disappears until it's allowed again) or swapped for any freebie. Per-box choices
    /// (ItemBoxes.SetBox) only last for the loaded stage and aren't saved.
    /// </summary>
    internal static class BoxRules
    {
        private static MelonPreferences_Entry<string> _offPref, _swapPref;

        public static eInstageItemType[] BoxItems => ItemBoxes.MaskItems;

        public static void Init(MelonPreferences_Category cat)
        {
            _offPref = cat.CreateEntry("ItemBoxOff", "", "Item boxes: freebies off",
                "Story freebie boxes don't give these (comma-separated, e.g. Pickel,Timer); a box left with nothing disappears.");
            _swapPref = cat.CreateEntry("ItemBoxSwaps", "", "Item boxes: swaps",
                "Story freebie boxes give another freebie instead (comma-separated from=to, e.g. Drink=Shoes,Sonar=SP_Crown).");
            Load(_offPref.Value, _swapPref.Value);
        }

        public static void SetEnabled(eInstageItemType item, bool on) { ItemBoxes.SetEnabled(item, on); Save(); }

        public static void SetSwap(eInstageItemType from, eInstageItemType to) { ItemBoxes.SetSwap(from, to); Save(); }

        /// <summary>Every box back to what the game made (the saved rules too).</summary>
        public static void Reset() { ItemBoxes.Reset(); Save(); }

        /// <summary>Tests: the saved rules as they are, to put back afterwards.</summary>
        public static (string off, string swaps) Snapshot() => (_offPref?.Value ?? "", _swapPref?.Value ?? "");

        public static void Restore((string off, string swaps) saved)
        {
            foreach (var i in BoxItems) { ItemBoxes.SetEnabled(i, true); ItemBoxes.SetSwap(i, i); }
            Load(saved.off, saved.swaps);
            Save();
        }

        /// <summary>The rules as one line ("Pickel off; Drink -> Shoes"), or "none".</summary>
        public static string Summary()
        {
            var parts = new List<string>();
            foreach (var i in BoxItems)
            {
                if (!ItemBoxes.IsEnabled(i)) parts.Add($"{StageControl.Pretty(i)} off");
                var to = ItemBoxes.SwapOf(i);
                if (to != i) parts.Add($"{StageControl.Pretty(i)} -> {StageControl.Pretty(to)}");
            }
            return parts.Count == 0 ? "none" : string.Join("; ", parts);
        }

        private static void Load(string off, string swaps)
        {
            try
            {
                foreach (var name in Split(off))
                    if (Enum.TryParse(name, out eInstageItemType item) && BoxItems.Contains(item)) ItemBoxes.SetEnabled(item, false);
                foreach (var pair in Split(swaps))
                {
                    var kv = pair.Split('=');
                    if (kv.Length == 2 && Enum.TryParse(kv[0].Trim(), out eInstageItemType from) && Enum.TryParse(kv[1].Trim(), out eInstageItemType to)
                        && BoxItems.Contains(from) && to != eInstageItemType.NoItem && to < eInstageItemType.InstageItemTypeMax)
                        ItemBoxes.SetSwap(from, to);
                }
            }
            catch (Exception e) { MelonLogger.Warning($"Item box rules: {e.Message}"); }
        }

        private static void Save()
        {
            if (_offPref == null) return;
            _offPref.Value = string.Join(",", BoxItems.Where(i => !ItemBoxes.IsEnabled(i)));
            _swapPref.Value = string.Join(",", BoxItems.Where(i => ItemBoxes.SwapOf(i) != i).Select(i => $"{i}={ItemBoxes.SwapOf(i)}"));
            MelonPreferences.Save();
        }

        private static IEnumerable<string> Split(string s) =>
            (s ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
