using System;
using HapticAI.Synth;

namespace HapticAI.Playback
{
    /// <summary>
    /// Converts an audio-rate <see cref="Waveform"/> into the 8-bit amplitude stream the controller
    /// plays: rectify, take a windowed peak while downsampling to the target haptic rate (~320 Hz),
    /// optionally re-impose the source's texture, apply gain, and map to 0..255. Pure and
    /// allocation-light so it is unit testable and cheap enough to run per generated clip.
    ///
    /// <b>Why texture needs re-imposing.</b> A plain peak-envelope conversion throws away everything
    /// above the envelope: an 8 kHz HaptiGen waveform becomes a smooth 320 Hz amplitude curve, so a
    /// gritty crackle and a clean thump differ only in their attack shape and otherwise feel like
    /// the same buzz. The controller's actuator is driven by this amplitude stream, and at 320 Hz it
    /// can reproduce modulation up to ~160 Hz — a band the envelope leaves completely empty.
    ///
    /// So with <c>textureDepth &gt; 0</c> the envelope is modulated by a carrier whose instantaneous
    /// frequency is read out of the source itself: the zero-crossing rate of each window, octave-
    /// folded down into the reproducible band. Roughness in the source becomes roughness you can
    /// feel, and it evolves across the clip the way the source does. Nothing is invented — the
    /// envelope AND the carrier are both derived from the generated waveform — but it is a rendering
    /// choice, so <c>textureDepth = 0</c> restores the pure-envelope behaviour for comparison.
    /// </summary>
    public static class WaveformToAmplitudes
    {
        /// <summary>Slowest carrier worth imposing; below this it reads as amplitude wobble, not texture.</summary>
        const float MinCarrierHz = 25f;
        /// <summary>Keep clear of Nyquist so the carrier does not alias into a beat tone.</summary>
        const float NyquistSafety = 0.9f;

        public static byte[] Convert(Waveform wf, int targetRateHz, float gain = 1.4f,
            float textureDepth = 0f, float maxSeconds = 4f)
        {
            if (wf == null || wf.Samples.Length == 0 || targetRateHz <= 0)
                return new byte[0];

            float duration = Math.Min(wf.DurationSeconds, maxSeconds);
            int outCount = Math.Max(1, (int)(duration * targetRateHz));
            var amps = new byte[outCount];

            double inPerOut = wf.SampleRateHz / (double)targetRateHz;
            float depth = Clamp01(textureDepth);
            // A carrier spends half its cycle below the envelope, so undo the energy it removes and
            // keep a textured clip as strong as the plain envelope would have been.
            float normalise = 1f / (1f - depth * 0.5f);
            float maxCarrierHz = targetRateHz * 0.5f * NyquistSafety;
            double phase = 0d;

            for (int o = 0; o < outCount; o++)
            {
                int start = (int)(o * inPerOut);
                int end = (int)((o + 1) * inPerOut);
                if (end <= start) end = start + 1;
                if (end > wf.Samples.Length) end = wf.Samples.Length;

                // Peak of the rectified window preserves punch better than an average.
                float peak = 0f;
                int crossings = 0;
                float previous = start > 0 ? wf.Samples[start - 1] : 0f;
                for (int i = start; i < end; i++)
                {
                    float s = wf.Samples[i];
                    float a = s < 0f ? -s : s;
                    if (a > peak) peak = a;
                    if ((s >= 0f) != (previous >= 0f)) crossings++;
                    previous = s;
                }

                float value = peak;
                if (depth > 0f)
                {
                    float windowSeconds = (end - start) / (float)wf.SampleRateHz;
                    float carrierHz = FoldIntoBand(crossings / (2f * Math.Max(1e-6f, windowSeconds)), maxCarrierHz);
                    phase += 2d * Math.PI * carrierHz / targetRateHz;
                    float carrier = 0.5f + 0.5f * (float)Math.Sin(phase);
                    value *= (1f - depth + depth * carrier) * normalise;
                }

                float scaled = value * gain;
                if (scaled > 1f) scaled = 1f;
                if (scaled < 0f) scaled = 0f;
                amps[o] = (byte)(scaled * 255f + 0.5f);
            }

            return amps;
        }

        /// <summary>
        /// Bring a source frequency into the actuator's band by halving it — an octave at a time, so
        /// clips that differ in pitch still differ after folding, which simple clamping would flatten.
        /// </summary>
        static float FoldIntoBand(float hz, float maxHz)
        {
            if (hz <= 0f || float.IsNaN(hz)) return MinCarrierHz;
            while (hz > maxHz) hz *= 0.5f;
            return hz < MinCarrierHz ? MinCarrierHz : hz;
        }

        static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
    }
}
