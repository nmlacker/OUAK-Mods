using System;
using System.IO;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

namespace OUAKModAPI
{
    /// <summary>
    /// Pictures of the game's own frame, with everything drawn over it (HUD, mod overlays, the mod window):
    /// read back from the game's frame buffer at the very end of the API's OnGUI (Repaint), so a picture only
    /// ever holds this game's image, never anything else on the screen. Saved as PNG.
    /// </summary>
    public static class FrameCapture
    {
        private static string _pendingName, _pendingFolder;
        private static Action<string> _pendingDone;

        /// <summary>The folder test pictures go to by default: UserData/OUAKModMenu/TestShots.</summary>
        public static string TestShotsFolder => Path.Combine(MelonEnvironment.UserDataDirectory, "OUAKModMenu", "TestShots");

        /// <summary>What happened to the last picture ("name saved (WxH)" or the error).</summary>
        public static string Last { get; private set; } = "";

        /// <summary>A picture is waiting for the next frame.</summary>
        public static bool Busy => _pendingName != null;

        /// <summary>Takes a picture of the next frame as &lt;folder&gt;/&lt;name&gt;.png (TestShots by default).</summary>
        /// <param name="done">Called with the saved file's path (or null when it failed).</param>
        public static void Request(string name, string folder = null, Action<string> done = null)
        {
            // Any name works: characters Windows doesn't allow in file names become "_".
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            _pendingName = name;
            _pendingFolder = folder ?? TestShotsFolder;
            _pendingDone = done;
        }

        /// <summary>The API calls this at the end of OnGUI.</summary>
        internal static void AfterGui()
        {
            if (_pendingName == null || Event.current == null || Event.current.type != EventType.Repaint) return;
            string name = _pendingName, folder = _pendingFolder;
            var done = _pendingDone;
            _pendingName = null;
            _pendingDone = null;
            string path = null;
            Texture2D tex = null;
            try
            {
                int w = Screen.width, h = Screen.height;
                // The Texture2D calls this game keeps (other overloads are stripped).
                tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0f, 0f, w, h), 0, 0);
                tex.Apply();
                Directory.CreateDirectory(folder);
                path = Path.Combine(folder, name + ".png");
                File.WriteAllBytes(path, Png.Encode(tex.GetPixels32(), w, h));
                Last = $"{name} saved ({w}x{h})";
            }
            catch (Exception e)
            {
                Last = $"{name} failed: {e.Message}";
                path = null;
            }
            finally { if (tex != null) UnityEngine.Object.Destroy(tex); }
            try { done?.Invoke(path); } catch (Exception e) { ApiCore.Log.Warn($"Screenshot callback failed: {e.Message}"); }
        }

        /// <summary>Saves a texture (any texture, also non-readable ones and render textures) as &lt;folder&gt;/&lt;name&gt;.png.</summary>
        public static string SaveTexture(Texture src, string name, string folder = null, bool keepAlpha = false)
        {
            if (src == null) return $"{name}: no picture";
            try
            {
                var bytes = ReadTexture(src, out int w, out int h);
                string dir = folder ?? TestShotsFolder;
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, name + ".png"), Png.Encode(bytes, w, h, keepAlpha));
                return $"{name} saved";
            }
            catch (Exception e) { return $"{name} failed: {e.Message}"; }
        }

        /// <summary>A copy of any texture's pixels (rows from the bottom), through a render texture.</summary>
        public static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Color32> ReadTexture(Texture src, out int w, out int h)
        {
            RenderTexture rt = null;
            Texture2D tex = null;
            var prev = RenderTexture.active;
            w = src.width;
            h = src.height;
            try
            {
                // Render textures are read directly; anything else is copied into one (the 4-argument Blit is the one this game keeps).
                var asRt = src.TryCast<RenderTexture>();
                if (asRt == null)
                {
                    rt = new RenderTexture(w, h, 0) { hideFlags = HideFlags.HideAndDontSave };
                    Graphics.Blit(src, rt, Vector2.one, Vector2.zero);
                    asRt = rt;
                }
                RenderTexture.active = asRt;
                tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0f, 0f, w, h), 0, 0);
                tex.Apply();
                return tex.GetPixels32();
            }
            finally
            {
                RenderTexture.active = prev;
                if (tex != null) UnityEngine.Object.Destroy(tex);
                if (rt != null) UnityEngine.Object.Destroy(rt);
            }
        }
    }

    /// <summary>
    /// Screenshots for players: the game's frame (with the HUD and overlays) to
    /// UserData/OUAKModAPI/Screenshots/OUAK_yyyy-MM-dd_HH-mm-ss.png. Bound to Ctrl+F12 by default (Keys tab).
    /// </summary>
    public static class Screenshots
    {
        public static string Folder => Path.Combine(MelonEnvironment.UserDataDirectory, "OUAKModAPI", "Screenshots");

        /// <summary>The last screenshot's path, or null.</summary>
        public static string LastPath { get; private set; }

        internal static void Init(MelonPreferences_Category keys)
        {
            Hotkeys.Register(keys, "Screenshot", "Take a screenshot (game frame, to UserData/OUAKModAPI/Screenshots)", "Ctrl+F12", Take, "OUAK API");
        }

        /// <summary>Takes a screenshot of the next frame (the mod window closes first).</summary>
        public static string Take() => Take(true);

        /// <summary>Takes a screenshot of the next frame; <paramref name="hideWindow"/> closes the mod window first.</summary>
        public static string Take(bool hideWindow)
        {
            if (hideWindow && ModMenu.IsOpen) ModMenu.Close();
            string name = "OUAK_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            FrameCapture.Request(name, Folder, path =>
            {
                LastPath = path;
                if (path != null) ModTasks.Toast("Screenshot saved: " + Path.GetFileName(path));
                else ModTasks.Toast("Screenshot failed: " + FrameCapture.Last, true);
            });
            return "Taking a screenshot...";
        }
    }
}
