using System.Collections.Generic;
using HapticAI.Cache;
using HapticAI.Pipeline;
using HapticAI.Prompt;
using HapticAI.Synth;
using UnityEngine;

namespace HapticAI.Config
{
    /// <summary>The assembled haptics stack, plus what it is actually going to generate with.</summary>
    public struct HapticStack
    {
        public HapticDirector Director;
        /// <summary>True when events go to the HaptiGen backend; false when everything is the on-device mock.</summary>
        public bool UsingBackend;
        /// <summary>One line for the log and the on-headset readout, e.g. "HaptiGen backend (localhost:8080)".</summary>
        public string SourceDescription;
        /// <summary>Backend base URL, or empty when running on-device only.</summary>
        public string ServiceUrl;
        /// <summary>Clips already on disk for this profile at startup.</summary>
        public int CachedClipCount;
    }

    /// <summary>
    /// Assembles a ready-to-use <see cref="HapticDirector"/> from a <see cref="HapticAIConfig"/>
    /// plus runtime secrets. This is the single wiring point. Since the prompting move it decides
    /// exactly one thing: whether events are generated on the backend
    /// (<see cref="RemoteWaveformSource"/>, when a service URL is configured) or fully on-device
    /// (<see cref="LocalWaveformSource"/>). The remote path always keeps the local one as its
    /// offline fallback.
    /// </summary>
    public static class HapticPipelineFactory
    {
        public static HapticStack Build(HapticAIConfig cfg, HapticSecrets secrets, int sampleRateHz)
        {
            var local = BuildLocalSource(cfg);

            bool hasUrl = !string.IsNullOrEmpty(secrets?.hapticServiceUrl);
            bool usingBackend = hasUrl && !cfg.forceOfflineSynthesis;

            IHapticWaveformSource source = usingBackend
                ? new RemoteWaveformSource(secrets.hapticServiceUrl, local, cfg.requestTimeoutSeconds)
                : local;

            string description;
            if (usingBackend)
            {
                description = $"HaptiGen backend ({secrets.hapticServiceUrl})";
            }
            else if (cfg.forceOfflineSynthesis)
            {
                description = "on-device mock synth (forceOfflineSynthesis is on)";
                Debug.LogWarning("[HapticAI] forceOfflineSynthesis is enabled - the backend will not be " +
                                 "called even though a URL is configured.");
            }
            else
            {
                description = "on-device mock synth (no service URL)";
            }
            Debug.Log($"[HapticAI] Waveform source: {description}");

            var pipeline = new HapticPipeline(source, sampleRateHz, cfg.clipDurationSeconds,
                cfg.amplitudeGain, cfg.textureDepth);

            // Persist clips across sessions unless explicitly disabled: generation is a 17-34 s
            // backend round trip, so a session-only cache pays that cost on every run.
            HapticClipStore store = null;
            if (cfg.persistClipCache)
            {
                string profile = HapticClipStore.ProfileFor(sampleRateHz, cfg.clipDurationSeconds,
                    cfg.amplitudeGain, cfg.textureDepth, usingBackend ? "srv" : "mock");
                store = new HapticClipStore(profile);
            }

            var cache = new HapticClipCache(cfg.cacheBuckets, store);
            if (store != null)
                Debug.Log($"[HapticAI] Clip cache: {cache.Count} clip(s) restored from {store.Directory}");

            return new HapticStack
            {
                Director = new HapticDirector(pipeline, cache, sampleRateHz),
                UsingBackend = usingBackend,
                SourceDescription = description,
                ServiceUrl = usingBackend ? secrets.hapticServiceUrl : string.Empty,
                CachedClipCount = cache.Count,
            };
        }

        static LocalWaveformSource BuildLocalSource(HapticAIConfig cfg)
        {
            // Environmental cue extraction — add contributors here to feed in new offline signals.
            var contributors = new List<IContextContributor>
            {
                new EventTypeContributor(),
                new SurfaceMaterialContributor(),
                new FireIntensityContributor(),
                new DistanceContributor(),
                new TriggerDepthContributor(),
            };
            var contextBuilder = new ContextBuilder(contributors);

            // Offline template only — the real (Gemini) chain now lives on the backend.
            var promptChain = new PromptChain(new IPromptStage[] { new OfflinePromptStage() });

            var synth = new MockHapticSynth(cfg.mockSampleRate, cfg.mockLatencyMs);
            return new LocalWaveformSource(contextBuilder, promptChain, synth);
        }
    }
}
