using UnityEngine;

namespace HapticAI.Config
{
    /// <summary>
    /// Central, inspector-editable configuration for the AI haptics pipeline. Since the prompting
    /// move, the LLM prompt chain and the Gemini API key live on the backend server — not here.
    /// This asset now configures only the on-device pieces: the offline fallback synth, the cache,
    /// playback, and how a backend request behaves. The backend URL itself is a secret, kept out of
    /// source control in <see cref="HapticSecrets"/>.
    /// </summary>
    [CreateAssetMenu(fileName = "HapticAIConfig", menuName = "HapticAI/Config")]
    public sealed class HapticAIConfig : ScriptableObject
    {
        [Header("Generation")]
        [Tooltip("Requested clip length (seconds) for generated haptics.")]
        public float clipDurationSeconds = 1.0f;
        [Tooltip("Gain applied when converting the waveform to controller amplitudes.")]
        public float amplitudeGain = 1.4f;

        [Header("Backend service")]
        [Tooltip("Force on-device offline synthesis even when a service URL is configured (useful for testing).")]
        public bool forceOfflineSynthesis = false;
        [Tooltip("HTTP timeout (seconds) for a backend request before falling back to on-device synthesis.")]
        public int requestTimeoutSeconds = 30;

        [Header("On-device offline synth (no server / fallback)")]
        public int mockSampleRate = 8000;
        [Tooltip("Fake network latency to exercise the hybrid fallback path in-editor.")]
        public int mockLatencyMs = 0;

        [Header("Cache")]
        [Tooltip("Bucket granularity for numeric cues when forming cache keys (higher = more variety).")]
        public int cacheBuckets = 4;

        [Header("Playback")]
        [Tooltip("Overridden at runtime by OVRHaptics.Config.SampleRateHz; edit only for tests.")]
        public int targetSampleRateHz = 320;
    }
}
