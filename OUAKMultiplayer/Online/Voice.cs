using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using UnityEngine;
using OUAKModAPI;

namespace OUAKMultiplayer
{
    /// <summary>
    /// IMA ADPCM, 4 bits a sample: 16 kHz speech at 64 kbit/s. Every packet starts with the coder's state, so each
    /// one decodes on its own and a lost packet is only a 40 ms gap.
    /// </summary>
    internal static class Adpcm
    {
        private static readonly int[] IndexTable = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 };

        private static readonly int[] StepTable =
        {
            7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97,
            107, 118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796,
            876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428,
            4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350,
            22385, 24623, 27086, 29794, 32767,
        };

        public struct State
        {
            public int Predictor, Index;
        }

        /// <summary>Samples (-1..1) to 3 header bytes (the state it starts from) and 4 bits a sample.</summary>
        public static byte[] Encode(float[] samples, int count, ref State st)
        {
            var o = new byte[3 + (count + 1) / 2];
            o[0] = (byte)(st.Predictor & 0xFF);
            o[1] = (byte)((st.Predictor >> 8) & 0xFF);
            o[2] = (byte)st.Index;
            int pred = st.Predictor, index = st.Index;
            for (int i = 0; i < count; i++)
            {
                int sample = Clamp16((int)(samples[i] * 32767f));
                int step = StepTable[index];
                int diff = sample - pred, code = 0;
                if (diff < 0) { code = 8; diff = -diff; }
                int vpdiff = step >> 3;
                if (diff >= step) { code |= 4; diff -= step; vpdiff += step; }
                step >>= 1;
                if (diff >= step) { code |= 2; diff -= step; vpdiff += step; }
                step >>= 1;
                if (diff >= step) { code |= 1; vpdiff += step; }
                pred = Clamp16((code & 8) != 0 ? pred - vpdiff : pred + vpdiff);
                index = Math.Clamp(index + IndexTable[code], 0, 88);
                if ((i & 1) == 0) o[3 + i / 2] = (byte)code;
                else o[3 + i / 2] |= (byte)(code << 4);
            }
            st.Predictor = pred;
            st.Index = index;
            return o;
        }

        /// <summary>Back to samples; returns how many (at most output.Length).</summary>
        public static int Decode(byte[] data, float[] output)
        {
            if (data == null || data.Length < 3) return 0;
            int pred = (short)(data[0] | (data[1] << 8));
            int index = Math.Clamp((int)data[2], 0, 88);
            int n = Math.Min((data.Length - 3) * 2, output.Length);
            for (int i = 0; i < n; i++)
            {
                int b = data[3 + i / 2];
                int code = (i & 1) == 0 ? b & 0x0F : b >> 4;
                int step = StepTable[index];
                int vpdiff = step >> 3;
                if ((code & 4) != 0) vpdiff += step;
                if ((code & 2) != 0) vpdiff += step >> 1;
                if ((code & 1) != 0) vpdiff += step >> 2;
                pred = Clamp16((code & 8) != 0 ? pred - vpdiff : pred + vpdiff);
                index = Math.Clamp(index + IndexTable[code], 0, 88);
                output[i] = pred / 32768f;
            }
            return n;
        }

        private static int Clamp16(int v) => v < -32768 ? -32768 : v > 32767 ? 32767 : v;
    }

    /// <summary>
    /// Voice chat, inside Unity. Your microphone is read with Unity's own Microphone class: the game's code left it
    /// out, but the engine (UnityPlayer.dll) still has the native microphone functions and MelonLoader rebuilds the
    /// class over them. The calls that take a device name can't be used: they all go through the rebuilt
    /// GetMicrophoneDeviceIDFromName(string), whose body refers to a Span member MelonLoader didn't generate, so .NET
    /// refuses to compile it ("Method not found: ReadOnlySpan.GetPinnableReference"). So the mod asks the engine for
    /// the device id itself (GetMicrophoneDeviceIDFromName_Injected, handed the name the way Unity's own binding code
    /// does: an empty span for Windows' default device, else the name's UTF-16 characters) and then uses the calls
    /// that take that id: GetDeviceCaps, StartRecord, GetRecordPosition, IsRecording, EndRecord. All on the main
    /// thread. Sound goes out as 40 ms
    /// IMA ADPCM packets (Msg.Voice) to the host, which passes them on; each player who talks is played through
    /// the game's own AudioSource and AudioClip (a looping one-second clip filled ahead of its play head with
    /// SetData, the play head read from AudioSource.time), so it mixes with the game's sound.
    /// Nothing leaves your PC unless you hold the talk key (or, with open mic, speak). Everything here is yours
    /// only: whether you use your microphone, the key, the volumes, who you mute.
    /// </summary>
    internal static class Voice
    {
        public const int Rate = 16000, Frame = 640;
        private const int Ring = Rate, Tail = 6400, Cushion = 1600, MaxAhead = 4800, MaxLatency = 8000;
        public const byte FlagEnd = 1;
        private const int MaxPayload = 3 + Frame / 2;

        // ---------------------------------------------------------------- settings (yours only)

        /// <summary>Hear other players. Using your microphone is a separate choice (<see cref="Talk"/>, off at first).</summary>
        public static bool Listen = true, Talk, OpenMic;
        public static int Key, Volume = 4, Gain = 1, Sensitivity = 1;
        /// <summary>The microphone by the name Unity lists; "" = Windows' default recording device.</summary>
        public static string Device = "";
        /// <summary>For trying it alone: your own voice played back to you as the others hear it (not saved).</summary>
        public static bool HearSelf;

        public static readonly KeyCode[] Keys = { KeyCode.B, KeyCode.V, KeyCode.CapsLock, KeyCode.LeftAlt, KeyCode.Mouse3, KeyCode.Mouse4 };
        public static readonly string[] KeyNames = { "B", "V", "Caps Lock", "Left Alt", "mouse back button", "mouse forward button" };
        public static readonly string[] ModeNames = { "holding a key (push to talk)", "speaking (open mic)" };
        public static readonly float[] Volumes = { 0f, 0.25f, 0.5f, 0.75f, 1f, 1.5f, 2f };
        public static readonly string[] VolumeNames = { "silent", "25%", "50%", "75%", "100%", "150%", "200%" };
        public static readonly float[] Gains = { 0.5f, 1f, 1.5f, 2f, 3f };
        public static readonly string[] GainNames = { "50%", "100%", "150%", "200%", "300%" };
        /// <summary>Open mic: the loudness (RMS) that opens it.</summary>
        private static readonly float[] Thresholds = { 0.05f, 0.025f, 0.012f };
        public static readonly string[] SensitivityNames = { "low (only loud speech)", "medium", "high (quiet speech too)" };

        /// <summary>Players you don't want to hear (this session; cleared when you leave).</summary>
        public static readonly HashSet<byte> Muted = new();

        /// <summary>Self-test: the playback volume while a test runs (null = the setting).</summary>
        public static float? TestVolume;

        private static MelonPreferences_Entry<bool> _listen, _talk, _openMic;
        private static MelonPreferences_Entry<int> _key, _volume, _gain, _sensitivity;
        private static MelonPreferences_Entry<string> _device;

        public static void Init(MelonPreferences_Category cat)
        {
            _listen = cat.CreateEntry("VoiceListen", true, "Hear other players' voices", "Voice chat in a session.");
            _talk = cat.CreateEntry("VoiceTalk", false, "Use my microphone for voice chat", "Off at first. Nothing is sent unless you hold the talk key (or speak, with open mic).");
            _openMic = cat.CreateEntry("VoiceOpenMic", false, "Open mic", "Send whenever you speak instead of while a key is held.");
            _key = cat.CreateEntry("VoiceKey", 0, "Push-to-talk key", "0 B, 1 V, 2 Caps Lock, 3 Left Alt, 4 mouse back, 5 mouse forward.");
            _volume = cat.CreateEntry("VoiceVolume", 4, "Voice volume", "0 silent, 1-4 25%-100%, 5 150%, 6 200%.");
            _gain = cat.CreateEntry("VoiceMicGain", 1, "Microphone volume", "0 50%, 1 100%, 2 150%, 3 200%, 4 300%.");
            _sensitivity = cat.CreateEntry("VoiceSensitivity", 1, "Open mic sensitivity", "0 low, 1 medium, 2 high.");
            _device = cat.CreateEntry("VoiceDevice", "", "Microphone", "The name Unity lists for it; empty = Windows' default recording device.");
            Listen = _listen.Value;
            Talk = _talk.Value;
            OpenMic = _openMic.Value;
            Key = Clamp(_key.Value, Keys.Length);
            Volume = Clamp(_volume.Value, Volumes.Length, 4);
            Gain = Clamp(_gain.Value, Gains.Length, 1);
            Sensitivity = Clamp(_sensitivity.Value, Thresholds.Length, 1);
            Device = _device.Value ?? "";
        }

        public static void Save()
        {
            if (_listen == null) return;
            _listen.Value = Listen;
            _talk.Value = Talk;
            _openMic.Value = OpenMic;
            _key.Value = Key;
            _volume.Value = Volume;
            _gain.Value = Gain;
            _sensitivity.Value = Sensitivity;
            _device.Value = Device ?? "";
            MelonPreferences.Save();
        }

        public static int Hash() =>
            ((Listen ? 1 : 0) | (Talk ? 2 : 0) | (OpenMic ? 4 : 0) | (Key << 3) | (Volume << 6) | (Gain << 9) | (Sensitivity << 12))
            ^ ((Device ?? "").GetHashCode() << 15);

        private static int Clamp(int v, int count, int fallback = 0) => v < 0 || v >= count ? fallback : v;

        /// <summary>The menu's one-line summary.</summary>
        public static string Summary()
        {
            string hear = Listen ? $"hearing others at {VolumeNames[Volume]}" : "not hearing others";
            if (!Talk) return $"Voice chat: {hear}; your microphone is off";
            return $"Voice chat: {hear}; you talk by {(OpenMic ? "speaking (open mic)" : "holding " + KeyNames[Key])}" +
                   (MicOn ? "" : $" ({MicStatus})");
        }

        // ---------------------------------------------------------------- your microphone

        private static AudioClip _mic;
        private static int _micId = -1, _micRate, _micLen, _micChannels, _read = -1, _chunk;
        private static Il2CppStructArray<float> _micBuf;
        private static float[] _mono = new float[1], _frame = new float[Frame];
        private static readonly List<float> Pending = new();
        private static double _rsPos;
        private static float _rsLast, _nextMicTry, _gateUntil, _level, _peakHold;
        private static Adpcm.State _enc;
        private static ushort _seq;
        private static bool _sending, _micFailedLogged;
        private static string[] _devices = Array.Empty<string>();
        private static float _devicesAt = -100f;

        public static bool MicOn => _mic != null;
        public static string MicStatus { get; private set; } = "off";
        /// <summary>Your microphone's level (0..1, with a short hold), for the menu's meter.</summary>
        public static float Level => _level;
        /// <summary>True while your voice is going out.</summary>
        public static bool Transmitting => _sending;
        public static int FramesSent { get; private set; }

        /// <summary>Microphones Unity can see (cached for a few seconds).</summary>
        public static string[] Devices(bool refresh = false)
        {
            float now = Time.unscaledTime;
            if (!refresh && now - _devicesAt < 5f) return _devices;
            _devicesAt = now;
            try
            {
                var list = Microphone.devices;
                _devices = list == null ? Array.Empty<string>() : Enumerable.Range(0, list.Length).Select(i => (string)list[i] ?? "").Where(s => s.Length > 0).ToArray();
            }
            catch (Exception e)
            {
                _devices = Array.Empty<string>();
                MicStatus = "couldn't list microphones: " + e.Message;
            }
            return _devices;
        }

        private static string _micName;

        /// <summary>The microphone in use: its name, or null for Windows' default (or when it's off).</summary>
        public static string MicName => _mic != null ? _micName : null;

        /// <summary>Unity's ManagedSpanWrapper: where a string's UTF-16 characters are and how many (begin null = no string).</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct NameSpan
        {
            public IntPtr Begin;
            public int Length;
        }

        private delegate int IdFromNameFn(IntPtr name);

        private static IdFromNameFn _idFromName;

        /// <summary>
        /// Unity's id for a microphone, -1 if there's none by that name: the engine's own lookup (the icall the rebuilt
        /// GetMicrophoneDeviceIDFromName_Injected wraps, called the same way), handed the name the way Unity's binding
        /// code does: no string (an empty span) = the default device, else the name's characters, pinned for the call.
        /// </summary>
        private static unsafe int DeviceId(string name)
        {
            _idFromName ??= IL2CPP.ResolveICall<IdFromNameFn>("UnityEngine.Microphone::GetMicrophoneDeviceIDFromName_Injected");
            var span = new NameSpan();
            if (string.IsNullOrEmpty(name)) return _idFromName((IntPtr)(&span));
            fixed (char* chars = name)
            {
                span.Begin = (IntPtr)chars;
                span.Length = name.Length;
                return _idFromName((IntPtr)(&span));
            }
        }

        private static void StartMic(float now)
        {
            _nextMicTry = now + 5f;
            try
            {
                var devices = Devices(true);
                if (devices.Length == 0) { MicStatus = "no microphone found"; return; }
                // The one you picked if Unity still lists it, else Windows' default.
                string name = Device.Length > 0 && devices.Contains(Device) ? Device : null;
                int id = DeviceId(name);
                if (id < 0) { MicStatus = name == null ? "no default microphone" : "that microphone isn't there"; return; }
                Microphone.GetDeviceCaps(id, out int min, out int max);
                int rate = min == 0 && max == 0 ? Rate : Mathf.Clamp(Rate, min, max);
                var clip = Microphone.StartRecord(id, true, 1f, rate);
                if (clip == null) { MicStatus = "the microphone didn't start"; return; }
                // Nothing in a scene uses it, so a scene change's unload would destroy it and the mic would restart every load.
                clip.hideFlags = HideFlags.HideAndDontSave;
                _mic = clip;
                _micId = id;
                _micName = name;
                _micRate = Mathf.Max(1, clip.frequency);
                _micLen = Mathf.Max(1, clip.samples);
                _micChannels = Mathf.Max(1, clip.channels);
                _chunk = Mathf.Max(1, _micRate / 50);
                _micBuf = new Il2CppStructArray<float>(_chunk * _micChannels);
                if (_mono.Length < _chunk) _mono = new float[_chunk];
                _read = -1;
                _rsPos = 0;
                _rsLast = 0f;
                MicStatus = $"on: {name ?? "Windows' default microphone"} ({_micRate} Hz)";
                MelonLogger.Msg($"[Voice] Microphone on: {(name == null ? "Windows' default recording device" : "the one picked")} (id {id}), {_micRate} Hz, {_micChannels} channel(s), {devices.Length} listed.");
            }
            catch (Exception e)
            {
                _mic = null;
                MicStatus = "the microphone didn't start: " + e.Message;
                if (!_micFailedLogged) { _micFailedLogged = true; MelonLogger.Warning("[Voice] " + MicStatus); }
            }
        }

        /// <summary>Another microphone was picked: the current one closes and the new one opens next frame.</summary>
        public static void RestartMic()
        {
            if (_mic != null) StopMic();
            _nextMicTry = 0f;
            _micFailedLogged = false;
        }

        private static void StopMic()
        {
            if (_mic == null) return;
            EndTalk();
            try { if (_micId >= 0) Microphone.EndRecord(_micId); } catch { }
            _mic = null;
            _micId = -1;
            _level = 0f;
            MicStatus = "off";
            MelonLogger.Msg("[Voice] Microphone off.");
        }

        /// <summary>Reads what the microphone recorded since last frame, 20 ms at a time.</summary>
        private static void Capture(float now)
        {
            if (!Microphone.IsRecording(_micId))
            {
                StopMic();
                MicStatus = "the microphone stopped; trying again";
                _nextMicTry = now + 3f;
                return;
            }
            int pos = Microphone.GetRecordPosition(_micId);
            if (_read < 0) { _read = pos; return; }
            int avail = (pos - _read + _micLen) % _micLen;
            // Fallen far behind (a long frame): skip to the newest audio.
            if (avail > _micLen / 2) { _read = (pos - _chunk + _micLen) % _micLen; avail = _chunk; }
            bool key = PushToTalkHeld();
            while (avail >= _chunk)
            {
                _mic.GetData(_micBuf, _read);
                _read = (_read + _chunk) % _micLen;
                avail -= _chunk;
                for (int i = 0; i < _chunk; i++)
                {
                    float v = 0f;
                    for (int c = 0; c < _micChannels; c++) v += _micBuf[i * _micChannels + c];
                    _mono[i] = v / _micChannels;
                }
                Process(_mono, _chunk, _micRate, key, now);
            }
            _peakHold = Mathf.Max(0f, _peakHold - Time.unscaledDeltaTime);
        }

        private static bool PushToTalkHeld()
        {
            if (TextInput.IsTyping || Chat.IsOpen) return false;
            try { return Input.GetKey(Keys[Key]); }
            catch { return false; }
        }

        /// <summary>One chunk of sound (any rate): gain, level, the talk gate, then 16 kHz frames out.</summary>
        private static void Process(float[] chunk, int n, int rate, bool key, float now)
        {
            float gain = Gains[Gain];
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                float v = chunk[i] * gain;
                chunk[i] = v;
                sum += v * v;
            }
            float rms = (float)Math.Sqrt(sum / Math.Max(1, n));
            float shown = Mathf.Clamp01(rms * 6f);
            if (shown >= _level) { _level = shown; _peakHold = 0.3f; }
            else if (_peakHold <= 0f) _level = Mathf.Max(shown, _level * 0.9f);

            bool gate;
            if (OpenMic)
            {
                if (rms >= Thresholds[Sensitivity]) _gateUntil = now + 0.45f;
                gate = now < _gateUntil && !TextInput.IsTyping;
            }
            else gate = key;

            if (!gate)
            {
                if (_sending) EndTalk();
                return;
            }
            if (!_sending)
            {
                _sending = true;
                _enc = default;
                Pending.Clear();
                if (!NetSession.Connected) return;
            }
            Resample(chunk, n, rate);
            while (Pending.Count >= Frame) SendFrame(Frame, false);
        }

        /// <summary>To 16 kHz: whole multiples are averaged (a simple low-pass), anything else is interpolated.</summary>
        private static void Resample(float[] src, int n, int rate)
        {
            if (rate == Rate)
            {
                for (int i = 0; i < n; i++) Pending.Add(src[i]);
                return;
            }
            if (rate % Rate == 0)
            {
                int k = rate / Rate;
                for (int i = 0; i + k <= n; i += k)
                {
                    float s = 0f;
                    for (int j = 0; j < k; j++) s += src[i + j];
                    Pending.Add(s / k);
                }
                return;
            }
            double step = (double)rate / Rate, p = _rsPos;
            while (p < n - 1)
            {
                float a, b;
                double f;
                if (p < 0) { a = _rsLast; b = src[0]; f = p + 1; }
                else { int i = (int)p; a = src[i]; b = src[i + 1]; f = p - i; }
                Pending.Add((float)(a + (b - a) * f));
                p += step;
            }
            _rsPos = p - n;
            _rsLast = src[n - 1];
        }

        private static void SendFrame(int n, bool end)
        {
            n = Math.Min(n, Pending.Count);
            for (int i = 0; i < n; i++) _frame[i] = Pending[i];
            Pending.RemoveRange(0, n);
            byte[] data = n > 0 ? Adpcm.Encode(_frame, n, ref _enc) : Array.Empty<byte>();
            _seq++;
            Send(_seq, end, data);
        }

        private static void Send(ushort seq, bool end, byte[] data)
        {
            if (!NetSession.Connected) return;
            var w = Proto.Begin(Msg.Voice, NetSession.MyId).U16(seq).U8(end ? FlagEnd : (byte)0).Bytes(data);
            if (NetSession.Mode == NetSession.Role.Host) NetSession.Broadcast(w);
            else NetSession.SendToHost(w);
            FramesSent++;
            if (HearSelf) Heard(NetSession.MyId, seq, end, data, Time.unscaledTime);
        }

        /// <summary>Talking stopped: what's left goes out, marked as the end.</summary>
        private static void EndTalk()
        {
            if (!_sending) return;
            _sending = false;
            SendFrame(Pending.Count, true);
        }

        // ---------------------------------------------------------------- per frame

        public static void Tick()
        {
            if (_broken) return;
            try
            {
                float now = Time.unscaledTime;
                bool micWanted = Talk && NetSession.Connected;
                if (!micWanted && _mic != null) StopMic();
                if (micWanted && _mic == null && now >= _nextMicTry) StartMic(now);
                if (_mic != null) Capture(now);
                TickTest(now);
                TickPlayers(now);
            }
            catch (Exception e)
            {
                _broken = true;
                MelonLogger.Warning($"[Voice] Voice chat turned off: {e.Message}");
                try { StopMic(); } catch { }
                Reset();
            }
        }

        private static bool _broken;

        /// <summary>Left the session: microphone off, every player's sound gone, mutes forgotten.</summary>
        public static void Reset()
        {
            try { StopMic(); } catch { }
            foreach (var p in Players.Values) p.Destroy();
            Players.Clear();
            Muted.Clear();
            Rates.Clear();
            _test = null;
            HearSelf = false;
        }

        // ---------------------------------------------------------------- the network

        private static readonly Dictionary<byte, (float since, int count)> Rates = new();

        /// <summary>Host: a player's voice packet. False when it's malformed or too fast (not passed on then).</summary>
        public static bool HostOnVoice(Peer from, PacketReader r)
        {
            ushort seq = r.U16();
            bool end = (r.U8() & FlagEnd) != 0;
            var data = r.Bytes();
            if (data.Length > MaxPayload) return false;
            float now = Time.unscaledTime;
            var (since, count) = Rates.TryGetValue(from.Id, out var rc) ? rc : (now, 0);
            if (now - since > 1f) { since = now; count = 0; }
            if (++count > 40) { Rates[from.Id] = (since, count); return false; }
            Rates[from.Id] = (since, count);
            Heard(from.Id, seq, end, data, now);
            return true;
        }

        /// <summary>Client: a voice packet the host passed on (or the host's own).</summary>
        public static void ClientOnVoice(byte speaker, PacketReader r)
        {
            ushort seq = r.U16();
            bool end = (r.U8() & FlagEnd) != 0;
            var data = r.Bytes();
            if (speaker == NetSession.MyId || speaker >= NetSession.MaxPlayers || data.Length > MaxPayload) return;
            Heard(speaker, seq, end, data, Time.unscaledTime);
        }

        // ---------------------------------------------------------------- playing others

        private sealed class Player
        {
            public byte Id;
            public GameObject Go;
            public AudioSource Source;
            public AudioClip Clip;
            public Il2CppStructArray<float> Buf;
            public readonly Queue<float[]> Frames = new();
            public ushort LastSeq;
            public bool HasSeq, Playing, Ended;
            public long WriteAbs, PlayAbs;
            public int LastPos;
            public float LastHeard, Loud;
            public int Received, Written, Dropped, Lost, Starts, Underruns;
            public long Played;

            public void Destroy()
            {
                try { if (Source != null) Source.Stop(); } catch { }
                try { if (Go != null) UnityEngine.Object.Destroy(Go); } catch { }
                Go = null;
                Source = null;
                Clip = null;
            }
        }

        private static readonly Dictionary<byte, Player> Players = new();
        private static Il2CppStructArray<float> _zeros;

        private static void Heard(byte id, ushort seq, bool end, byte[] data, float now)
        {
            if (!Listen || Muted.Contains(id)) return;
            var p = PlayerFor(id);
            if (p == null) return;
            p.Received++;
            if (p.HasSeq && now - p.LastHeard < 1f)
            {
                short d = (short)(seq - p.LastSeq);
                if (d <= 0) { p.Dropped++; return; }
                // A packet or two lost: that much silence, so what follows keeps its timing.
                if (d > 1 && d <= 4 && p.Playing)
                    for (int i = 1; i < d; i++) { p.Frames.Enqueue(new float[Frame]); p.Lost++; }
            }
            p.HasSeq = true;
            p.LastSeq = seq;
            p.LastHeard = now;
            p.Ended = end;
            if (data.Length > 3)
            {
                var samples = new float[(data.Length - 3) * 2];
                int n = Adpcm.Decode(data, samples);
                if (n < samples.Length) Array.Resize(ref samples, n);
                p.Frames.Enqueue(samples);
            }
        }

        private static Player PlayerFor(byte id)
        {
            if (Players.TryGetValue(id, out var p) && p.Source != null) return p;
            p?.Destroy();
            try
            {
                var go = new GameObject($"OUAK voice {id}") { hideFlags = HideFlags.HideAndDontSave };
                UnityEngine.Object.DontDestroyOnLoad(go);
                var clip = AudioClip.Create($"OUAK voice {id}", Ring, 1, Rate, false);
                clip.hideFlags = HideFlags.HideAndDontSave;
                var src = go.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.clip = clip;
                src.loop = true;
                src.spatialBlend = 0f;
                src.panStereo = 0f;
                src.pitch = 1f;
                src.volume = PlaybackVolume();
                p = new Player { Id = id, Go = go, Source = src, Clip = clip, Buf = new Il2CppStructArray<float>(Frame + Tail) };
                Players[id] = p;
                if (_playerLogged++ < 4) MelonLogger.Msg($"[Voice] Playing {Votes.NameOf(id)}'s voice through a Unity AudioSource ({Rate} Hz, {Ring / (float)Rate:0.#} s ring).");
                return p;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[Voice] Couldn't set up playback: {e.Message}");
                return null;
            }
        }

        private static int _playerLogged;

        private static float PlaybackVolume() => Mathf.Clamp01(TestVolume ?? Volumes[Volume]);

        private static float Boost() => TestVolume.HasValue ? 1f : Mathf.Max(1f, Volumes[Volume]);

        private static void TickPlayers(float now)
        {
            if (Players.Count == 0) return;
            float vol = PlaybackVolume();
            foreach (var p in Players.Values)
            {
                if (p.Source == null) continue;
                if (!Listen || Muted.Contains(p.Id))
                {
                    if (p.Playing) Stop(p);
                    p.Frames.Clear();
                    continue;
                }
                if (Math.Abs(p.Source.volume - vol) > 0.001f) p.Source.volume = vol;
                if (!p.Playing)
                {
                    // A little in hand before starting (80 ms), so the network's unevenness doesn't cut it up.
                    if (p.Frames.Count >= 2 || (p.Frames.Count > 0 && (p.Ended || now - p.LastHeard > 0.1f))) Begin(p);
                    else continue;
                }
                int pos = Mathf.Clamp((int)(p.Source.time * Rate), 0, Ring - 1);
                int delta = (pos - p.LastPos + Ring) % Ring;
                p.LastPos = pos;
                p.PlayAbs += delta;
                p.Played += delta;
                long lead = p.WriteAbs - p.PlayAbs;
                if (lead < 0)
                {
                    // Played everything sent: the play head is in the silence written after it.
                    if (p.Frames.Count == 0)
                    {
                        if (lead < -2400) Stop(p);
                        continue;
                    }
                    p.Underruns++;
                    p.WriteAbs = p.PlayAbs + Cushion;
                    lead = Cushion;
                }
                // Too far behind (a long frame, or a burst): drop the oldest so it stays live.
                while (p.Frames.Count > 0 && lead + (long)p.Frames.Count * Frame > MaxLatency) { p.Frames.Dequeue(); p.Dropped++; }
                while (p.Frames.Count > 0 && lead < MaxAhead)
                {
                    var f = p.Frames.Dequeue();
                    Write(p, f);
                    lead += f.Length;
                }
            }
        }

        private static void Begin(Player p)
        {
            _zeros ??= new Il2CppStructArray<float>(Ring);
            p.Clip.SetData(_zeros, 0);
            p.Source.Play();
            p.LastPos = Mathf.Clamp((int)(p.Source.time * Rate), 0, Ring - 1);
            p.PlayAbs = p.LastPos;
            p.WriteAbs = p.PlayAbs + Cushion;
            p.Playing = true;
            p.Starts++;
        }

        private static void Stop(Player p)
        {
            p.Playing = false;
            p.Loud = 0f;
            try { p.Source.Stop(); } catch { }
        }

        /// <summary>One frame into the clip at the write head, then silence after it (so a stop never replays old sound).</summary>
        private static void Write(Player p, float[] f)
        {
            float boost = Boost();
            double sum = 0;
            for (int i = 0; i < Frame; i++)
            {
                float v = i < f.Length ? f[i] * boost : 0f;
                if (v > 1f) v = 1f;
                else if (v < -1f) v = -1f;
                p.Buf[i] = v;
                sum += v * v;
            }
            p.Clip.SetData(p.Buf, (int)(p.WriteAbs % Ring));
            p.WriteAbs += f.Length;
            p.Written++;
            p.Loud = (float)Math.Sqrt(sum / Frame);
        }

        /// <summary>A player's voice is being heard right now (for the player bar and name tags).</summary>
        public static bool IsTalking(byte id)
        {
            if (id == NetSession.MyId) return _sending || (_test != null && _test.Mine);
            return Players.TryGetValue(id, out var p) && p.Playing && p.WriteAbs > p.PlayAbs && Time.unscaledTime - p.LastHeard < 0.6f;
        }

        // ---------------------------------------------------------------- a test voice (no microphone)

        private sealed class TestTalkState
        {
            public bool Mine;
            public float Start, Seconds;
            public int Sent;
            public Adpcm.State Enc;
            public double Phase;
        }

        private static TestTalkState _test;

        /// <summary>
        /// Self-test: "talk" for a few seconds with a made-up voice (a hum with syllables), through the same
        /// encoder, packets and host relay as a real microphone, without opening one.
        /// </summary>
        public static string TestTalk(float seconds)
        {
            if (!NetSession.Connected) return "not in a session";
            _test = new TestTalkState { Mine = true, Start = Time.unscaledTime, Seconds = Mathf.Clamp(seconds, 0.2f, 10f) };
            return $"talking with a test voice for {_test.Seconds:0.#} s (no microphone)";
        }

        private static void TickTest(float now)
        {
            var t = _test;
            if (t == null) return;
            int total = (int)(t.Seconds * Rate / Frame);
            int due = Math.Min(total, (int)((now - t.Start) * Rate / Frame) + 1);
            while (t.Sent < due)
            {
                var samples = new float[Frame];
                TestSignal(samples, t.Sent * Frame, ref t.Phase);
                var data = Adpcm.Encode(samples, Frame, ref t.Enc);
                t.Sent++;
                _seq++;
                Send(_seq, t.Sent >= total, data);
            }
            if (t.Sent >= total) _test = null;
        }

        /// <summary>A voice-like test sound: a 150-190 Hz hum with harmonics, in syllables about 4 a second.</summary>
        public static void TestSignal(float[] buf, int startSample, ref double phase)
        {
            for (int i = 0; i < buf.Length; i++)
            {
                double t = (startSample + i) / (double)Rate;
                double f0 = 170.0 + 20.0 * Math.Sin(2.0 * Math.PI * 0.7 * t);
                phase += 2.0 * Math.PI * f0 / Rate;
                double env = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * 3.7 * t));
                double v = Math.Sin(phase) + 0.6 * Math.Sin(2 * phase) + 0.45 * Math.Sin(3 * phase) + 0.3 * Math.Sin(4 * phase)
                           + 0.2 * Math.Sin(5 * phase) + 0.12 * Math.Sin(6 * phase);
                buf[i] = (float)(0.22 * env * v);
            }
        }

        // ---------------------------------------------------------------- checks and reports

        /// <summary>Self-test: the codec on its own: a test voice encoded and decoded, as signal-to-noise in dB.</summary>
        public static string CodecCheck(out bool ok)
        {
            const int n = Rate * 2;
            var src = new float[n];
            double ph = 0;
            TestSignal(src, 0, ref ph);
            var st = new Adpcm.State();
            double sig = 0, err = 0;
            var outb = new float[Frame];
            for (int at = 0; at + Frame <= n; at += Frame)
            {
                var block = new float[Frame];
                Array.Copy(src, at, block, 0, Frame);
                var data = Adpcm.Encode(block, Frame, ref st);
                int m = Adpcm.Decode(data, outb);
                for (int i = 0; i < m; i++)
                {
                    sig += block[i] * (double)block[i];
                    double e = block[i] - outb[i];
                    err += e * e;
                }
            }
            double snr = 10.0 * Math.Log10(sig / Math.Max(1e-12, err));
            ok = snr > 20.0;
            return $"ADPCM codec: {snr:0.0} dB signal-to-noise on a test voice ({3 + Frame / 2} bytes per 40 ms)";
        }

        /// <summary>
        /// What Unity's sound side has for voice chat, without recording: the microphones it lists, their ids from
        /// the engine's lookup (default, by name, and a name that isn't there), the default one's sample rates, and
        /// whether there's an AudioListener to hear through.
        /// </summary>
        public static string Probe(out bool ok)
        {
            var sb = new StringBuilder();
            ok = true;
            try
            {
                var d = Devices(true);
                sb.Append($"{d.Length} microphone(s) listed by Unity");
                if (d.Length > 0)
                {
                    // The same lookups and calls as a real start, short of recording.
                    int def = DeviceId(null), first = DeviceId(d[0]), last = DeviceId(d[d.Length - 1]), none = DeviceId("OUAK no such microphone");
                    sb.Append($"; ids: default {def}, first {first}, last {last}, a made-up name {none}");
                    if (def >= 0)
                    {
                        Microphone.GetDeviceCaps(def, out int min, out int max);
                        sb.Append(min == 0 && max == 0 ? ", the default takes any sample rate" : $", the default takes {min}-{max} Hz");
                        sb.Append($", recording {Microphone.IsRecording(def)}");
                    }
                    ok = def >= 0 && first >= 0 && last >= 0 && none == -1;
                }
                sb.Append($"; anything recording: {Microphone.isAnyDeviceRecording}");
            }
            catch (Exception e)
            {
                ok = false;
                sb.Append("the microphone list failed: " + e.Message);
            }
            try
            {
                var l = UnityEngine.Object.FindAnyObjectByType(Il2CppType.Of<AudioListener>());
                sb.Append(l != null ? "; an AudioListener is in the scene" : "; NO AudioListener in the scene");
                sb.Append($"; listener volume {AudioListener.volume:0.##}{(AudioListener.pause ? ", paused" : "")}");
            }
            catch (Exception e) { sb.Append("; the listener check failed: " + e.Message); }
            return sb.ToString();
        }

        /// <summary>Self-test: one player's playback.</summary>
        public static string Describe(byte id)
        {
            if (!Players.TryGetValue(id, out var p)) return $"{Votes.NameOf(id)}: nothing heard";
            string src = "no source";
            try { if (p.Source != null) src = $"source {(p.Source.isPlaying ? "playing" : "stopped")} at {p.Source.time:0.00} s, volume {p.Source.volume:0.##}"; }
            catch (Exception e) { src = "source unreadable: " + e.Message; }
            return $"{Votes.NameOf(id)}: {p.Received} packets heard, {p.Written} written, {p.Lost} lost, {p.Dropped} dropped, " +
                   $"{p.Played / (float)Rate:0.00} s played, {p.Starts} start(s), {p.Underruns} underrun(s), talking {IsTalking(id)}, {src}";
        }

        public static long PlayedSamples(byte id) => Players.TryGetValue(id, out var p) ? p.Played : 0;
        public static int HeardPackets(byte id) => Players.TryGetValue(id, out var p) ? p.Received : 0;
        public static bool IsPlaying(byte id) => Players.TryGetValue(id, out var p) && p.Playing;

        // ---------------------------------------------------------------- the talking mark

        private static Texture2D _speaker;

        /// <summary>A small speaker with sound waves, white (tint it with GUI.color).</summary>
        public static Texture2D SpeakerIcon()
        {
            if (_speaker != null) return _speaker;
            const int S = 32;
            var px = new Color[S * S];
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    int hit = 0;
                    for (int sy = 0; sy < 4; sy++)
                        for (int sx = 0; sx < 4; sx++)
                            if (InSpeaker(x + (sx + 0.5f) / 4f, S - (y + (sy + 0.5f) / 4f))) hit++;
                    px[y * S + x] = new Color(1f, 1f, 1f, hit / 16f);
                }
            _speaker = new Texture2D(S, S, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            _speaker.SetPixels(px);
            _speaker.Apply();
            return _speaker;
        }

        /// <summary>In a 32x32 box, y down: the speaker's box and cone, and two waves to its right.</summary>
        private static bool InSpeaker(float x, float y)
        {
            if (x >= 3f && x <= 9f && y >= 12f && y <= 20f) return true;
            if (x >= 9f && x <= 16f)
            {
                float t = (x - 9f) / 7f;
                if (y >= 12f - 7f * t && y <= 20f + 7f * t) return true;
            }
            float dx = x - 16f, dy = y - 16f;
            if (dx <= 0f) return false;
            float d = Mathf.Sqrt(dx * dx + dy * dy);
            float ang = Mathf.Abs(Mathf.Atan2(dy, dx)) * Mathf.Rad2Deg;
            return ang <= 50f && ((d >= 5.5f && d <= 8f) || (d >= 10.5f && d <= 13f));
        }

        private static readonly Dictionary<string, float> NameWidths = new();

        /// <summary>Name tags: the speaker mark just left of a centred name while that player is talking.</summary>
        public static void TagMark(byte id, float centerX, float top, float height, string name, GUIStyle style)
        {
            if (!IsTalking(id) || style == null) return;
            name ??= "";
            string key = style.fontSize + "|" + name;
            if (!NameWidths.TryGetValue(key, out float w))
            {
                try { style.CalcMinMaxWidth(new GUIContent(name), out _, out w); }
                catch { w = name.Length * style.fontSize * 0.55f; }
                if (NameWidths.Count > 64) NameWidths.Clear();
                NameWidths[key] = w;
            }
            float s = Mathf.Min(height, 20f);
            DrawTalking(new Rect(centerX - w * 0.5f - s - 4f, top + (height - s) * 0.5f, s, s));
        }

        /// <summary>The speaker mark in green, pulsing a little (screen or GUI units, whatever the caller draws in).</summary>
        public static void DrawTalking(Rect r)
        {
            var prev = GUI.color;
            float pulse = 0.75f + 0.25f * Mathf.Sin(Time.unscaledTime * 12f);
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), SpeakerIcon());
            GUI.color = new Color(0.45f, 1f, 0.5f, pulse);
            GUI.DrawTexture(r, SpeakerIcon());
            GUI.color = prev;
        }
    }
}
