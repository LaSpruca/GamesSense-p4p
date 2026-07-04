using System;
using HapticAI.Synth;

namespace HapticAI.Playback
{
    /// <summary>
    /// Converts an audio-rate <see cref="Waveform"/> into the 8-bit amplitude envelope the
    /// controller plays: rectify, take a windowed peak while downsampling to the target haptic
    /// rate (~320 Hz), apply gain, and map to 0..255. Pure and allocation-light so it is unit
    /// testable and cheap enough to run per generated clip.
    /// </summary>
    public static class WaveformToAmplitudes
    {
        public static byte[] Convert(Waveform wf, int targetRateHz, float gain = 1.4f, float maxSeconds = 4f)
        {
            if (wf == null || wf.Samples.Length == 0 || targetRateHz <= 0)
                return new byte[0];

            float duration = Math.Min(wf.DurationSeconds, maxSeconds);
            int outCount = Math.Max(1, (int)(duration * targetRateHz));
            var amps = new byte[outCount];

            double inPerOut = wf.SampleRateHz / (double)targetRateHz;

            for (int o = 0; o < outCount; o++)
            {
                int start = (int)(o * inPerOut);
                int end = (int)((o + 1) * inPerOut);
                if (end <= start) end = start + 1;
                if (end > wf.Samples.Length) end = wf.Samples.Length;

                // Peak of the rectified window preserves punch better than an average.
                float peak = 0f;
                for (int i = start; i < end; i++)
                {
                    float a = wf.Samples[i];
                    if (a < 0f) a = -a;
                    if (a > peak) peak = a;
                }

                float scaled = peak * gain;
                if (scaled > 1f) scaled = 1f;
                amps[o] = (byte)(scaled * 255f + 0.5f);
            }

            return amps;
        }
    }
}
