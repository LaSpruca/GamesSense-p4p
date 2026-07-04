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
    /// </summary>
    [RequireComponent(typeof(ParticleSystem))]
    public class FoamSprayer : MonoBehaviour
    {
        [Tooltip("How far the trigger must be pulled (0-1) before foam starts spraying.")]
        public float triggerThreshold = 0.15f;
        [Tooltip("Foam particles emitted per second while spraying.")]
        public float emissionRate = 300f;
        public float hapticFrequency = 0.6f;
        public float hapticMaxAmplitude = 0.7f;

        ParticleSystem _foam;
        ParticleSystem.EmissionModule _emission;
        bool _spraying;

        public void Configure()
        {
            _foam = GetComponent<ParticleSystem>();
            _foam.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = _foam.main;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.8f, 1.4f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(5f, 8f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.03f, 0.08f);
            main.gravityModifier = 0.6f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 1500;
            main.startColor = new Color(0.96f, 0.97f, 1f, 0.9f);

            _emission = _foam.emission;
            _emission.rateOverTime = 0f; // driven by the trigger in Update.

            // Narrow cone firing out of the nozzle (local +Z).
            var shape = _foam.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 11f;
            shape.radius = 0.02f;

            var collision = _foam.collision;
            collision.enabled = true;
            collision.type = ParticleSystemCollisionType.World;
            collision.mode = ParticleSystemCollisionMode.Collision3D;
            collision.sendCollisionMessages = true; // required for FireSpot.OnParticleCollision.
            collision.dampen = 0.4f;
            collision.bounce = 0.1f;
            collision.lifetimeLoss = 0.2f;
            collision.quality = ParticleSystemCollisionQuality.Medium;

            var colorOverLifetime = _foam.colorOverLifetime;
            colorOverLifetime.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0.95f, 0f), new GradientAlphaKey(0.95f, 0.7f), new GradientAlphaKey(0f, 1f) });
            colorOverLifetime.color = new ParticleSystem.MinMaxGradient(grad);

            _foam.GetComponent<ParticleSystemRenderer>().material = FireSimVisuals.AlphaParticleMaterial;
            _foam.Play();
        }

        void Update()
        {
            float trigger = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, OVRInput.Controller.RTouch);
            bool wantSpray = trigger >= triggerThreshold;

            if (wantSpray)
            {
                _emission.rateOverTime = emissionRate;
                // Vibration intensity tracks how hard the trigger is pulled.
                float amplitude = Mathf.Lerp(0.15f, hapticMaxAmplitude, Mathf.InverseLerp(triggerThreshold, 1f, trigger));
                OVRInput.SetControllerVibration(hapticFrequency, amplitude, OVRInput.Controller.RTouch);
                _spraying = true;
            }
            else if (_spraying)
            {
                _emission.rateOverTime = 0f;
                OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.RTouch);
                _spraying = false;
            }
        }

        void OnDisable()
        {
            if (_spraying)
            {
                OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.RTouch);
                _spraying = false;
            }
        }
    }
}
