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

        [Header("Scene understanding")]
        [Tooltip("Query the headset for the real room. Ignored in the editor, where the XR " +
                 "Simulator has no scene understanding and the query never returns.")]
        public bool useDeviceSceneData = true;

        [Tooltip("Rooms used when the runtime provides no scene data (editor / XR Simulator). " +
                 "Leave empty to load every room prefab under Resources/" + FallbackRoomResourcePath + ".")]
        public GameObject[] fallbackRooms;

        const string FallbackRoomResourcePath = "HapticAI/Rooms";

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
            // Every runtime fire event carries a distance cue, so warm entries that omit it can never
            // match one. 3 m lands in the "across the room" bucket, which is what most events hit.
            var warm = new List<HapticEvent>
            {
                new HapticEvent(HapticEventType.Ignite).WithNumber("intensity", 1f)
                    .WithNumber("distance", 3f).WithTag("surface", "TABLE"),
                new HapticEvent(HapticEventType.Spread).WithNumber("intensity", 0.8f)
                    .WithNumber("distance", 3f).WithTag("surface", "WALL_FACE"),
                new HapticEvent(HapticEventType.Spread).WithNumber("intensity", 0.8f)
                    .WithNumber("distance", 3f).WithTag("surface", "FLOOR"),
                new HapticEvent(HapticEventType.Extinguish).WithNumber("intensity", 0.7f)
                    .WithNumber("distance", 3f),
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
            // Created inactive on purpose. MRUK kicks off its startup load in Awake(), and
            // AddComponent runs Awake synchronously — so a GameObject that is already active would
            // begin loading with the DEFAULT settings (DataSource = Device, no room prefabs) before
            // the assignment below ever lands, and set its _loadSceneCalled guard so the Update()
            // retry never fires either. Deferring activation is what makes these settings count.
            var mrukGo = new GameObject("MRUK");
            mrukGo.SetActive(false);
            var mruk = mrukGo.AddComponent<MRUK>();

            // In the editor the Meta XR Simulator has no scene-understanding support
            // (xrGetSpaceRoomMeshMETA is unsupported) and its device query never completes — so
            // DeviceWithPrefabFallback hangs inside the device load and never reaches its fallback,
            // leaving zero rooms, zero surfaces and no fires to train against. Ask for the prefab
            // room outright there. On a headset the device's real room still takes priority, with
            // the prefab kept as a genuine fallback.
            bool hasSceneUnderstanding = useDeviceSceneData && !Application.isEditor;
            mruk.SceneSettings = new MRUK.MRUKSettings
            {
                DataSource = hasSceneUnderstanding
                    ? MRUK.SceneDataSource.DeviceWithPrefabFallback
                    : MRUK.SceneDataSource.Prefab,
                RoomPrefabs = ResolveFallbackRooms(),
                RoomIndex = 0,
                LoadSceneOnStartup = true,
            };
            Debug.Log($"[HapticAI] MRUK data source: {mruk.SceneSettings.DataSource} " +
                      $"({mruk.SceneSettings.RoomPrefabs.Length} fallback room prefab(s))");

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

            mrukGo.SetActive(true);   // Awake() runs now, with the settings above in place.
        }

        /// <summary>
        /// Rooms used only when the runtime supplies no scene data (editor + XR Simulator). Loaded
        /// from Resources rather than an inspector reference because this bootstrap builds the whole
        /// scene in code; <see cref="fallbackRooms"/> overrides them when set.
        /// </summary>
        GameObject[] ResolveFallbackRooms()
        {
            if (fallbackRooms != null && fallbackRooms.Length > 0) return fallbackRooms;

            var loaded = Resources.LoadAll<GameObject>(FallbackRoomResourcePath);
            if (loaded == null || loaded.Length == 0)
            {
                Debug.LogWarning($"[HapticAI] No fallback room prefabs under Resources/{FallbackRoomResourcePath}. " +
                                 "Without device scene data the room will be empty and no fires will spawn.");
                return System.Array.Empty<GameObject>();
            }
            return loaded;
        }
    }
}
