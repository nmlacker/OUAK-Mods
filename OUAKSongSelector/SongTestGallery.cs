using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Il2Cpp;
using Il2CppApp.KatamariSin;
using Il2CppTMPro;
using Il2CppApp.KatamariSin.InputStatus;
using OUAKModAPI;
using UnityEngine;
using UnityEngine.UI;

namespace OUAKSongSelector
{
    /// <summary>
    /// songtest.flag with "gallery": read-only look at the game's music gallery ("Sound Gallery", FavoriteBgm*) for a later
    /// redesign. In the Plaza it opens the panel the way the Plaza menu does (FavoriteBgmActivator.FavoriteBgmPanelActive),
    /// dumps every object under it (active state, components, texts, rect) to gallery_dump.txt, logs the controller's
    /// state, takes frame captures of the album list, the next album, the favourites list, and closes it.
    /// Save writes stay blocked; nothing is selected, registered or played on purpose.
    /// </summary>
    internal static partial class SongTest
    {
        private static bool _gallery;
        private static readonly HookSet PadHooks = new("ouak.songselector.testpad");
        private static eKeyMap _press = eKeyMap.END;
        private static int _pressFrame = -1;
        private static int _pressesRead;

        /// <summary>One frame of a UI button, the way the game reads it (UIInputController.IsDownButton), like OUAK Multiplayer's VirtualPad.</summary>
        private static IEnumerable<float> Press(eKeyMap key)
        {
            if (PadHooks.Count == 0) Msg("pad hook: " + PadHooks.Prefix(typeof(UIInputController), nameof(UIInputController.IsDownButton), typeof(SongTest), nameof(BeforeIsDown)));
            int read = _pressesRead;
            _press = key; _pressFrame = -1;
            float end = Time.unscaledTime + 2f;
            while (_pressesRead == read && Time.unscaledTime < end) yield return 0f;
            yield return 0f;
            _press = eKeyMap.END;
            Msg($"pressed {key}: read by the game {_pressesRead > read}");
        }

        public static bool BeforeIsDown(eKeyMap __0, ref bool __result)
        {
            try
            {
                if (_press == eKeyMap.END || __0 != _press) return true;
                int f = Time.frameCount;
                if (_pressFrame < 0) { _pressFrame = f; _pressesRead++; }
                if (_pressFrame != f) return true;
                __result = true;
                return false;
            }
            catch { return true; }
        }

        private static IEnumerable<float> GallerySetup() { yield return 0.2f; }
        private static IEnumerable<float> GalleryStage() { yield return 0.2f; }

        private static void HideExplorer()
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var p = asm.GetType("UnityExplorer.UI.UIManager")?.GetProperty("ShowMenu", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (p == null) continue;
                    p.SetValue(null, false);
                    Msg("UnityExplorer's windows hidden for the pictures");
                    return;
                }
            }
            catch (Exception e) { Msg("couldn't hide UnityExplorer: " + e.Message); }
        }

        private static FavoriteBgmActivator FindActivator()
        {
            var all = Resources.FindObjectsOfTypeAll<FavoriteBgmActivator>();
            Msg($"FavoriteBgmActivator objects: {all.Length}");
            foreach (var a in all)
            {
                if (a == null || a.gameObject == null) continue;
                var sc = a.gameObject.scene;
                Msg($"  {PathOf(a.transform)} scene '{sc.name}' loaded {sc.isLoaded} activeInHierarchy {a.gameObject.activeInHierarchy}");
                if (sc.isLoaded) return a;
            }
            return null;
        }

        private static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (var x = t; x != null; x = x.parent) parts.Add(x.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static void Dump(Transform t, StringBuilder sb, int depth)
        {
            // One object that can't be read doesn't stop the dump.
            try { DumpOne(t, sb, depth); }
            catch (Exception e) { sb.Append(new string(' ', depth * 2)).Append("? ").Append(t.name).Append(" (").Append(e.Message).Append(")\n"); }
            for (int i = 0; i < t.childCount; i++) Dump(t.GetChild(i), sb, depth + 1);
        }

        private static void DumpOne(Transform t, StringBuilder sb, int depth)
        {
            var go = t.gameObject;
            var comps = go.GetComponents<Component>().Where(c => c != null).Select(c => c.GetIl2CppType().Name).Where(n => n != "Transform").ToList();
            string rect = "";
            var rt = t.TryCast<RectTransform>();
            if (rt != null) rect = $" rect pos ({rt.anchoredPosition.x:0},{rt.anchoredPosition.y:0}) size ({rt.rect.width:0}x{rt.rect.height:0}) anchors ({rt.anchorMin.x:0.##},{rt.anchorMin.y:0.##})-({rt.anchorMax.x:0.##},{rt.anchorMax.y:0.##})";
            string text = "";
            var tmp = go.GetComponent<TextMeshProUGUI>();
            if (tmp != null) text = $" text \"{(tmp.text ?? "").Replace("\n", "\\n")}\" size {tmp.fontSize:0.#} font {tmp.font?.name}";
            if (go.GetComponent<TextMeshLocalizer>() != null) text += " (localized)";
            var img = go.GetComponent<Image>();
            // ColorUtility.ToHtmlStringRGBA is stripped from this build (measured: "Method unstripping failed").
            if (img != null) { var c = img.color; text += $" image {img.sprite?.name ?? "-"} color ({c.r:0.##},{c.g:0.##},{c.b:0.##},{c.a:0.##})"; }
            sb.Append(new string(' ', depth * 2)).Append(go.activeSelf ? "+ " : "- ").Append(go.name)
              .Append(" [").Append(string.Join(", ", comps)).Append(']').Append(rect).Append(text).Append('\n');
        }

        private static void GalleryState(FavoriteBgmActivator a, string when)
        {
            try
            {
                var c = a.favoriteBgmController;
                var p = a.favoriteBgmPlayer;
                var m = a.favoriteBgmManager;
                var names = c?.albumNames;
                Msg($"{when}: panel {a.fbPanel?.activeInHierarchy} list {a.bgmList?.activeInHierarchy} favourites {a.fbList?.activeInHierarchy} | " +
                    $"album {c?.albumState} of {c?.albumMax} (unlocked {c?.unlockAlbumMax}), list {c?.bgmListState}/{c?.bgmListMax}, left focus {c?.bgmLeftFocusState}, right focus {c?.bgmRightFocusState}, " +
                    $"extra album {c?.extraAlbumFlg}/{c?.extraAlbumState}, names [{(names == null ? "" : string.Join(" | ", names))}] | " +
                    $"player shuffle {p?.isShuffle} lastPlayMode {p?.lastTimePlayMode} shuffleState {p?.shufflePlayState} | activator playMode {a.selectPlayMode} kadai {a.isKadai} | " +
                    $"save playModeState {GameRefs.Save?._playModeState} | manager state {m?.favoriteBgmState} album {m?.albumNumber} playing {m?.playBgmNum}");
                var pm = p?.playModeText;
                if (pm != null)
                {
                    var tmp = pm.GetComponentInChildren<TextMeshProUGUI>(true);
                    Msg($"  play mode text object '{PathOf(pm.transform)}' active {pm.activeInHierarchy} text \"{tmp?.text}\"");
                }
            }
            catch (Exception e) { Msg("gallery state: " + e.Message); }
        }

        private static IEnumerable<float> GalleryPlaza()
        {
            yield return 5f;
            var a = FindActivator();
            Check("the music gallery's activator is in the Plaza", a != null);
            if (a == null) yield break;
            GalleryState(a, "before opening");
            Msg("opening it like the Plaza menu: FavoriteBgmPanelActive(false)");
            a.FavoriteBgmPanelActive(false);
            yield return 4f;
            HideExplorer();
            GalleryState(a, "open");
            FrameCapture.Request("gallery_1_open", Shots, p => Msg("picture: " + p));
            yield return 1.5f;
            var sb = new StringBuilder();
            var root = a.fbPanel != null ? a.fbPanel.transform.root : a.transform.root;
            sb.Append("== ").Append(PathOf(root)).Append('\n');
            Dump(root, sb, 0);
            File.WriteAllText(Path.Combine(Dir, "gallery_dump.txt"), sb.ToString());
            Msg($"hierarchy written: gallery_dump.txt ({sb.Length} chars) from {PathOf(root)}");
            Check("the gallery opened", a.fbPanel != null && a.fbPanel.activeInHierarchy);

            // The notices the gallery opens with (new songs, DLC: manager states INFOBGM / INFOCOMP / INFODLC) close with
            // Confirm, like a player does; then the gallery is in UPDATE (6), where it takes input.
            for (int i = 0; i < 6 && a.favoriteBgmManager != null && (int)a.favoriteBgmManager.favoriteBgmState != 6; i++)
            {
                foreach (var w in Press(eKeyMap.Select)) yield return w;
                yield return 1.5f;
                GalleryState(a, $"after Confirm {i + 1}");
            }
            Check("the gallery reaches its interactive state", a.favoriteBgmManager != null && (int)a.favoriteBgmManager.favoriteBgmState == 6, a.favoriteBgmManager?.favoriteBgmState.ToString());
            FrameCapture.Request("gallery_1b_main", Shots, p => Msg("picture: " + p));
            yield return 1.5f;

            // Next album with R1 (the button the game shows as E on a keyboard), then back with L1.
            foreach (var w in Press(eKeyMap.R1)) yield return w;
            yield return 2.5f;
            GalleryState(a, "after R1");
            FrameCapture.Request("gallery_2_next_album", Shots, p => Msg("picture: " + p));
            yield return 1.5f;
            foreach (var w in Press(eKeyMap.L1)) yield return w;
            yield return 2.5f;
            GalleryState(a, "after L1");
            FrameCapture.Request("gallery_3_back", Shots, p => Msg("picture: " + p));
            yield return 1.5f;

            // Down the list a few rows (the game's own page input).
            try { a.favoriteBgmController.InputPageDown(); Msg("InputPageDown"); } catch (Exception e) { Msg("InputPageDown: " + e.Message); }
            yield return 2f;
            GalleryState(a, "after page down");
            FrameCapture.Request("gallery_4_page_down", Shots, p => Msg("picture: " + p));
            yield return 1.5f;

            // The play-mode selector: the game's own switch, once, then back.
            try { a.favoriteBgmPlayer.ChangeIsShuffle(); Msg("ChangeIsShuffle"); } catch (Exception e) { Msg("ChangeIsShuffle: " + e.Message); }
            yield return 2f;
            GalleryState(a, "play mode switched");
            FrameCapture.Request("gallery_5_play_mode", Shots, p => Msg("picture: " + p));
            yield return 1.5f;
            try { a.favoriteBgmPlayer.ChangeIsShuffle(); Msg("ChangeIsShuffle (back)"); } catch (Exception e) { Msg("ChangeIsShuffle: " + e.Message); }
            yield return 2f;
            GalleryState(a, "play mode back");

            Msg("closing: FavoriteBgmPanelDeactive()");
            try { a.FavoriteBgmPanelDeactive(); } catch (Exception e) { Msg("close: " + e.Message); }
            yield return 3f;
            GalleryState(a, "closed");
            FrameCapture.Request("gallery_6_closed", Shots, p => Msg("picture: " + p));
            yield return 2f;
        }
    }
}
