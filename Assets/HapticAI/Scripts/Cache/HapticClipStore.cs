using System;
using System.Collections.Generic;
using System.IO;
using HapticAI.Core;
using UnityEngine;

namespace HapticAI.Cache
{
    /// <summary>
    /// Disk backing for <see cref="HapticClipCache"/>. Without it the cache is per-session, so every
    /// play session re-generates the whole warm set — and a backend round trip is 17-34 s (the Gemini
    /// chain is 3 LLM calls before HaptiGen even starts), which is far longer than the client's
    /// request timeout. Persisting clips turns the second and every later run into a zero-request,
    /// zero-latency start, which matters far more than shaving the per-request time.
    ///
    /// Clips are keyed by <see cref="HapticClipCache.KeyFor"/> and stored one file per clip under a
    /// per-profile subdirectory. The profile folds in sample rate, clip duration and amplitude gain:
    /// those change the rendered amplitudes but NOT the cache key, so without it a gain tweak would
    /// silently keep replaying clips baked at the old gain.
    /// </summary>
    public sealed class HapticClipStore
    {
        const uint Magic = 0x504C4348; // "HCLP"
        const int Version = 1;

        readonly string _dir;

        public HapticClipStore(string profile, string rootPath = null)
        {
            string root = rootPath ?? Path.Combine(Application.persistentDataPath, "haptic-clips");
            _dir = Path.Combine(root, profile);
        }

        public string Directory => _dir;

        /// <summary>
        /// Profile tag for everything that changes a clip's bytes but not its cache key. Sample
        /// rate, duration, gain and texture depth all re-render the amplitudes, so without this a
        /// tweak to any of them would silently keep replaying clips baked at the old setting.
        ///
        /// <paramref name="sourceTag"/> separates mock clips from backend ones, which matters more
        /// than it looks: run once with no service URL and the on-device mock fills the cache, and
        /// from then on a correctly configured backend is never called, because every event is
        /// already a cache hit. Different tag, different directory, no shadowing.
        /// </summary>
        public static string ProfileFor(int sampleRateHz, float durationSeconds, float amplitudeGain,
            float textureDepth, string sourceTag) =>
            $"{sourceTag}-sr{sampleRateHz}-d{durationSeconds:0.###}-g{amplitudeGain:0.###}-t{textureDepth:0.##}";

        /// <summary>Every clip on disk for this profile. Unreadable files are skipped, never fatal.</summary>
        public IEnumerable<HapticClipData> LoadAll()
        {
            if (!System.IO.Directory.Exists(_dir)) yield break;

            foreach (var file in System.IO.Directory.GetFiles(_dir, "*.hclip"))
            {
                HapticClipData clip = null;
                try
                {
                    clip = Read(file);
                }
                catch (Exception e)
                {
                    // A truncated or half-written clip must never break startup — drop it and move on.
                    Debug.LogWarning($"[HapticAI] Discarding unreadable cached clip {Path.GetFileName(file)}: {e.Message}");
                    TryDelete(file);
                }
                if (clip != null) yield return clip;
            }
        }

        /// <summary>Write a clip. Writes to a temp file then moves, so a crash can't leave a torn file.</summary>
        public void Save(HapticClipData clip)
        {
            // Degraded clips (backend failed, on-device synth stood in) stay in memory only: a
            // single timeout must not permanently replace a real clip on disk.
            if (clip == null || clip.IsFallback || clip.IsDegraded) return;
            try
            {
                System.IO.Directory.CreateDirectory(_dir);
                string path = PathFor(clip.Key);
                string tmp = path + ".tmp";

                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                using (var w = new BinaryWriter(fs))
                {
                    w.Write(Magic);
                    w.Write(Version);
                    w.Write(clip.Key);
                    w.Write(clip.SampleRateHz);
                    w.Write(clip.Prompt ?? string.Empty);
                    w.Write(clip.Amplitudes.Length);
                    w.Write(clip.Amplitudes);
                }

                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception e)
            {
                // Caching is an optimisation; a read-only or full disk must not break playback.
                Debug.LogWarning($"[HapticAI] Could not persist clip '{clip.Key}': {e.Message}");
            }
        }

        public void Clear()
        {
            try
            {
                if (System.IO.Directory.Exists(_dir)) System.IO.Directory.Delete(_dir, true);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HapticAI] Could not clear clip cache: {e.Message}");
            }
        }

        static HapticClipData Read(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var r = new BinaryReader(fs);

            if (r.ReadUInt32() != Magic) throw new InvalidDataException("bad magic");
            int version = r.ReadInt32();
            if (version != Version) throw new InvalidDataException($"unsupported version {version}");

            string key = r.ReadString();
            int sampleRate = r.ReadInt32();
            string prompt = r.ReadString();
            int length = r.ReadInt32();
            if (length < 0 || length > (1 << 24)) throw new InvalidDataException($"implausible length {length}");

            byte[] amplitudes = r.ReadBytes(length);
            if (amplitudes.Length != length) throw new InvalidDataException("truncated amplitudes");

            return new HapticClipData(key, amplitudes, sampleRate, prompt);
        }

        // Keys contain '|' and ':' and are unbounded, so they can't be filenames. FNV-1a is stable
        // across processes (unlike string.GetHashCode). The full key is stored inside the file and
        // is what the cache indexes on, so a hash collision costs one regenerated clip (the second
        // write evicts the first) rather than ever returning the wrong clip for a key.
        string PathFor(string key) => Path.Combine(_dir, $"{Hashing.Fnv1a(key):x8}.hclip");

        static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }
}
