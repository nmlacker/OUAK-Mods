using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

namespace OUAKModAPI
{
    /// <summary>
    /// Base class for hot mods: small mods that load from UserData/OUAKModAPI/HotMods/*.dll and reload by
    /// themselves when their DLL changes, without restarting the game. Override what you need:
    /// <code>
    /// public sealed class MyHotMod : HotMod
    /// {
    ///     public override void OnLoad() { AddTab("My hot mod", Draw); Log.Info("loaded"); }
    ///     private void Draw() { ImGui.Text("Edit me and rebuild!"); }
    /// }
    /// </code>
    /// Everything added through the helpers here (tabs, hotkeys, overlays, game modes, <see cref="Hooks"/>,
    /// anything passed to <see cref="Track"/>) is removed when the mod unloads, before the new build loads.
    /// <para>Limits: a hot mod can't register new Il2Cpp types (ClassInjector) and must not keep game objects
    /// alive across reloads; the old build's code stays in memory (unused) until the game closes.</para>
    /// </summary>
    public abstract class HotMod
    {
        /// <summary>The mod's name (its class name unless overridden).</summary>
        public virtual string Name => GetType().Name;
        public ModLog Log { get; internal set; }
        /// <summary>Harmony hooks removed when the mod unloads.</summary>
        public HookSet Hooks { get; internal set; }
        /// <summary>The DLL it came from.</summary>
        public string File { get; internal set; }

        internal readonly List<IDisposable> Owned = new();
        internal int Errors;

        public virtual void OnLoad() { }
        /// <summary>Before the mod is removed (its DLL changed or was deleted, or the game is closing).</summary>
        public virtual void OnUnload() { }
        public virtual void OnUpdate() { }
        public virtual void OnGUI() { }
        public virtual void OnSceneLoaded(string scene) { }

        /// <summary>Adds a page to the mod window (removed on unload).</summary>
        protected void AddTab(string title, Action draw, int order = 500) =>
            Owned.Add(ModMenu.AddTab($"hot:{Name}:{title}", title, draw, order, Name));

        /// <summary>Adds a Dear ImGui window of its own (<see cref="ModWindows"/>; removed on unload).</summary>
        protected ModWindow AddWindow(string id, Action draw, bool open = false, bool takesControl = true)
        {
            var w = ModWindows.Add($"hot:{Name}:{id}", draw, open, takesControl, 0, Name);
            Owned.Add(w);
            return w;
        }

        /// <summary>Adds a rebindable hotkey (removed on unload; the binding is remembered).</summary>
        protected HotkeyAction AddHotkey(string id, string label, string defaultBinding, Func<string> run)
        {
            var a = Hotkeys.Register(HotMods.KeysCategory, $"{Name}_{id}", label, defaultBinding, run, Name);
            Owned.Add(new ModMenu.Remover(() => Hotkeys.Unregister(a)));
            return a;
        }

        /// <summary>Draws over the game every frame (removed on unload).</summary>
        protected void AddOverlay(Action draw, int order = 0) => Owned.Add(ModGui.AddOverlay(draw, order, Name));

        /// <summary>Adds a game mode (removed on unload).</summary>
        protected void AddGameMode(GameMode mode)
        {
            GameModes.Register(mode);
            Owned.Add(new ModMenu.Remover(() => GameModes.Unregister(mode)));
        }

        /// <summary>Runs <paramref name="undo"/> when the mod unloads, e.g. <c>Track(() =&gt; GameEvents.StagePlaying -= OnPlaying)</c>.</summary>
        protected void Track(Action undo) => Owned.Add(new ModMenu.Remover(undo));
    }

    /// <summary>
    /// Loads hot mods (<see cref="HotMod"/>) from UserData/OUAKModAPI/HotMods and reloads one as soon as its DLL
    /// changes (build straight into that folder: set InstallDir to it, as the ExampleHotMod sample does).
    /// Each build loads into its own load context, so the same mod can load again and again; the assemblies the
    /// game already has (this API, MelonLoader, Unity and the game's interop) are shared, never loaded twice.
    /// </summary>
    public static class HotMods
    {
        /// <summary>One DLL in the folder.</summary>
        public sealed class Entry
        {
            public string File;
            public DateTime Stamp;
            public readonly List<HotMod> Mods = new();
            /// <summary>Why the last load failed, or null.</summary>
            public string Error;
            /// <summary>How many times it has been loaded.</summary>
            public int Loads;
            internal int Retries;
        }

        private sealed class Context : AssemblyLoadContext
        {
            public Context(string name) : base(name, isCollectible: false) { }

            protected override Assembly Load(AssemblyName name)
            {
                // Share what the game already has loaded: one copy of the API's (and Unity's) types for everyone.
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (a.IsDynamic || GetLoadContext(a) is Context) continue;
                    if (string.Equals(a.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase)) return a;
                }
                // A hot mod's own helper DLLs sit in HotMods/lib.
                string lib = Path.Combine(Folder, "lib", name.Name + ".dll");
                if (System.IO.File.Exists(lib)) return LoadFromStream(new MemoryStream(System.IO.File.ReadAllBytes(lib)));
                return null;
            }
        }

        public static string Folder => Path.Combine(MelonEnvironment.UserDataDirectory, "OUAKModAPI", "HotMods");

        /// <summary>Every hot mod DLL the API knows, loaded or not.</summary>
        public static IReadOnlyList<Entry> Entries => _entries;

        /// <summary>All hot mods running now.</summary>
        public static IEnumerable<HotMod> Running
        {
            get
            {
                foreach (var e in _entries)
                    foreach (var m in e.Mods) yield return m;
            }
        }

        /// <summary>Reload a hot mod as soon as its DLL changes.</summary>
        public static bool AutoReload
        {
            get => _autoPref?.Value ?? true;
            set { if (_autoPref != null) { _autoPref.Value = value; MelonPreferences.Save(); } }
        }

        /// <summary>Fired after a hot mod DLL was (re)loaded or unloaded.</summary>
        public static event Action<Entry> Changed;

        internal static MelonPreferences_Category KeysCategory { get; private set; }

        private static readonly List<Entry> _entries = new();
        private static MelonPreferences_Entry<bool> _autoPref, _enabledPref;
        private static FileSystemWatcher _watcher;
        private static volatile bool _dirty;
        private static float _dirtyAt;

        internal static void Init(MelonPreferences_Category cat)
        {
            _autoPref = cat.GetEntry<bool>("HotModsAutoReload") ?? cat.CreateEntry("HotModsAutoReload", true, "Reload hot mods when they change",
                "Watch UserData/OUAKModAPI/HotMods and reload a hot mod as soon as its DLL changes.");
            _enabledPref = cat.GetEntry<bool>("HotModsEnabled") ?? cat.CreateEntry("HotModsEnabled", true, "Load hot mods",
                "Load the DLLs in UserData/OUAKModAPI/HotMods at startup.");
            KeysCategory = ApiCore.Category("OUAKModAPI_HotKeys", "OUAK hot mod key bindings");
            Directory.CreateDirectory(Folder);
            try
            {
                _watcher = new FileSystemWatcher(Folder, "*.dll") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
                _watcher.Changed += (object sender, FileSystemEventArgs args) => _dirty = true;
                _watcher.Created += (object sender, FileSystemEventArgs args) => _dirty = true;
                _watcher.Deleted += (object sender, FileSystemEventArgs args) => _dirty = true;
                _watcher.Renamed += (object sender, RenamedEventArgs args) => _dirty = true;
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception e) { ApiCore.Log.Warn($"Can't watch the hot mod folder: {e.Message}"); }
            GameEvents.SceneLoaded += scene => ForEach(m => m.OnSceneLoaded(scene), "OnSceneLoaded");
            if (_enabledPref.Value) Sync();
        }

        /// <summary>Loads new DLLs, reloads changed ones and unloads deleted ones. Returns a summary.</summary>
        public static string Sync()
        {
            var files = Directory.Exists(Folder) ? Directory.GetFiles(Folder, "*.dll") : Array.Empty<string>();
            int loaded = 0, unloaded = 0;
            foreach (var e in _entries.Where(e => !files.Contains(e.File, StringComparer.OrdinalIgnoreCase)).ToList())
            {
                UnloadEntry(e);
                _entries.Remove(e);
                unloaded++;
            }
            foreach (string f in files)
            {
                var stamp = System.IO.File.GetLastWriteTimeUtc(f);
                var e = _entries.FirstOrDefault(x => string.Equals(x.File, f, StringComparison.OrdinalIgnoreCase));
                if (e != null && e.Stamp == stamp && e.Error == null) continue;
                if (e == null) _entries.Add(e = new Entry { File = f });
                if (LoadEntry(e, stamp)) loaded++;
            }
            return $"Hot mods: {Running.Count()} running ({loaded} (re)loaded, {unloaded} removed).";
        }

        /// <summary>Unloads and loads one DLL again (even if it didn't change).</summary>
        public static string Reload(Entry e)
        {
            if (e == null) return "Nothing to reload.";
            LoadEntry(e, System.IO.File.Exists(e.File) ? System.IO.File.GetLastWriteTimeUtc(e.File) : DateTime.MinValue);
            return e.Error == null ? $"Reloaded {Path.GetFileName(e.File)} ({e.Mods.Count} mod(s))." : $"{Path.GetFileName(e.File)}: {e.Error}";
        }

        /// <summary>Stops a DLL's mods (until it changes again or Sync runs).</summary>
        public static string Unload(Entry e)
        {
            if (e == null) return "Nothing to unload.";
            UnloadEntry(e);
            e.Error = "unloaded";
            Fire(e);
            return $"Unloaded {Path.GetFileName(e.File)}.";
        }

        internal static void UnloadAll()
        {
            foreach (var e in _entries) UnloadEntry(e);
        }

        private static bool LoadEntry(Entry e, DateTime stamp)
        {
            UnloadEntry(e);
            e.Stamp = stamp;
            e.Error = null;
            string name = Path.GetFileNameWithoutExtension(e.File);
            try
            {
                byte[] dll = System.IO.File.ReadAllBytes(e.File);
                string pdbPath = Path.ChangeExtension(e.File, ".pdb");
                byte[] pdb = System.IO.File.Exists(pdbPath) ? System.IO.File.ReadAllBytes(pdbPath) : null;
                var ctx = new Context($"HotMod {name} #{e.Loads + 1}");
                var asm = ctx.LoadFromStream(new MemoryStream(dll), pdb != null ? new MemoryStream(pdb) : null);
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException rtle) { types = rtle.Types.Where(t => t != null).ToArray(); }
                foreach (var t in types.Where(t => typeof(HotMod).IsAssignableFrom(t) && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null))
                {
                    var mod = (HotMod)Activator.CreateInstance(t);
                    mod.File = e.File;
                    mod.Log = ModLog.For(mod.Name);
                    mod.Hooks = new HookSet($"hotmod.{mod.Name}.{e.Loads + 1}");
                    try
                    {
                        mod.OnLoad();
                        e.Mods.Add(mod);
                    }
                    catch (Exception ex)
                    {
                        Cleanup(mod);
                        throw new InvalidOperationException($"{t.Name}.OnLoad failed: {ex.InnerException?.Message ?? ex.Message}", ex);
                    }
                }
                e.Loads++;
                e.Retries = 0;
                if (e.Mods.Count == 0) e.Error = "no HotMod classes in it";
                ApiCore.Log.Info($"Hot mod {Path.GetFileName(e.File)} loaded (#{e.Loads}): {string.Join(", ", e.Mods.Select(m => m.Name))}");
                Fire(e);
                return e.Error == null;
            }
            catch (Exception ex) when (ex is IOException or BadImageFormatException && e.Retries < 4)
            {
                // Still being written by the build: try again shortly.
                e.Retries++;
                e.Error = "busy, retrying";
                e.Stamp = DateTime.MinValue;
                _dirty = true;
                _dirtyAt = Time.unscaledTime + 1f;
                return false;
            }
            catch (Exception ex)
            {
                e.Error = ex.Message;
                ApiCore.Log.Warn($"Hot mod {Path.GetFileName(e.File)} didn't load: {ex}");
                Fire(e);
                return false;
            }
        }

        private static void UnloadEntry(Entry e)
        {
            foreach (var m in e.Mods)
            {
                try { m.OnUnload(); }
                catch (Exception ex) { ApiCore.Log.Warn($"{m.Name}.OnUnload failed: {ex.Message}"); }
                Cleanup(m);
                ApiCore.Log.Info($"Hot mod {m.Name} unloaded.");
            }
            e.Mods.Clear();
        }

        private static void Cleanup(HotMod m)
        {
            foreach (var d in m.Owned)
                try { d.Dispose(); } catch (Exception ex) { ApiCore.Log.Warn($"{m.Name}: cleanup failed: {ex.Message}"); }
            m.Owned.Clear();
            m.Hooks?.UnpatchAll();
        }

        private static void Fire(Entry e)
        {
            try { Changed?.Invoke(e); } catch (Exception ex) { GameEvents.Report(Changed, ex); }
        }

        internal static void Tick()
        {
            if (_dirty && (AutoReload || _dirtyAt > 0f))
            {
                float now = Time.unscaledTime;
                if (_dirtyAt <= 0f) _dirtyAt = now + 0.6f;   // let the build finish writing
                else if (now >= _dirtyAt)
                {
                    _dirty = false;
                    _dirtyAt = 0f;
                    Sync();
                }
            }
            ForEach(m => m.OnUpdate(), "OnUpdate");
        }

        internal static void OnGUI() => ForEach(m => m.OnGUI(), "OnGUI");

        private static void ForEach(Action<HotMod> call, string what)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                var mods = _entries[i].Mods;
                for (int j = 0; j < mods.Count; j++)
                {
                    var m = mods[j];
                    if (m.Errors >= 5) continue;
                    try { call(m); }
                    catch (Exception ex)
                    {
                        // Five failures and that mod's callbacks stop (until it reloads).
                        if (++m.Errors == 1 || m.Errors == 5)
                            m.Log.Warn($"{what} failed{(m.Errors == 5 ? " 5 times; its callbacks are off until it reloads" : "")}: {ex.InnerException?.Message ?? ex.Message}");
                    }
                }
            }
        }
    }
}
