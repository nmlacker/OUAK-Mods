using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MelonLoader.Utils;

namespace OUAKSongSelector
{
    /// <summary>
    /// How long one play-through of each game song lasts, read from the game's CRI cue sheets (StreamingAssets/Sound/*.acb)
    /// as plain files. The game's songs are endless loops (cue length 0xFFFFFFFF); each song's track event carries command
    /// 2001 with the play-through length in ms (BGM01: 222364). Measured: BGM01 started at 210000 ms reached that point
    /// 12.4 s later, where the game's end-of-song callback fires (the one its own shuffle uses to change songs).
    /// No CRI call is used here (CriAtomExAcb.GetCueInfo crashed the game in a test).
    /// </summary>
    public static class AcbInfo
    {
        private static Dictionary<string, long> _ends;
        public static string Report { get; private set; } = "";

        /// <summary>Play-through length of a cue in ms, or -1 if unknown (endless / block-based cues).</summary>
        public static long EndMs(string cueName)
        {
            if (_ends == null) Load();
            return cueName != null && _ends.TryGetValue(cueName, out long v) ? v : -1;
        }

        private static void Load()
        {
            _ends = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            string dir = Path.Combine(MelonEnvironment.GameRootDirectory, "OnceUponaKATAMARI_Data", "StreamingAssets", "Sound");
            int files = 0, bad = 0;
            try
            {
                foreach (string f in Directory.EnumerateFiles(dir, "BGM_*.acb", SearchOption.AllDirectories))
                {
                    try { Read(File.ReadAllBytes(f)); files++; }
                    catch (Exception e) { bad++; SongSelectorMod.Log.Warn($"Couldn't read {Path.GetFileName(f)}: {e.Message}"); }
                }
            }
            catch (Exception e) { SongSelectorMod.Log.Warn("Cue sheets: " + e.Message); }
            Report = $"{_ends.Count} song lengths from {files} cue sheet(s){(bad > 0 ? $", {bad} unreadable" : "")}";
            SongSelectorMod.Log.Info(Report);
        }

        private static void Read(byte[] b)
        {
            var top = Utf.Parse(b, 0);
            if (top.Rows.Count == 0) return;
            var h = top.Rows[0];
            var cues = Sub(b, h, "CueTable"); var names = Sub(b, h, "CueNameTable"); var seqs = Sub(b, h, "SequenceTable");
            var tracks = Sub(b, h, "TrackTable"); var events = Sub(b, h, "TrackEventTable");
            if (cues == null || names == null || seqs == null || tracks == null || events == null) return;
            var nameOf = new Dictionary<int, string>();
            foreach (var n in names.Rows) nameOf[Convert.ToInt32(n["CueIndex"])] = n["CueName"] as string;
            for (int i = 0; i < cues.Rows.Count; i++)
            {
                if (!nameOf.TryGetValue(i, out string cue) || cue == null) continue;
                var c = cues.Rows[i];
                if (Convert.ToInt32(c["ReferenceType"]) != 3) continue;          // 3 = sequence (8 = block sequence: no fixed end)
                int si = Convert.ToInt32(c["ReferenceIndex"]);
                if (si < 0 || si >= seqs.Rows.Count || !(seqs.Rows[si]["TrackIndex"] is Blob ti)) continue;
                long end = -1;
                for (int k = 0; k + 1 < ti.Size && end < 0; k += 2)
                {
                    int t = (b[ti.Offset + k] << 8) | b[ti.Offset + k + 1];
                    if (t >= tracks.Rows.Count) continue;
                    int ev = Convert.ToInt32(tracks.Rows[t]["EventIndex"]);
                    if (ev < 0 || ev >= events.Rows.Count || !(events.Rows[ev]["Command"] is Blob cmd)) continue;
                    end = Command2001(b, cmd);
                }
                if (end > 0) _ends[cue] = end;
            }
        }

        // Track event commands: big-endian u16 opcode, u8 size, data.
        private static long Command2001(byte[] b, Blob cmd)
        {
            int p = cmd.Offset, e = cmd.Offset + cmd.Size;
            while (p + 3 <= e)
            {
                int op = (b[p] << 8) | b[p + 1];
                int sz = b[p + 2];
                if (op == 2001 && sz == 4 && p + 7 <= e)
                    return ((long)b[p + 3] << 24) | ((long)b[p + 4] << 16) | ((long)b[p + 5] << 8) | b[p + 6];
                p += 3 + sz;
            }
            return -1;
        }

        private static Utf.Table Sub(byte[] b, Dictionary<string, object> row, string col) =>
            row.TryGetValue(col, out var v) && v is Blob bl && bl.Size > 0 ? Utf.Parse(b, bl.Offset) : null;

        private sealed class Blob { public int Offset, Size; }

        /// <summary>CRI @UTF tables (big-endian).</summary>
        private static class Utf
        {
            public sealed class Table { public string Name; public List<Dictionary<string, object>> Rows = new(); }

            public static Table Parse(byte[] b, int at)
            {
                if (Encoding.ASCII.GetString(b, at, 4) != "@UTF") throw new InvalidDataException("not a @UTF table");
                int t = at + 8;
                int rowsOff = U16(b, t + 2);
                int strOff = (int)U32(b, t + 4), dataOff = (int)U32(b, t + 8), nameOff = (int)U32(b, t + 12);
                int ncols = U16(b, t + 16), rowLen = U16(b, t + 18);
                int nrows = (int)U32(b, t + 20);
                string Str(long o)
                {
                    int s = t + strOff + (int)o, e = s;
                    while (e < b.Length && b[e] != 0) e++;
                    return Encoding.UTF8.GetString(b, s, e - s);
                }
                object Val(int type, int p)
                {
                    switch (type)
                    {
                        case 0: return (long)b[p];
                        case 1: return (long)(sbyte)b[p];
                        case 2: return (long)U16(b, p);
                        case 3: return (long)(short)U16(b, p);
                        case 4: return (long)U32(b, p);
                        case 5: return (long)(int)U32(b, p);
                        case 6: case 7: return (long)((U32(b, p) << 32) | U32(b, p + 4));
                        case 8: return (double)BitConverter.ToSingle(BitConverter.IsLittleEndian ? new[] { b[p + 3], b[p + 2], b[p + 1], b[p] } : new[] { b[p], b[p + 1], b[p + 2], b[p + 3] }, 0);
                        case 9: return 0.0;
                        case 0xA: return Str(U32(b, p));
                        case 0xB: return new Blob { Offset = t + dataOff + (int)U32(b, p), Size = (int)U32(b, p + 4) };
                        default: throw new InvalidDataException("column type " + type);
                    }
                }
                int Size(int type) => type switch { 0 or 1 => 1, 2 or 3 => 2, 4 or 5 or 8 or 0xA => 4, _ => 8 };
                var cols = new List<(string name, int storage, int type, object constant)>();
                int pc = t + 24;
                for (int i = 0; i < ncols; i++)
                {
                    int flags = b[pc];
                    string name = Str(U32(b, pc + 1));
                    pc += 5;
                    int storage = flags & 0xF0, type = flags & 0x0F;
                    object constant = null;
                    if (storage == 0x30 || storage == 0x70) { constant = Val(type, pc); pc += Size(type); }
                    cols.Add((name, storage, type, constant));
                }
                var table = new Table { Name = Str(nameOff) };
                for (int r = 0; r < nrows; r++)
                {
                    int p = t + rowsOff + r * rowLen;
                    var row = new Dictionary<string, object>();
                    foreach (var c in cols)
                    {
                        if (c.storage == 0x50) { row[c.name] = Val(c.type, p); p += Size(c.type); }
                        else if (c.storage == 0x30 || c.storage == 0x70) row[c.name] = c.constant;
                        else row[c.name] = null;
                    }
                    table.Rows.Add(row);
                }
                return table;
            }

            private static int U16(byte[] b, int p) => (b[p] << 8) | b[p + 1];
            private static long U32(byte[] b, int p) => ((long)b[p] << 24) | ((long)b[p + 1] << 16) | ((long)b[p + 2] << 8) | b[p + 3];
        }
    }
}
