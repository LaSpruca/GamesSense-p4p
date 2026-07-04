using System.Collections.Generic;
using UnityEngine;
using Meta.XR.MRUtilityKit;

namespace FireSim
{
    /// <summary>
    /// A single burning patch sitting on a detected surface. It renders flame particles,
    /// holds "health" that foam reduces, and exposes its position/normal/anchor so the
    /// <see cref="SurfaceFireManager"/> can spread fire from it to nearby surfaces.
    ///
    /// Foam reaches a spot through Unity's particle collision messages: the foam particle
    /// system has "Send Collision Messages" enabled, and this component lives on the same
    /// GameObject as a (non-trigger) collider, so <see cref="OnParticleCollision"/> fires
    /// once per frame with every foam particle that hit this patch.
    /// </summary>
    [RequireComponent(typeof(SphereCollider))]
    public class FireSpot : MonoBehaviour
    {
        public MRUKAnchor Anchor { get; private set; }
        public Vector3 Position => transform.position;
        public Vector3 SurfaceNormal { get; private set; }
        public bool IsBurning { get; private set; }

        SurfaceFireManager _manager;
        ParticleSystem _flames;
        Light _glow;
        float _health;
        float _maxHealth;
        // How much "health" each foam particle removes when it strikes this patch.
        float _foamDamagePerParticle;
        readonly List<ParticleCollisionEvent> _collisionEvents = new List<ParticleCollisionEvent>();

        public void Initialise(SurfaceFireManager manager, Vector3 position, Vector3 surfaceNormal,
            MRUKAnchor anchor, float radius, float health, float foamDamagePerParticle)
        {
            _manager = manager;
            Anchor = anchor;
            SurfaceNormal = surfaceNormal;
            _maxHealth = health;
            _health = health;
            _foamDamagePerParticle = foamDamagePerParticle;
            IsBurning = true;

            transform.position = position + surfaceNormal * 0.02f;
            // Orient so the flames rise away from the surface (handles walls, floors, ceilings).
            transform.rotation = Quaternion.LookRotation(surfaceNormal);

            var collider = GetComponent<SphereCollider>();
            collider.isTrigger = false; // particle World collisions ignore triggers.
            collider.radius = radius;

            BuildFlames(radius);
            BuildGlow(radius);

            // Pop in so spreading reads as growth rather than instant appearance.
            StartCoroutine(GrowIn());
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
            emission.rateOverTime = 50f;

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
            _glow.intensity = 1.5f;
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

        void OnParticleCollision(GameObject other)
        {
            if (!IsBurning || _manager == null) return;
            // Only foam puts the fire out; ignore any other particle systems.
            if (other.GetComponent<FoamSprayer>() == null) return;

            var foamPs = other.GetComponent<ParticleSystem>();
            if (foamPs == null) return;

            int hits = foamPs.GetCollisionEvents(gameObject, _collisionEvents);
            if (hits <= 0) return;

            ApplyFoam(hits * _foamDamagePerParticle);
        }

        public void ApplyFoam(float amount)
        {
            if (!IsBurning) return;
            _health -= amount;

            // Flames weaken visibly as the patch is doused.
            float frac = Mathf.Clamp01(_health / _maxHealth);
            var emission = _flames.emission;
            emission.rateOverTime = Mathf.Lerp(8f, 50f, frac);
            if (_glow != null) _glow.intensity = Mathf.Lerp(0.2f, 1.5f, frac);

            if (_health <= 0f) Extinguish();
        }

        void Extinguish()
        {
            if (!IsBurning) return;
            IsBurning = false;

            _flames.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            if (_glow != null) _glow.enabled = false;
            GetComponent<SphereCollider>().enabled = false;

            PlaySteamPuff();

            if (_manager != null) _manager.OnSpotExtinguished(this);
            // Leave the steam to finish, then remove the object entirely so it cannot reignite.
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
