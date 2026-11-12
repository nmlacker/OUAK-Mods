using System;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace OUAKModAPI
{
    /// <summary>
    /// PNG files in managed code: this game's build has no ImageConversion (EncodeToPNG / LoadImage are gone),
    /// so the API reads and writes PNGs itself. <see cref="Decode"/> reads 8-bit greyscale, grey+alpha, RGB, RGBA
    /// and palette images (with tRNS), interlaced or not; 16-bit images are read at 8 bits.
    /// </summary>
    public static class Png
    {
        /// <summary>A decoded image: RGBA bytes, rows from the TOP (like the file).</summary>
        public sealed class Image
        {
            public int Width, Height;
            public byte[] Rgba;

            /// <summary>The same pixels with rows from the BOTTOM, the order Unity's textures use (LoadRawTextureData).</summary>
            public byte[] RgbaBottomUp()
            {
                var o = new byte[Rgba.Length];
                int stride = Width * 4;
                for (int y = 0; y < Height; y++) Buffer.BlockCopy(Rgba, y * stride, o, (Height - 1 - y) * stride, stride);
                return o;
            }
        }

        /// <summary>Encodes Unity pixels (rows from the bottom, as GetPixels32 gives them) as a PNG.</summary>
        public static byte[] Encode(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Color32> px, int w, int h, bool keepAlpha = false)
        {
            var raw = new byte[(w * 4 + 1) * h];
            int o = 0;
            for (int y = h - 1; y >= 0; y--)
            {
                raw[o++] = 0;
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    var c = px[row + x];
                    raw[o++] = c.r;
                    raw[o++] = c.g;
                    raw[o++] = c.b;
                    raw[o++] = keepAlpha ? c.a : (byte)255;
                }
            }
            return EncodeRaw(raw, w, h);
        }

        /// <summary>Encodes RGBA bytes (rows from the top) as a PNG.</summary>
        public static byte[] EncodeRgba(byte[] rgba, int w, int h)
        {
            var raw = new byte[(w * 4 + 1) * h];
            for (int y = 0; y < h; y++)
            {
                raw[y * (w * 4 + 1)] = 0;
                Buffer.BlockCopy(rgba, y * w * 4, raw, y * (w * 4 + 1) + 1, w * 4);
            }
            return EncodeRaw(raw, w, h);
        }

        private static byte[] EncodeRaw(byte[] raw, int w, int h)
        {
            byte[] packed;
            using (var ms = new MemoryStream())
            {
                using (var zs = new ZLibStream(ms, System.IO.Compression.CompressionLevel.Fastest, true))
                    zs.Write(raw, 0, raw.Length);
                packed = ms.ToArray();
            }
            using var png = new MemoryStream();
            png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            var ihdr = new byte[13];
            BigEndian(ihdr, 0, w);
            BigEndian(ihdr, 4, h);
            ihdr[8] = 8;   // bits per channel
            ihdr[9] = 6;   // RGBA
            Chunk(png, "IHDR", ihdr);
            Chunk(png, "IDAT", packed);
            Chunk(png, "IEND", Array.Empty<byte>());
            return png.ToArray();
        }

        /// <summary>Reads a PNG file's bytes. Throws <see cref="InvalidDataException"/> for files it can't read.</summary>
        public static Image Decode(byte[] file)
        {
            if (file == null || file.Length < 33 || file[0] != 137 || file[1] != 80 || file[2] != 78 || file[3] != 71)
                throw new InvalidDataException("not a PNG file");
            int pos = 8, w = 0, h = 0, depth = 0, color = 0, interlace = 0;
            byte[] palette = null, trns = null;
            using var idat = new MemoryStream();
            while (pos + 8 <= file.Length)
            {
                int len = ReadBE(file, pos);
                string type = System.Text.Encoding.ASCII.GetString(file, pos + 4, 4);
                int data = pos + 8;
                if (len < 0 || data + len > file.Length) throw new InvalidDataException("truncated PNG");
                switch (type)
                {
                    case "IHDR":
                        w = ReadBE(file, data);
                        h = ReadBE(file, data + 4);
                        depth = file[data + 8];
                        color = file[data + 9];
                        interlace = file[data + 12];
                        break;
                    case "PLTE":
                        palette = new byte[len];
                        Buffer.BlockCopy(file, data, palette, 0, len);
                        break;
                    case "tRNS":
                        trns = new byte[len];
                        Buffer.BlockCopy(file, data, trns, 0, len);
                        break;
                    case "IDAT":
                        idat.Write(file, data, len);
                        break;
                }
                if (type == "IEND") break;
                pos = data + len + 4;
            }
            if (w <= 0 || h <= 0 || w > 16384 || h > 16384) throw new InvalidDataException($"bad PNG size {w}x{h}");
            int channels = color switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new InvalidDataException($"PNG colour type {color} isn't supported") };
            if (depth != 8 && depth != 16 && !(color == 3 || color == 0) ) throw new InvalidDataException($"PNG bit depth {depth} isn't supported");

            byte[] inflated;
            idat.Position = 0;
            using (var zs = new ZLibStream(idat, CompressionMode.Decompress))
            using (var outMs = new MemoryStream())
            {
                zs.CopyTo(outMs);
                inflated = outMs.ToArray();
            }

            var img = new Image { Width = w, Height = h, Rgba = new byte[w * h * 4] };
            int bitsPerPixel = channels * depth;
            if (interlace == 0)
            {
                Unfilter(inflated, 0, w, h, bitsPerPixel, out _);
                Expand(inflated, 0, w, h, depth, color, palette, trns, img, 0, 0, 1, 1);
            }
            else
            {
                // Adam7: seven passes of sub-images.
                int[] sx = { 0, 4, 0, 2, 0, 1, 0 }, sy = { 0, 0, 4, 0, 2, 0, 1 }, dx = { 8, 8, 4, 4, 2, 2, 1 }, dy = { 8, 8, 8, 4, 4, 2, 2 };
                int at = 0;
                for (int p = 0; p < 7; p++)
                {
                    int pw = (w - sx[p] + dx[p] - 1) / dx[p], ph = (h - sy[p] + dy[p] - 1) / dy[p];
                    if (pw <= 0 || ph <= 0) continue;
                    Unfilter(inflated, at, pw, ph, bitsPerPixel, out int used);
                    Expand(inflated, at, pw, ph, depth, color, palette, trns, img, sx[p], sy[p], dx[p], dy[p]);
                    at += used;
                }
            }
            return img;
        }

        private static void Unfilter(byte[] d, int start, int w, int h, int bpp, out int used)
        {
            int stride = (w * bpp + 7) / 8, step = Math.Max(1, bpp / 8);
            used = (stride + 1) * h;
            if (start + used > d.Length) throw new InvalidDataException("PNG data too short");
            for (int y = 0; y < h; y++)
            {
                int row = start + y * (stride + 1), filter = d[row], cur = row + 1, prev = cur - (stride + 1);
                for (int x = 0; x < stride; x++)
                {
                    int a = x >= step ? d[cur + x - step] : 0;
                    int b = y > 0 ? d[prev + x] : 0;
                    int c = y > 0 && x >= step ? d[prev + x - step] : 0;
                    int v = d[cur + x];
                    switch (filter)
                    {
                        case 1: v += a; break;
                        case 2: v += b; break;
                        case 3: v += (a + b) >> 1; break;
                        case 4:
                        {
                            int pp = a + b - c, pa = Math.Abs(pp - a), pb = Math.Abs(pp - b), pc = Math.Abs(pp - c);
                            v += pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
                            break;
                        }
                    }
                    d[cur + x] = (byte)v;
                }
            }
        }

        private static void Expand(byte[] d, int start, int w, int h, int depth, int color, byte[] pal, byte[] trns, Image img, int ox, int oy, int sx, int sy)
        {
            int channels = color switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
            int bpp = channels * depth, stride = (w * bpp + 7) / 8;
            for (int y = 0; y < h; y++)
            {
                int row = start + y * (stride + 1) + 1;
                for (int x = 0; x < w; x++)
                {
                    byte r, g, b, a = 255;
                    if (depth < 8)
                    {
                        int bit = x * depth, v = (d[row + bit / 8] >> (8 - depth - bit % 8)) & ((1 << depth) - 1);
                        if (color == 3)
                        {
                            r = Pal(pal, v, 0); g = Pal(pal, v, 1); b = Pal(pal, v, 2);
                            if (trns != null && v < trns.Length) a = trns[v];
                        }
                        else
                        {
                            r = g = b = (byte)(v * 255 / ((1 << depth) - 1));
                        }
                    }
                    else
                    {
                        int bytes = depth / 8, p = row + x * channels * bytes;
                        byte S(int ch) => d[p + ch * bytes];
                        switch (color)
                        {
                            case 0: r = g = b = S(0); break;
                            case 2: r = S(0); g = S(1); b = S(2); break;
                            case 3:
                                r = Pal(pal, S(0), 0); g = Pal(pal, S(0), 1); b = Pal(pal, S(0), 2);
                                if (trns != null && S(0) < trns.Length) a = trns[S(0)];
                                break;
                            case 4: r = g = b = S(0); a = S(1); break;
                            default: r = S(0); g = S(1); b = S(2); a = S(3); break;
                        }
                    }
                    int px = ox + x * sx, py = oy + y * sy;
                    int o = (py * img.Width + px) * 4;
                    img.Rgba[o] = r; img.Rgba[o + 1] = g; img.Rgba[o + 2] = b; img.Rgba[o + 3] = a;
                }
            }
        }

        private static byte Pal(byte[] pal, int i, int ch) => pal != null && i * 3 + ch < pal.Length ? pal[i * 3 + ch] : (byte)0;

        private static int ReadBE(byte[] b, int at) => (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3];

        private static void Chunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4];
            BigEndian(len, 0, data.Length);
            s.Write(len);
            var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            s.Write(typeBytes);
            s.Write(data);
            uint crc = Crc(Crc(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
            var c = new byte[4];
            BigEndian(c, 0, (int)crc);
            s.Write(c);
        }

        private static void BigEndian(byte[] b, int at, int v)
        {
            b[at] = (byte)(v >> 24);
            b[at + 1] = (byte)(v >> 16);
            b[at + 2] = (byte)(v >> 8);
            b[at + 3] = (byte)v;
        }

        private static uint[] _crcTable;

        private static uint Crc(uint crc, byte[] data)
        {
            if (_crcTable == null)
            {
                _crcTable = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    _crcTable[n] = c;
                }
            }
            foreach (byte b in data) crc = _crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}
