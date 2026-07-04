using System;
using System.Collections.Generic;
using UnityEngine;

namespace HapticAI.Config
{
    public enum SynthProviderKind
    {
        Mock,
        HaptiGenHttp,
    }

    /// <summary>One Gemini stage as editable data, so prompt wording is tuned without recompiling.</summary>
    [Serializable]
    public sealed class PromptStageTemplate
    {
        public string name = "stage";
        [TextArea(2, 6)] public string systemInstruction = "";
        [TextArea(2, 6)] public string userTemplate = "{input}";
    }

    /// <summary>
    /// Central, inspector-editable configuration for the AI haptics pipeline. Secrets (Gemini key,
    /// HaptiGen URL) live outside this asset in a gitignored StreamingAssets file; see
    /// <see cref="HapticSecrets"/>.
    /// </summary>
    [CreateAssetMenu(fileName = "HapticAIConfig", menuName = "HapticAI/Config")]
    public sealed class HapticAIConfig : ScriptableObject
    {
        [Header("Synthesis provider")]
        public SynthProviderKind provider = SynthProviderKind.Mock;
        [Tooltip("Requested clip length (seconds) for generated haptics.")]
        public float clipDurationSeconds = 1.0f;
        [Tooltip("Gain applied when converting the waveform to controller amplitudes.")]
        public float amplitudeGain = 1.4f;

        [Header("Mock provider (no server)")]
        public int mockSampleRate = 8000;
        [Tooltip("Fake network latency to exercise the hybrid fallback path in-editor.")]
        public int mockLatencyMs = 0;

        [Header("Prompt chain (Gemini)")]
        [Tooltip("When off, an offline template stage is used instead of Gemini (runs with no key).")]
        public bool useGemini = true;
        public string geminiModel = "gemini-2.5-flash";
        public string geminiEndpointBase = "https://generativelanguage.googleapis.com/v1beta";
        [Tooltip("Chain of Gemini stages, run top to bottom. Add/reorder to change prompt generation.")]
        public List<PromptStageTemplate> promptStages = new List<PromptStageTemplate>
        {
            new PromptStageTemplate
            {
                name = "environment-summary",
                systemInstruction = "You describe a VR fire-training moment in one vivid sentence, " +
                                    "focusing on the physical sensation a hand would feel.",
                userTemplate = "Cues: {brief}\nSummarise the moment in one sentence.",
            },
            new PromptStageTemplate
            {
                name = "haptic-design-brief",
                systemInstruction = "You are a haptics designer. Turn a described moment into a short " +
                                    "brief naming texture, sharpness, rhythm and how it evolves over time.",
                userTemplate = "Moment: {input}\nWrite the haptic design brief.",
            },
            new PromptStageTemplate
            {
                name = "haptigen-prompt",
                systemInstruction = "You write concise text prompts for a text-to-haptics model (HaptiGen). " +
                                    "Output ONLY the prompt, one line, no preamble.",
                userTemplate = "Design brief: {input}\nWrite the HaptiGen prompt.",
            },
        };

        [Header("Cache")]
        [Tooltip("Bucket granularity for numeric cues when forming cache keys (higher = more variety).")]
        public int cacheBuckets = 4;

        [Header("Playback")]
        [Tooltip("Overridden at runtime by OVRHaptics.Config.SampleRateHz; edit only for tests.")]
        public int targetSampleRateHz = 320;
    }
}
