using System;
using System.Collections.Generic;
using HapticAI.Core;
using HapticAI.Playback;
using HapticAI.Synth;
using UnityEngine;

namespace HapticAI.Profiles
{
    /// <summary>
    /// The headset's collection of playable haptics for the current scene: the planned
    /// <see cref="HapticProfileSet"/>, the clips that have arrived for it, a procedural stand-in
    /// for every role that has not, and the disk store that makes the next session instant.
    ///
    /// It does not care where the palette comes from - the session socket, a stored copy, or the
    /// on-device offline synth all feed it through the same two calls (<see cref="AdoptManifest"/>
    /// and <see cref="ReceiveWaveform"/>) - and it never plays anything; the relay asks it
    /// <see cref="ClipFor"/> a role and does the playing.
    /// </summary>
    public sealed class HapticProfileLibrary
    {
        readonly int _sampleRateHz;
        readonly float _amplitudeGain;
        readonly float _textureDepth;
        readonly HapticProfileStore _store;
        readonly Dictionary<string, HapticClipData> _fallbacks = new Dictionary<string, HapticClipData>(StringComparer.Ordinal);

        public HapticProfileLibrary(int sampleRateHz, float amplitudeGain, float textureDepth, HapticProfileStore store)
        {
            _sampleRateHz = sampleRateHz > 0 ? sampleRateHz : 320;
            _amplitudeGain = amplitudeGain;
            _textureDepth = textureDepth;
            _store = store;
        }

        public HapticProfileSet Set { get; private set; }
        public bool HasManifest => Set != null;
        public int ProfileCount => Set?.Profiles.Count ?? 0;
        public int ClipCount => Set?.ClipCount ?? 0;
        /// <summary>Clips received this session (as opposed to restored from disk).</summary>
        public int ReceivedCount { get; private set; }
        public int RestoredCount { get; private set; }
        public string Source => Set?.Source ?? "(no palette yet)";

        /// <summary>Raised on the main thread when a new palette is adopted.</summary>
        public event Action<HapticProfileSet> ManifestChanged;
        /// <summary>Raised when a profile's clip arrives or is replaced.</summary>
        public event Action<HapticProfile> ClipChanged;

        /// <summary>
        /// Take a freshly planned palette as the current one. Clips already on disk for the same
        /// set id are adopted straight away. Returns the profile ids still without a clip - what to
        /// ask the server for - which is empty on a warm start.
        /// </summary>
        public List<string> AdoptManifest(HapticProfileSet set)
        {
            if (set == null) return new List<string>();

            if (Set != null && Set.SetId == set.SetId)
            {
                // Same set replanned (a reconnect): keep every clip we already hold.
                HapticProfileStore.AdoptClips(Set, set);
            }

            var stored = _store?.TryLoad(set.SetId);
            int restored = HapticProfileStore.AdoptClips(stored, set);
            RestoredCount += restored;
            if (restored > 0)
                Debug.Log($"[HapticAI] Restored {restored}/{set.Profiles.Count} clip(s) for set {set.SetId} from {_store.Directory}");

            Set = set;
            ManifestChanged?.Invoke(set);
            return set.MissingClipIds();
        }

        /// <summary>A clip arrived as WAV bytes (the socket's binary frame). False if it was not for the current set.</summary>
        public bool ReceiveWav(string setId, string profileId, string source, byte[] wav)
        {
            Waveform waveform;
            try
            {
                waveform = WavCodec.Decode(wav);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HapticAI] Bad clip for {profileId}: {e.Message}");
                return false;
            }
            return ReceiveWaveform(setId, profileId, source, waveform);
        }

        public bool ReceiveWaveform(string setId, string profileId, string source, Waveform waveform)
        {
            if (Set == null || Set.SetId != setId)
            {
                Debug.LogWarning($"[HapticAI] Ignoring clip {profileId} for set {setId}; current set is {Set?.SetId ?? "(none)"}.");
                return false;
            }
            var profile = Set.ById(profileId);
            if (profile == null)
            {
                Debug.LogWarning($"[HapticAI] Ignoring clip for unknown profile {profileId}.");
                return false;
            }

            byte[] amps = WaveformToAmplitudes.Convert(waveform, _sampleRateHz, _amplitudeGain, _textureDepth);
            if (amps.Length == 0) return false;
            profile.SetClip(new HapticClipData(setId + "|" + profileId, amps, _sampleRateHz, profile.Prompt), source);
            ReceivedCount++;
            _store?.Save(Set);
            ClipChanged?.Invoke(profile);
            return true;
        }

        /// <summary>
        /// The clip to play for a role right now: the profile's generated clip if it has landed,
        /// otherwise the procedural stand-in. <paramref name="profileGain"/> is the planner's level
        /// for the profile (1 for a stand-in).
        /// </summary>
        public HapticClipData ClipFor(string role, out float profileGain, out bool isFallback)
        {
            var profile = Set?.ByRole(role);
            if (profile != null && profile.HasClip)
            {
                profileGain = profile.Gain;
                isFallback = false;
                return profile.Clip;
            }

            profileGain = 1f;
            isFallback = true;
            if (!_fallbacks.TryGetValue(role, out var fallback))
            {
                fallback = FallbackHaptics.ForRole(role, _sampleRateHz);
                _fallbacks[role] = fallback;
            }
            return fallback;
        }

        public HapticProfile ProfileFor(string role) => Set?.ByRole(role);
    }
}
