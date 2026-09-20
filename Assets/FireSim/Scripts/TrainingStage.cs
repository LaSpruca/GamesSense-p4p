namespace FireSim
{
    /// <summary>
    /// One stage of the drill. Plain data, immutable after construction: every field is readonly and
    /// set once by the constructor, so a stage can be handed around (session, manager, haptics,
    /// LLM scene description) without anyone being able to quietly retune it mid-run.
    ///
    /// Difficulty is expressed through four independent dials rather than one "hardness" number,
    /// because each maps to a distinct PASS habit: aim accuracy (Aim at the base), spreading
    /// (commit to one patch), sweep sectors (Sweep), and re-ignition (watch the embers).
    /// </summary>
    public sealed class TrainingStage
    {
        /// <summary>1-based stage number.</summary>
        public readonly int Index;
        /// <summary>Short name, e.g. "First flames".</summary>
        public readonly string Name;
        /// <summary>One line shown in the briefing title area.</summary>
        public readonly string Objective;
        /// <summary>Bullet lines for the briefing body (2-4 short lines).</summary>
        public readonly string[] TechniqueNotes;
        /// <summary>"forgiving" | "moderate" | "tight" | "strict" - for a scene description sent to an LLM.</summary>
        public readonly string AimLabel;

        // Fire
        /// <summary>Fires lit at the start of the stage (1..3).</summary>
        public readonly int InitialFires;
        public readonly bool SpreadEnabled;
        /// <summary>Seconds of grace after ignition before fire can spread at all.</summary>
        public readonly float FirstSpreadDelay;
        /// <summary>Seconds between spread attempts.</summary>
        public readonly float SpreadInterval;
        /// <summary>Chance (0-1) that a given eligible patch spreads on each tick.</summary>
        public readonly float SpreadChance;
        /// <summary>Hard cap on new patches per tick.</summary>
        public readonly int MaxSpreadsPerTick;
        /// <summary>Upper bound on simultaneous patches (burning or smouldering).</summary>
        public readonly int MaxSpots;

        // Aim accuracy
        /// <summary>Multiplier on the capsule collider radius (1.8 = very forgiving). Visuals are unchanged.</summary>
        public readonly float HitRadiusScale;
        /// <summary>
        /// Fraction (0..1) of the flame height, from the base up, that counts as a FULL-damage hit.
        /// 1.0 means anywhere on the column is as good as the base.
        /// </summary>
        public readonly float BaseBandFraction;
        /// <summary>
        /// Damage multiplier at the very top of the column. Falloff runs from the top of the base
        /// band to the top of the column.
        /// </summary>
        public readonly float TopOfFlameDamageScale;

        // Sweep
        /// <summary>1 = no sweep needed; 3 = base split into 3 lateral sectors that must each be doused.</summary>
        public readonly int SweepSectors;

        // Re-ignition
        public readonly bool ReigniteEnabled;
        /// <summary>Seconds of foam a smouldering patch needs before it is out for good.</summary>
        public readonly float SoakSeconds;
        /// <summary>Seconds without foam before a smouldering patch decides whether to flare back.</summary>
        public readonly float ReigniteDelay;
        /// <summary>Probability (0-1) that a neglected smouldering patch re-ignites rather than dying.</summary>
        public readonly float ReigniteChance;
        /// <summary>Health a re-ignited patch comes back with, as a fraction of max.</summary>
        public readonly float ReigniteHealthFraction;

        // Recovery (health regain when foam stops)
        /// <summary>Seconds without foam before a knocked-down patch starts climbing back.</summary>
        public readonly float RecoveryDelay;
        /// <summary>Health per second regained once recovery starts.</summary>
        public readonly float RecoveryRate;

        /// <summary>
        /// Named-argument constructor. The spread and re-ignition groups default to "off" so the
        /// easy stages only spell out what they actually use.
        /// </summary>
        public TrainingStage(
            int index, string name, string objective, string[] techniqueNotes, string aimLabel,
            int initialFires,
            float hitRadiusScale, float baseBandFraction, float topOfFlameDamageScale,
            int sweepSectors,
            float recoveryDelay, float recoveryRate,
            bool spreadEnabled = false, float firstSpreadDelay = 0f, float spreadInterval = 0f,
            float spreadChance = 0f, int maxSpreadsPerTick = 0, int maxSpots = 1,
            bool reigniteEnabled = false, float soakSeconds = 0f, float reigniteDelay = 0f,
            float reigniteChance = 0f, float reigniteHealthFraction = 0f)
        {
            Index = index;
            Name = name;
            Objective = objective;
            TechniqueNotes = techniqueNotes;
            AimLabel = aimLabel;
            InitialFires = initialFires;
            SpreadEnabled = spreadEnabled;
            FirstSpreadDelay = firstSpreadDelay;
            SpreadInterval = spreadInterval;
            SpreadChance = spreadChance;
            MaxSpreadsPerTick = maxSpreadsPerTick;
            MaxSpots = maxSpots;
            HitRadiusScale = hitRadiusScale;
            BaseBandFraction = baseBandFraction;
            TopOfFlameDamageScale = topOfFlameDamageScale;
            SweepSectors = sweepSectors;
            ReigniteEnabled = reigniteEnabled;
            SoakSeconds = soakSeconds;
            ReigniteDelay = reigniteDelay;
            ReigniteChance = reigniteChance;
            ReigniteHealthFraction = reigniteHealthFraction;
            RecoveryDelay = recoveryDelay;
            RecoveryRate = recoveryRate;
        }
    }

    /// <summary>
    /// The five stages of the drill, in order. Each one adds a single new demand on top of the last
    /// so a trainee who fails can tell exactly which habit let them down.
    ///
    /// Numbers are tuned against the manager defaults (spotRadius 0.14, flameHeight 0.5,
    /// spotHealth 100, foamDamagePerParticle 0.45): a well-aimed spray lands 100-180 particles/s,
    /// so a base hit kills a fresh patch in ~1.5-2 s and recovery rates stay well below that.
    /// </summary>
    public static class TrainingStages
    {
        public static readonly TrainingStage[] All =
        {
            new TrainingStage(
                index: 1, name: "First flames",
                objective: "Put out one small fire.",
                techniqueNotes: new[]
                {
                    "Hold the right trigger to spray.",
                    "Point the nozzle at the fire.",
                    "Keep spraying until the flames are out.",
                },
                aimLabel: "forgiving",
                initialFires: 1,
                hitRadiusScale: 1.8f, baseBandFraction: 1.0f, topOfFlameDamageScale: 1.0f,
                sweepSectors: 1,
                recoveryDelay: 2.0f, recoveryRate: 6f),

            new TrainingStage(
                index: 2, name: "Aim at the base",
                objective: "Aim low - foam on the flames does little.",
                techniqueNotes: new[]
                {
                    "Aim at the BASE of the fire, where it meets the surface.",
                    "Foam that lands high on the flames barely counts.",
                    "The fire will start to spread if you take too long.",
                },
                aimLabel: "moderate",
                initialFires: 1,
                hitRadiusScale: 1.3f, baseBandFraction: 0.55f, topOfFlameDamageScale: 0.4f,
                sweepSectors: 1,
                recoveryDelay: 1.5f, recoveryRate: 10f,
                spreadEnabled: true, firstSpreadDelay: 8f, spreadInterval: 3f,
                spreadChance: 0.35f, maxSpreadsPerTick: 1, maxSpots: 4),

            new TrainingStage(
                index: 3, name: "Sweep side to side",
                objective: "Sweep the base from side to side.",
                techniqueNotes: new[]
                {
                    "Keep aiming at the base.",
                    "Sweep the nozzle left and right across the whole patch.",
                    "Holding still on one point leaves the edges burning.",
                },
                aimLabel: "tight",
                initialFires: 1,
                hitRadiusScale: 1.1f, baseBandFraction: 0.4f, topOfFlameDamageScale: 0.2f,
                sweepSectors: 3,
                recoveryDelay: 1.5f, recoveryRate: 12f,
                spreadEnabled: true, firstSpreadDelay: 8f, spreadInterval: 2.5f,
                spreadChance: 0.35f, maxSpreadsPerTick: 1, maxSpots: 6),

            new TrainingStage(
                index: 4, name: "Watch for re-ignition",
                objective: "Knocked-down fire can flare back - soak it.",
                techniqueNotes: new[]
                {
                    "When the flames drop, the patch is only smouldering.",
                    "Keep soaking the embers for a couple of seconds.",
                    "Walk away too soon and it will flare up again.",
                },
                aimLabel: "strict",
                initialFires: 1,
                hitRadiusScale: 1.0f, baseBandFraction: 0.3f, topOfFlameDamageScale: 0.12f,
                sweepSectors: 3,
                recoveryDelay: 1.5f, recoveryRate: 14f,
                spreadEnabled: true, firstSpreadDelay: 8f, spreadInterval: 2.5f,
                spreadChance: 0.35f, maxSpreadsPerTick: 1, maxSpots: 6,
                reigniteEnabled: true, soakSeconds: 1.5f, reigniteDelay: 2.5f,
                reigniteChance: 0.85f, reigniteHealthFraction: 0.5f),

            new TrainingStage(
                index: 5, name: "Full room",
                objective: "Two fires, fast spread. Use everything.",
                techniqueNotes: new[]
                {
                    "Pull, Aim at the base, Squeeze, Sweep.",
                    "Finish one patch before moving to the next.",
                    "Soak every patch until it stops smouldering.",
                },
                aimLabel: "strict",
                initialFires: 2,
                hitRadiusScale: 1.0f, baseBandFraction: 0.2f, topOfFlameDamageScale: 0.05f,
                sweepSectors: 3,
                recoveryDelay: 1.5f, recoveryRate: 14f,
                spreadEnabled: true, firstSpreadDelay: 4f, spreadInterval: 1.75f,
                spreadChance: 0.55f, maxSpreadsPerTick: 2, maxSpots: 12,
                reigniteEnabled: true, soakSeconds: 2f, reigniteDelay: 2f,
                reigniteChance: 0.9f, reigniteHealthFraction: 0.6f),
        };

        public static int Count => All.Length;

        /// <summary>Stage by 1-based index. Out-of-range indices clamp to the first/last stage.</summary>
        public static TrainingStage Get(int index)
        {
            if (index < 1) index = 1;
            if (index > All.Length) index = All.Length;
            return All[index - 1];
        }
    }
}
