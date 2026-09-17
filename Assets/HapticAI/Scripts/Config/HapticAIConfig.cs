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
        [Tooltip("How strongly the source waveform's own roughness is re-imposed on the amplitude " +
                 "stream. 0 = plain peak envelope, which makes every clip feel like the same buzz " +
                 "with a different volume shape. Around 0.5-0.7 is where clips start feeling " +
                 "distinct. Changing this invalidates the persistent clip cache automatically.")]
        [Range(0f, 1f)] public float textureDepth = 0.55f;

        [Header("Backend service")]
        [Tooltip("Force on-device offline synthesis even when a service URL is configured (useful for testing).")]
        public bool forceOfflineSynthesis = false;
        [Tooltip("In the Editor only, fall back to http://localhost:8080 when no URL is configured, " +
                 "so a local server is actually used instead of silently running the mock synth.")]
        public bool useEditorLocalhostFallback = true;
        [Tooltip("HTTP timeout (seconds) for a backend request before falling back to on-device synthesis.")]
        public int requestTimeoutSeconds = 30;

        [Header("On-device offline synth (no server / fallback)")]
        public int mockSampleRate = 8000;
        [Tooltip("Fake network latency to exercise the hybrid fallback path in-editor.")]
        public int mockLatencyMs = 0;

        [Header("Cache")]
        [Tooltip("Bucket granularity for numeric cues when forming cache keys (higher = more variety).")]
        public int cacheBuckets = 4;
        [Tooltip("Persist generated clips to disk so later sessions start instantly instead of " +
                 "re-running the backend (17-34s per clip). Disable to force regeneration.")]
        public bool persistClipCache = true;

        [Header("Playback")]
        [Tooltip("Overridden at runtime by OVRHaptics.Config.SampleRateHz; edit only for tests.")]
        public int targetSampleRateHz = 320;
    }
}
