using System;

namespace HapticAI.Profiles
{
    /// <summary>
    /// The fixed vocabulary of haptic profiles a session holds. Kept in step with <c>ROLES</c> in
    /// <c>server/haptic_profiles.py</c>: the server plans one profile per role, and the headset
    /// asks for a clip by role name, so the two sides never need to agree on anything else.
    ///
    /// The looping roles are the spray states the selector chooses between while the trigger is
    /// held; the rest are one-shot cues the relay fires when the matching world event happens.
    /// </summary>
    public static class HapticRoles
    {
        public const string SprayDry = "spray_dry";
        public const string SprayTop = "spray_top";
        public const string SprayBase = "spray_base";
        public const string SpraySweep = "spray_sweep";
        public const string SpraySoak = "spray_soak";
        public const string Ignite = "ignite";
        public const string Spread = "spread";
        public const string Extinguish = "extinguish";
        public const string Reignite = "reignite";
        public const string SmoulderWarning = "smoulder_warning";
        public const string StageComplete = "stage_complete";

        /// <summary>One entry per role: name, whether it loops, clip length, and the offline wording.</summary>
        public readonly struct Spec
        {
            public readonly string Name;
            public readonly bool Loop;
            public readonly float DurationSeconds;
            public readonly string What;
            public readonly string ShouldFeel;

            public Spec(string name, bool loop, float durationSeconds, string what, string shouldFeel)
            {
                Name = name;
                Loop = loop;
                DurationSeconds = durationSeconds;
                What = what;
                ShouldFeel = shouldFeel;
            }
        }

        // Same wording as the server's offline templates, so an on-device session and a server
        // session without a Gemini key describe each role identically. Every clip is 1.0 s, as on
        // the server, where the NPU decoder is compiled per clip length and 1.0 s is the cached one.
        public static readonly Spec[] All =
        {
            new Spec(SprayDry, true, 1.0f,
                "the trigger is held but the foam is landing on nothing that is burning",
                "an even, uneventful pressurised hiss - steady and slightly hollow, nothing to reward"),
            new Spec(SprayTop, true, 1.0f,
                "foam is hitting the TOP of the flames instead of the base - poor aim",
                "thin, fluttery and unsatisfying: the foam is flashing off in the heat, little resistance"),
            new Spec(SprayBase, true, 1.0f,
                "foam is landing squarely on the BASE of the fire - correct technique",
                "dense and rewarding: a firm, chugging resistance with a crisp fizz as flames are smothered"),
            new Spec(SpraySweep, true, 1.0f,
                "foam is being swept side to side across the base of the fire - ideal technique",
                "rolling and rhythmic: the same dense resistance as the base hit, with a slow lateral surge"),
            new Spec(SpraySoak, true, 1.0f,
                "foam is soaking an already knocked-down, smouldering patch to stop it re-igniting",
                "soft, bubbling and settling - a wet crackle that gradually calms to a low hum"),
            new Spec(Ignite, false, 1.0f,
                "a fire suddenly ignites nearby",
                "a sharp whump: fast attack, short roar, quick decay"),
            new Spec(Spread, false, 1.0f,
                "flames jump to a neighbouring surface",
                "a crackling flare-up, lighter and faster than ignition"),
            new Spec(Extinguish, false, 1.0f,
                "a burning patch is snuffed out into a hiss of steam",
                "a relieving hiss that fades - no impact, all release"),
            new Spec(Reignite, false, 1.0f,
                "a smouldering patch the trainee walked away from flares back up",
                "an unwelcome double-pulse: a warning thud then a rising crackle"),
            new Spec(SmoulderWarning, false, 1.0f,
                "a knocked-down patch is smouldering, unattended, and could flare back",
                "a subtle, insistent ember pulse - a gentle nudge, not an alarm"),
            new Spec(StageComplete, false, 1.0f,
                "every fire in this stage is out; the room falls quiet",
                "a firm, settling all-clear: two soft confirming taps then a long calm fade"),
        };

        public static bool TryGet(string name, out Spec spec)
        {
            for (int i = 0; i < All.Length; i++)
            {
                if (string.Equals(All[i].Name, name, StringComparison.Ordinal))
                {
                    spec = All[i];
                    return true;
                }
            }
            spec = default;
            return false;
        }

        public static bool IsLoop(string name) => TryGet(name, out var spec) && spec.Loop;

        /// <summary>The prompt used for a role when no server (or no Gemini) is available.</summary>
        public static string OfflinePrompt(Spec spec, string surfaces)
        {
            string where = string.IsNullOrEmpty(surfaces) ? "unknown surfaces" : surfaces.ToLowerInvariant();
            return $"A vibrotactile haptic effect for a VR fire-extinguisher drill in a room with {where}: " +
                   $"{spec.What}. It should feel {spec.ShouldFeel}.";
        }
    }
}
