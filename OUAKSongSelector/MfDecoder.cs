using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

#pragma warning disable CA1416 // Windows only, like the game

namespace OUAKSongSelector
{
    /// <summary>
    /// Decodes compressed music files (MP3, M4A/AAC/ALAC, FLAC, WMA, Opus in WebM/MKA...) to PCM with Windows' own
    /// Media Foundation (mfplat.dll / mfreadwrite.dll, part of Windows 10/11): nothing to download or ship.
    /// Which file types work is whatever this Windows has a Media Foundation "byte stream handler" for (registry
    /// HKLM\SOFTWARE\Microsoft\Windows Media Foundation\ByteStreamHandlers); OGG needs Microsoft's free
    /// "Web Media Extensions". Windows "N" editions need the Media Feature Pack.
    /// COM is called through the interfaces' vtables directly (no interop assemblies). Runs on a worker thread.
    /// </summary>
    public static unsafe class MfDecoder
    {
        /// <summary>Audio file types worth listing; each is offered only if Windows has a handler for it.</summary>
        private static readonly string[] Candidates =
        {
            ".mp3", ".m4a", ".aac", ".adts", ".flac", ".wma", ".ogg", ".oga", ".opus", ".mka", ".weba", ".webm",
            ".mp4", ".mpa", ".mp2", ".ac3", ".ec3", ".amr", ".3gp", ".aif", ".aiff",
        };

        private static HashSet<string> _handled;

        /// <summary>The extensions (lower case, with the dot) Windows can decode here.</summary>
        public static IReadOnlyCollection<string> Extensions
        {
            get
            {
                if (_handled != null) return _handled;
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows Media Foundation\ByteStreamHandlers");
                    var names = key?.GetSubKeyNames() ?? Array.Empty<string>();
                    foreach (var c in Candidates) if (names.Any(n => string.Equals(n, c, StringComparison.OrdinalIgnoreCase))) set.Add(c);
                }
                catch (Exception e) { SongSelectorMod.Log.Warn("Media Foundation handlers: " + e.Message); }
                _handled = set;
                return set;
            }
        }

        /// <summary>All the candidate types (for telling people which of theirs can't be read here).</summary>
        public static IReadOnlyList<string> AllCandidates => Candidates;

        // ------------------------------------------------------------------ Windows

        [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint coInit);
        [DllImport("ole32.dll")] private static extern void CoUninitialize();
        [DllImport("mfplat.dll")] private static extern int MFStartup(uint version, uint flags);
        [DllImport("mfplat.dll")] private static extern int MFShutdown();
        [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out IntPtr type);
        [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] private static extern int MFCreateSourceReaderFromURL(string url, IntPtr attributes, out IntPtr reader);

        private const uint MF_VERSION = 0x00020070, MFSTARTUP_LITE = 1, COINIT_MULTITHREADED = 0;
        private const uint ALL_STREAMS = 0xFFFFFFFE, FIRST_AUDIO_STREAM = 0xFFFFFFFD;
        private const uint READERF_ERROR = 0x1, READERF_ENDOFSTREAM = 0x2, READERF_CURRENTMEDIATYPECHANGED = 0x20;

        private static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        private static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
        private static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        private static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
        private static readonly Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00aa00389b71");
        private static readonly Guid MFAudioFormat_PCM = new("00000001-0000-0010-8000-00aa00389b71");
        private static readonly Guid MFAudioFormat_Float = new("00000003-0000-0010-8000-00aa00389b71");

        // vtable slots (IUnknown 0-2; IMFAttributes 3-32; IMFSample 33+; IMFSourceReader 3+; IMFMediaBuffer 3+)
        private const int Release_ = 2, GetUINT32_ = 7, GetGUID_ = 10, SetGUID_ = 24;
        private const int SetStreamSelection_ = 4, GetCurrentMediaType_ = 6, SetCurrentMediaType_ = 7, ReadSample_ = 9;
        private const int ConvertToContiguousBuffer_ = 41;
        private const int Lock_ = 3, Unlock_ = 4;

        private static IntPtr Fn(IntPtr obj, int slot) => (*(IntPtr**)obj)[slot];
        private static void Release(ref IntPtr obj)
        {
            if (obj == IntPtr.Zero) return;
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Fn(obj, Release_))(obj);
            obj = IntPtr.Zero;
        }
        private static void Check(int hr, string what)
        {
            if (hr < 0) throw new IOException($"{what} failed (0x{hr:X8})");
        }

        private static int SetGuid(IntPtr attrs, Guid key, Guid value) =>
            ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)Fn(attrs, SetGUID_))(attrs, &key, &value);
        private static Guid GetGuid(IntPtr attrs, Guid key)
        {
            Guid v;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)Fn(attrs, GetGUID_))(attrs, &key, &v), "GetGUID");
            return v;
        }
        private static uint GetUInt(IntPtr attrs, Guid key, uint fallback)
        {
            uint v;
            return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint*, int>)Fn(attrs, GetUINT32_))(attrs, &key, &v) >= 0 ? v : fallback;
        }

        // ------------------------------------------------------------------ decoding

        /// <summary>Longest song decoded (memory guard: 20 min of stereo 48 kHz is about 460 MB as floats).</summary>
        public const double MaxSeconds = 20 * 60;

        /// <summary>Decodes a whole file to interleaved float samples (-1..1).</summary>
        public static float[] Decode(string path, out int channels, out int rate)
        {
            channels = 0; rate = 0;
            int co = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
            bool uninit = co >= 0;   // S_OK or S_FALSE; RPC_E_CHANGED_MODE: the thread already has COM, fine
            bool started = false;
            IntPtr reader = IntPtr.Zero, want = IntPtr.Zero, cur = IntPtr.Zero;
            try
            {
                Check(MFStartup(MF_VERSION, MFSTARTUP_LITE), "Media Foundation start");
                started = true;
                int hr = MFCreateSourceReaderFromURL(Path.GetFullPath(path), IntPtr.Zero, out reader);
                if (hr < 0) throw new IOException($"Windows can't open this file type (0x{hr:X8})" +
                    (Path.GetExtension(path).ToLowerInvariant() is ".ogg" or ".oga" or ".opus" ? ": OGG needs Microsoft's free \"Web Media Extensions\"" : ""));
                var select = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, int>)Fn(reader, SetStreamSelection_);
                select(reader, ALL_STREAMS, 0);
                Check(select(reader, FIRST_AUDIO_STREAM, 1), "no audio stream in the file;");

                // Ask the decoder for float samples, else plain PCM (FLAC/ALAC decoders give integers).
                Check(MFCreateMediaType(out want), "MFCreateMediaType");
                Check(SetGuid(want, MF_MT_MAJOR_TYPE, MFMediaType_Audio), "SetGUID");
                var setType = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint*, IntPtr, int>)Fn(reader, SetCurrentMediaType_);
                Check(SetGuid(want, MF_MT_SUBTYPE, MFAudioFormat_Float), "SetGUID");
                if (setType(reader, FIRST_AUDIO_STREAM, null, want) < 0)
                {
                    Check(SetGuid(want, MF_MT_SUBTYPE, MFAudioFormat_PCM), "SetGUID");
                    Check(setType(reader, FIRST_AUDIO_STREAM, null, want), "no PCM output for this file;");
                }

                bool isFloat = false; int bits = 16;
                void ReadFormat(out int ch, out int r)
                {
                    Release(ref cur);
                    IntPtr t;
                    Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Fn(reader, GetCurrentMediaType_))(reader, FIRST_AUDIO_STREAM, &t), "GetCurrentMediaType");
                    cur = t;
                    isFloat = GetGuid(cur, MF_MT_SUBTYPE) == MFAudioFormat_Float;
                    ch = (int)GetUInt(cur, MF_MT_AUDIO_NUM_CHANNELS, 0);
                    r = (int)GetUInt(cur, MF_MT_AUDIO_SAMPLES_PER_SECOND, 0);
                    bits = (int)GetUInt(cur, MF_MT_AUDIO_BITS_PER_SAMPLE, isFloat ? 32u : 16u);
                }
                ReadFormat(out channels, out rate);
                if (channels <= 0 || rate <= 0) throw new IOException("the decoder gave no channel count or sample rate");
                if (!isFloat && bits != 8 && bits != 16 && bits != 24 && bits != 32) throw new IOException($"{bits}-bit output isn't supported");

                long max = (long)(MaxSeconds * rate * channels);
                var outBuf = new float[Math.Min(max, (long)rate * channels * 60 * 4)];
                long n = 0;
                var readSample = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint*, uint*, long*, IntPtr*, int>)Fn(reader, ReadSample_);
                while (true)
                {
                    uint idx, flags; long ts; IntPtr sample = IntPtr.Zero;
                    Check(readSample(reader, FIRST_AUDIO_STREAM, 0, &idx, &flags, &ts, &sample), "ReadSample");
                    if ((flags & READERF_ERROR) != 0) { Release(ref sample); throw new IOException("the decoder reported an error"); }
                    if ((flags & READERF_CURRENTMEDIATYPECHANGED) != 0)
                    {
                        ReadFormat(out int ch2, out int r2);
                        if (ch2 != channels || r2 != rate) { Release(ref sample); throw new IOException("the format changes inside the file"); }
                    }
                    if (sample != IntPtr.Zero)
                    {
                        IntPtr buf = IntPtr.Zero;
                        try
                        {
                            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Fn(sample, ConvertToContiguousBuffer_))(sample, &buf), "ConvertToContiguousBuffer");
                            byte* data; uint maxLen, len;
                            Check(((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, int>)Fn(buf, Lock_))(buf, &data, &maxLen, &len), "Lock");
                            try
                            {
                                int bytesPer = isFloat ? 4 : bits / 8;
                                long count = len / bytesPer;
                                if (n + count > max) { count = Math.Max(0, max - n); flags |= READERF_ENDOFSTREAM; }
                                if (n + count > outBuf.LongLength) Array.Resize(ref outBuf, (int)Math.Min(max, Math.Max(n + count, outBuf.LongLength * 2)));
                                Convert(data, count, isFloat, bits, outBuf, n);
                                n += count;
                            }
                            finally { ((delegate* unmanaged[Stdcall]<IntPtr, int>)Fn(buf, Unlock_))(buf); }
                        }
                        finally { Release(ref buf); Release(ref sample); }
                    }
                    if ((flags & READERF_ENDOFSTREAM) != 0) break;
                }
                n -= n % channels;
                if (n <= 0) throw new IOException("no sound in the file");
                if (n != outBuf.LongLength) Array.Resize(ref outBuf, (int)n);
                return outBuf;
            }
            finally
            {
                Release(ref cur);
                Release(ref want);
                Release(ref reader);
                if (started) MFShutdown();
                if (uninit) CoUninitialize();
            }
        }

        private static void Convert(byte* p, long count, bool isFloat, int bits, float[] dst, long at)
        {
            if (count <= 0) return;
            if (isFloat) { fixed (float* d = &dst[at]) Buffer.MemoryCopy(p, d, count * 4, count * 4); return; }
            switch (bits)
            {
                case 8: for (long i = 0; i < count; i++) dst[at + i] = (p[i] - 128) / 128f; break;
                case 16: { short* s = (short*)p; for (long i = 0; i < count; i++) dst[at + i] = s[i] / 32768f; break; }
                case 24:
                    for (long i = 0; i < count; i++)
                    {
                        byte* b = p + i * 3;
                        int v = (b[0] | (b[1] << 8) | (b[2] << 16)) << 8 >> 8;
                        dst[at + i] = v / 8388608f;
                    }
                    break;
                default: { int* s = (int*)p; for (long i = 0; i < count; i++) dst[at + i] = s[i] / 2147483648f; break; }
            }
        }
    }
}
