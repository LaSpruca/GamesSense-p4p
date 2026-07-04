using System.Collections.Generic;
using System.Threading.Tasks;
using FireSim;
using HapticAI.Config;
using HapticAI.Core;
using HapticAI.Pipeline;
using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace HapticAI.Scene
{
    /// <summary>
    /// Entry point for the AI-haptics fire-training scene. It stands up the same fire simulation
    /// as <c>FireSimBootstrap</c> (passthrough + MRUK room + spreading fire + foam sprayer) but
    /// replaces the fixed scalar rumble with the AI pipeline:
    ///
    ///   cues → Gemini prompt chain → HaptiGen (or mock) → controller waveform.
    ///
    /// Order matters: the haptics pipeline and event relay are built and the cache pre-warmed
    /// BEFORE MRUK is asked to load the room, so the very first ignite already has clips ready.
    /// </summary>
    public sealed class FireSimAIBootstrap : MonoBehaviour
    {
        [Header("Config")]
        [Tooltip("Pipeline configuration. If left empty a default (mock synth) config is created at runtime.")]
        public HapticAIConfig config;

        [Header("Foam nozzle")]
        public Vector3 nozzleOffset = new Vector3(0f, -0.02f, 0.08f);

        OVRCameraRig _rig;
        SurfaceFireManager _fireManager;
        HapticDirector _director;
        HapticPlayer _player;

        async void Start()
        {
            try
            {
                _rig = EnsureCameraRig();
                ConfigurePassthrough(_rig);

                await BuildHapticsAsync();          // director + player ready before anything can fire.
                BuildFireManager();
                BuildFoamSprayerAndRelay();
                await WarmCacheAsync();

                BuildSceneUnderstanding();          // starts the MRUK room load.
                MRUK.Instance.RegisterSceneLoadedCallback(_fireManager.OnSceneReady);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[HapticAI] Bootstrap failed: {e}");
            }
        }

        async Task BuildHapticsAsync()
        {
            if (config == null)
            {
                Debug.LogWarning("[HapticAI] No HapticAIConfig assigned — using runtime defaults (mock synth).");
                config = ScriptableObject.CreateInstance<HapticAIConfig>();
            }

            var secrets = await HapticSecrets.LoadAsync();
            int sampleRate = HapticPlayer.SampleRateHz;
            config.targetSampleRateHz = sampleRate;

            _director = HapticPipelineFactory.Build(config, secrets, sampleRate);
            _player = new HapticPlayer();
        }

        void BuildFireManager()
        {
            var managerGo = new GameObject("SurfaceFireManager");
            _fireManager = managerGo.AddComponent<SurfaceFireManager>();
        }

        void BuildFoamSprayerAndRelay()
        {
            var parent = _rig != null && _rig.rightHandAnchor != null ? _rig.rightHandAnchor : transform;
            var nozzle = new GameObject("FoamNozzle");
            nozzle.transform.SetParent(parent, false);
            nozzle.transform.localPosition = nozzleOffset;
            nozzle.transform.localRotation = Quaternion.identity;

            nozzle.AddComponent<ParticleSystem>().Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var sprayer = nozzle.AddComponent<FoamSprayer>();
            sprayer.useBuiltInHaptics = false; // the AI pipeline drives haptics instead.
            sprayer.Configure();

            var head = _rig != null ? _rig.centerEyeAnchor : null;
            var relayGo = new GameObject("FireHapticRelay");
            relayGo.transform.SetParent(transform, false);
            relayGo.AddComponent<FireHapticRelay>()
                .Initialise(_director, _player, head, _fireManager, sprayer);
        }

        async Task WarmCacheAsync()
        {
            // Canonical events pre-generated so runtime hits play instantly.
            var warm = new List<HapticEvent>
            {
                new HapticEvent(HapticEventType.Ignite).WithNumber("intensity", 1f).WithTag("surface", "TABLE"),
                new HapticEvent(HapticEventType.Spread).WithNumber("intensity", 0.8f),
                new HapticEvent(HapticEventType.Extinguish).WithNumber("intensity", 0.7f),
                new HapticEvent(HapticEventType.SprayTick).WithNumber("triggerDepth", 0.3f),
                new HapticEvent(HapticEventType.SprayTick).WithNumber("triggerDepth", 0.7f),
                new HapticEvent(HapticEventType.SprayTick).WithNumber("triggerDepth", 1.0f),
            };
            await _director.WarmAsync(warm);
            Debug.Log("[HapticAI] Cache warm complete.");
        }

        // --- Below mirrors FireSimBootstrap so the AI scene is self-contained. ---

        OVRCameraRig EnsureCameraRig()
        {
            var rig = FindFirstObjectByType<OVRCameraRig>();
            if (rig != null) return rig;

            var rigGo = new GameObject("OVRCameraRig");
            rigGo.AddComponent<OVRManager>();
            return rigGo.AddComponent<OVRCameraRig>();
        }

        void ConfigurePassthrough(OVRCameraRig rig)
        {
            var manager = rig.GetComponent<OVRManager>();
            if (manager == null) manager = rig.gameObject.AddComponent<OVRManager>();
            manager.isInsightPassthroughEnabled = true;

            var passthrough = rig.GetComponent<OVRPassthroughLayer>();
            if (passthrough == null) passthrough = rig.gameObject.AddComponent<OVRPassthroughLayer>();
            passthrough.overlayType = OVROverlay.OverlayType.Underlay;
            passthrough.projectionSurfaceType = OVRPassthroughLayer.ProjectionSurfaceType.Reconstructed;

            foreach (var cam in rig.GetComponentsInChildren<Camera>(true))
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            }
        }

        void BuildSceneUnderstanding()
        {
            var mrukGo = new GameObject("MRUK");
            var mruk = mrukGo.AddComponent<MRUK>();
            mruk.SceneSettings = new MRUK.MRUKSettings
            {
                DataSource = MRUK.SceneDataSource.Device,
                LoadSceneOnStartup = true,
            };

            var effectMesh = mrukGo.AddComponent<EffectMesh>();
            effectMesh.Labels =
                MRUKAnchor.SceneLabels.WALL_FACE | MRUKAnchor.SceneLabels.FLOOR | MRUKAnchor.SceneLabels.CEILING |
                MRUKAnchor.SceneLabels.TABLE | MRUKAnchor.SceneLabels.COUCH | MRUKAnchor.SceneLabels.STORAGE |
                MRUKAnchor.SceneLabels.BED | MRUKAnchor.SceneLabels.SCREEN | MRUKAnchor.SceneLabels.WALL_ART |
                MRUKAnchor.SceneLabels.OTHER | MRUKAnchor.SceneLabels.DOOR_FRAME | MRUKAnchor.SceneLabels.WINDOW_FRAME |
                MRUKAnchor.SceneLabels.PLANT | MRUKAnchor.SceneLabels.LAMP;
            effectMesh.Colliders = true;
            effectMesh.MeshMaterial = null;
            effectMesh.CastShadow = false;
        }
    }
}
