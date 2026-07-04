using System;
using HapticAI.Core;

namespace HapticAI.Playback
{
    /// <summary>
    /// Instant, purely-procedural clips used the first time an event is seen (while the real
    /// Gemini→HaptiGen clip generates in the background) and whenever generation fails. No async,
    /// no allocation beyond the buffer — safe to call on the hot path so the user always feels
    /// something immediately.
    /// </summary>
    public static class FallbackHaptics
    {
        public static HapticClipData Build(HapticEvent evt, int sampleRateHz, string key)
        {
            // Per-event shape: (duration seconds, sustain amplitude 0..1, attack sharpness).
            float duration, level, decay;
            switch (evt.Type)
            {
                case HapticEventType.Ignite:     duration = 0.5f; level = 0.9f; decay = 6f; break;
                case HapticEventType.Spread:     duration = 0.35f; level = 0.6f; decay = 8f; break;
                case HapticEventType.Extinguish: duration = 0.6f; level = 0.7f; decay = 3f; break;
                case HapticEventType.SprayTick:  duration = 0.12f; level = 0.5f; decay = 0.5f; break;
                default:                         duration = 0.3f; level = 0.6f; decay = 5f; break;
            }

            // Scale by intensity/trigger cues if present.
            level *= 0.5f + 0.5f * Math.Max(evt.Number("intensity", 1f), evt.Number("triggerDepth", 1f));

            int n = Math.Max(1, (int)(duration * sampleRateHz));
            var amps = new byte[n];
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)sampleRateHz;
                float env = (float)Math.Exp(-decay * t);
                float buzz = 0.7f + 0.3f * (float)Math.Sin(2 * Math.PI * 60f * t);
                float a = Math.Min(1f, level * env * buzz);
                amps[i] = (byte)(a * 255f + 0.5f);
            }

            return new HapticClipData(key, amps, sampleRateHz, prompt: "(procedural fallback)", isFallback: true);
        }
    }
}
