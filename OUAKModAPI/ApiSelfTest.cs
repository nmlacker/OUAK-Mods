using System;
using System.IO;
using System.Linq;
using MelonLoader.Utils;
using UnityEngine;

namespace OUAKModAPI
{
    /// <summary>
    /// The API's own smoke test, armed by creating UserData/OUAKModAPI/apitest.flag before launch. Runs on the
    /// title screen (no save needed, nothing written to saves): PNG and WAV round trips, a prompt chosen by code,
    /// a screenshot, a live texture replacement (a file dropped into Assets/Textures swaps a texture in place),
    /// a hot mod reloading when its DLL changes (if ExampleHotMod.dll is in the HotMods folder), Steam, tabs and
    /// game modes. Logs "[ApiTest] PASS/FAIL ..." and closes the game.
    /// </summary>
    internal static class ApiSelfTest
    {
        private static readonly ModLog Log = ModLog.For("ApiTest");
        private static string FlagPath => Path.Combine(MelonEnvironment.UserDataDirectory, "OUAKModAPI", "apitest.flag");

        private static bool _armed;
        private static int _step;
        private static float _next;
        private static bool _pngOk, _wavOk, _promptOk, _shotOk, _texOk, _hotOk = true, _gameTexOk, _noRepeat;
        private static bool _patchesOff, _ok6, _updShown, _restartTest;
        private static int _updCount;
        private static string _logNote = "";
        private static Texture2D _game;
        private static int _sameNameBefore;
        private static bool _promptPicked;
        private static string _hotNote = "no ExampleHotMod.dll in the HotMods folder (skipped)", _texNote = "", _gameTexNote = "";
        private static Texture2D _probe;
        private static string _probePng, _gamePng;
        private static int _hotLoadsBefore;
        private static HotMods.Entry _hot;

        internal static void CheckFlag()
        {
            if (!File.Exists(FlagPath)) return;
            try { _restartTest = File.ReadAllText(FlagPath).Contains("restart"); } catch { }
            try { File.Delete(FlagPath); } catch { }
            _armed = true;
            Log.Info("armed");
        }

        internal static void Tick()
        {
            if (!_armed || Time.unscaledTime < _next) return;
            try { Step(); }
            catch (Exception e)
            {
                Log.Error($"step {_step} threw: {e}");
                Finish($"FAIL: step {_step} threw {e.GetType().Name}: {e.Message}");
            }
        }

        private static void Step()
        {
            float now = Time.unscaledTime;
            switch (_step)
            {
                case 0:
                {
                    // The title screen (or the save list) is up.
                    string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                    if (GameRefs.Global == null || (scene != "Title" && scene != "SelectData")) { _next = now + 1f; return; }
                    Log.Info($"scene {scene}; tabs: {string.Join(", ", ModMenu.Tabs.Select(t => $"{t.Title} ({t.Owner})"))}");
                    Log.Info($"hotkeys: {string.Join(", ", Hotkeys.Actions.Select(a => $"{a.Id}={a.Binding} ({a.Owner})"))}");
                    Log.Info($"game modes: {string.Join(", ", GameModes.All.Select(m => m.Id))}; pause hooks {PauseMenu.HooksActive}; training rules {TrainingRules.PatchActive}");
                    Log.Info($"patches: {string.Join("; ", GamePatches.All.Select(p => p.ToString()))}");
                    _patchesOff = GamePatches.All.All(p => p.Ready);
                    Log.Info(Steam.Available ? $"Steam: signed in (SteamID available, name {Steam.MyName.Length} characters)" : "Steam: not available");
                    Log.Info($"multiplayer mod: {(Multiplayer.Current != null ? "installed" : "not installed")}");

                    // PNG: encode a picture, read it back.
                    var rgba = new byte[3 * 2 * 4];
                    for (int i = 0; i < rgba.Length; i++) rgba[i] = (byte)(i * 11);
                    var img = Png.Decode(Png.EncodeRgba(rgba, 3, 2));
                    _pngOk = img.Width == 3 && img.Height == 2 && img.Rgba.SequenceEqual(rgba);
                    Log.Info($"PNG round trip {_pngOk}");

                    // WAV: 16-bit stereo to samples.
                    var wav = MakeWav(new short[] { 0, 16384, -16384, 32767 }, 2, 22050);
                    var samples = Wav.Decode(wav, out int ch, out int rate);
                    _wavOk = ch == 2 && rate == 22050 && samples.Length == 4 && Mathf.Abs(samples[1] - 0.5f) < 0.001f && Mathf.Abs(samples[2] + 0.5f) < 0.001f;
                    Log.Info($"WAV decode {_wavOk}");

                    // A prompt picked by code runs its choice on the next Update.
                    Prompt.Show("API test", "A prompt picked by the self-test.", new[]
                    {
                        new PromptOption("First", () => { }), new PromptOption("Second", () => _promptPicked = true),
                    });
                    _step = 1;
                    _next = now + 0.8f;
                    return;
                }
                case 1:
                    FrameCapture.Request("api_prompt");
                    _step = 2;
                    _next = now + 0.6f;
                    return;
                case 2:
                    Prompt.Choose(1);
                    _step = 3;
                    _next = now + 0.5f;
                    return;
                case 3:
                {
                    _promptOk = _promptPicked && !Prompt.Active;
                    Log.Info($"prompt: picked by code {_promptPicked}, closed {!Prompt.Active}; picture: {FrameCapture.Last}");
                    // A screenshot through the player-facing path.
                    ModMenu.Open();
                    ModMenu.CurrentTab = "api";
                    Screenshots.Take(false);
                    _step = 4;
                    _next = now + 1.5f;
                    return;
                }
                case 4:
                {
                    _shotOk = Screenshots.LastPath != null && File.Exists(Screenshots.LastPath);
                    Log.Info($"screenshot of the Mods tab: {Screenshots.LastPath} exists {_shotOk}");
                    if (_shotOk) { try { File.Copy(Screenshots.LastPath, Path.Combine(FrameCapture.TestShotsFolder, "api_modstab.png"), true); File.Delete(Screenshots.LastPath); } catch { } }
                    ModMenu.Close();

                    // Live texture replacement: a texture of ours, a PNG with its name dropped into Assets/Textures.
                    _probe = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = "OUAKApiTestTexture", hideFlags = HideFlags.HideAndDontSave };
                    _probe.SetPixels(Enumerable.Repeat(Color.black, 16).ToArray());
                    _probe.Apply();
                    var red = new byte[8 * 8 * 4];
                    for (int i = 0; i < red.Length; i += 4) { red[i] = 255; red[i + 3] = 255; }
                    _probePng = Path.Combine(AssetReplacer.Folder, "Textures", "OUAKApiTestTexture.png");
                    File.WriteAllBytes(_probePng, Png.EncodeRgba(red, 8, 8));

                    // And one of the game's own textures, the kind a player replaces: compressed (DXT1/DXT5), not readable,
                    // with mipmaps. The replacement is a solid colour picture of ANOTHER size (it gets scaled to fit).
                    _game = Resources.FindObjectsOfTypeAll<Texture2D>().FirstOrDefault(t => t != null && !string.IsNullOrEmpty(t.name)
                        && t.width >= 64 && t.width <= 512 && t.height >= 64 && t.height <= 512 && t.width % 4 == 0 && t.height % 4 == 0
                        && !t.isReadable && (t.format == TextureFormat.DXT1 || t.format == TextureFormat.DXT5)
                        && t.name.IndexOf("OUAK", StringComparison.Ordinal) < 0);
                    if (_game != null)
                    {
                        var magenta = new byte[48 * 40 * 4];
                        for (int i = 0; i < magenta.Length; i += 4) { magenta[i] = 255; magenta[i + 1] = 0; magenta[i + 2] = 255; magenta[i + 3] = 255; }
                        _gamePng = Path.Combine(AssetReplacer.Folder, "Textures", _game.name + ".png");
                        File.WriteAllBytes(_gamePng, Png.EncodeRgba(magenta, 48, 40));
                        _gameTexNote = $"{_game.name} ({_game.width}x{_game.height} {_game.format}, {_game.mipmapCount} mips, readable {_game.isReadable})";
                        _sameNameBefore = Resources.FindObjectsOfTypeAll<Texture2D>().Count(t => t != null && t.name == _game.name);
                    }
                    else _gameTexNote = "no compressed game texture loaded";
                    _hotLoadsBefore = AssetReplacer.TexturesReplaced;
                    _step = 5;
                    _next = now + 3f;
                    return;
                }
                case 5:
                {
                    // Ours (readable, 4x4) took the 8x8 picture: in place, scaled? No: a readable texture is rebuilt at its own size.
                    var c = (Color)_probe.GetPixels32()[_probe.width + 1];
                    _texOk = c.r > 0.9f && c.g < 0.1f && c.b < 0.1f;
                    _texNote = $"ours is {_probe.width}x{_probe.height}, colour {c}";
                    // The game's: read back from the graphics card, it should be magenta all over now.
                    if (_game != null)
                    {
                        var px = FrameCapture.ReadTexture(_game, out int gw, out int gh);
                        var mid = px[(gh / 2) * gw + gw / 2];
                        int sameName = Resources.FindObjectsOfTypeAll<Texture2D>().Count(t => t != null && t.name == _game.name);
                        bool magenta = mid.r > 230 && mid.g < 25 && mid.b > 230;
                        _gameTexOk = magenta && sameName == _sameNameBefore;
                        _gameTexNote += $": now {mid} in the middle, copies with that name {_sameNameBefore} -> {sameName}";
                    }
                    else _gameTexOk = true;
                    Log.Info($"live texture replacement: {_texNote} ({(_texOk ? "replaced" : "NOT replaced")}); game texture {_gameTexNote} ({(_gameTexOk ? "replaced" : "NOT replaced")}); {AssetReplacer.TexturesReplaced - _hotLoadsBefore} replaced in all");
                    FrameCapture.Request("api_texture");
                    // Another pass over the same textures must not touch them again (no loop, no new copies).
                    int before = AssetReplacer.TexturesReplaced;
                    AssetReplacer.ApplyTextures();
                    _noRepeat = AssetReplacer.TexturesReplaced == before;
                    Log.Info($"a second pass changed {AssetReplacer.TexturesReplaced - before} texture(s) (should be 0)");
                    try { File.Delete(_probePng); } catch { }
                    if (_gamePng != null) try { File.Delete(_gamePng); } catch { }

                    // A hot mod reloading when its DLL changes.
                    _hot = HotMods.Entries.FirstOrDefault(e => Path.GetFileName(e.File).Equals("ExampleHotMod.dll", StringComparison.OrdinalIgnoreCase));
                    if (_hot != null)
                    {
                        _hotLoadsBefore = _hot.Loads;
                        bool running = _hot.Mods.Count > 0;
                        // Write the same bytes again: a changed file to the watcher, like a rebuild.
                        var bytes = File.ReadAllBytes(_hot.File);
                        File.WriteAllBytes(_hot.File, bytes);
                        File.SetLastWriteTimeUtc(_hot.File, DateTime.UtcNow);
                        _hotNote = $"running before {running} (loaded {_hotLoadsBefore}x)";
                        _hotOk = false;
                    }
                    _step = 6;
                    _next = now + 3f;
                    return;
                }
                case 6:
                {
                    if (_hot != null)
                    {
                        _hotOk = _hot.Loads > _hotLoadsBefore && _hot.Mods.Count > 0 && _hot.Error == null;
                        _hotNote += $", after the DLL changed: loaded {_hot.Loads}x, running {string.Join(", ", _hot.Mods.Select(m => m.Name))}, tab {ModMenu.Tabs.Any(t => t.Owner == "ExampleHotMod")}";
                    }
                    Log.Info("hot mod: " + _hotNote);
                    string logNote = SessionLogTest.Run(out bool logsOk);
                    Log.Info(logNote);
                    _ok6 = _pngOk && _wavOk && _promptOk && _shotOk && _texOk && _gameTexOk && _noRepeat && _hotOk && _patchesOff && logsOk;
                    _logNote = logNote;
                    // The restart prompt after an update (Updater): forced, it comes back when closed.
                    Updater.TestRequireRestart("vTEST");
                    _step = 7;
                    _next = now + 0.5f;
                    return;
                }
                case 7:
                    _updShown = Prompt.Active && Prompt.CurrentTitle == "OUAK mods updated";
                    _updCount = Updater.PromptsShown;
                    Prompt.Close();   // like Esc
                    _step = 8;
                    _next = now + 0.5f;
                    return;
                case 8:
                {
                    bool back = Prompt.Active && Prompt.CurrentTitle == "OUAK mods updated" && Updater.PromptsShown > _updCount;
                    Updater.TestClear();
                    bool updOk = _updShown && back;
                    Log.Info($"update prompt: shown {_updShown}, came back after closing {back}");
                    bool ok = _ok6 && updOk;
                    string logNote = _logNote + $", update prompt {(updOk ? "forced" : "FAILED")}";
                    Finish(ok
                        ? $"PASS: PNG and WAV round trips, a prompt, a screenshot, live texture replacement (ours and a game texture), hot mod reload, tabs from every mod, every patch in place, {logNote}"
                        : $"FAIL: png {_pngOk}, wav {_wavOk}, prompt {_promptOk}, screenshot {_shotOk}, texture {_texOk}, game texture {_gameTexOk}, no repeat {_noRepeat}, hot mod {_hotOk}, patches in place {_patchesOff}, {logNote}");
                    return;
                }
            }
        }

        private static void Finish(string verdict)
        {
            _armed = false;
            Log.Info(verdict);
            Log.Info("DONE");
            // "restart" in the flag: end through the update prompt's Restart now (the game must come back by itself).
            ModTasks.Later(_restartTest ? Updater.Restart : Application.Quit, 1.5f);
        }

        private static byte[] MakeWav(short[] samples, int channels, int rate)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            w.Write(36 + samples.Length * 2);
            w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16);
            w.Write((short)1);
            w.Write((short)channels);
            w.Write(rate);
            w.Write(rate * channels * 2);
            w.Write((short)(channels * 2));
            w.Write((short)16);
            w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            w.Write(samples.Length * 2);
            foreach (var s in samples) w.Write(s);
            w.Flush();
            return ms.ToArray();
        }
    }
}
