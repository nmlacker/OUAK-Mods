using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OUAKModAPI;
using UnityEngine;

namespace OUAKSongSelector
{
    /// <summary>
    /// Plays the files from the Music folder through one Unity AudioSource (the game's own music is CRI, so these
    /// sit next to it). A file is decoded on a worker thread (WAV: the API's <see cref="Wav"/> decoder; everything else, and
    /// WAVs it can't read: Windows' Media Foundation, <see cref="MfDecoder"/>), then copied into an
    /// AudioClip a few seconds of sound per frame, so a long song never stalls the game. At most two songs are kept
    /// loaded (the one playing and the next).
    /// Measured: Unity audio is live in this game (one AudioListener on the title screen and in the Plaza; a test tone
    /// gave an output level of 0.025 RMS through AudioListener.GetOutputData and 0 when paused).
    /// </summary>
    public static class CustomAudio
    {
        private sealed class Entry
        {
            public string Rel;
            public AudioClip Clip;
            public bool Ready;
            public string Error;
            public float[] Pcm;
            public int Channels, Rate, Frames, Uploaded;
            public Task Decode;
            public float LastUsed;
        }

        private static readonly Dictionary<string, Entry> _clips = new(StringComparer.OrdinalIgnoreCase);
        private static GameObject _go;
        private static AudioSource _src;
        private static AudioListener _listener;
        private static float _nextListenerCheck;
        private const int FramesPerTick = 48000 * 4;   // ~4 s of sound copied into a clip per frame

        public static AudioSource Source => _src;
        /// <summary>The file the AudioSource holds now.</summary>
        public static string Loaded { get; private set; }
        public static bool IsPlaying => _src != null && _src.isPlaying;
        public static float Time => _src != null && _src.clip != null ? _src.time : 0f;
        public static float Length => _src != null && _src.clip != null ? _src.clip.length : 0f;

        private static void EnsureSource()
        {
            if (_go != null) return;
            _go = new GameObject("OUAKSongSelector.Music");
            UnityEngine.Object.DontDestroyOnLoad(_go);
            _go.hideFlags = HideFlags.HideAndDontSave;
            _src = _go.AddComponent<AudioSource>();
            _src.playOnAwake = false;
            _src.loop = false;
            _src.spatialBlend = 0f;
            _src.volume = 0f;
        }

        /// <summary>Starts loading a file (no-op if it's loading or loaded). Returns the error if it can't be read.</summary>
        public static string Request(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return "no file";
            if (_clips.TryGetValue(rel, out var e)) { e.LastUsed = UnityEngine.Time.unscaledTime; return e.Error; }
            string path = Path.Combine(Catalog.MusicFolder, rel);
            e = new Entry { Rel = rel, LastUsed = UnityEngine.Time.unscaledTime };
            _clips[rel] = e;
            e.Decode = Task.Run(() =>
            {
                try
                {
                    e.Pcm = DecodeFile(path, out e.Channels, out e.Rate);
                    e.Frames = e.Channels > 0 ? e.Pcm.Length / e.Channels : 0;
                    if (e.Frames <= 0) e.Error = "empty WAV file";
                }
                catch (Exception ex) { e.Error = ex.Message; }
            });
            Trim(rel);
            return null;
        }

        /// <summary>WAV through the API's reader (fast, no Windows parts); anything else through Media Foundation.</summary>
        internal static float[] DecodeFile(string path, out int channels, out int rate)
        {
            if (string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase))
            {
                try { return Wav.Decode(File.ReadAllBytes(path), out channels, out rate); }
                catch (InvalidDataException) { /* e.g. ADPCM or MP3-in-WAV: Windows may know it */ }
            }
            return MfDecoder.Decode(path, out channels, out rate);
        }

        public static bool IsReady(string rel) => rel != null && _clips.TryGetValue(rel, out var e) && e.Ready;
        public static string ErrorOf(string rel) => rel != null && _clips.TryGetValue(rel, out var e) ? e.Error : null;

        /// <summary>Puts a loaded file into the AudioSource from the start (not playing yet).</summary>
        public static bool Load(string rel)
        {
            if (!_clips.TryGetValue(rel, out var e) || !e.Ready) return false;
            EnsureSource();
            _src.Stop();
            _src.clip = e.Clip;
            _src.time = 0f;
            Loaded = rel;
            e.LastUsed = UnityEngine.Time.unscaledTime;
            return true;
        }

        public static void Play() { if (_src != null && _src.clip != null) _src.Play(); }
        public static void Pause() { if (_src != null) _src.Pause(); }
        public static void UnPause() { if (_src != null) _src.UnPause(); }
        public static void SetTime(float t) { if (_src != null && _src.clip != null) _src.time = Mathf.Clamp(t, 0f, Mathf.Max(0f, _src.clip.length - 0.05f)); }
        public static void SetVolume(float v) { if (_src != null) _src.volume = Mathf.Clamp01(v); }

        public static void Stop()
        {
            if (_src == null) return;
            _src.Stop();
        }

        /// <summary>Stops and forgets everything (leaving a place).</summary>
        public static void Unload()
        {
            Stop();
            if (_src != null) _src.clip = null;
            Loaded = null;
            foreach (var e in _clips.Values) Free(e);
            _clips.Clear();
        }

        private static void Free(Entry e)
        {
            e.Pcm = null;
            if (e.Clip != null) { try { UnityEngine.Object.Destroy(e.Clip); } catch { } }
            e.Clip = null;
        }

        // Keeps the newest few: the one asked for, the one playing, and one more.
        private static void Trim(string keep)
        {
            var old = _clips.Values.Where(c => c.Rel != keep && c.Rel != Loaded).OrderByDescending(c => c.LastUsed).Skip(1).ToList();
            foreach (var c in old) { if (c.Decode != null && !c.Decode.IsCompleted) continue; Free(c); _clips.Remove(c.Rel); }
        }

        /// <summary>Every frame: finishes decodes, copies the next part of a clip, keeps an AudioListener around.</summary>
        public static void Tick()
        {
            float now = UnityEngine.Time.unscaledTime;
            foreach (var e in _clips.Values)
            {
                if (e.Ready || e.Error != null || e.Decode == null || !e.Decode.IsCompleted) continue;
                try
                {
                    if (e.Clip == null)
                    {
                        e.Clip = AudioClip.Create("OUAKSongSelector " + Path.GetFileNameWithoutExtension(e.Rel), e.Frames, e.Channels, e.Rate, false);
                        e.Clip.hideFlags = HideFlags.HideAndDontSave;
                        e.Uploaded = 0;
                    }
                    int frames = Math.Min(FramesPerTick, e.Frames - e.Uploaded);
                    var part = new float[frames * e.Channels];
                    Array.Copy(e.Pcm, (long)e.Uploaded * e.Channels, part, 0, part.Length);
                    e.Clip.SetData(part, e.Uploaded);
                    e.Uploaded += frames;
                    if (e.Uploaded >= e.Frames) { e.Ready = true; e.Pcm = null; SongSelectorMod.Log.Info($"Loaded {e.Rel}: {e.Frames / (float)e.Rate:0.0} s, {e.Channels} ch, {e.Rate} Hz"); }
                }
                catch (Exception ex) { e.Error = ex.Message; SongSelectorMod.Log.Warn($"{e.Rel}: {ex.Message}"); }
                break;   // one clip's part per frame
            }

            // Unity needs one AudioListener for any AudioSource to be heard; add ours only while the scene has none.
            if (_go != null && now >= _nextListenerCheck)
            {
                _nextListenerCheck = now + 1f;
                try
                {
                    int n = UnityEngine.Object.FindObjectsOfType<AudioListener>().Length;
                    bool ours = _listener != null;
                    if (!ours && n == 0) { _listener = _go.AddComponent<AudioListener>(); SongSelectorMod.Log.Info("No AudioListener in this scene: added one for your music."); }
                    else if (ours && n > 1) { UnityEngine.Object.Destroy(_listener); _listener = null; }
                }
                catch { }
            }
        }
    }
}
