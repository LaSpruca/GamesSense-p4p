using UnityEngine;

namespace HapticAI.Config
{
    /// <summary>
    /// Central, inspector-editable configuration for the AI haptics pipeline. The LLM planning, the
    /// Gemini API key and the profile prompts all live on the backend server — not here. This asset
    /// configures only the on-device pieces: how waveforms are rendered to the controller, the
    /// offline fallback synth, the profile store, and how the session socket behaves. The backend
    /// URL itself is a secret, kept out of source control in <see cref="HapticSecrets"/>.
    /// </summary>
    [CreateAssetMenu(fileName = "HapticAIConfig", menuName = "HapticAI/Config")]
    public sealed class HapticAIConfig : ScriptableObject
    {
        [Header("Rendering")]
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
        [Tooltip("Seconds to wait for the session socket to connect. If it has not connected by " +
                 "then the on-device palette is used so the briefing is never held up; the socket " +
                 "keeps retrying and the server palette takes over when it arrives.")]
        public int connectTimeoutSeconds = 8;
        [Tooltip("How often (per second) the spray reading is streamed to the server while the " +
                 "trigger is held. Gemini is asked at most about once a second regardless.")]
        public float stateSendHz = 8f;

        [Header("On-device offline synth (no server / fallback)")]
        public int mockSampleRate = 8000;
        [Tooltip("Fake synthesis latency to exercise the fallback-then-upgrade path in-editor.")]
        public int mockLatencyMs = 0;

        [Header("Profile store")]
        [Tooltip("Persist each planned palette and its clips to disk so a later session in the same " +
                 "room starts instantly instead of re-synthesising (~30 s per clip on the server). " +
                 "Disable to force regeneration.")]
        public bool persistClipCache = true;

        [Header("Playback")]
        [Tooltip("Overridden at runtime by OVRHaptics.Config.SampleRateHz; edit only for tests.")]
        public int targetSampleRateHz = 320;
    }
}
