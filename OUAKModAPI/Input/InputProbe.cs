using System;
using System.IO;
using System.Linq;
using System.Text;
using Il2Cpp;
using Il2CppApp.KatamariSin;
using Il2CppApp.KatamariSin.InputStatus;
using eKeyboardSpot = Il2CppApp.KatamariSin.InputKeyboard.eKeyboardSpot;
using MelonLoader.Utils;
using UnityEngine;

namespace OUAKModAPI
{
    /// <summary>
    /// Test only: UserData/OUAKModAPI/inputprobe.flag writes what the game's input and button-icon objects hold to the
    /// session log (pads and their icon types, the keyboard maps, the icon tables, the prompt-text sprite assets and
    /// their character names), then quits. Read-only: nothing is changed.
    /// </summary>
    internal static class InputProbe
    {
        private static string FlagPath => Path.Combine(MelonEnvironment.UserDataDirectory, "OUAKModAPI", "inputprobe.flag");
        private static bool _armed;
        private static float _at = -1f;
        private static readonly ModLog Log = ApiCore.Log.In("InputProbe");

        internal static void CheckFlag()
        {
            if (!File.Exists(FlagPath)) return;
            try { File.Delete(FlagPath); } catch { }
            _armed = true;
            GameEvents.Update += Tick;
            Log.Info("armed");
        }

        private static void Tick()
        {
            if (!_armed) return;
            if (_at < 0f) { _at = Time.unscaledTime + 25f; return; }   // the title screen, input set up
            if (Time.unscaledTime < _at) return;
            _armed = false;
            try { Dump(); } catch (Exception e) { Log.Error("probe: " + e); }
            Log.Info("DONE");
            ModTasks.Later(Application.Quit, 1.5f);
        }

        private static void Dump()
        {
            var ic = SingletonMonoBehaviour<InputController>.Instance;
            if (ic == null) { Log.Warn("no InputController"); return; }
            var sb = new StringBuilder();
            for (int i = 0; i < 2; i++)
            {
                try
                {
                    var pad = ic.Pad((InputController.ePadNo)i);
                    if (pad == null) { sb.AppendLine($"pad {i}: none"); continue; }
                    sb.AppendLine($"pad {i}: {pad.GetIl2CppType().Name}, connected {pad.IsConnectPad}, useKeyboard {pad.UseKeyboard}, icon type {pad.IconType}, " +
                                  $"buffers down {pad.keylistDown?.Length} kbDown {pad.keylistKeyboardDown?.Length} idx {string.Join(",", pad.bufferIndexNow ?? new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(0))}");
                }
                catch (Exception e) { sb.AppendLine($"pad {i}: {e.Message}"); }
            }
            var flags = InputController.KeysFlag;
            sb.AppendLine("KeysFlag: " + string.Join(", ", Enumerable.Range(0, Math.Min(flags?.Length ?? 0, 44)).Select(k => $"{(eKeyMap)k}=0x{flags[k]:X}")));
            var kb = ic.Keyboard;
            if (kb != null)
            {
                foreach (var spot in new[] { eKeyboardSpot.Game, eKeyboardSpot.UI })
                {
                    try
                    {
                        sb.AppendLine($"keyboard {spot}: " + string.Join(", ", Enumerable.Range(0, 44).Select(k =>
                        {
                            try { return $"{(eKeyMap)k}={kb.GetKeyboardMap(spot, (eKeyMap)k)}"; } catch { return $"{(eKeyMap)k}=?"; }
                        })));
                    }
                    catch (Exception e) { sb.AppendLine($"keyboard {spot}: {e.Message}"); }
                }
                try { sb.AppendLine($"keyboard layout {kb.keyboardLayout}, locked keys {kb.dicLockKey?.Count}, renamed {kb.replacingKeyName?.Count}"); } catch { }
            }
            var icon = ic.InputIcon;
            if (icon != null)
            {
                try
                {
                    var map = icon.dicKeymapToIconmap;
                    if (map != null) { var parts = new StringBuilder(); foreach (var kv in map) parts.Append($"{kv.Key}->{kv.Value} "); sb.AppendLine("keymap->iconmap: " + parts); }
                }
                catch (Exception e) { sb.AppendLine("iconmap: " + e.Message); }
                try
                {
                    var pads = icon.spritePads;
                    sb.AppendLine($"spritePads: {pads?.Length} sets");
                    if (pads != null) for (int t = 0; t < pads.Length; t++) { var set = pads[t]; sb.AppendLine($"  set {t}: {set?.GetIl2CppType().Name} {(set is null ? "" : set.ToString())}"); }
                }
                catch (Exception e) { sb.AppendLine("spritePads: " + e.Message); }
                foreach (eIconType t in Enum.GetValues(typeof(eIconType)))
                {
                    try { sb.AppendLine($"GetSprite({t}, A)={icon.GetSprite(t, eIconKeyMap.A)?.name}, B={icon.GetSprite(t, eIconKeyMap.B)?.name}, L2={icon.GetSprite(t, eIconKeyMap.L2)?.name}"); } catch (Exception e) { sb.AppendLine($"GetSprite {t}: {e.Message}"); }
                }
            }
            var spa = ic.InputSpriteAsset;
            if (spa != null)
            {
                try
                {
                    var d = spa.tmpSpriteAssetPads;
                    if (d != null)
                        foreach (var kv in d)
                        {
                            var arr = kv.Value;
                            for (int n = 0; arr != null && n < arr.Length; n++)
                            {
                                var a = arr[n];
                                var chars = a?.spriteCharacterTable;
                                sb.AppendLine($"TMP pad asset {kv.Key}[{n}]: {a?.name}, {chars?.Count} characters: " +
                                              (chars == null ? "" : string.Join(", ", Enumerable.Range(0, chars.Count).Select(k => $"{chars[k].name}#{chars[k].glyphIndex}"))));
                            }
                        }
                    sb.AppendLine($"current icon type {spa.curIconType}");
                    var names = spa.dictIconNameAndIndex;
                    if (names != null) { var parts = new StringBuilder(); foreach (var kv in names) parts.Append($"{kv.Key}={kv.Value} "); sb.AppendLine("icon names: " + parts); }
                }
                catch (Exception e) { sb.AppendLine("TMP assets: " + e.Message); }
            }
            Log.Info("\n" + sb);
        }
    }
}
