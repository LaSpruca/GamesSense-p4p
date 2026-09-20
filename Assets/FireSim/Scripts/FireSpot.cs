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
    /// A single burning patch sitting on a detected surface. It renders flame particles,
    /// holds "health" that foam reduces, and exposes its position/normal/anchor so the
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
    /// Lifecycle as the manager sees it: IsBurning -> (flames out: OnSpotExtinguished) ->
    /// [IsSmouldering: OnSpotSmouldering -> maybe OnSpotReignited -> back to IsBurning]* ->
    /// out for good: OnSpotOut -> destroyed.
    /// </summary>
    [RequireComponent(typeof(CapsuleCollider))]
    public class FireSpot : MonoBehaviour
    {
        const float MaxEmissionRate = 50f;
        const float MaxGlowIntensity = 1.5f;
        const float SmoulderGlowIntensity = 0.15f;
        /// <summary>A sector counts as "covered" if foam landed on it this recently.</summary>
        const float CoverageWindow = 0.6f;
        /// <summary>Share of damage on a dead sector that leaks into the live ones.</summary>
        const float SpillFraction = 0.25f;

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
        ParticleSystem _embers;
        Light _glow;
        FireSpotSettings _settings;
        float _maxHealth;
        float _sectorMax;
        float[] _sectorHealth;
        float[] _sectorLastHitTime;
        float _lastFoamTime = -999f;
        float _soakSeconds;
        float _flickerSeed;
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
            BuildFlames(settings.Radius);
            BuildGlow(settings.Radius);
            if (settings.ReigniteEnabled) BuildEmbers(settings.Radius);

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

        void BuildFlames(float radius)
        {
            var flamesGo = new GameObject("Flames");
            flamesGo.transform.SetParent(transform, false);
            // Local +Z is the surface normal; flames should travel along it.
            _flames = flamesGo.AddComponent<ParticleSystem>();
            _flames.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = _flames.main;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.4f, 0.9f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.3f * radius * 10f, 0.7f * radius * 10f);
            main.startSize = new ParticleSystem.MinMaxCurve(radius * 0.8f, radius * 1.6f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 200;
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1f, 0.75f, 0.2f, 1f), new Color(1f, 0.35f, 0.05f, 1f));

            var emission = _flames.emission;
            emission.rateOverTime = MaxEmissionRate;

            var shape = _flames.shape;
            shape.shapeType = ParticleSystemShapeType.Hemisphere;
            shape.radius = radius * 0.6f;

            // Fade out and shrink near end of life for a flame-like taper.
            var colorOverLifetime = _flames.colorOverLifetime;
            colorOverLifetime.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(new Color(1f, 0.8f, 0.3f), 0f), new GradientColorKey(new Color(0.6f, 0.1f, 0.05f), 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.2f), new GradientAlphaKey(0f, 1f) });
            colorOverLifetime.color = new ParticleSystem.MinMaxGradient(grad);

            var sizeOverLifetime = _flames.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f,
                new AnimationCurve(new Keyframe(0f, 0.4f), new Keyframe(0.3f, 1f), new Keyframe(1f, 0.1f)));

            var renderer = _flames.GetComponent<ParticleSystemRenderer>();
            renderer.material = FireSimVisuals.AdditiveParticleMaterial;
            renderer.sortingFudge = -5f;

            _flames.Play();
        }

        void BuildGlow(float radius)
        {
            var glowGo = new GameObject("Glow");
            glowGo.transform.SetParent(transform, false);
            glowGo.transform.localPosition = Vector3.forward * radius;
            _glow = glowGo.AddComponent<Light>();
            _glow.type = LightType.Point;
            _glow.color = new Color(1f, 0.55f, 0.2f);
            _glow.range = radius * 8f;
            _glow.intensity = MaxGlowIntensity;
        }

        /// <summary>
        /// A few slow, dim orange motes drifting off the surface. Built stopped and only played
        /// while smouldering - it is the visual tell that the patch is not finished.
        /// </summary>
        void BuildEmbers(float radius)
        {
            var embersGo = new GameObject("Embers");
            embersGo.transform.SetParent(transform, false);
            _embers = embersGo.AddComponent<ParticleSystem>();
            _embers.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = _embers.main;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.0f, 2.0f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.05f, 0.15f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.012f, 0.03f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 30;
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1f, 0.45f, 0.1f, 0.7f), new Color(0.9f, 0.25f, 0.05f, 0.5f));

            var emission = _embers.emission;
            emission.rateOverTime = 7f;

            var shape = _embers.shape;
            shape.shapeType = ParticleSystemShapeType.Hemisphere;
            shape.radius = radius * 0.5f;

            var colorOverLifetime = _embers.colorOverLifetime;
            colorOverLifetime.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(new Color(1f, 0.5f, 0.15f), 0f), new GradientColorKey(new Color(0.4f, 0.08f, 0.02f), 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0.8f, 0.3f), new GradientAlphaKey(0f, 1f) });
            colorOverLifetime.color = new ParticleSystem.MinMaxGradient(grad);

            var renderer = _embers.GetComponent<ParticleSystemRenderer>();
            renderer.material = FireSimVisuals.AdditiveParticleMaterial;
            renderer.sortingFudge = -5f;
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

        void Update()
        {
            if (_isOut) return;
            if (IsSmouldering) { UpdateSmouldering(); return; }
            if (!IsBurning) return;
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
                return;
            }

            if (_glow != null)
            {
                float flicker = 0.7f + 0.3f * Mathf.PerlinNoise(Time.time * 6f, _flickerSeed);
                _glow.intensity = SmoulderGlowIntensity * flicker;
            }
        }

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

            if (IsBurning)
            {
                RefreshVisuals();
                if (AllSectorsDead()) OnHealthDepleted();
            }
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

        /// <summary>Flames weaken visibly as the patch is doused, and swell again as it recovers.</summary>
        void RefreshVisuals()
        {
            float frac = HealthFraction;
            var emission = _flames.emission;
            emission.rateOverTime = Mathf.Lerp(8f, MaxEmissionRate, frac);
            if (_glow != null) _glow.intensity = Mathf.Lerp(0.2f, MaxGlowIntensity, frac);
        }

        /// <summary>
        /// The flames-out moment. The manager hears OnSpotExtinguished either way; what happens
        /// next depends on the stage - die immediately, or smoulder and maybe come back.
        /// </summary>
        void OnHealthDepleted()
        {
            if (!IsBurning) return;
            IsBurning = false;
            _flames.Stop(true, ParticleSystemStopBehavior.StopEmitting);

            if (_manager != null) _manager.OnSpotExtinguished(this);

            if (_settings.ReigniteEnabled) EnterSmouldering();
            else GoOut();
        }

        void EnterSmouldering()
        {
            IsSmouldering = true;
            _soakSeconds = 0f;
            // The collider stays enabled on purpose: foam must still be able to land so the user
            // can soak the embers. Glow drops to a dim flicker; the embers system is the tell.
            if (_glow != null) { _glow.enabled = true; _glow.intensity = SmoulderGlowIntensity; }
            if (_embers != null) _embers.Play();

            if (_manager != null) _manager.OnSpotSmouldering(this);
        }

        void Reignite()
        {
            IsSmouldering = false;
            IsBurning = true;
            if (_embers != null) _embers.Stop(true, ParticleSystemStopBehavior.StopEmitting);

            float per = Mathf.Clamp01(_settings.ReigniteHealthFraction) * _maxHealth / _sectorHealth.Length;
            for (int i = 0; i < _sectorHealth.Length; i++) _sectorHealth[i] = per;

            _flames.Play();
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

            _flames.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            if (_embers != null) _embers.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            if (_glow != null) _glow.enabled = false;
            GetComponent<CapsuleCollider>().enabled = false;

            PlaySteamPuff();

            if (_manager != null) _manager.OnSpotOut(this);
            // Leave the steam to finish, then remove the object entirely.
            Destroy(gameObject, 2.5f);
        }

        void PlaySteamPuff()
        {
            var steamGo = new GameObject("Steam");
            steamGo.transform.SetParent(transform, false);
            var steam = steamGo.AddComponent<ParticleSystem>();
            steam.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = steam.main;
            main.loop = false;
            main.duration = 0.5f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.6f, 1.2f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.4f, 0.9f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.1f, 0.2f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startColor = new Color(0.85f, 0.85f, 0.85f, 0.5f);

            var emission = steam.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)25) });

            var shape = steam.shape;
            shape.shapeType = ParticleSystemShapeType.Hemisphere;
            shape.radius = 0.1f;

            var colorOverLifetime = steam.colorOverLifetime;
            colorOverLifetime.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.gray, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0.5f, 0.2f), new GradientAlphaKey(0f, 1f) });
            colorOverLifetime.color = new ParticleSystem.MinMaxGradient(grad);

            steam.GetComponent<ParticleSystemRenderer>().material = FireSimVisuals.AlphaParticleMaterial;
            steam.Play();
        }
    }

    /// <summary>
    /// Lazily-built, shared particle materials and a soft round particle texture, generated
    /// at runtime so the simulation needs no imported art assets. Uses "Sprites/Default",
    /// which renders correctly (unlit, vertex-coloured) under URP.
    /// </summary>
    public static class FireSimVisuals
    {
        static Texture2D _softDot;
        static Material _additive;
        static Material _alpha;

        public static Texture2D SoftDot
        {
            get
            {
                if (_softDot != null) return _softDot;
                const int size = 64;
                _softDot = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "FireSim_SoftDot" };
                Vector2 c = new Vector2(size * 0.5f, size * 0.5f);
                for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x, y), c) / (size * 0.5f);
                    float a = Mathf.Clamp01(1f - d);
                    a = a * a; // softer falloff
                    _softDot.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
                _softDot.Apply();
                _softDot.wrapMode = TextureWrapMode.Clamp;
                return _softDot;
            }
        }

        public static Material AdditiveParticleMaterial
        {
            get
            {
                if (_additive != null) return _additive;
                _additive = new Material(Shader.Find("Sprites/Default")) { name = "FireSim_FlameMat" };
                _additive.mainTexture = SoftDot;
                // Additive blending for a glowing flame look.
                _additive.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                _additive.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.One);
                _additive.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                return _additive;
            }
        }

        public static Material AlphaParticleMaterial
        {
            get
            {
                if (_alpha != null) return _alpha;
                _alpha = new Material(Shader.Find("Sprites/Default")) { name = "FireSim_FoamMat" };
                _alpha.mainTexture = SoftDot;
                return _alpha;
            }
        }
    }
}
