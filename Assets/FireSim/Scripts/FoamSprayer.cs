using UnityEngine;

namespace FireSim
{
    /// <summary>
    /// Lives on the foam particle system that sits at the tip of the (right-hand) fire
    /// extinguisher. While the right index trigger is held it sprays foam particles and
    /// vibrates the right controller. The particle system has World collisions with
    /// "Send Collision Messages" enabled so <see cref="FireSpot"/> patches can detect hits.
    ///
    /// The presence of this component on the particle system's GameObject is what
    /// <see cref="FireSpot.OnParticleCollision"/> uses to recognise foam.
    ///
    /// Visually the spray is three systems. The jet on this GameObject is the one that collides
    /// and does the extinguishing: velocity-stretched blobs shaded as lit spheres, so it reads as
    /// a stream of liquid foam near the nozzle and as clumps once it slows. Two children dress it:
    /// a mist of fine spray around the jet, and "splats" - a collision sub-emitter that leaves foam
    /// sitting on whatever the jet hits, slumping and dissolving over a few seconds, which is what
    /// makes the foam look like it is smothering the fire rather than passing through it.
    /// </summary>
    [RequireComponent(typeof(ParticleSystem))]
    public class FoamSprayer : MonoBehaviour
    {
        [Tooltip("How far the trigger must be pulled (0-1) before foam starts spraying.")]
        public float triggerThreshold = 0.15f;
        [Tooltip("Foam particles emitted per second while spraying.")]
        public float emissionRate = 300f;
        [Tooltip("Fine-spray particles per second while spraying (visual only, no collisions).")]
        public float mistRate = 110f;
        [Tooltip("Chance that a jet particle hitting a surface leaves a foam splat behind.")]
        [Range(0f, 1f)] public float splatChance = 0.22f;
        public float hapticFrequency = 0.6f;
        public float hapticMaxAmplitude = 0.7f;
        [Tooltip("When false, the built-in scalar rumble is skipped so an external system (e.g. the " +
                 "AI haptics pipeline) can drive haptics via the spray events instead.")]
        public bool useBuiltInHaptics = true;

        /// <summary>
        /// Optional replacement for the Quest controller's analog trigger, e.g. the ESP32 custom
        /// controller's force sensor + reed switch (see HapticAI.Esp.EspTriggerSource). When set,
        /// this is read instead of OVRInput every frame; when null, behaviour is unchanged.
        /// </summary>
        public IExtinguisherTriggerSource TriggerSource { get; set; }

        /// <summary>
        /// Whether the trigger does anything. The session disarms the extinguisher outside a run so
        /// the user can't hose the room while the briefing is still up.
        /// </summary>
        public bool Armed { get; set; }

        public bool IsSpraying => _spraying;

        /// <summary>
        /// The nozzle: this component's own transform, whose +Z is the spray direction. Exposed so
        /// <see cref="SurfaceFireManager"/> can grade aim and sweep without knowing the hierarchy.
        /// </summary>
        public Transform Nozzle => transform;

        /// <summary>Raised on the frame spraying starts, with the trigger value (0-1).</summary>
        public event System.Action<float> SprayStarted;
        /// <summary>Raised on the frame spraying stops.</summary>
        public event System.Action SprayStopped;
        /// <summary>
        /// Raised every frame while foam is spraying, with the current trigger value (0-1). The AI
        /// haptics path subscribes to this to track spray force; static mode ignores it.
        /// </summary>
        public event System.Action<float> Spraying;
        /// <summary>
        /// The built-in rumble's amplitude (0-1) each time it is set, including 0 when it stops, so
        /// an external actuator can mirror the static condition. Silent while
        /// <see cref="useBuiltInHaptics"/> is off.
        /// </summary>
        public event System.Action<float> BuiltInVibration;

        static readonly Color FoamWhite = new Color(0.98f, 0.98f, 0.95f, 0.96f);
        static readonly Color FoamCream = new Color(0.93f, 0.94f, 0.96f, 0.92f);

        ParticleSystem _foam;
        ParticleSystem _mist;
        ParticleSystem _splats;
        ParticleSystem.EmissionModule _emission;
        ParticleSystem.EmissionModule _mistEmission;
        bool _spraying;

        public void Configure()
        {
            _foam = GetComponent<ParticleSystem>();
            _foam.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            ConfigureJet();
            BuildMist();
            BuildSplats();

            _foam.Play();
        }

        // --- The jet ------------------------------------------------------------------------

        void ConfigureJet()
        {
            var main = _foam.main;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.9f, 1.5f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(5.5f, 8.5f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.03f, 0.065f);
            main.startRotation = new ParticleSystem.MinMaxCurve(-3.14f, 3.14f);
            main.gravityModifier = 0.6f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 1500;
            main.startColor = new ParticleSystem.MinMaxGradient(FoamWhite, FoamCream);

            _emission = _foam.emission;
            _emission.rateOverTime = 0f; // driven by the trigger in Update.

            // Narrow cone firing out of the nozzle (local +Z).
            var shape = _foam.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 9f;
            shape.radius = 0.016f;
            shape.radiusThickness = 1f;

            var collision = _foam.collision;
            collision.enabled = true;
            collision.type = ParticleSystemCollisionType.World;
            collision.mode = ParticleSystemCollisionMode.Collision3D;
            collision.sendCollisionMessages = true; // required for FireSpot.OnParticleCollision.
            collision.dampen = 0.55f;
            collision.bounce = 0.05f;
            collision.lifetimeLoss = 0.3f;
            collision.quality = ParticleSystemCollisionQuality.Medium;

            // Foam expands as it leaves the nozzle, then holds; only the very end fades.
            var size = _foam.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f,
                new AnimationCurve(new Keyframe(0f, 0.7f), new Keyframe(0.25f, 1.2f), new Keyframe(1f, 1.1f)));

            var colourOverLifetime = _foam.colorOverLifetime;
            colourOverLifetime.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 0.75f), new GradientAlphaKey(0f, 1f) });
            colourOverLifetime.color = new ParticleSystem.MinMaxGradient(grad);

            // A little turbulence so the cone breaks up into a ragged stream rather than a tube.
            var noise = _foam.noise;
            noise.enabled = true;
            noise.strength = new ParticleSystem.MinMaxCurve(0.09f);
            noise.frequency = 1.6f;
            noise.scrollSpeed = 1.2f;
            noise.damping = true;
            noise.quality = ParticleSystemNoiseQuality.Low;

            var renderer = _foam.GetComponent<ParticleSystemRenderer>();
            FireSimVisuals.ApplyParticleMaterial(renderer, FireSimVisuals.FoamMaterial);
            // Stretched along velocity: long while it flies, round once it has landed and slowed.
            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.lengthScale = 1.2f;
            renderer.velocityScale = 0.02f;
            // Foam draws over flames: it is in front of what it is smothering.
            renderer.sortingFudge = -9f;
        }

        // --- Dressing -----------------------------------------------------------------------

        ParticleSystem NewChild(string name, Material material, int maxParticles, float sortingFudge)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.loop = true;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = maxParticles;

            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            FireSimVisuals.ApplyParticleMaterial(renderer, material);
            renderer.sortingFudge = sortingFudge;
            return ps;
        }

        /// <summary>Fine spray around the jet: fast, faint, short-lived puffs that swell and vanish.</summary>
        void BuildMist()
        {
            _mist = NewChild("Mist", FireSimVisuals.SmokeMaterial, 120, -8f);

            var main = _mist.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.35f, 0.75f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(2.5f, 4.5f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.1f);
            main.startRotation = new ParticleSystem.MinMaxCurve(-3.14f, 3.14f);
            main.gravityModifier = 0.12f;
            main.startColor = new Color(0.95f, 0.96f, 1f, 0.26f);

            _mistEmission = _mist.emission;
            _mistEmission.rateOverTime = 0f;

            var shape = _mist.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 22f;
            shape.radius = 0.02f;
            shape.radiusThickness = 1f;

            var colour = _mist.colorOverLifetime;
            colour.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.6f, 0.4f), new GradientAlphaKey(0f, 1f) });
            colour.color = new ParticleSystem.MinMaxGradient(grad);

            var size = _mist.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f,
                new AnimationCurve(new Keyframe(0f, 0.6f), new Keyframe(1f, 2.4f)));

            _mist.Play();
        }

        /// <summary>
        /// Foam that stays where the jet lands. Spawned by the jet's collision sub-emitter with no
        /// inherited velocity, so each splat sits on the surface, swells for a moment, then slumps
        /// and dissolves. No collisions of its own: it must not be mistaken for foam by the patches.
        /// </summary>
        void BuildSplats()
        {
            _splats = NewChild("Splats", FireSimVisuals.FoamMaterial, 400, -8.5f);

            var main = _splats.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(3.0f, 6.0f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.11f);
            main.startRotation = new ParticleSystem.MinMaxCurve(-3.14f, 3.14f);
            main.gravityModifier = 0f;
            main.startColor = new ParticleSystem.MinMaxGradient(FoamWhite, FoamCream);

            var emission = _splats.emission;
            emission.rateOverTime = 0f;   // only ever spawned by the jet's collisions.

            var shape = _splats.shape;
            shape.enabled = false;

            var colour = _splats.colorOverLifetime;
            colour.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(new Color(0.9f, 0.92f, 0.95f), 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 0.65f), new GradientAlphaKey(0f, 1f) });
            colour.color = new ParticleSystem.MinMaxGradient(grad);

            var size = _splats.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f,
                new AnimationCurve(new Keyframe(0f, 0.5f), new Keyframe(0.15f, 1f), new Keyframe(0.7f, 0.9f), new Keyframe(1f, 0.25f)));

            _splats.Play();

            var subEmitters = _foam.subEmitters;
            subEmitters.enabled = true;
            subEmitters.AddSubEmitter(_splats, ParticleSystemSubEmitterType.Collision,
                ParticleSystemSubEmitterProperties.InheritNothing, splatChance);
        }

        // --- Input --------------------------------------------------------------------------

        void Update()
        {
            float trigger = TriggerSource != null && TriggerSource.TryGetTrigger(out float external)
                ? external
                : OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, OVRInput.Controller.RTouch);
            bool wantSpray = Armed && trigger >= triggerThreshold;

            if (wantSpray)
            {
                _emission.rateOverTime = emissionRate;
                if (_mist != null) _mistEmission.rateOverTime = mistRate;
                if (useBuiltInHaptics)
                {
                    // Vibration intensity tracks how hard the trigger is pulled.
                    float amplitude = Mathf.Lerp(0.15f, hapticMaxAmplitude, Mathf.InverseLerp(triggerThreshold, 1f, trigger));
                    OVRInput.SetControllerVibration(hapticFrequency, amplitude, OVRInput.Controller.RTouch);
                    BuiltInVibration?.Invoke(amplitude);
                }
                if (!_spraying)
                {
                    _spraying = true;
                    SprayStarted?.Invoke(trigger);
                }
                Spraying?.Invoke(trigger);
            }
            else if (_spraying)
            {
                StopSpraying();
            }
        }

        void StopSpraying()
        {
            _emission.rateOverTime = 0f;
            if (_mist != null) _mistEmission.rateOverTime = 0f;
            if (useBuiltInHaptics)
            {
                OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.RTouch);
                BuiltInVibration?.Invoke(0f);
            }
            _spraying = false;
            SprayStopped?.Invoke();
        }

        void OnDisable()
        {
            if (_spraying) StopSpraying();
        }
    }
}
