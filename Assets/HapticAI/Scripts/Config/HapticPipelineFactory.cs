using System.Collections.Generic;
using HapticAI.Cache;
using HapticAI.Pipeline;
using HapticAI.Prompt;
using HapticAI.Synth;
using UnityEngine;

namespace HapticAI.Config
{
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
        public static HapticDirector Build(HapticAIConfig cfg, HapticSecrets secrets, int sampleRateHz)
        {
            var source = BuildWaveformSource(cfg, secrets);
            var pipeline = new HapticPipeline(source, sampleRateHz, cfg.clipDurationSeconds, cfg.amplitudeGain);

            // Persist clips across sessions unless explicitly disabled: generation is a 17-34 s
            // backend round trip, so a session-only cache pays that cost on every run.
            HapticClipStore store = null;
            if (cfg.persistClipCache)
            {
                string profile = HapticClipStore.ProfileFor(sampleRateHz, cfg.clipDurationSeconds, cfg.amplitudeGain);
                store = new HapticClipStore(profile);
            }

            var cache = new HapticClipCache(cfg.cacheBuckets, store);
            if (store != null)
                Debug.Log($"[HapticAI] Clip cache: {cache.Count} clip(s) restored from {store.Directory}");

            return new HapticDirector(pipeline, cache, sampleRateHz);
        }

        static IHapticWaveformSource BuildWaveformSource(HapticAIConfig cfg, HapticSecrets secrets)
        {
            // On-device offline generator: context → offline template → mock synth. Always built —
            // used both as standalone offline mode and as the remote source's failure fallback.
            var local = BuildLocalSource(cfg);

            bool hasUrl = !string.IsNullOrEmpty(secrets?.hapticServiceUrl);
            if (cfg.forceOfflineSynthesis || !hasUrl)
            {
                if (!hasUrl && !cfg.forceOfflineSynthesis)
                    Debug.LogWarning("[HapticAI] No haptic service URL configured — " +
                                     "using on-device offline synthesis (mock).");
                return local;
            }

            return new RemoteWaveformSource(secrets.hapticServiceUrl, local, cfg.requestTimeoutSeconds);
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
