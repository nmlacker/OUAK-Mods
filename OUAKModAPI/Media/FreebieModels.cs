using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppApp.KatamariSin;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OUAKModAPI
{
    /// <summary>
    /// 3D models of the freebies, the one standard way to show a freebie in the world (item boxes use it:
    /// <see cref="ItemBoxes"/>). The game has no model library for freebies: each story item box carries a model of its own
    /// freebie (<c>MainGameItemBox.ItemParent</c> → "ItemObject" → e.g. "MAGNET"), and some battle effects carry others
    /// (the washtub, crown and mushroom meshes). <see cref="Create"/> uses, in this order:
    /// <list type="number">
    /// <item>a model a mod registered (<see cref="Register"/>);</item>
    /// <item>the game's own: a story box model seen this session (kept when you leave the stage), a game mesh loaded
    /// right now (<see cref="GameMeshNames"/>), or the game's model loaded from its asset catalog on demand
    /// (<see cref="GameAddresses"/>; only its meshes and materials are copied, never the collectible object's
    /// gameplay parts);</item>
    /// <item>a card with the freebie's HUD icon, facing the camera, so every freebie can always be shown.</item>
    /// </list>
    /// Specials use their normal freebie's model (the game's Special icons differ only in their frame).
    /// </summary>
    public static class FreebieModels
    {
        /// <summary>Game meshes that show a freebie, by freebie (the first one loaded is used).</summary>
        public static readonly Dictionary<eInstageItemType, string[]> GameMeshNames = new()
        {
            [eInstageItemType.Magnet] = new[] { "MAGNET" },
            [eInstageItemType.Sonar] = new[] { "SONAR" },
            [eInstageItemType.Timer] = new[] { "Timer_Body" },
            [eInstageItemType.Tarai] = new[] { "TARAI01_C_M" },
            [eInstageItemType.Mushroom] = new[] { "MUSHROOM2" },
            [eInstageItemType.Crown] = new[] { "CROWN", "mul_crown" },
            [eInstageItemType.Shoes] = new[] { "SHOES01_B" },
            [eInstageItemType.Dice] = new[] { "DICE01_A_L" },
        };

        /// <summary>
        /// The game's own models in its asset catalog (Addressables), tried in order when the stage hasn't loaded one. The
        /// item boxes' freebie models live in the stages' scenes, but the game ships these objects as catalog prefabs
        /// (found in StreamingAssets/aa/catalog.bin, 2026-11-12).
        /// </summary>
        public static readonly Dictionary<eInstageItemType, string[]> GameAddresses = new()
        {
            [eInstageItemType.Shoes] = new[] { "SHOES01_B", "00190_SHOES01_B", "10304_SHOES01_B" },
            [eInstageItemType.Crown] = new[] { "CROWN01_C", "11060_CROWN01_C" },
            [eInstageItemType.Dice] = new[] { "DICE01_A", "10825_DICE01_A" },
            [eInstageItemType.Mushroom] = new[] { "MUSHROOM01_C", "23397_MUSHROOM01_C" },
            [eInstageItemType.Tarai] = new[] { "TARAI01_C", "11292_TARAI01_C" },
        };

        private static readonly Dictionary<eInstageItemType, (Mesh mesh, Material[] mats)> _loaded = new();
        private static readonly HashSet<eInstageItemType> _loadFailed = new();

        private static readonly Dictionary<eInstageItemType, GameObject> _registered = new();
        private static readonly Dictionary<eInstageItemType, GameObject> _harvested = new();
        private static readonly List<Transform> _cards = new();
        private static GameObject _keep;

        /// <summary>
        /// Uses <paramref name="template"/> (kept as it is; instances are copies) as the model of <paramref name="item"/>
        /// everywhere the API shows one. Null removes the registration. Dispose to undo.
        /// </summary>
        public static IDisposable Register(eInstageItemType item, GameObject template)
        {
            if (template == null) { _registered.Remove(item); return new ModMenu.Remover(() => { }); }
            _registered[item] = template;
            ItemBoxes.Refresh();
            return new ModMenu.Remover(() => { if (_registered.TryGetValue(item, out var t) && t == template) { _registered.Remove(item); ItemBoxes.Refresh(); } });
        }

        /// <summary>The normal freebie a Special shows as (SP_Magnet → Magnet; others unchanged).</summary>
        public static eInstageItemType ModelOf(eInstageItemType item)
        {
            int n = (int)item;
            return n switch { >= 16 and <= 24 => (eInstageItemType)(n - 15), 25 => eInstageItemType.Crown, 26 => eInstageItemType.Dice, _ => item };
        }

        /// <summary>Where <see cref="Create"/> would take <paramref name="item"/>'s model from right now ("registered", "game: MAGNET", "icon card").</summary>
        public static string Source(eInstageItemType item)
        {
            var m = ModelOf(item);
            if (_registered.ContainsKey(item) || _registered.ContainsKey(m)) return "registered";
            if (Harvested(m) != null) return "game: box model";
            var mesh = FindMesh(m, out _);
            if (mesh == null && _loaded.ContainsKey(m)) return "game catalog: " + _loaded[m].mesh.name;
            if (mesh != null) return "game: " + mesh.name;
            return "icon card";
        }

        /// <summary>
        /// A new model of <paramref name="item"/> under <paramref name="parent"/> (local position 0), its largest side
        /// <paramref name="size"/> world units (0 = as it comes). Null only when nothing can be shown (no icon loaded yet).
        /// </summary>
        public static GameObject Create(eInstageItemType item, Transform parent, float size = 0f, Material look = null)
        {
            GameObject go = null;
            var m = ModelOf(item);
            if (_registered.TryGetValue(item, out var reg) && reg != null || _registered.TryGetValue(m, out reg) && reg != null)
            {
                go = Object.Instantiate(reg);
                go.SetActive(true);
            }
            else if (Harvested(m) is GameObject h)
            {
                go = Object.Instantiate(h);
                go.SetActive(true);
            }
            else if ((FindMesh(m, out var mats) ?? LoadFromCatalog(m, out mats)) is Mesh mesh)
            {
                go = new GameObject($"FreebieModel_{m}");
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterials = mats;
            }
            else
            {
                // The card never takes the box's own material: the game's lit shader doesn't use the icon set on it the
                // way an unlit one does, and the card came out as a black square (the user's Sonar -> Shoes, 2026-11-10).
                go = IconCard(item, null);
                if (go == null) return null;
            }
            go.name = $"FreebieModel_{item}";
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            if (size > 0f) FitTo(go, size);
            return go;
        }

        /// <summary>Keeps a copy of a story box's own model so the freebie can be shown after leaving the stage.</summary>
        internal static void Harvest(eInstageItemType item, GameObject model)
        {
            if (model == null || Harvested(item) != null) return;
            try
            {
                if (_keep == null) { _keep = new GameObject("OUAK FreebieModels"); _keep.SetActive(false); Object.DontDestroyOnLoad(_keep); }
                var copy = Object.Instantiate(model, _keep.transform);
                copy.name = $"Harvested_{item}";
                copy.transform.localPosition = Vector3.zero;
                copy.transform.localRotation = Quaternion.identity;
                copy.transform.localScale = model.transform.lossyScale;
                _harvested[item] = copy;
            }
            catch (Exception e) { ApiCore.Log.Debug($"FreebieModels: couldn't keep {item}'s model: {e.Message}"); }
        }

        private static GameObject Harvested(eInstageItemType item) =>
            _harvested.TryGetValue(item, out var g) && g != null ? g : null;

        /// <summary>The name of the mesh the game's catalog gives <paramref name="item"/> (loads it if needed), or null.</summary>
        public static string CatalogModel(eInstageItemType item) => LoadFromCatalog(ModelOf(item), out _)?.name;

        /// <summary>The freebie's mesh and materials from the game's catalog prefab (loaded once; null if it has none).</summary>
        private static Mesh LoadFromCatalog(eInstageItemType item, out Material[] mats)
        {
            mats = null;
            if (_loaded.TryGetValue(item, out var got)) { mats = got.mats; return got.mesh; }
            if (_loadFailed.Contains(item) || !GameAddresses.TryGetValue(item, out var keys)) return null;
            foreach (var key in keys)
            {
                try
                {
                    var prefab = AssetUtil.Load<GameObject>(key);
                    if (prefab == null) continue;
                    foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>(true))
                    {
                        if (mf == null || mf.sharedMesh == null) continue;
                        var r = mf.GetComponent<MeshRenderer>();
                        if (r == null || r.sharedMaterials == null || r.sharedMaterials.Length == 0) continue;
                        _loaded[item] = (mf.sharedMesh, r.sharedMaterials);
                        mats = r.sharedMaterials;
                        ApiCore.Log.Debug($"FreebieModels: {item} from the game's catalog ({key}: {mf.sharedMesh.name})");
                        return mf.sharedMesh;
                    }
                }
                catch (Exception e) { ApiCore.Log.Debug($"FreebieModels: {key}: {e.Message}"); }
            }
            _loadFailed.Add(item);
            return null;
        }

        private static Mesh FindMesh(eInstageItemType item, out Material[] mats)
        {
            mats = null;
            if (!GameMeshNames.TryGetValue(item, out var names)) return null;
            foreach (var name in names)
            {
                foreach (var mf in Resources.FindObjectsOfTypeAll<MeshFilter>())
                {
                    try
                    {
                        if (mf == null || mf.sharedMesh == null || mf.sharedMesh.name != name) continue;
                        var r = mf.GetComponent<MeshRenderer>();
                        if (r == null || r.sharedMaterials == null || r.sharedMaterials.Length == 0) continue;
                        mats = r.sharedMaterials;
                        return mf.sharedMesh;
                    }
                    catch { }
                }
            }
            return null;
        }

        /// <summary>A camera-facing card with the freebie's HUD icon (material: a copy of <paramref name="look"/> or of any loaded unlit one).</summary>
        private static GameObject IconCard(eInstageItemType item, Material look)
        {
            var icon = GameSprites.Freebie(item);
            if (icon == null) return null;
            var go = new GameObject($"FreebieCard_{item}");
            var mesh = new Mesh { name = "FreebieCard" };
            mesh.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f) };
            mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) };
            // Both faces, so the card shows from either side.
            mesh.triangles = new[] { 0, 2, 1, 2, 3, 1, 0, 1, 2, 2, 1, 3 };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            var mat = new Material(look ?? CardMaterial() ?? new Material(Shader.Find("Universal Render Pipeline/Unlit")));
            foreach (var p in new[] { "_BaseMap", "_MainTex" }) if (mat.HasProperty(p)) mat.SetTexture(p, icon);
            mat.mainTexture = icon;
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);
            r.sharedMaterial = mat;
            _cards.Add(go.transform);
            return go;
        }

        private static Material _cardMat;
        private static Material CardMaterial()
        {
            if (_cardMat != null) return _cardMat;
            foreach (var name in new[] { "Universal Render Pipeline/Unlit", "Unlit/Transparent", "Sprites/Default", "UI/Default" })
            {
                try { var s = Shader.Find(name); if (s != null) return _cardMat = new Material(s); } catch { }
            }
            return null;
        }

        private static void FitTo(GameObject go, float size)
        {
            try
            {
                var rs = go.GetComponentsInChildren<Renderer>(true);
                if (rs.Length == 0) return;
                var b = rs[0].bounds;
                foreach (var r in rs) b.Encapsulate(r.bounds);
                float now = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
                if (now > 1e-5f) go.transform.localScale *= size / now;
            }
            catch { }
        }

        /// <summary>Turns the icon cards to the camera (the API calls this every frame).</summary>
        internal static void Tick()
        {
            if (_cards.Count == 0) return;
            var cam = Camera.main;
            for (int i = _cards.Count - 1; i >= 0; i--)
            {
                var t = _cards[i];
                if (t == null) { _cards.RemoveAt(i); continue; }
                if (cam != null && t.gameObject.activeInHierarchy) t.rotation = Quaternion.LookRotation(t.position - cam.transform.position, cam.transform.up);
            }
        }
    }
}
