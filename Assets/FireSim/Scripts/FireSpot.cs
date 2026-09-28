using System.Collections.Generic;
using UnityEngine;
using Meta.XR.MRUtilityKit;

namespace FireSim
{
    /// <summary>
    /// Tuning for one burning patch. Passed as a struct because the manager owns every one of these
    /// values (most of them straight off the current <see cref="TrainingStage"/>) and the
    /// alternative is a seventeen-argument Initialise call.
    /// </summary>
    public struct FireSpotSettings
    {
        /// <summary>Radius of the burning patch on the surface, in metres. Visual size.</summary>
        public float Radius;
        /// <summary>How far the flame column (and its collider) extends along the surface normal.</summary>
        public float FlameHeight;
        public float Health;
        /// <summary>Health removed by one foam particle striking the very base of the flames.</summary>
        public float FoamDamagePerParticle;
        /// <summary>
        /// Damage multiplier for a particle striking the TOP of the flame column rather than the
        /// base. This is what makes aiming matter - see <see cref="FireSpot.HitQuality"/>.
        /// </summary>
        public float TopOfFlameDamageScale;
        /// <summary>Seconds without foam before a partially-doused patch starts recovering.</summary>
        public float RecoveryDelay;
        /// <summary>Health regained per second once recovery kicks in.</summary>
        public float RecoveryRate;

        /// <summary>Multiplier on the collider radius only. Easy stages are forgiving about aim.</summary>
        public float HitRadiusScale;
        /// <summary>Fraction of the flame height, from the base up, that counts as a full-damage hit.</summary>
        public float BaseBandFraction;
        /// <summary>1 = single pool of health; N > 1 = N lateral sectors that must each be doused.</summary>
        public int SweepSectors;

        public bool ReigniteEnabled;
        /// <summary>Seconds of foam a smouldering patch needs before it is out for good.</summary>
        public float SoakSeconds;
        /// <summary>Seconds without foam before a smouldering patch decides whether to flare back.</summary>
        public float ReigniteDelay;
        /// <summary>Probability (0-1) that a neglected smouldering patch re-ignites.</summary>
        public float ReigniteChance;
        /// <summary>Health a re-ignited patch comes back with, as a fraction of max.</summary>
        public float ReigniteHealthFraction;
    }

    /// <summary>
    /// A single burning patch sitting on a detected surface. It renders the fire, holds "health"
    /// that foam reduces, and exposes its position/normal/anchor so the
    /// <see cref="SurfaceFireManager"/> can spread fire from it to nearby surfaces.
    ///
    /// Foam reaches a spot through Unity's particle collision messages: the foam particle
    /// system has "Send Collision Messages" enabled, and this component lives on the same
    /// GameObject as a (non-trigger) collider, so <see cref="OnParticleCollision"/> fires
    /// once per frame with every foam particle that hit this patch.
    ///
    /// Four things make a patch require technique rather than a one-second blast, each switched
    /// on by a later training stage:
    ///   * Aim at the base. The collider is a capsule spanning the whole flame column, and damage
    ///     falls off with how high up the column a particle lands - the real-world "aim at the
    ///     base of the fire" rule from PASS extinguisher training.
    ///   * Recovery. A patch you stop hosing before it dies climbs back up, so sweeping across
    ///     every fire in the room without finishing any of them gets you nowhere.
    ///   * Sectors. The base is split laterally into N pools of health; hosing one point kills
    ///     that sector and only trickles into the rest, so you must sweep.
    ///   * Re-ignition. At zero health the patch smoulders instead of dying. Soak it and it goes
    ///     out; walk away and it will most likely flare back.
    ///
    /// The picture is layered from several cheap parts so it holds up in passthrough on a headset:
    /// flame tongues (a procedural erosion shader on stretched billboards) over a hot core, smoke
    /// rising off the top, the odd spark, a scorch mark with glowing embers on the surface itself,
    /// steam where foam lands, and the flames' light thrown onto the real room through
    /// <see cref="FireLightField"/>. Every layer scales with health, so a patch being knocked
    /// down visibly weakens and one left alone visibly recovers.
    ///
    /// Lifecycle as the manager sees it: IsBurning -> (flames out: OnSpotExtinguished) ->
    /// [IsSmouldering: OnSpotSmouldering -> maybe OnSpotReignited -> back to IsBurning]* ->
    /// out for good: OnSpotOut -> destroyed.
    /// </summary>
    [RequireComponent(typeof(CapsuleCollider))]
    public class FireSpot : MonoBehaviour
    {
        // Emission rates at full health. Each flame particle is a whole tongue, so these are low.
        const float FlameRate = 26f;
        const float CoreRate = 10f;
        const float SmokeRate = 5f;
        const float SparkRate = 5f;
        const float SmoulderSmokeRate = 2.5f;
        const float EmberRate = 9f;

        const float MaxGlowIntensity = 1.6f;
        const float SmoulderGlowFraction = 0.16f;
        /// <summary>How far the light reaches across the real room, in metres.</summary>
        const float RoomLightRange = 1.7f;
        /// <summary>Seconds of burning after which the surface is fully sooted.</summary>
        const float FullCharSeconds = 14f;
        /// <summary>How long a released scorch mark lingers before it has faded away.</summary>
        const float ScorchFadeSeconds = 30f;
        /// <summary>Steam particles per foam hit, on average.</summary>
        const float SteamPerHit = 0.12f;

        /// <summary>A sector counts as "covered" if foam landed on it this recently.</summary>
        const float CoverageWindow = 0.6f;
        /// <summary>Share of damage on a dead sector that leaks into the live ones.</summary>
        const float SpillFraction = 0.25f;

        static readonly Color FlameLightColor = new Color(1f, 0.56f, 0.2f);
        static readonly Color EmberLightColor = new Color(1f, 0.38f, 0.1f);

        public MRUKAnchor Anchor { get; private set; }
        public Vector3 Position => transform.position;
        public Vector3 SurfaceNormal { get; private set; }
        /// <summary>Visual radius of the patch (the collider may be larger - see HitRadiusScale).</summary>
        public float Radius => _settings.Radius;
        /// <summary>Flames are up. The spread loop only grows fire from burning patches.</summary>
        public bool IsBurning { get; private set; }
        /// <summary>Flames are down but the patch is not yet out - it can still flare back.</summary>
        public bool IsSmouldering { get; private set; }
        /// <summary>Remaining health as 0..1 (sum over sectors). The manager gates spreading on this.</summary>
        public float HealthFraction
        {
            get
            {
                if (_maxHealth <= 0f || _sectorHealth == null) return 0f;
                float sum = 0f;
                for (int i = 0; i < _sectorHealth.Length; i++) sum += _sectorHealth[i];
                return Mathf.Clamp01(sum / _maxHealth);
            }
        }
        /// <summary>True while foam has landed on this patch in the last frame or two.</summary>
        public bool IsBeingDoused => Time.time - _lastFoamTime < 0.2f;
        /// <summary>
        /// Fraction of sectors foam has touched in the last 0.6 s. With one sector this is simply
        /// 1 while being hit; with three it only reaches 1 when the user is actually sweeping.
        /// </summary>
        public float Coverage
        {
            get
            {
                if (_sectorLastHitTime == null) return 0f;
                int hit = 0;
                for (int i = 0; i < _sectorLastHitTime.Length; i++)
                    if (Time.time - _sectorLastHitTime[i] < CoverageWindow) hit++;
                return (float)hit / _sectorLastHitTime.Length;
            }
        }

        SurfaceFireManager _manager;
        ParticleSystem _flames;
        ParticleSystem _core;
        ParticleSystem _smoke;
        ParticleSystem _sparks;
        ParticleSystem _steam;
        ParticleSystem _embers;
        ScorchMark _scorch;
        Light _glow;
        FireSpotSettings _settings;
        float _maxHealth;
        float _sectorMax;
        float[] _sectorHealth;
        float[] _sectorLastHitTime;
        float _lastFoamTime = -999f;
        float _soakSeconds;
        float _flickerSeed;
        float _burnSeconds;
        float _steamDebt;
        /// <summary>Current light output as a fraction of full, flicker included.</summary>
        float _lightLevel;
        bool _isOut;
        readonly List<ParticleCollisionEvent> _collisionEvents = new List<ParticleCollisionEvent>();

        public void Initialise(SurfaceFireManager manager, Vector3 position, Vector3 surfaceNormal,
            MRUKAnchor anchor, FireSpotSettings settings)
        {
            _manager = manager;
            Anchor = anchor;
            SurfaceNormal = surfaceNormal;
            _settings = settings;
            _maxHealth = settings.Health;
            _flickerSeed = Random.value * 100f;

            int sectors = Mathf.Max(1, settings.SweepSectors);
            _sectorMax = _maxHealth / sectors;
            _sectorHealth = new float[sectors];
            _sectorLastHitTime = new float[sectors];
            for (int i = 0; i < sectors; i++)
            {
                _sectorHealth[i] = _sectorMax;
                _sectorLastHitTime[i] = -999f;
            }

            IsBurning = true;
            IsSmouldering = false;
            _isOut = false;

            transform.position = position + surfaceNormal * 0.02f;
            // Orient so the flames rise away from the surface (handles walls, floors, ceilings).
            // Local +X (transform.right) is then a stable lateral axis in the surface plane, which
            // is what the sweep sectors are laid out along.
            transform.rotation = Quaternion.LookRotation(surfaceNormal);

            BuildCollider();
            _scorch = ScorchMark.Create(transform, settings.Radius);
            BuildFlames(settings.Radius, settings.FlameHeight);
            BuildCore(settings.Radius, settings.FlameHeight);
            BuildSmoke(settings.Radius, settings.FlameHeight);
            BuildSparks(settings.Radius);
            BuildSteam(settings.Radius);
            BuildGlow(settings.Radius);
            if (settings.ReigniteEnabled) BuildEmbers(settings.Radius);
            FireLightField.Register(this);

            // Pop in so spreading reads as growth rather than instant appearance.
            StartCoroutine(GrowIn());
        }

        /// <summary>
        /// A capsule spanning the flame column, not a sphere around the base. The height is what
        /// gives <see cref="HitQuality"/> something to grade: foam landing low scores near 1, foam
        /// landing on the tips of the flames scores near TopOfFlameDamageScale. The radius is
        /// scaled by the stage's HitRadiusScale so early stages accept a sloppier aim without the
        /// flames themselves looking any bigger.
        /// </summary>
        void BuildCollider()
        {
            var capsule = GetComponent<CapsuleCollider>();
            capsule.isTrigger = false;  // particle World collisions ignore triggers.
            capsule.direction = 2;      // local +Z, i.e. along the surface normal.
            capsule.radius = _settings.Radius * Mathf.Max(0.1f, _settings.HitRadiusScale);
            capsule.height = Mathf.Max(_settings.FlameHeight, capsule.radius * 2f);
            capsule.center = new Vector3(0f, 0f, capsule.height * 0.5f);
        }

        // --- Visual layers ------------------------------------------------------------------

        /// <summary>
        /// A stopped, looping, world-space particle system on a new child, with the shared
        /// FireSim material and vertex streams applied. Every layer starts from this.
        /// </summary>
        ParticleSystem NewLayer(string name, Material material, int maxParticles, float sortingFudge)
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

        /// <summary>
        /// The tongues. Tall billboards emitted from the base along the surface normal, then lifted
        /// by buoyancy (negative gravity) so a wall fire still licks upward, with a little noise so
        /// no two rise the same way. The shader does the flickering and erosion per pixel.
        /// </summary>
        void BuildFlames(float radius, float height)
        {
            _flames = NewLayer("Flames", FireSimVisuals.FlameMaterial, 80, -5f);

            var main = _flames.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.55f, 1.0f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(height * 0.6f, height * 1.3f);
            main.startSize3D = true;
            main.startSizeX = new ParticleSystem.MinMaxCurve(radius * 1.3f, radius * 2.0f);
            main.startSizeY = new ParticleSystem.MinMaxCurve(height * 0.7f, height * 1.0f);
            main.startSizeZ = new ParticleSystem.MinMaxCurve(1f);
            main.startRotation = new ParticleSystem.MinMaxCurve(-0.2f, 0.2f);
            main.gravityModifier = new ParticleSystem.MinMaxCurve(-0.22f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1f, 1f, 1f, 1f), new Color(1f, 0.9f, 0.8f, 1f));

            var emission = _flames.emission;
            emission.rateOverTime = FlameRate;

            var shape = _flames.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 12f;
            shape.radius = radius * 0.55f;
            shape.radiusThickness = 1f;

            var colour = _flames.colorOverLifetime;
            colour.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(1f, 1f, 1f), 0f),
                    new GradientColorKey(new Color(1f, 0.8f, 0.55f), 0.45f),
                    new GradientColorKey(new Color(0.9f, 0.35f, 0.15f), 0.85f),
                    new GradientColorKey(new Color(0.6f, 0.15f, 0.08f), 1f),
                },
                new[]
                {
                    new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.08f),
                    new GradientAlphaKey(1f, 0.6f), new GradientAlphaKey(0f, 1f),
                });
            colour.color = new ParticleSystem.MinMaxGradient(grad);

            var size = _flames.sizeOverLifetime;
            size.enabled = true;
            size.separateAxes = true;
            size.x = new ParticleSystem.MinMaxCurve(1f,
                new AnimationCurve(new Keyframe(0f, 0.7f), new Keyframe(0.3f, 1f), new Keyframe(1f, 0.45f)));
            size.y = new ParticleSystem.MinMaxCurve(1f,
                new AnimationCurve(new Keyframe(0f, 0.6f), new Keyframe(0.35f, 1f), new Keyframe(1f, 0.7f)));
            size.z = new ParticleSystem.MinMaxCurve(1f);

            var rotation = _flames.rotationOverLifetime;
            rotation.enabled = true;
            rotation.z = new ParticleSystem.MinMaxCurve(-0.35f, 0.35f);

            var noise = _flames.noise;
            noise.enabled = true;
            noise.strength = new ParticleSystem.MinMaxCurve(radius * 0.9f);
            noise.frequency = 0.55f;
            noise.scrollSpeed = 0.7f;
            noise.damping = true;
            noise.quality = ParticleSystemNoiseQuality.Low;

            _flames.Play();
        }

        /// <summary>The hot, near-white base of the fire: small, quick, tightly packed tongues.</summary>
        void BuildCore(float radius, float height)
        {
            _core = NewLayer("Core", FireSimVisuals.FlameMaterial, 30, -6f);

            var main = _core.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.3f, 0.55f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(height * 0.3f, height * 0.7f);
            main.startSize3D = true;
            main.startSizeX = new ParticleSystem.MinMaxCurve(radius * 0.8f, radius * 1.2f);
            main.startSizeY = new ParticleSystem.MinMaxCurve(height * 0.35f, height * 0.55f);
            main.startSizeZ = new ParticleSystem.MinMaxCurve(1f);
            main.startRotation = new ParticleSystem.MinMaxCurve(-0.15f, 0.15f);
            main.gravityModifier = new ParticleSystem.MinMaxCurve(-0.1f);
            main.startColor = new Color(1f, 0.98f, 0.9f, 1f);

            var emission = _core.emission;
            emission.rateOverTime = CoreRate;

            var shape = _core.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 8f;
            shape.radius = radius * 0.35f;
            shape.radiusThickness = 1f;

            var colour = _core.colorOverLifetime;
            colour.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(new Color(1f, 0.85f, 0.6f), 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.15f), new GradientAlphaKey(0f, 1f) });
            colour.color = new ParticleSystem.MinMaxGradient(grad);

            _core.Play();
        }

        /// <summary>
        /// Smoke off the top of the flames: slow, dark, swelling puffs that thin out as they rise.
        /// In passthrough this is the layer that most sells the fire, because it darkens the real
        /// room behind it. Recoloured pale grey while smouldering.
        /// </summary>
        void BuildSmoke(float radius, float height)
        {
            _smoke = NewLayer("Smoke", FireSimVisuals.SmokeMaterial, 40, 0f);

            var main = _smoke.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.8f, 3.0f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.3f, 0.55f);
            main.startSize = new ParticleSystem.MinMaxCurve(radius * 1.3f, radius * 2.1f);
            main.startRotation = new ParticleSystem.MinMaxCurve(-3.14f, 3.14f);
            main.gravityModifier = new ParticleSystem.MinMaxCurve(-0.1f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(0.13f, 0.12f, 0.12f, 0.5f), new Color(0.22f, 0.2f, 0.19f, 0.4f));

            var emission = _smoke.emission;
            emission.rateOverTime = SmokeRate;

            var shape = _smoke.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 18f;
            shape.radius = radius * 0.4f;
            shape.radiusThickness = 1f;
            shape.position = new Vector3(0f, 0f, height * 0.6f);

            var colour = _smoke.colorOverLifetime;
            colour.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[]
                {
                    new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.15f),
                    new GradientAlphaKey(0.7f, 0.6f), new GradientAlphaKey(0f, 1f),
                });
            colour.color = new ParticleSystem.MinMaxGradient(grad);

            var size = _smoke.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f,
                new AnimationCurve(new Keyframe(0f, 0.5f), new Keyframe(1f, 1.7f)));

            var rotation = _smoke.rotationOverLifetime;
            rotation.enabled = true;
            rotation.z = new ParticleSystem.MinMaxCurve(-0.3f, 0.3f);

            var noise = _smoke.noise;
            noise.enabled = true;
            noise.strength = new ParticleSystem.MinMaxCurve(0.12f);
            noise.frequency = 0.35f;
            noise.scrollSpeed = 0.3f;
            noise.damping = true;
            noise.quality = ParticleSystemNoiseQuality.Low;

            _smoke.Play();
        }

        /// <summary>The occasional spark carried up out of the flames on a wandering path.</summary>
        void BuildSparks(float radius)
        {
            _sparks = NewLayer("Sparks", FireSimVisuals.GlowMaterial, 24, -7f);

            var main = _sparks.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.6f, 1.4f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.5f, 1.3f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.006f, 0.014f);
            main.gravityModifier = new ParticleSystem.MinMaxCurve(-0.05f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1f, 0.75f, 0.3f, 1f), new Color(1f, 0.5f, 0.15f, 1f));

            var emission = _sparks.emission;
            emission.rateOverTime = SparkRate;

            var shape = _sparks.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 20f;
            shape.radius = radius * 0.5f;
            shape.radiusThickness = 1f;

            var colour = _sparks.colorOverLifetime;
            colour.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(new Color(1f, 0.9f, 0.6f), 0f), new GradientColorKey(new Color(1f, 0.3f, 0.05f), 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 0.7f), new GradientAlphaKey(0f, 1f) });
            colour.color = new ParticleSystem.MinMaxGradient(grad);

            var noise = _sparks.noise;
            noise.enabled = true;
            noise.strength = new ParticleSystem.MinMaxCurve(0.35f);
            noise.frequency = 1.2f;
            noise.scrollSpeed = 1f;
            noise.damping = true;
            noise.quality = ParticleSystemNoiseQuality.Low;

            var renderer = _sparks.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.velocityScale = 0.06f;
            renderer.lengthScale = 1.5f;

            _sparks.Play();
        }

        /// <summary>Steam where foam meets the fire. Emitted per hit from OnParticleCollision.</summary>
        void BuildSteam(float radius)
        {
            _steam = NewLayer("Steam", FireSimVisuals.SmokeMaterial, 40, -1f);

            var main = _steam.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.9f, 1.6f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.45f, 0.9f);
            main.startSize = new ParticleSystem.MinMaxCurve(radius * 0.9f, radius * 1.5f);
            main.startRotation = new ParticleSystem.MinMaxCurve(-3.14f, 3.14f);
            main.gravityModifier = new ParticleSystem.MinMaxCurve(-0.18f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(0.88f, 0.9f, 0.93f, 0.42f), new Color(0.75f, 0.77f, 0.8f, 0.35f));

            var emission = _steam.emission;
            emission.rateOverTime = 0f;   // driven by foam hits

            var shape = _steam.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 30f;
            shape.radius = radius * 0.6f;
            shape.radiusThickness = 1f;

            var colour = _steam.colorOverLifetime;
            colour.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.15f), new GradientAlphaKey(0f, 1f) });
            colour.color = new ParticleSystem.MinMaxGradient(grad);

            var size = _steam.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f,
                new AnimationCurve(new Keyframe(0f, 0.6f), new Keyframe(1f, 1.9f)));

            _steam.Play();
        }

        void BuildGlow(float radius)
        {
            var glowGo = new GameObject("Glow");
            glowGo.transform.SetParent(transform, false);
            glowGo.transform.localPosition = Vector3.forward * (radius * 1.2f);
            _glow = glowGo.AddComponent<Light>();
            _glow.type = LightType.Point;
            _glow.color = FlameLightColor;
            _glow.range = RoomLightRange;
            _glow.intensity = MaxGlowIntensity;
            _glow.shadows = LightShadows.None;
        }

        /// <summary>
        /// Embers lifting off a smouldering patch: tiny bright motes on a wandering rise. Built
        /// stopped and only played while smouldering - it is the tell that the patch is not finished.
        /// </summary>
        void BuildEmbers(float radius)
        {
            _embers = NewLayer("Embers", FireSimVisuals.GlowMaterial, 30, -7f);

            var main = _embers.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.2f, 2.4f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.05f, 0.18f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.006f, 0.014f);
            main.gravityModifier = new ParticleSystem.MinMaxCurve(-0.03f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1f, 0.45f, 0.1f, 1f), new Color(0.95f, 0.28f, 0.05f, 0.8f));

            var emission = _embers.emission;
            emission.rateOverTime = EmberRate;

            var shape = _embers.shape;
            shape.shapeType = ParticleSystemShapeType.Hemisphere;
            shape.radius = radius * 0.6f;

            var colour = _embers.colorOverLifetime;
            colour.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(new Color(1f, 0.55f, 0.15f), 0f), new GradientColorKey(new Color(0.45f, 0.08f, 0.02f), 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.2f), new GradientAlphaKey(0f, 1f) });
            colour.color = new ParticleSystem.MinMaxGradient(grad);

            var noise = _embers.noise;
            noise.enabled = true;
            noise.strength = new ParticleSystem.MinMaxCurve(0.12f);
            noise.frequency = 0.8f;
            noise.scrollSpeed = 0.6f;
            noise.damping = true;
            noise.quality = ParticleSystemNoiseQuality.Low;
        }

        System.Collections.IEnumerator GrowIn()
        {
            const float duration = 0.6f;
            float t = 0f;
            Vector3 target = transform.localScale;
            while (t < duration)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / duration);
                transform.localScale = target * Mathf.SmoothStep(0.2f, 1f, k);
                yield return null;
            }
            transform.localScale = target;
        }

        // --- Per-frame ----------------------------------------------------------------------

        void Update()
        {
            if (_isOut) return;
            UpdateAmbience();
            if (IsSmouldering) { UpdateSmouldering(); return; }
            if (!IsBurning) return;

            _burnSeconds += Time.deltaTime;
            if (Time.time - _lastFoamTime < _settings.RecoveryDelay) return;

            // Left alone, a knocked-down patch climbs back. This is what stops "spray everything
            // for half a second each" from being a winning strategy. Every sector regenerates,
            // so a dead sector you swept past comes back too.
            float regen = _settings.RecoveryRate * Time.deltaTime / _sectorHealth.Length;
            bool changed = false;
            for (int i = 0; i < _sectorHealth.Length; i++)
            {
                if (_sectorHealth[i] >= _sectorMax) continue;
                _sectorHealth[i] = Mathf.Min(_sectorMax, _sectorHealth[i] + regen);
                changed = true;
            }
            if (changed) RefreshVisuals();
        }

        /// <summary>
        /// The flicker: light, ember glow and soot, every frame in both live states. Two Perlin
        /// bands - a slow swell and a fast jitter - read as flame rather than a sine wave.
        /// </summary>
        void UpdateAmbience()
        {
            float t = Time.time;
            float flicker = 0.78f + 0.22f * Mathf.PerlinNoise(t * 6.5f, _flickerSeed)
                                  + 0.14f * (Mathf.PerlinNoise(t * 21f, _flickerSeed + 3.3f) - 0.5f);
            float charAmount = Mathf.Clamp01(0.2f + _burnSeconds / FullCharSeconds);

            if (IsBurning)
            {
                float frac = HealthFraction;
                _lightLevel = Mathf.Lerp(0.2f, 1f, frac) * flicker;
                if (_glow != null) { _glow.color = FlameLightColor; _glow.intensity = MaxGlowIntensity * _lightLevel; }
                if (_scorch != null) _scorch.SetState(charAmount, Mathf.Lerp(0.6f, 1.8f, frac) * flicker);
            }
            else if (IsSmouldering)
            {
                _lightLevel = SmoulderGlowFraction * flicker;
                if (_glow != null) { _glow.color = EmberLightColor; _glow.intensity = MaxGlowIntensity * _lightLevel; }
                // Brighter than the light suggests: the embers are the one cue that this is not done.
                if (_scorch != null) _scorch.SetState(charAmount, 1.1f * flicker);
            }
        }

        /// <summary>
        /// The smouldering decision. Foam accumulates soak time; enough of it and the patch is out
        /// for good. Go ReigniteDelay without foam and the patch rolls the dice: most of the time
        /// it flares back (the lesson), otherwise it dies quietly.
        /// </summary>
        void UpdateSmouldering()
        {
            if (IsBeingDoused)
            {
                _soakSeconds += Time.deltaTime;
                if (_soakSeconds >= _settings.SoakSeconds) { GoOut(); return; }
            }
            else if (Time.time - _lastFoamTime >= _settings.ReigniteDelay)
            {
                if (Random.value < _settings.ReigniteChance) Reignite();
                else GoOut();
            }
        }

        /// <summary>
        /// This patch as a light on the real room, for <see cref="FireLightField"/>. False once
        /// there is nothing glowing.
        /// </summary>
        public bool TryGetLight(out Vector3 position, out float range, out Color color)
        {
            if (_isOut || _glow == null || (!IsBurning && !IsSmouldering))
            {
                position = Vector3.zero; range = 0f; color = Color.black;
                return false;
            }
            position = _glow.transform.position;
            range = RoomLightRange * Mathf.Lerp(0.6f, 1f, _lightLevel);
            color = _glow.color * (_lightLevel * 1.1f);
            return true;
        }

        // --- Foam ---------------------------------------------------------------------------

        void OnParticleCollision(GameObject other)
        {
            if (_isOut || (!IsBurning && !IsSmouldering) || _manager == null) return;
            // Only foam puts the fire out; ignore any other particle systems.
            if (other.GetComponent<FoamSprayer>() == null) return;

            var foamPs = other.GetComponent<ParticleSystem>();
            if (foamPs == null) return;

            int hits = foamPs.GetCollisionEvents(gameObject, _collisionEvents);
            if (hits <= 0) return;

            _lastFoamTime = Time.time;

            float qualitySum = 0f;
            for (int i = 0; i < hits; i++)
            {
                Vector3 point = _collisionEvents[i].intersection;
                float quality = HitQuality(point);
                qualitySum += quality;

                int sector = SectorOf(point);
                _sectorLastHitTime[sector] = Time.time;
                // Smouldering patches take no damage - foam on them is soaking, tracked in
                // UpdateSmouldering via IsBeingDoused.
                if (IsBurning) ApplySectorDamage(sector, _settings.FoamDamagePerParticle * quality);
            }

            _manager.ReportFoamHit(this, hits, qualitySum / hits, IsSmouldering);
            EmitSteam(hits);

            if (IsBurning)
            {
                RefreshVisuals();
                if (AllSectorsDead()) OnHealthDepleted();
            }
        }

        /// <summary>Steam in proportion to the foam landing, a fraction of a puff per hit.</summary>
        void EmitSteam(int hits)
        {
            if (_steam == null) return;
            _steamDebt += hits * SteamPerHit;
            int n = Mathf.FloorToInt(_steamDebt);
            if (n <= 0) return;
            _steamDebt -= n;
            _steam.Emit(Mathf.Min(n, 4));
        }

        /// <summary>
        /// How much a foam particle striking <paramref name="worldPoint"/> counts for: 1 anywhere
        /// in the base band (the bottom BaseBandFraction of the column), then falling linearly to
        /// TopOfFlameDamageScale at the top of the column.
        ///
        /// The intersection point comes from the particle system's Medium collision quality, which
        /// approximates colliders with cached planes - so this estimates height up the column rather
        /// than giving an exact contact point. That is plenty for grading aim, and Medium keeps ~300
        /// foam particles affordable on-device where High quality (a raycast per particle per frame)
        /// would not be.
        /// </summary>
        float HitQuality(Vector3 worldPoint)
        {
            float height = Vector3.Dot(worldPoint - transform.position, SurfaceNormal);
            float flame = Mathf.Max(0.01f, _settings.FlameHeight);
            float bandTop = Mathf.Clamp01(_settings.BaseBandFraction) * flame;
            if (height <= bandTop) return 1f;

            float k = Mathf.Clamp01((height - bandTop) / Mathf.Max(0.01f, flame - bandTop));
            return Mathf.Lerp(1f, _settings.TopOfFlameDamageScale, k);
        }

        /// <summary>
        /// Which lateral sector a world point falls in. Sectors span the diameter along local +X;
        /// points outside it (possible because the collider can be wider than the visual radius)
        /// clamp to the nearest edge sector.
        /// </summary>
        int SectorOf(Vector3 worldPoint)
        {
            int n = _sectorHealth.Length;
            if (n <= 1) return 0;
            float localX = Vector3.Dot(worldPoint - transform.position, transform.right);
            float t = localX / (2f * Mathf.Max(0.01f, _settings.Radius)) + 0.5f;
            return Mathf.Clamp(Mathf.FloorToInt(t * n), 0, n - 1);
        }

        /// <summary>
        /// Damage lands on the hit sector. Once that sector is dead only a quarter of it leaks into
        /// the sectors that are still alive, so hosing one point forever is slow but not impossible
        /// - the drill should push you to sweep, not trap you.
        /// </summary>
        void ApplySectorDamage(int sector, float damage)
        {
            if (damage <= 0f) return;
            if (_sectorHealth[sector] > 0f)
            {
                _sectorHealth[sector] = Mathf.Max(0f, _sectorHealth[sector] - damage);
                return;
            }

            int alive = 0;
            for (int i = 0; i < _sectorHealth.Length; i++)
                if (_sectorHealth[i] > 0f) alive++;
            if (alive == 0) return;

            float share = damage * SpillFraction / alive;
            for (int i = 0; i < _sectorHealth.Length; i++)
                if (_sectorHealth[i] > 0f) _sectorHealth[i] = Mathf.Max(0f, _sectorHealth[i] - share);
        }

        bool AllSectorsDead()
        {
            for (int i = 0; i < _sectorHealth.Length; i++)
                if (_sectorHealth[i] > 0f) return false;
            return true;
        }

        /// <summary>Apply un-aimed damage spread evenly across every sector (scripted/test use).</summary>
        public void ApplyFoam(float amount)
        {
            if (!IsBurning || amount <= 0f) return;
            _lastFoamTime = Time.time;
            float per = amount / _sectorHealth.Length;
            for (int i = 0; i < _sectorHealth.Length; i++)
            {
                _sectorLastHitTime[i] = Time.time;
                _sectorHealth[i] = Mathf.Max(0f, _sectorHealth[i] - per);
            }

            RefreshVisuals();
            if (AllSectorsDead()) OnHealthDepleted();
        }

        /// <summary>
        /// Every layer weakens as the patch is doused and swells again as it recovers. A patch being
        /// knocked down also smokes harder - the flames are being smothered, not switched off.
        /// </summary>
        void RefreshVisuals()
        {
            float frac = HealthFraction;
            SetRate(_flames, Mathf.Lerp(5f, FlameRate, frac));
            SetRate(_core, Mathf.Lerp(1.5f, CoreRate, frac));
            SetRate(_sparks, Mathf.Lerp(0.5f, SparkRate, frac));
            float smoke = Mathf.Lerp(SmokeRate * 0.6f, SmokeRate, frac);
            if (IsBeingDoused) smoke += Mathf.Lerp(6f, 2f, frac);
            SetRate(_smoke, smoke);
        }

        static void SetRate(ParticleSystem ps, float rate)
        {
            if (ps == null) return;
            var emission = ps.emission;
            emission.rateOverTime = rate;
        }

        // --- State changes ------------------------------------------------------------------

        /// <summary>
        /// The flames-out moment. The manager hears OnSpotExtinguished either way; what happens
        /// next depends on the stage - die immediately, or smoulder and maybe come back.
        /// </summary>
        void OnHealthDepleted()
        {
            if (!IsBurning) return;
            IsBurning = false;
            StopEmitting(_flames);
            StopEmitting(_core);
            StopEmitting(_sparks);

            if (_manager != null) _manager.OnSpotExtinguished(this);

            if (_settings.ReigniteEnabled) EnterSmouldering();
            else GoOut();
        }

        void EnterSmouldering()
        {
            IsSmouldering = true;
            _soakSeconds = 0f;
            // The collider stays enabled on purpose: foam must still be able to land so the user
            // can soak the embers. The light drops to a dim flicker; the embers, the glowing scorch
            // and a thin pale smoke are the tells.
            if (_glow != null) _glow.enabled = true;
            if (_embers != null) _embers.Play();
            if (_smoke != null)
            {
                var main = _smoke.main;
                main.startColor = new ParticleSystem.MinMaxGradient(
                    new Color(0.55f, 0.55f, 0.56f, 0.3f), new Color(0.7f, 0.7f, 0.7f, 0.22f));
                SetRate(_smoke, SmoulderSmokeRate);
            }

            if (_manager != null) _manager.OnSpotSmouldering(this);
        }

        void Reignite()
        {
            IsSmouldering = false;
            IsBurning = true;
            StopEmitting(_embers);

            float per = Mathf.Clamp01(_settings.ReigniteHealthFraction) * _maxHealth / _sectorHealth.Length;
            for (int i = 0; i < _sectorHealth.Length; i++) _sectorHealth[i] = per;

            if (_smoke != null)
            {
                var main = _smoke.main;
                main.startColor = new ParticleSystem.MinMaxGradient(
                    new Color(0.13f, 0.12f, 0.12f, 0.5f), new Color(0.22f, 0.2f, 0.19f, 0.4f));
            }
            _flames.Play();
            if (_core != null) _core.Play();
            if (_sparks != null) _sparks.Play();
            if (_glow != null) _glow.enabled = true;
            RefreshVisuals();

            if (_manager != null) _manager.OnSpotReignited(this);
        }

        /// <summary>Out for good: nothing left that can burn. Removes itself once the steam clears.</summary>
        void GoOut()
        {
            if (_isOut) return;
            _isOut = true;
            IsBurning = false;
            IsSmouldering = false;

            StopEmitting(_flames);
            StopEmitting(_core);
            StopEmitting(_sparks);
            StopEmitting(_smoke);
            StopEmitting(_embers);
            if (_glow != null) _glow.enabled = false;
            GetComponent<CapsuleCollider>().enabled = false;
            FireLightField.Unregister(this);

            PlaySteamPuff();

            // The mark outlives the patch: hand it to the fire root to fade on its own.
            if (_scorch != null)
            {
                _scorch.Release(transform.parent, ScorchFadeSeconds);
                _scorch = null;
            }

            if (_manager != null) _manager.OnSpotOut(this);
            // Leave the steam and the last of the smoke to finish, then remove the object entirely.
            Destroy(gameObject, 4f);
        }

        void OnDestroy()
        {
            FireLightField.Unregister(this);
        }

        static void StopEmitting(ParticleSystem ps)
        {
            if (ps != null) ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        }

        /// <summary>A last, larger gout of steam as the fire is quenched.</summary>
        void PlaySteamPuff()
        {
            if (_steam == null) return;
            var main = _steam.main;
            main.startSize = new ParticleSystem.MinMaxCurve(_settings.Radius * 1.4f, _settings.Radius * 2.2f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.5f, 1.1f);
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.2f, 2.0f);
            _steam.Emit(22);
        }
    }
}
