using System;

namespace HapticAI.Profiles
{
    /// <summary>
    /// What the foam is doing right now, as the fire sim measures it. The relay fills this from
    /// <c>SurfaceFireManager.Telemetry</c> every frame; it is also what gets streamed to the server
    /// for Gemini to direct from, so the field names mirror the wire format.
    /// </summary>
    public struct SprayReading
    {
        public bool Spraying;
        /// <summary>Trigger depth 0..1.</summary>
        public float Trigger;
        /// <summary>Foam particles per second landing on burning or smouldering fire.</summary>
        public float HitRate;
        /// <summary>Mean hit quality 0..1 where 1 is the base of the flames.</summary>
        public float HitQuality;
        /// <summary>The foam is landing on a knocked-down, smouldering patch.</summary>
        public bool Soaking;
        /// <summary>The nozzle is being swept laterally across the base.</summary>
        public bool Sweeping;
        public float Coverage;
        public float NearestFireMetres;
        public float AimErrorDegrees;
        public int FiresBurning;
        public int FiresSmouldering;
        public int Stage;
    }

    /// <summary>The selector's answer: which looping role plays, and how loud.</summary>
    public struct ProfileSelection
    {
        /// <summary>A looping role name, or empty for silence.</summary>
        public string Role;
        public float Gain;
        /// <summary>Beeps per second the loop is gated into, or 0 for a continuous rumble.</summary>
        public float PulseHz;
        /// <summary>"rules" or "gemini".</summary>
        public string Source;
        /// <summary>The coarse situation this choice was made for. See <see cref="HapticProfileSelector.CoarseKey"/>.</summary>
        public string StateKey;
        public string Reason;

        public bool IsSilent => string.IsNullOrEmpty(Role);

        public static ProfileSelection Silence(string key) =>
            new ProfileSelection { Role = string.Empty, Gain = 0f, Source = "rules", StateKey = key, Reason = "not spraying" };
    }

    /// <summary>
    /// The rule-based selector: the instant, always-available answer to "what should the spray
    /// feel like right now". Gemini's answer (arriving over the session socket) refines it, but
    /// only while the coarse situation it was asked about still holds - so a change from missing to
    /// hitting the base is felt on the very frame it happens, not a round-trip later.
    ///
    /// Ported line for line from <c>coarse_state_key</c> / <c>rule_select</c> in
    /// <c>server/haptic_profiles.py</c>; the two must agree on the key or a Gemini choice could be
    /// pinned to the wrong situation.
    /// </summary>
    public static class HapticProfileSelector
    {
        public const string IdleKey = "idle";
        /// <summary>Below this many particles/second the foam is not meaningfully on the fire.</summary>
        public const float ContactHitRate = 15f;
        /// <summary>Hit quality at or above this counts as "on the base".</summary>
        public const float BaseQuality = 0.6f;

        public static string CoarseKey(in SprayReading r)
        {
            if (!r.Spraying) return IdleKey;
            string tb = r.Trigger < 0.4f ? "t0" : r.Trigger < 0.8f ? "t1" : "t2";
            string contact;
            if (r.HitRate < ContactHitRate)
            {
                contact = r.Soaking ? "soak" : "dry";
            }
            else
            {
                contact = r.HitQuality >= BaseQuality ? "base" : "top";
                if (contact == "base" && r.Sweeping) contact = "sweep";
            }
            return contact + "|" + tb;
        }

        /// <summary>
        /// How strongly the spray loop plays for a situation: quiet when the technique is right,
        /// heavy when it is wrong, so the rumble itself says "you're off". It is the only feedback a
        /// single-actuator device (the ESP32 motor) can carry, since it cannot render texture.
        /// Trigger depth deliberately plays no part - a hard squeeze aimed well must stay quiet.
        /// Must match <c>technique_gain</c> in server/haptic_profiles.py.
        /// </summary>
        public static float TechniqueGain(string contact, in SprayReading r) =>
            IsGoodContact(contact) ? GoodBuzzGain : 1f;

        /// <summary>The steady medium buzz for good technique.</summary>
        public const float GoodBuzzGain = 0.5f;

        static bool IsGoodContact(string contact) => contact == "sweep" || contact == "base" || contact == "soak";

        /// <summary>
        /// The rhythm half of the same message: good technique is a continuous medium buzz, poor
        /// technique is a "thump ... thump" - full-strength hits (<see cref="ThumpSeconds"/> each)
        /// separated by complete silence, coming faster the further off it is. Decided on the
        /// headset only - Gemini never changes rhythm.
        /// </summary>
        public static float TechniquePulseHz(string contact, in SprayReading r)
        {
            if (IsGoodContact(contact)) return 0f;
            if (contact == "top") return 1.5f + 0.75f * Clamp01(1f - Clamp01(r.HitQuality) / BaseQuality);
            return 2.5f;   // dry: missing the fire entirely
        }

        /// <summary>
        /// How long each thump is on. Long enough for an ERM motor to spin up (~30-50 ms) and land a
        /// solid hit, short enough to read as a knock rather than a buzz.
        /// </summary>
        public const float ThumpSeconds = 0.1f;

        /// <summary>
        /// The range Gemini may choose within for a situation, so its refinements can never invert
        /// the good-quiet / bad-heavy rule. Must match <c>GAIN_BANDS</c> in server/haptic_profiles.py.
        /// </summary>
        public static void GainBand(string contact, out float min, out float max)
        {
            if (IsGoodContact(contact)) { min = 0.40f; max = 0.60f; }
            else { min = 1.00f; max = 1.00f; }
        }

        /// <summary>The situation part of a coarse key ("base|t1" -> "base"), or the key itself.</summary>
        public static string ContactOf(string stateKey)
        {
            if (string.IsNullOrEmpty(stateKey)) return string.Empty;
            int bar = stateKey.IndexOf('|');
            return bar < 0 ? stateKey : stateKey.Substring(0, bar);
        }

        static float Clamp01(float v) => Math.Max(0f, Math.Min(1f, v));

        public static ProfileSelection Rules(in SprayReading r)
        {
            string key = CoarseKey(r);
            if (key == IdleKey) return ProfileSelection.Silence(key);

            string contact = ContactOf(key);
            float gain = TechniqueGain(contact, r);
            string role;
            switch (contact)
            {
                case "top": role = HapticRoles.SprayTop; break;
                case "base": role = HapticRoles.SprayBase; break;
                case "sweep": role = HapticRoles.SpraySweep; break;
                case "soak": role = HapticRoles.SpraySoak; break;
                default: role = HapticRoles.SprayDry; break;
            }
            return new ProfileSelection { Role = role, Gain = gain, PulseHz = TechniquePulseHz(contact, r), Source = "rules", StateKey = key, Reason = "rule: " + contact };
        }
    }
}
