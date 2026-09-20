using System;
using HapticAI.Core;
using HapticAI.Profiles;

namespace HapticAI.Playback
{
    /// <summary>
    /// Instant, purely-procedural clips, one shape per role. Played while a profile's real clip is
    /// still being synthesised on the server (or streaming down), and for any role whose clip never
    /// arrives. No async, no allocation beyond the buffer - safe on the hot path, so the trainee
    /// always feels something immediately.
    ///
    /// The looping spray shapes are deliberately distinct from each other in the same direction the
    /// designed profiles are (base dense, top thin, dry hollow, soak soft), so the technique feedback
    /// is present from the first spray of a cold session rather than only after the palette lands.
    /// </summary>
    public static class FallbackHaptics
    {
        public static HapticClipData ForRole(string role, int sampleRateHz)
        {
            // (duration s, level 0..1, exponential decay, carrier Hz, modulation depth, modulation Hz)
            float duration, level, decay, carrierHz, modDepth, modHz;
            switch (role)
            {
                case HapticRoles.SprayDry:    duration = 1.0f; level = 0.40f; decay = 0f;   carrierHz = 45f;  modDepth = 0.10f; modHz = 0f;  break;
                case HapticRoles.SprayTop:    duration = 1.0f; level = 0.35f; decay = 0f;   carrierHz = 110f; modDepth = 0.60f; modHz = 14f; break;
                case HapticRoles.SprayBase:   duration = 1.0f; level = 0.80f; decay = 0f;   carrierHz = 30f;  modDepth = 0.35f; modHz = 6f;  break;
                case HapticRoles.SpraySweep:  duration = 1.2f; level = 0.80f; decay = 0f;   carrierHz = 30f;  modDepth = 0.55f; modHz = 1.6f; break;
                case HapticRoles.SpraySoak:   duration = 1.2f; level = 0.45f; decay = 0f;   carrierHz = 26f;  modDepth = 0.45f; modHz = 3f;  break;
                case HapticRoles.Ignite:      duration = 0.5f; level = 0.95f; decay = 6f;   carrierHz = 60f;  modDepth = 0.30f; modHz = 0f;  break;
                case HapticRoles.Spread:      duration = 0.35f; level = 0.65f; decay = 8f;  carrierHz = 80f;  modDepth = 0.50f; modHz = 20f; break;
                case HapticRoles.Extinguish:  duration = 0.6f; level = 0.70f; decay = 3f;   carrierHz = 90f;  modDepth = 0.40f; modHz = 12f; break;
                case HapticRoles.Reignite:    duration = 0.7f; level = 0.90f; decay = 2f;   carrierHz = 50f;  modDepth = 0.90f; modHz = 4f;  break;
                case HapticRoles.SmoulderWarning: duration = 0.45f; level = 0.55f; decay = 4f; carrierHz = 35f; modDepth = 0.80f; modHz = 5f; break;
                case HapticRoles.StageComplete: duration = 1.1f; level = 0.85f; decay = 1.6f; carrierHz = 40f; modDepth = 0.70f; modHz = 2.2f; break;
                default:                      duration = 0.5f; level = 0.60f; decay = 4f;   carrierHz = 60f;  modDepth = 0.30f; modHz = 0f;  break;
            }

            int n = Math.Max(1, (int)(duration * sampleRateHz));
            var amps = new byte[n];
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)sampleRateHz;
                float env = decay > 0f ? (float)Math.Exp(-decay * t) : 1f;
                float carrier = 0.7f + 0.3f * (float)Math.Sin(2 * Math.PI * carrierHz * t);
                float mod = modHz > 0f
                    ? 1f - modDepth * 0.5f * (1f - (float)Math.Sin(2 * Math.PI * modHz * t))
                    : 1f - modDepth * 0.5f;
                float a = Math.Min(1f, level * env * carrier * mod);
                amps[i] = (byte)(a * 255f + 0.5f);
            }

            return new HapticClipData("fallback|" + role, amps, sampleRateHz,
                prompt: "(procedural fallback for " + role + ")", isFallback: true);
        }
    }
}
