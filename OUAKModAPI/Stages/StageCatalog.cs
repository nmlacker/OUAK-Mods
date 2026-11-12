using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppApp.KatamariSin;
using MelonLoader;
using eStage = Il2CppApp.KatamariSin.MainGameManager.eStage;
using Era = Il2Cpp.SelectHirobaEnum.Stage;

namespace OUAKModAPI
{
    public enum MapKind { Stage, Multiplayer, Plaza }

    public sealed class MapEntry
    {
        public MapKind Kind;
        public eStage Stage;
        public string PlazaKey;
        public string Name;
        public string Internal;
        public string Group;
        public int GroupOrder;
        public bool NeedsAA;
    }

    /// <summary>
    /// Every loadable map with the name the game itself shows. Stage names come from the King's mission list
    /// (SelectHiroba/MissionItemData -> StageNameTable); anything not in it is matched through the stage's
    /// Japanese tag, and only then falls back to a readable version of the internal name.
    /// </summary>
    public static class StageCatalog
    {
        private static readonly HashSet<eStage> Online = new()
        {
            eStage.Stage_ONRace1, eStage.Stage_ONTarget1, eStage.Stage_ONBattle1, eStage.Stage_ONTraining,
            eStage.Stage_OnEdo, eStage.Stage_OnGold, eStage.Stage_OnSkull, eStage.Stage_OnPyramid, eStage.Stage_OnCave,
        };

        // Shown when the game's own text can't be found (e.g. before the tables are loaded).
        private static readonly Dictionary<eStage, string> OnlineFallback = new()
        {
            [eStage.Stage_ONRace1] = "Race",
            [eStage.Stage_ONTarget1] = "Target",
            [eStage.Stage_ONBattle1] = "Battle (Jurassic)",
            [eStage.Stage_ONTraining] = "Training",
            [eStage.Stage_OnEdo] = "Edo",
            [eStage.Stage_OnGold] = "Gold Rush (Wild West)",
            [eStage.Stage_OnSkull] = "Skull Island (Sea)",
            [eStage.Stage_OnPyramid] = "Pyramid (Egypt)",
            [eStage.Stage_OnCave] = "Ice Cave",
        };

        // SelectHirobaManager.StageKeyToIndex keys, in the order the game lists them.
        private static readonly (string key, string name)[] Plazas =
        {
            ("Map_Tutorial", "Tutorial"), ("Map_Western", "Wild West"), ("Map_Edo", "Edo"),
            ("Map_GreatVoyage", "Great Voyage"), ("Map_Greece", "Ancient Greece"), ("Map_Egypt", "Ancient Egypt"),
            ("Map_Prehistory", "Stone Age"), ("Map_IceAge", "Ice Age"), ("Map_Jurassic", "Jurassic"),
            ("Map_Asia", "Asia"), ("Map_Makimono", "Picture Scroll"), ("SelectHirobaStage/Map_AA", "Super Stream (Rolling Live DLC)"),
        };

        private static readonly Era[] EraOrder =
        {
            Era.TUTORIAL, Era.EDO, Era.WESTERN, Era.JURASSIC, Era.ICEAGE, Era.STONEAGE, Era.EGYPT,
            Era.GREECS, Era.GREATVOYAGE, Era.PRESENTDAY, Era.SCROLL, Era.AA,
        };

        private static List<MapEntry> _entries;
        private static bool _complete;

        public static List<MapEntry> Entries
        {
            get
            {
                if (_entries == null || !_complete) _entries = Build();
                return _entries;
            }
        }

        public static void Invalidate() => _entries = null;

        public static string EraName(Era era) => era switch
        {
            Era.JURASSIC => "Jurassic",
            Era.ICEAGE => "Ice Age",
            Era.STONEAGE => "Stone Age",
            Era.EGYPT => "Ancient Egypt",
            Era.GREECS => "Ancient Greece",
            Era.GREATVOYAGE => "Great Voyage",
            Era.EDO => "Edo",
            Era.WESTERN => "Wild West",
            Era.PRESENTDAY => "Present Day",
            Era.TUTORIAL => "Tutorial",
            Era.SCROLL => "Picture Scroll",
            Era.AA => "Super Stream (Rolling Live DLC)",
            _ => era.ToString(),
        };

        public static string NameOf(eStage stage) =>
            Entries.FirstOrDefault(e => e.Kind != MapKind.Plaza && e.Stage == stage)?.Name ?? Pretty(stage.ToString());

        private static List<MapEntry> Build()
        {
            var g = GameRefs.Global;
            _complete = g != null && g.StageNameTable != null;
            var list = new List<MapEntry>();

            // eStage -> (name, era) from the King's mission list.
            var missions = new Dictionary<eStage, (string name, Era era)>();
            try
            {
                var table = g != null ? AssetUtil.Load<LssueListTable>("SelectHiroba/MissionItemData") : null;
                var datas = table?._datas;
                if (datas != null)
                    for (int i = 0; i < datas.Count; i++)
                    {
                        var m = datas[i];
                        if (m == null || missions.ContainsKey(m._eStageID)) continue;
                        string name = AssetUtil.Text(g.StageNameTable, m._stageName);
                        if (!string.IsNullOrWhiteSpace(name)) missions[m._eStageID] = (Clean(name), m._stageType);
                    }
                else _complete = false;
            }
            catch (Exception e) { _complete = false; MelonLogger.Warning($"Stage names: {e.Message}"); }

            StageData data = g?.AllStageData;
            if (data == null && g != null)
            {
                // MainGameManager.Start() loads this the same way and reuses it if already set.
                data = AssetUtil.Load<StageData>("StageDataAsset");
                if (data != null) g.AllStageData = data;
            }
            foreach (eStage s in Enum.GetValues(typeof(eStage)))
            {
                if (s == eStage.Stage_Proto) continue;
                string internalName = s.ToString();
                var e = new MapEntry { Stage = s, Internal = internalName, NeedsAA = internalName.StartsWith("Stage_AA_") };
                string tagName = TagName(data, s);

                if (Online.Contains(s))
                {
                    e.Kind = MapKind.Multiplayer;
                    e.Name = MatchJapanese(g, tagName) ?? OnlineFallback[s];
                    e.Group = "Multiplayer maps (offline training)";
                    e.GroupOrder = 100;
                }
                else if (missions.TryGetValue(s, out var mission))
                {
                    e.Kind = MapKind.Stage;
                    e.Name = mission.name;
                    e.Group = EraName(mission.era);
                    e.GroupOrder = Array.IndexOf(EraOrder, mission.era) is int idx && idx >= 0 ? idx : 50;
                }
                else
                {
                    e.Kind = MapKind.Stage;
                    e.Name = MatchJapanese(g, tagName) ?? (s == eStage.Stage_Tutorial ? "Tutorial" : Pretty(internalName));
                    e.Group = s == eStage.Stage_Tutorial ? EraName(Era.TUTORIAL) : "Other stages (not on the King's list)";
                    e.GroupOrder = s == eStage.Stage_Tutorial ? 0 : 90;
                }
                list.Add(e);
            }

            for (int i = 0; i < Plazas.Length; i++)
            {
                var (key, name) = Plazas[i];
                list.Add(new MapEntry
                {
                    Kind = MapKind.Plaza, PlazaKey = key, Name = name, Internal = key, Group = "Plaza maps",
                    GroupOrder = 110, NeedsAA = key.EndsWith("Map_AA"),
                });
            }

            return list.OrderBy(x => x.GroupOrder).ThenBy(x => x.Group).ToList();
        }

        private static string TagName(StageData data, eStage s)
        {
            if (data?.list == null || (int)s >= data.list.Count) return null;
            string tag = data.list[(int)s]?.tagName;
            if (string.IsNullOrEmpty(tag)) return null;
            int colon = tag.IndexOf(':');
            return colon >= 0 ? tag.Substring(colon + 1) : tag;
        }

        /// <summary>Finds the text entry whose Japanese matches the stage's tag and returns its current-language text.</summary>
        private static string MatchJapanese(GlobalManager g, string japanese)
        {
            if (g == null || string.IsNullOrEmpty(japanese)) return null;
            foreach (var table in new[] { g.StageNameTable, g.StringTable, g.SystemTable })
            {
                var datas = table?.datas;
                if (datas == null) continue;
                for (int i = 0; i < datas.Count; i++)
                {
                    var t = datas[i];
                    if (t?.japanese == japanese) return Clean(t.Text);
                }
            }
            return null;
        }

        private static string Clean(string s) => s?.Replace("\r", " ").Replace("\n", " ").Replace("  ", " ").Trim();

        public static string Pretty(string internalName)
        {
            string s = internalName.StartsWith("Stage_") ? internalName.Substring(6) : internalName;
            return s.Replace('_', ' ');
        }
    }

    public static class AssetUtil
    {
        private static readonly Dictionary<string, UnityEngine.Object> Cache = new();

        public static T Load<T>(string address) where T : UnityEngine.Object
        {
            try
            {
                if (!Cache.TryGetValue(address, out var obj) || obj == null)
                {
                    var handle = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<UnityEngine.Object>((Il2CppSystem.String)address);
                    obj = handle.WaitForCompletion();
                    Cache[address] = obj;
                }
                return obj != null ? obj.TryCast<T>() : null;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"Could not load {address}: {e.Message}");
                return null;
            }
        }

        /// <summary>Text in the game's current language, or null.</summary>
        public static string Text(TextCommonTable table, string id)
        {
            if (table == null || string.IsNullOrEmpty(id)) return null;
            try
            {
                var item = table.GetItem(id);
                if (item == null) return null;
                string t = item.Text;
                return string.IsNullOrEmpty(t) ? item.english : t;
            }
            catch { return null; }
        }
    }
}
