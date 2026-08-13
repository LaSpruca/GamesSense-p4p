using System.Collections.Generic;

namespace HapticAI.Core
{
    /// <summary>
    /// A playable haptic clip in a device-agnostic form: 8-bit amplitudes (0..255) sampled at
    /// <see cref="SampleRateHz"/> (the controller's haptic rate, ~320 Hz on Quest). The OVR glue
    /// wraps <see cref="Amplitudes"/> in an OVRHapticsClip. Also carries the prompt/trace that
    /// produced it, for on-device debugging.
    /// </summary>
    public sealed class HapticClipData
    {
        public readonly string Key;
        public readonly byte[] Amplitudes;
        public readonly int SampleRateHz;
        public readonly string Prompt;
        public readonly IReadOnlyList<string> Trace;
        public readonly bool IsFallback;
        /// <summary>
        /// Produced by the on-device synth only because the backend request failed. Cached for the
        /// session so playback stays responsive, but deliberately not persisted — the next run
        /// should retry the backend rather than inherit a degraded clip permanently.
        /// </summary>
        public readonly bool IsDegraded;

        public HapticClipData(string key, byte[] amplitudes, int sampleRateHz,
            string prompt = null, IReadOnlyList<string> trace = null, bool isFallback = false,
            bool isDegraded = false)
        {
            Key = key;
            Amplitudes = amplitudes ?? new byte[0];
            SampleRateHz = sampleRateHz > 0 ? sampleRateHz : 1;
            Prompt = prompt;
            Trace = trace ?? new List<string>();
            IsFallback = isFallback;
            IsDegraded = isDegraded;
        }

        public float DurationSeconds => Amplitudes.Length / (float)SampleRateHz;
    }
}
