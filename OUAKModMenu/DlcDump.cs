using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Il2Cpp;
using Il2CppApp.KatamariSin;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using eStage = Il2CppApp.KatamariSin.MainGameManager.eStage;
using OUAKModAPI;

namespace OUAKModMenu
{
    /// <summary>
    /// Read-only inventory of DLC content (songs, costume pieces, cousins, quests, faces) written to
    /// UserData/OUAKModMenu/dlc_items.txt. Armed by UserData/OUAKModMenu/dlcdump.flag; runs once at the title screen.
    /// </summary>
    internal static class DlcDump
    {
        private static string Dir => Path.Combine(MelonEnvironment.UserDataDirectory, "OUAKModMenu");
        private static bool _armed;
        private static float _next;

        public static void CheckFlag()
        {
            string flag = Path.Combine(Dir, "dlcdump.flag");
            if (!File.Exists(flag)) return;
            _armed = true;
            try { File.Delete(flag); } catch { }
            MelonLogger.Msg("[DlcDump] armed");
        }

        public static void Tick()
        {
            if (!_armed || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f;
            string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (GameRefs.Global == null || (scene != "Title" && scene != "SelectData")) return;
            _armed = false;
            try
            {
                string path = Path.Combine(Dir, "dlc_items.txt");
                File.WriteAllText(path, Build());
                MelonLogger.Msg($"[DlcDump] DONE: {path}");
            }
            catch (Exception e) { MelonLogger.Error($"[DlcDump] FAILED: {e}"); }
        }

        private static string Build()
        {
            var g = GameRefs.Global;
            var sb = new StringBuilder();
            void Section(string title, Action body)
            {
                sb.AppendLine();
                sb.AppendLine("== " + title);
                try { body(); } catch (Exception e) { sb.AppendLine("   (failed: " + e.Message + ")"); }
            }

            Section("DLC packs", () =>
            {
                foreach (var p in DlcLock.Packs(refresh: true))
                    sb.AppendLine($"pack {p.Id}: {p.DisplayName} | owned={p.Owned} | entries: {string.Join(", ", p.Entries.Select(e => $"{e.Name} (app {e.AppId})"))}");
            });

            Section("Songs by album (album N = DLC pack N-1; 0 = base game)", () =>
            {
                FavoriteBgmTable table = g.FavoriteBgmTable;
                if (table == null) table = Load<FavoriteBgmTable>("ScriptableObject/FavoriteBgmTable");
                var datas = table?.datas;
                if (datas == null) { sb.AppendLine("   (FavoriteBgmTable unavailable)"); return; }
                var counts = new Dictionary<int, int>();
                for (int i = 0; i < datas.Count; i++)
                {
                    var b = datas[i];
                    if (b == null) continue;
                    counts[b.albumId] = counts.TryGetValue(b.albumId, out int c) ? c + 1 : 1;
                    if (b.albumId < 1) continue;
                    // Title and source game only.
                    sb.AppendLine($"album {b.albumId} | #{b.bgmNumber} id {b.bgmId} | {English(g.BgmTable, b.localizeId)} | from: {Resolve(g, b.firstReleaseTitle)} | key {b.localizeId}");
                }
                sb.AppendLine("album sizes: " + string.Join(", ", counts.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")));
            });

            Section("Costume pieces (presents) per pack", () =>
            {
                // Same tables PresentAssetData.LoadAllDLCData reads.
                var sources = new (string label, string address)[]
                {
                    ("base", "Present/UIPresentData"),
                    ("DLC1", "ScriptableObject/Present/DLC1"),
                    ("DLC2", "ScriptableObject/Present/DLC2"),
                    ("DLC3", "ScriptableObject/Present/DLC3"),
                    ("DLC4", "ScriptableObject/Present/DLC4"),
                    ("AA", "Present/AAPresnetData"),
                    ("FreeUpdate02", "Present/FreeUpdate02"),
                };
                var byId = new Dictionary<int, UIPresentData>();
                var from = new Dictionary<int, string>();
                foreach (var (label, address) in sources)
                {
                    var ui = Load<UIPresentDataTable>(address);
                    int n = 0;
                    if (ui?._datas != null)
                        for (int i = 0; i < ui._datas.Count; i++)
                        {
                            var d = ui._datas[i];
                            if (d == null) continue;
                            n++;
                            if (!byId.ContainsKey(d._presentID)) { byId[d._presentID] = d; from[d._presentID] = label; }
                        }
                    sb.AppendLine($"(table {label} = {address}: {n} entries)");
                }
                var tables = new[]
                {
                    SelectHirobaConstants.PresentIDDLC01, SelectHirobaConstants.PresentIDDLC02, SelectHirobaConstants.PresentIDDLC03,
                    SelectHirobaConstants.PresentIDDLC04, SelectHirobaConstants.PresentIDDLC05,
                };
                for (int pack = 0; pack < tables.Length; pack++)
                {
                    var ids = tables[pack];
                    if (ids == null) continue;
                    foreach (int id in ids)
                    {
                        string name = byId.TryGetValue(id, out var d)
                            ? $"{English(g.PresentNameTable, d._textID)} | text {d._textID} | internal {d._presentName}"
                            : "(no UI entry)";
                        sb.AppendLine($"pack {pack} | present {id} | {name}");
                    }
                }
                var dlcIds = new HashSet<int>();
                foreach (var t in tables)
                    if (t != null)
                        foreach (int id in t) dlcIds.Add(id);
                sb.AppendLine("-- every present in any table (id | source table | name | internal | in a DLC pack list?)");
                foreach (var kv in byId.OrderBy(k => k.Key))
                    sb.AppendLine($"present {kv.Key} | {from[kv.Key]} | {English(g.PresentNameTable, kv.Value._textID)} | text {kv.Value._textID} | internal {kv.Value._presentName} | dlcPack={dlcIds.Contains(kv.Key)}");
                sb.AppendLine("-- ids 100-175 not in any DLC pack list");
                for (int id = 100; id <= 175; id++)
                    if (!dlcIds.Contains(id))
                        sb.AppendLine($"present {id} | {(byId.TryGetValue(id, out var d) ? $"{from[id]} | {English(g.PresentNameTable, d._textID)} | internal {d._presentName}" : "not defined in any table")}");
            });

            Section("Rolling Live cousins (Game.AAItokoGroup)", () =>
            {
                foreach (var name in Game.AAItokoGroup)
                    sb.AppendLine($"cousin {(int)name} {name} | {CousinName(g, (int)name, name.ToString())}");
                sb.AppendLine($"cousin 58 KING (other-KATAMARI bonus) | {CousinName(g, 58, "KING")}");
            });

            Section("Rolling Live quests (AA stages)", () =>
            {
                StageData data = g.AllStageData;
                foreach (eStage s in Enum.GetValues(typeof(eStage)))
                {
                    if (!s.ToString().StartsWith("Stage_AA_")) continue;
                    string extra = "";
                    if (data?.list != null && (int)s < data.list.Count)
                    {
                        var p = data.list[(int)s];
                        if (p != null)
                            extra = $" | tag {p.tagName} | result text {p.ResultTextID} -> {English(g.StageNameTable, p.ResultTextID)} | star {p.ResultStarID}";
                    }
                    sb.AppendLine($"{s}{extra}");
                    // The tag is "NNNN:<Japanese title>"; find the same Japanese text in any table to get the English.
                    string title = data?.list != null && (int)s < data.list.Count ? data.list[(int)s]?.tagName : null;
                    if (title != null && title.Contains(':')) title = title.Substring(title.IndexOf(':') + 1);
                    if (!string.IsNullOrEmpty(title))
                        foreach (var (tname, table) in TextTables(g))
                        {
                            var datas = table?.datas;
                            if (datas == null) continue;
                            for (int i = 0; i < datas.Count; i++)
                            {
                                var t = datas[i];
                                if (t?.japanese != null && (t.japanese == title || t.japanese.Contains(title) || (t.id != null && t.id.Contains(title))))
                                    sb.AppendLine($"     -> {tname}[{t.id}] = {t.english}");
                            }
                        }
                }
            });

            Section("Pre-order faces (EarlyBirdSpecialController.EarlyBirdFaces)", () =>
            {
                var faces = new List<int>();
                foreach (int f in EarlyBirdSpecialController.EarlyBirdFaces) faces.Add(f);
                sb.AppendLine("face skin ids: " + string.Join(", ", faces));
                foreach (string address in new[] { "CustomFacceTable", "ScriptableObject/CustomFacceTable", "ScriptableObject/Custom/CustomFacceTable", "Custom/CustomFacceTable", "CharaCustom/FaceV" })
                {
                    var table = Load<CustomDataTable>(address);
                    if (table?._datas == null) continue;
                    sb.AppendLine($"(face table loaded from {address}: {table._datas.Count} entries)");
                    for (int i = 0; i < table._datas.Count; i++)
                    {
                        var d = table._datas[i];
                        if (d == null) continue;
                        string label = g.CustomTable != null && d._name != null && g.CustomTable.IsExist(d._name) ? English(g.CustomTable, d._name) : d._name;
                        sb.AppendLine($"   face id {d._id} (display {d._displayIndex}) | {label}{(faces.Contains(d._id) ? "   <- pre-order bonus" : "")}");
                    }
                    break;
                }
            });

            return "OUAK DLC item inventory (read from the game's own tables)\n" + sb;
        }

        private static IEnumerable<(string, TextCommonTable)> TextTables(GlobalManager g) => new[]
        {
            ("Stage", g.StageNameTable), ("String", g.StringTable), ("System", g.SystemTable), ("Guide", g.GuideTable),
            ("Tips", g.TipsTable), ("Instage", g.InstageTable), ("Custom", g.CustomTable), ("Category", g.CategoryTable),
        };

        private static string CousinName(GlobalManager g, int id, string enumName)
        {
            var t = g.ItokoNameTable;
            if (t == null) return "?";
            foreach (string key in new[] { enumName, $"ITOKO_{id:000}", $"ITOKO_{id:00}", $"ITOKO_{id}", $"ITOKONAME_{id}" })
                if (t.IsExist(key)) return $"{English(t, key)} (key {key})";
            if (t.datas != null && id < t.datas.Count && t.datas[id] != null)
                return $"{t.datas[id].english} (by index, key {t.datas[id].id})";
            return "?";
        }

        private static string Resolve(GlobalManager g, string maybeId)
        {
            if (string.IsNullOrEmpty(maybeId)) return "-";
            foreach (var t in new[] { g.BgmTable, g.StringTable, g.SystemTable })
                if (t != null && t.IsExist(maybeId)) return English(t, maybeId);
            return maybeId;
        }

        private static string English(TextCommonTable table, string id)
        {
            if (table == null || string.IsNullOrEmpty(id)) return "?";
            try
            {
                var item = table.GetItem(id);
                if (item == null) return "?";
                return string.IsNullOrEmpty(item.english) ? item.Text : item.english;
            }
            catch { return "?"; }
        }

        private static void DumpMatching(StringBuilder sb, TextCommonTable table, Func<TextCommon, bool> match)
        {
            var datas = table?.datas;
            if (datas == null) { sb.AppendLine("   (table unavailable)"); return; }
            for (int i = 0; i < datas.Count; i++)
            {
                var t = datas[i];
                if (t?.id != null && match(t)) sb.AppendLine($"   [{i}] {t.id} = {t.english}");
            }
        }

        private static T Load<T>(string address) where T : UnityEngine.Object
        {
            try
            {
                var handle = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<UnityEngine.Object>((Il2CppSystem.String)address);
                var obj = handle.WaitForCompletion();
                return obj != null ? obj.TryCast<T>() : null;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[DlcDump] could not load {address}: {e.Message}");
                return null;
            }
        }
    }
}
