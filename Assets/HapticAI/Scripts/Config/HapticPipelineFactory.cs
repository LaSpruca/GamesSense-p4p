using HapticAI.Net;
using HapticAI.Profiles;
using HapticAI.Synth;
using UnityEngine;

namespace HapticAI.Config
{
    /// <summary>The assembled haptics stack, plus what it is actually going to generate with.</summary>
    public struct HapticStack
    {
        /// <summary>The palette and its clips; what the relay plays from.</summary>
        public HapticProfileLibrary Library;
        /// <summary>The session socket to the server, or null when running on-device only.</summary>
        public HapticSessionClient Client;
        /// <summary>The on-device palette source. Always present: it is the fallback when the socket is down.</summary>
        public OfflineProfileSynth Offline;
        /// <summary>True when the palette is planned and synthesised on the server; false when everything is the on-device mock.</summary>
        public bool UsingBackend;
        /// <summary>One line for the log and the on-headset readout, e.g. "HaptiGen server (192.168.1.5:8080)".</summary>
        public string SourceDescription;
        /// <summary>Backend base URL, or empty when running on-device only.</summary>
        public string ServiceUrl;
    }

    /// <summary>
    /// Assembles the haptics stack from a <see cref="HapticAIConfig"/> plus runtime secrets. This
    /// is the single wiring point, and it decides exactly one thing: whether the palette comes from
    /// the server over the session socket (when a service URL is configured) or is synthesised
    /// on-device. The on-device synth is built either way, because the socket can be down.
    /// </summary>
    public static class HapticPipelineFactory
    {
        public static HapticStack Build(HapticAIConfig cfg, HapticSecrets secrets, int sampleRateHz)
        {
            bool hasUrl = !string.IsNullOrEmpty(secrets?.hapticServiceUrl);
            bool usingBackend = hasUrl && !cfg.forceOfflineSynthesis;

            string description;
            if (usingBackend)
            {
                description = $"HaptiGen server ({secrets.hapticServiceUrl})";
            }
            else if (cfg.forceOfflineSynthesis)
            {
                description = "on-device mock synth (forceOfflineSynthesis is on)";
                Debug.LogWarning("[HapticAI] forceOfflineSynthesis is enabled - the server will not be " +
                                 "contacted even though a URL is configured.");
            }
            else
            {
                description = "on-device mock synth (no service URL)";
            }
            Debug.Log($"[HapticAI] Haptic palette source: {description}");

            // Persist palettes across sessions unless explicitly disabled: a server palette is
            // ~30 s of synthesis per profile, so a session-only copy pays that on every run.
            HapticProfileStore store = null;
            if (cfg.persistClipCache)
            {
                string profile = HapticProfileStore.ProfileFor(sampleRateHz, cfg.amplitudeGain,
                    cfg.textureDepth, usingBackend ? "srv" : "mock");
                store = new HapticProfileStore(profile);
            }

            var library = new HapticProfileLibrary(sampleRateHz, cfg.amplitudeGain, cfg.textureDepth, store);
            var offline = new OfflineProfileSynth(new MockHapticSynth(cfg.mockSampleRate, cfg.mockLatencyMs));
            var client = usingBackend
                ? new HapticSessionClient(secrets.hapticServiceUrl, cfg.connectTimeoutSeconds)
                : null;

            return new HapticStack
            {
                Library = library,
                Client = client,
                Offline = offline,
                UsingBackend = usingBackend,
                SourceDescription = description,
                ServiceUrl = usingBackend ? secrets.hapticServiceUrl : string.Empty,
            };
        }
    }
}
