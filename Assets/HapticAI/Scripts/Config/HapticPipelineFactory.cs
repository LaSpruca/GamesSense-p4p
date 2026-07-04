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
    /// plus runtime secrets. This is the single wiring point: change providers, the contributor
    /// set, or the prompt chain here and the rest of the system follows.
    /// </summary>
    public static class HapticPipelineFactory
    {
        public static HapticDirector Build(HapticAIConfig cfg, HapticSecrets secrets, int sampleRateHz)
        {
            // 1. Environmental cue extraction — add contributors here to feed in new signals.
            var contributors = new List<IContextContributor>
            {
                new EventTypeContributor(),
                new SurfaceMaterialContributor(),
                new FireIntensityContributor(),
                new DistanceContributor(),
                new TriggerDepthContributor(),
            };
            var contextBuilder = new ContextBuilder(contributors);

            // 2. Prompt chain — Gemini stages from config, or a single offline stage as fallback.
            var promptChain = BuildPromptChain(cfg, secrets);

            // 3. Haptic synthesis provider.
            IHapticSynthProvider synth = BuildSynth(cfg, secrets);

            // 4. Assemble.
            var pipeline = new HapticPipeline(contextBuilder, promptChain, synth,
                sampleRateHz, cfg.clipDurationSeconds, cfg.amplitudeGain);
            var cache = new HapticClipCache(cfg.cacheBuckets);
            return new HapticDirector(pipeline, cache, sampleRateHz);
        }

        static PromptChain BuildPromptChain(HapticAIConfig cfg, HapticSecrets secrets)
        {
            bool canUseGemini = cfg.useGemini && !string.IsNullOrEmpty(secrets?.geminiApiKey);
            if (!canUseGemini)
            {
                if (cfg.useGemini)
                    Debug.LogWarning("[HapticAI] Gemini requested but no API key found — using offline prompt stage.");
                return new PromptChain(new IPromptStage[] { new OfflinePromptStage() });
            }

            var client = new GeminiClient(secrets.geminiApiKey, cfg.geminiModel, cfg.geminiEndpointBase);
            var stages = new List<IPromptStage>();
            foreach (var t in cfg.promptStages)
                stages.Add(new GeminiPromptStage(t.name, client, t.systemInstruction, t.userTemplate));
            if (stages.Count == 0) stages.Add(new OfflinePromptStage());
            return new PromptChain(stages);
        }

        static IHapticSynthProvider BuildSynth(HapticAIConfig cfg, HapticSecrets secrets)
        {
            if (cfg.provider == SynthProviderKind.HaptiGenHttp && !string.IsNullOrEmpty(secrets?.hapticGenUrl))
                return new HaptiGenHttpSynth(secrets.hapticGenUrl);

            if (cfg.provider == SynthProviderKind.HaptiGenHttp)
                Debug.LogWarning("[HapticAI] HaptiGen provider selected but no URL configured — using mock synth.");

            return new MockHapticSynth(cfg.mockSampleRate, cfg.mockLatencyMs);
        }
    }
}
