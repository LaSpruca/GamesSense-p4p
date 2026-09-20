using System;
using System.Collections.Generic;
using System.IO;
using HapticAI.Core;
using UnityEngine;

namespace HapticAI.Profiles
{
    /// <summary>
    /// Disk backing for a <see cref="HapticProfileSet"/>. A palette costs ~30 s of HaptiGen per
    /// profile on the server, so the second session in a room should never wait for it: the server
    /// replans (cached, sub-second), the headset finds the same set id here, and tells the server
    /// it needs no clips at all.
    ///
    /// One JSON file per set, under a profile directory that folds in everything that changes the
    /// rendered amplitudes but not the set id (sample rate, gain, texture depth) - otherwise a gain
    /// tweak would silently keep replaying clips baked at the old setting. Source-tagged too, so
    /// on-device mock clips never shadow server ones.
    /// </summary>
    public sealed class HapticProfileStore
    {
        const int Version = 1;

        readonly string _dir;

        public HapticProfileStore(string profile, string rootPath = null)
        {
            string root = rootPath ?? Path.Combine(Application.persistentDataPath, "haptic-profiles");
            _dir = Path.Combine(root, profile);
        }

        public string Directory => _dir;

        public static string ProfileFor(int sampleRateHz, float amplitudeGain, float textureDepth, string sourceTag) =>
            $"{sourceTag}-sr{sampleRateHz}-g{amplitudeGain:0.###}-t{textureDepth:0.##}";

        [Serializable]
        sealed class ProfileDto
        {
            public string id;
            public string role;
            public string prompt;
            public float duration_s;
            public bool loop;
            public float gain;
            public string clip_source;
            public int sample_rate;
            public string amplitudes_b64;
        }

        [Serializable]
        sealed class SetDto
        {
            public int version;
            public string set_id;
            public string scene_key;
            public string source;
            public string notes;
            public ProfileDto[] profiles;
        }

        /// <summary>Write the set with every clip it currently holds. Temp-then-move, so a crash can't tear it.</summary>
        public void Save(HapticProfileSet set)
        {
            if (set == null || string.IsNullOrEmpty(set.SetId)) return;
            try
            {
                System.IO.Directory.CreateDirectory(_dir);
                var dto = new SetDto
                {
                    version = Version,
                    set_id = set.SetId,
                    scene_key = set.SceneKey,
                    source = set.Source,
                    notes = set.Notes,
                    profiles = new ProfileDto[set.Profiles.Count],
                };
                for (int i = 0; i < set.Profiles.Count; i++)
                {
                    var p = set.Profiles[i];
                    // Fallback clips are the procedural stand-in; persisting them would pin the
                    // placeholder in place of the real clip forever.
                    bool real = p.HasClip && !p.Clip.IsFallback;
                    dto.profiles[i] = new ProfileDto
                    {
                        id = p.Id,
                        role = p.Role,
                        prompt = p.Prompt,
                        duration_s = p.DurationSeconds,
                        loop = p.Loop,
                        gain = p.Gain,
                        clip_source = real ? p.ClipSource : null,
                        sample_rate = real ? p.Clip.SampleRateHz : 0,
                        amplitudes_b64 = real ? Convert.ToBase64String(p.Clip.Amplitudes) : null,
                    };
                }

                string path = PathFor(set.SetId);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonUtility.ToJson(dto));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HapticAI] Could not persist profile set {set.SetId}: {e.Message}");
            }
        }

        /// <summary>Load a set by id, with whatever clips were saved. Null if absent or unreadable.</summary>
        public HapticProfileSet TryLoad(string setId)
        {
            if (string.IsNullOrEmpty(setId)) return null;
            string path = PathFor(setId);
            if (!File.Exists(path)) return null;
            try
            {
                var dto = JsonUtility.FromJson<SetDto>(File.ReadAllText(path));
                if (dto == null || dto.version != Version || dto.profiles == null) return null;

                var profiles = new List<HapticProfile>(dto.profiles.Length);
                foreach (var d in dto.profiles)
                {
                    if (d == null || string.IsNullOrEmpty(d.id)) continue;
                    var p = new HapticProfile(d.id, d.role, d.prompt, d.duration_s, d.loop, d.gain);
                    if (!string.IsNullOrEmpty(d.amplitudes_b64) && d.sample_rate > 0)
                    {
                        byte[] amps = Convert.FromBase64String(d.amplitudes_b64);
                        p.SetClip(new HapticClipData(setId + "|" + d.id, amps, d.sample_rate, d.prompt), d.clip_source);
                    }
                    profiles.Add(p);
                }
                return new HapticProfileSet(dto.set_id, dto.scene_key, dto.source, dto.notes, profiles);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HapticAI] Discarding unreadable profile set {setId}: {e.Message}");
                try { File.Delete(path); } catch { /* best effort */ }
                return null;
            }
        }

        /// <summary>Copy the clips of a stored set onto a freshly planned one with the same id.</summary>
        public static int AdoptClips(HapticProfileSet from, HapticProfileSet into)
        {
            if (from == null || into == null) return 0;
            int adopted = 0;
            foreach (var p in into.Profiles)
            {
                var stored = from.ById(p.Id);
                if (stored == null || !stored.HasClip || p.HasClip) continue;
                p.SetClip(stored.Clip, stored.ClipSource);
                adopted++;
            }
            return adopted;
        }

        public void Clear()
        {
            try
            {
                if (System.IO.Directory.Exists(_dir)) System.IO.Directory.Delete(_dir, true);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HapticAI] Could not clear profile store: {e.Message}");
            }
        }

        // Set ids are short hex from the server, but an offline id is hashed here too; keep the
        // filename safe either way.
        string PathFor(string setId) => Path.Combine(_dir, $"{Hashing.Fnv1a(setId):x8}-{Sanitise(setId)}.json");

        static string Sanitise(string s)
        {
            var chars = s.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (!char.IsLetterOrDigit(chars[i])) chars[i] = '_';
            return new string(chars, 0, Math.Min(chars.Length, 32));
        }
    }
}
