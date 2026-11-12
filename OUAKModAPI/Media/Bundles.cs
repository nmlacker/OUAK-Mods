using System;
using System.Collections.Generic;
using System.IO;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace OUAKModAPI
{
    /// <summary>
    /// Unity asset bundles (new models, textures, prefabs built with Unity 6000.0.30f1, the game's version).
    /// This game's build only keeps the asynchronous loaders, so everything here calls back when it's done
    /// (on the main thread). A bundle file is loaded once; asking again gives the same bundle.
    /// <code>Bundles.Load(path, b =&gt; Bundles.LoadAsset&lt;GameObject&gt;(b, "MyHat", prefab =&gt; Object.Instantiate(prefab)));</code>
    /// </summary>
    public static class Bundles
    {
        private sealed class Waiting
        {
            public AsyncOperation Op;
            public Action Done;
        }

        private static readonly Dictionary<string, AssetBundle> _loaded = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, List<Action<AssetBundle>>> _loading = new(StringComparer.OrdinalIgnoreCase);
        private static readonly List<Waiting> _waiting = new();

        /// <summary>Loads a bundle file; <paramref name="done"/> gets null when it couldn't.</summary>
        public static void Load(string path, Action<AssetBundle> done)
        {
            path = Path.GetFullPath(path);
            if (_loaded.TryGetValue(path, out var have) && have != null) { done?.Invoke(have); return; }
            if (_loading.TryGetValue(path, out var list)) { list.Add(done); return; }
            if (!File.Exists(path)) { ApiCore.Log.Warn($"Bundle not found: {path}"); done?.Invoke(null); return; }
            _loading[path] = new List<Action<AssetBundle>> { done };
            AssetBundleCreateRequest req;
            try { req = AssetBundle.LoadFromFileAsync(path); }
            catch (Exception e)
            {
                ApiCore.Log.Warn($"Bundle {Path.GetFileName(path)}: {e.Message}");
                Finish(path, null);
                return;
            }
            _waiting.Add(new Waiting
            {
                Op = req,
                Done = () =>
                {
                    AssetBundle b = null;
                    try { b = req.assetBundle; } catch { }
                    if (b == null) ApiCore.Log.Warn($"Bundle {Path.GetFileName(path)} didn't load (built for another Unity version?).");
                    Finish(path, b);
                },
            });
        }

        private static void Finish(string path, AssetBundle b)
        {
            if (b != null) _loaded[path] = b;
            if (!_loading.TryGetValue(path, out var list)) return;
            _loading.Remove(path);
            foreach (var cb in list)
                try { cb?.Invoke(b); } catch (Exception e) { ApiCore.Log.Warn($"Bundle callback failed: {e.Message}"); }
        }

        /// <summary>Loads one asset of type <typeparamref name="T"/> from a bundle; <paramref name="done"/> gets null when it isn't there.</summary>
        public static void LoadAsset<T>(AssetBundle bundle, string name, Action<T> done) where T : UnityEngine.Object
        {
            if (bundle == null) { done?.Invoke(null); return; }
            AssetBundleRequest req;
            try { req = bundle.LoadAssetAsync(name, Il2CppType.Of<T>()); }
            catch (Exception e)
            {
                ApiCore.Log.Warn($"Asset {name}: {e.Message}");
                done?.Invoke(null);
                return;
            }
            _waiting.Add(new Waiting
            {
                Op = req,
                Done = () =>
                {
                    T asset = null;
                    try { asset = req.asset?.TryCast<T>(); } catch { }
                    try { done?.Invoke(asset); } catch (Exception e) { ApiCore.Log.Warn($"Asset callback failed: {e.Message}"); }
                },
            });
        }

        /// <summary>Unloads a bundle (and, with <paramref name="unloadObjects"/>, everything loaded from it).</summary>
        public static void Unload(AssetBundle bundle, bool unloadObjects = false)
        {
            if (bundle == null) return;
            foreach (var kv in new List<KeyValuePair<string, AssetBundle>>(_loaded))
                if (kv.Value == bundle) _loaded.Remove(kv.Key);
            try { bundle.Unload(unloadObjects); } catch (Exception e) { ApiCore.Log.Warn($"Bundle unload: {e.Message}"); }
        }

        internal static void Tick()
        {
            for (int i = 0; i < _waiting.Count; i++)
            {
                var w = _waiting[i];
                bool done;
                try { done = w.Op == null || w.Op.isDone; } catch { done = true; }
                if (!done) continue;
                _waiting.RemoveAt(i--);
                w.Done();
            }
        }
    }
}
