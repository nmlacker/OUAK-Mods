using System;
using System.IO;
using System.Linq;
using HarmonyLib;
using Il2Cpp;
using Il2CppApp.KatamariSin;
using Il2CppApp.KatamariSin.InputStatus;
using MelonLoader.Utils;
using UnityEngine;

namespace OUAKModAPI
{
    /// <summary>
    /// Test only: UserData/OUAKModAPI/inputtest.flag checks <see cref="PadBindings"/> on the title screen. A press of R1 is
    /// put into the controller's button word right before the API's remap (there's no controller on the test machine);
    /// with R1 and L2 swapped the game must see L2 and not R1, the icon sheets and pictures must show the swap, and Reset
    /// must undo all of it. Logs "[InputTest] PASS/FAIL" and quits. The user's bindings are put back afterwards.
    /// </summary>
    internal static class InputTest
    {
        private static string FlagPath => Path.Combine(MelonEnvironment.UserDataDirectory, "OUAKModAPI", "inputtest.flag");
        private static readonly ModLog Log = ApiCore.Log.In("InputTest");
        private static bool _armed, _hooked;
        private static int _step;
        private static float _at = -1f;
        private static ulong _inject;              // bits pressed on the pad this frame (test)
        private static ulong _seen;                // the word the game got after the remap
        private static int _fails;
        private static string _saved;

        internal static void CheckFlag()
        {
            if (!File.Exists(FlagPath)) return;
            try { File.Delete(FlagPath); } catch { }
            _armed = true;
            GameEvents.Update += Tick;
            Log.Info("armed");
        }

        private static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            var h = ApiCore.Instance.HarmonyInstance;
            h.Patch(AccessTools.Method(typeof(InputController), "Tick"),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(InputTest), nameof(Press))) { priority = Priority.First });
            // Read after the game's whole input update (InputController.Tick runs every pad's Tick), so after the API's remap.
            h.Patch(AccessTools.Method(typeof(InputController), "Tick"), postfix: new HarmonyMethod(AccessTools.Method(typeof(InputTest), nameof(Read))));
        }

        // Before the API's remap: the test's "controller" presses.
        private static void Press(InputController __instance)
        {
            if (_inject == 0) return;
            _calls++;
            var pad = __instance.Pad(0);
            var idx = pad?.bufferIndexNow; var down = pad?.keylistDown;
            if (idx == null || down == null || idx.Length == 0) return;
            _matched++;
            down[idx[0]] |= _inject;
        }

        // After the game's whole input update: what the game sees on the first pad.
        private static void Read(InputController __instance)
        {
            if (_inject == 0) return;
            var pad = __instance.Pad(0);
            var idx = pad?.bufferIndexNow; var down = pad?.keylistDown;
            if (idx == null || down == null || idx.Length == 0) return;
            _seen = down[idx[0]];
        }

        private static void Check(string what, bool ok, string detail)
        {
            if (!ok) _fails++;
            Log.Info($"{(ok ? "OK  " : "FAIL")} {what}: {detail}");
        }

        private static string Pic(Il2CppSystem.Collections.Generic.Dictionary<int, Sprite> t, int k) => t != null && t.ContainsKey(k) ? t[k]?.name : null;

        private static string Names(Il2CppTMPro.TMP_SpriteAsset a, string n) => string.Join("/", Enumerable.Range(0, a.spriteCharacterTable.Count).Where(k => a.spriteCharacterTable[k].name == n).Select(k => a.spriteCharacterTable[k].glyphIndex));

        private static int _pushR1, _pushL2, _frames;

        private static void Tick()
        {
            if (!_armed) return;
            if (_inject != 0)
            {
                try
                {
                    var p0 = SingletonMonoBehaviour<InputController>.Instance?.Pad(0);
                    _frames++;
                    if (p0 != null && p0.IsPush(eKeyMap.R1, 0)) _pushR1++;
                    if (p0 != null && p0.IsPush(eKeyMap.L2, 0)) _pushL2++;
                }
                catch { }
            }
            float now = Time.unscaledTime;
            if (_at < 0f) { _at = now + 20f; return; }
            if (now < _at) return;
            var ic = SingletonMonoBehaviour<InputController>.Instance;
            ulong r1 = InputController.KeysFlag[(int)eKeyMap.R1], l2 = InputController.KeysFlag[(int)eKeyMap.L2];
            var xinput = ic.InputSpriteAsset.tmpSpriteAssetPads[0][0];
            var pics = ic.InputIcon.spritePads[0];
            int iconL2 = (int)ic.InputIcon.KeymapToIconmap(eKeyMap.L2), iconR1 = (int)ic.InputIcon.KeymapToIconmap(eKeyMap.R1);
            switch (_step++)
            {
                case 0:
                    Hook();
                    PadBindings.TestNoSave = true;   // nothing the test sets is saved; the user's bindings come back at the end
                    PadBindings.Reset();
                    _origL2Glyph = Names(xinput, "L2"); _origR1Glyph = Names(xinput, "R1");
                    _origL2Pic = Pic(pics, iconL2); _origR1Pic = Pic(pics, iconR1);
                    _inject = r1;
                    _at = now + 0.3f;
                    return;
                case 1:
                    Log.Info($"pad updates seen {PadBindings.ReadsSeen}; pad 0 connected {ic.Pad(0)?.IsConnectPad}, useKeyboard {ic.Pad(0)?.UseKeyboard}; test press hook: {_calls} calls, {_matched} on pad 0, {_otherPads} on other pads; pad 0 is {ic.Pad(0)?.GetIl2CppType().Name}");
                    Check("no bindings: the game sees R1 pressed, not L2", _pushR1 > 0 && _pushL2 == 0, $"over {_frames} frames: R1 pressed in {_pushR1}, L2 in {_pushL2}");
                    _pushR1 = _pushL2 = _frames = 0;
                    PadBindings.Swap(eKeyMap.R1, eKeyMap.L2);
                    _at = now + 0.3f;
                    return;
                case 2:
                    Check("R1 and L2 swapped: pressing R1, the game sees L2 pressed, not R1", _pushL2 > 0 && _pushR1 == 0,
                        $"over {_frames} frames: L2 pressed in {_pushL2}, R1 in {_pushR1}");
                    _pushR1 = _pushL2 = _frames = 0;
                    Log.Info("raw:");
                    Check("(raw word)", true,
                        $"word 0x{_seen:X}, frames remapped {PadBindings.FramesRemapped}; API remap on pad 0: 0x{PadBindings.LastPad0Before:X} -> 0x{PadBindings.LastPad0After:X} (keyboard 0x{PadBindings.LastPad0Keyboard:X}); map {string.Join(",", PadBindings.Map.Select(kv => kv.Key + ">" + kv.Value))}");
                    Check("prompt icons: 'L2' shows R1's icon and 'R1' shows L2's (Xbox sheet)", Names(xinput, "L2") == _origR1Glyph && Names(xinput, "R1") == _origL2Glyph,
                        $"L2 glyphs {Names(xinput, "L2")} (R1's were {_origR1Glyph}), R1 glyphs {Names(xinput, "R1")} (L2's were {_origL2Glyph}); sheets updated {PadBindings.SheetsUpdated}");
                    if (_origL2Pic == null) Log.Info("icon pictures: the Xbox table is empty until an Xbox controller is used (not checked)");
                    else Check("icon pictures: L2's entry shows R1's picture", Pic(pics, iconL2) == _origR1Pic && Pic(pics, iconR1) == _origL2Pic,
                        $"L2 -> {Pic(pics, iconL2)} (R1 was {_origR1Pic}), R1 -> {Pic(pics, iconR1)} (L2 was {_origL2Pic})");
                    PadBindings.Reset();
                    _at = now + 0.3f;
                    return;
                case 3:
                    Check("reset: the game sees R1 again, icons back", _pushR1 > 0 && _pushL2 == 0 && Names(xinput, "L2") == _origL2Glyph && Pic(pics, iconL2) == _origL2Pic,
                        $"over {_frames} frames: R1 pressed in {_pushR1}, L2 in {_pushL2}; L2 glyphs {Names(xinput, "L2")}");
                    _inject = 0;
                    // The user's own (saved) bindings back.
                    PadBindings.ReloadSaved();
                    PadBindings.TestNoSave = false;
                    _armed = false;
                    Log.Info(_fails == 0 ? "PASS" : $"FAIL ({_fails})");
                    Log.Info("DONE");
                    ModTasks.Later(Application.Quit, 1.5f);
                    return;
            }
        }

        private static int _calls, _matched, _otherPads;
        private static string _origL2Glyph, _origR1Glyph, _origL2Pic, _origR1Pic;
    }
}
