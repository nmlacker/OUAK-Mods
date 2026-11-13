using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Il2Cpp;
using Il2CppApp.KatamariSin;
using Il2CppApp.KatamariSin.InputStatus;
using MelonLoader;
using UnityEngine;

namespace OUAKModAPI
{
    /// <summary>
    /// Controller rebinding (the user, 2026-11-12: "key rebinding and controller rebinding (update the glyphs too)"; the
    /// screen for it goes on the game's own Key Assign screen, once the user has shown where). The game has keyboard
    /// rebinding of its own (<c>KeyAssign</c>, <c>InputKeyboard.SetKeyboardMap</c>) but none for controllers.
    /// <para>How the game reads a controller (traced 2026-11-12): <c>InputPadInputSystem.Tick</c> →
    /// <c>UpdateInput</c> builds one button word per frame (<c>keylistDown[bufferIndexNow[0]]</c>, bits =
    /// <c>InputController.KeysFlag[eKeyMap]</c>) from the pad's buttons and ORs the keyboard's (<c>GetKeyboardInput</c>)
    /// into it. Each <see cref="eKeyMap"/> is a physical button whose job is fixed in the game's code, so a rebinding is a
    /// permutation of buttons: after the game's input update (<c>InputController.Tick</c>) the API moves the bits that came from the controller (the
    /// keyboard's stay as they are; the keyboard has its own rebinding), and the L2 / R2 trigger values with them.</para>
    /// <para>Button pictures follow: prompt text names a button's icon by the job (<c>&lt;sprite name="L2"&gt;</c>),
    /// so in every controller icon sheet (<c>InputSpriteAsset.tmpSpriteAssetPads</c>) the icon of the button doing a job
    /// gets that job's name, the way the game's keyboard rebinding renames key icons; and the icon tables of
    /// <c>InputIcon.spritePads</c> get the same swap. Saved in MelonPreferences [OUAKModAPI_Pad].</para>
    /// <code>PadBindings.Swap(eKeyMap.L2, eKeyMap.R1);   // R1 now does what L2 did, and L2 what R1 did; prompts show it
    /// PadBindings.Reset();</code>
    /// </summary>
    public static class PadBindings
    {
        /// <summary>The buttons that can be rebound (digital buttons and the D-pad).</summary>
        public static readonly eKeyMap[] Rebindable =
        {
            eKeyMap.A, eKeyMap.B, eKeyMap.X, eKeyMap.Y, eKeyMap.L1, eKeyMap.R1, eKeyMap.L2, eKeyMap.R2,
            eKeyMap.L3, eKeyMap.R3, eKeyMap.Back, eKeyMap.Start, eKeyMap.DpadLeft, eKeyMap.DpadRight, eKeyMap.DpadUp, eKeyMap.DpadDown,
        };

        // physical button -> the job it does (only buttons that differ from the game's)
        private static readonly Dictionary<eKeyMap, eKeyMap> _map = new();
        private static MelonPreferences_Entry<string> _pref;

        /// <summary>Frames whose controller buttons were rebound.</summary>
        public static int FramesRemapped { get; private set; }
        /// <summary>Raised after the bindings change (and their icons have been updated).</summary>
        public static event Action Changed;

        /// <summary>The job <paramref name="physical"/> does now (itself when not rebound).</summary>
        public static eKeyMap JobOf(eKeyMap physical) => _map.TryGetValue(physical, out var j) ? j : physical;

        /// <summary>The physical button that does <paramref name="job"/> now.</summary>
        public static eKeyMap ButtonFor(eKeyMap job)
        {
            foreach (var kv in _map) if (kv.Value == job) return kv.Key;
            return job;   // the bindings are a permutation: a job no rebinding names is still on its own button
        }

        /// <summary>The rebound buttons, as "physical>job" pairs (empty when everything is as the game has it).</summary>
        public static IReadOnlyDictionary<eKeyMap, eKeyMap> Map => _map;

        /// <summary>Makes <paramref name="physical"/> do <paramref name="job"/>; the button that did it takes over <paramref name="physical"/>'s old job.</summary>
        public static void Set(eKeyMap physical, eKeyMap job)
        {
            if (!Rebindable.Contains(physical) || !Rebindable.Contains(job)) throw new ArgumentException("not a rebindable button");
            var other = ButtonFor(job);
            var old = JobOf(physical);
            Put(physical, job);
            Put(other, old);
            Apply();
        }

        /// <summary>Swaps the jobs of two buttons.</summary>
        public static void Swap(eKeyMap a, eKeyMap b) => Set(a, JobOf(b));

        /// <summary>Every button back to the game's own job.</summary>
        public static void Reset() { _map.Clear(); Apply(); }

        private static void Put(eKeyMap physical, eKeyMap job)
        {
            if (physical == job) _map.Remove(physical); else _map[physical] = job;
        }

        internal static void Init(HarmonyLib.Harmony h, MelonPreferences_Category prefs)
        {
            _pref = prefs.GetEntry<string>("PadBindings") ?? prefs.CreateEntry("PadBindings", "", "Controller bindings",
                "Rebound controller buttons as physical>job pairs, e.g. \"R1>L2,L2>R1\" (empty = the game's own).");
            foreach (var pair in (_pref.Value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = pair.Split('>');
                if (p.Length == 2 && Enum.TryParse(p[0].Trim(), out eKeyMap a) && Enum.TryParse(p[1].Trim(), out eKeyMap b) && Rebindable.Contains(a) && Rebindable.Contains(b))
                    Put(a, b);
            }
            // After the game's input update (InputController.Tick), on the word the game then reads, keylistDown[bufferIndexNow[0]]:
            // the point where presses written by tests reach the game (measured: changes made inside a pad's own Tick don't).
            h.Patch(AccessTools.Method(typeof(InputController), "Tick"),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(PadBindings), nameof(AfterInputTick))) { priority = Priority.Last });
            GameEvents.SceneLoaded += _ => ApplyIcons();   // icon sheets can be (re)loaded with a scene
        }

        /// <summary>Test only: bindings changed while this is on are never saved (a crashed test can't leave them behind).</summary>
        internal static bool TestNoSave;

        /// <summary>Test only: the bindings back to what is saved.</summary>
        internal static void ReloadSaved()
        {
            _map.Clear();
            foreach (var pair in (_pref?.Value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = pair.Split('>');
                if (p.Length == 2 && Enum.TryParse(p[0].Trim(), out eKeyMap a) && Enum.TryParse(p[1].Trim(), out eKeyMap b) && Rebindable.Contains(a) && Rebindable.Contains(b))
                    Put(a, b);
            }
            ApplyIcons();
        }

        private static void Apply()
        {
            if (_pref != null && !TestNoSave)
            {
                _pref.Value = string.Join(",", _map.Select(kv => $"{kv.Key}>{kv.Value}"));
                try { MelonPreferences.Save(); } catch { }
            }
            ApplyIcons();
            try { Changed?.Invoke(); } catch (Exception e) { ApiCore.Log.Warn($"PadBindings.Changed: {e.Message}"); }
        }

        // ------------------------------------------------------------------ buttons

        /// <summary>Test: the first pad's last word before / after the remap, and the keyboard bits taken out.</summary>
        internal static ulong LastPad0Before, LastPad0After, LastPad0Keyboard;

        /// <summary>Input updates seen (InputController.Tick).</summary>
        public static int ReadsSeen { get; private set; }

        private static void AfterInputTick(InputController __instance)
        {
            ReadsSeen++;
            if (_map.Count == 0 || __instance == null) return;
            for (int i = 0; i < 8; i++)
            {
                InputPadBase pad;
                try { pad = __instance.Pad((InputController.ePadNo)i); } catch { continue; }
                if (pad != null) Remap(pad, __instance);
            }
        }

        private static void Remap(InputPadBase __instance, InputController ic)
        {
            try
            {
                var down = __instance.keylistDown;
                var idx = __instance.bufferIndexNow;
                if (down == null || idx == null || idx.Length == 0) return;
                int cur = idx[0];
                if (cur < 0 || cur >= down.Length) return;
                var flags = InputController.KeysFlag;
                ulong word = down[cur];
                ulong keyboard = 0;
                if (__instance.UseKeyboard)
                {
                    try { keyboard = ic.Keyboard?.GetKeyboardInput() ?? 0; } catch { }
                }
                ulong pad = word & ~keyboard;
                ulong moved = 0, cleared = 0;
                foreach (var kv in _map)
                {
                    ulong from = flags[(int)kv.Key], to = flags[(int)kv.Value];
                    cleared |= from;
                    if ((pad & from) != 0) moved |= to;
                }
                ulong result = (pad & ~cleared) | moved | keyboard;
                if (result != word) { down[cur] = result; FramesRemapped++; }
                if (__instance.Pointer == ic.Pad(0)?.Pointer) { LastPad0Before = word; LastPad0After = result; LastPad0Keyboard = keyboard; }
                // The triggers' analog values follow L2 / R2.
                RemapTriggers(__instance, cur);
            }
            catch (Exception e) { ApiCore.Log.Debug($"PadBindings: {e.Message}"); }
        }

        private static void RemapTriggers(InputPadBase pad, int cur)
        {
            var l2 = pad.keylistAnalogL2; var r2 = pad.keylistAnalogR2;
            if (l2 == null || r2 == null || cur >= l2.Length || cur >= r2.Length) return;
            float vl = l2[cur], vr = r2[cur];
            float nl = 0f, nr = 0f;
            void To(eKeyMap job, float v) { if (job == eKeyMap.L2) nl = Mathf.Max(nl, v); else if (job == eKeyMap.R2) nr = Mathf.Max(nr, v); }
            To(JobOf(eKeyMap.L2), vl);
            To(JobOf(eKeyMap.R2), vr);
            l2[cur] = nl; r2[cur] = nr;
        }

        // ------------------------------------------------------------------ icons

        // The names the icon sheets had before the API renamed anything (per sheet, per character).
        private static readonly Dictionary<IntPtr, string[]> _origNames = new();
        // The icon tables' own sprites (per table, per icon number).
        private static readonly Dictionary<IntPtr, Dictionary<int, Sprite>> _origSprites = new();

        /// <summary>Icon sheets updated by the last <see cref="ApplyIcons"/>.</summary>
        public static int SheetsUpdated { get; private set; }

        /// <summary>Puts the button pictures in line with the bindings (called by the API when they change).</summary>
        public static void ApplyIcons()
        {
            var ic = SingletonMonoBehaviour<InputController>.Instance;
            if (ic == null) return;
            int sheets = 0;
            // Prompt text: rename the icon of each button to the job it does.
            try
            {
                var assets = ic.InputSpriteAsset?.tmpSpriteAssetPads;
                if (assets != null)
                    foreach (var kv in assets)
                    {
                        var arr = kv.Value;
                        for (int n = 0; arr != null && n < arr.Length; n++)
                        {
                            var a = arr[n];
                            var chars = a?.spriteCharacterTable;
                            if (chars == null) continue;
                            if (!_origNames.TryGetValue(a.Pointer, out var orig))
                            {
                                orig = new string[chars.Count];
                                for (int k = 0; k < chars.Count; k++) orig[k] = chars[k].name;
                                _origNames[a.Pointer] = orig;
                            }
                            if (orig.Any(nm => nm != null && nm.StartsWith("keyb_"))) continue;   // keyboard sheets: the game's own rebinding
                            bool changed = false;
                            for (int k = 0; k < chars.Count && k < orig.Length; k++)
                            {
                                string want = IconJobName(orig[k]);
                                if (chars[k].name != want) { chars[k].name = want; changed = true; }
                            }
                            if (changed) { a.UpdateLookupTables(); sheets++; }
                        }
                    }
            }
            catch (Exception e) { ApiCore.Log.Debug($"PadBindings icons (text): {e.Message}"); }
            // Icon pictures: each job's entry shows the button that does it.
            try
            {
                var icon = ic.InputIcon;
                var tables = icon?.spritePads;
                if (tables != null)
                    for (int t = 0; t < tables.Length; t++)
                    {
                        var table = tables[t];
                        if (table == null) continue;
                        if (!_origSprites.TryGetValue(table.Pointer, out var orig))
                        {
                            orig = new Dictionary<int, Sprite>();
                            foreach (var e in table) orig[e.Key] = e.Value;
                            _origSprites[table.Pointer] = orig;
                        }
                        foreach (var job in Rebindable)
                        {
                            int jobIcon = (int)icon.KeymapToIconmap(job);
                            int btnIcon = (int)icon.KeymapToIconmap(ButtonFor(job));
                            if (orig.TryGetValue(btnIcon, out var s) && table.ContainsKey(jobIcon)) table[jobIcon] = s;
                        }
                    }
            }
            catch (Exception e) { ApiCore.Log.Debug($"PadBindings icons (pictures): {e.Message}"); }
            SheetsUpdated = sheets;
        }

        // An icon named after a button (as the sheet had it) is shown for the job that button does now.
        private static string IconJobName(string originalName)
        {
            if (originalName == null) return null;
            foreach (var b in Rebindable)
                if (originalName == IconName(b)) return IconName(JobOf(b));
            return originalName;
        }

        // The icon name prompt text uses for a button (the sheets' names: A, B, L2, Back, Start, DpadLeft...).
        private static string IconName(eKeyMap b) => b.ToString();
    }
}
