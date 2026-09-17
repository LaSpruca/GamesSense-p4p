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
    /// Single entry point for the fire-extinguisher training experience. The scene ships with an
    /// OVRCameraRig prefab instance; this component finds it, switches it into Insight Passthrough,
    /// and builds everything else in code so nothing needs hand-wiring:
    ///
    ///   * Insight Passthrough as an underlay, with the eye cameras cleared to transparent.
    ///   * MRUK loading the room, plus an EffectMesh producing invisible colliders on every
    ///     detected surface (so foam splashes on them and fire sits on them).
    ///   * A SurfaceFireManager that spreads fire across those surfaces.
    ///   * A foam sprayer at the tip of the right controller, driven by the index trigger.
    ///   * The AI haptics stack, and a TrainingSession that runs briefing → run → result.
    ///
    /// This replaces the two near-identical bootstraps the project used to carry (one per scene).
    /// Both haptics conditions now live here and are switched at runtime by the session, so a
    /// participant can be flipped between them without a rebuild, and the static baseline can no
    /// longer drift away from the AI build the way two hand-maintained scenes did.
    ///
    /// Ordering note: the MRUK room load is started FIRST and left to run while the haptics stack
    /// builds and warms. Those two are independent and each takes seconds; doing them in sequence
    /// (as the AI scene used to) simply added the two waits together before anything appeared.
    /// </summary>
    public sealed class FireTrainingBootstrap : MonoBehaviour
    {
        [Header("Config")]
        [Tooltip("Pipeline configuration. If left empty a default (mock synth) config is created at runtime.")]
        public HapticAIConfig config;

        [Tooltip("Condition used only when this headset has never been through the setup scene. " +
                 "Once it has, the condition chosen there wins - this field is not consulted.")]
        public HapticsMode startingHapticsMode = HapticsMode.Ai;

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
        FoamSprayer _sprayer;
        FireHapticRelay _relay;
        TrainingSession _session;
        HapticStack _haptics;
        HapticPlayer _player;
        string _backendStatus;

        async void Start()
        {
            try
            {
                _rig = XrRig.EnsureCameraRig();
                XrRig.ConfigurePassthrough(_rig);

                // Kick the room load off immediately - it is the long pole and needs nothing else.
                BuildFireManager();
                BuildSceneUnderstanding();
                MRUK.Instance.RegisterSceneLoadedCallback(_fireManager.OnSceneReady);

                BuildFoamSprayer();

                var secrets = await BuildHapticsAsync();
                BuildRelay();
                BuildSession();

                // Warming is an optimisation, and its failure must not strand the user on the
                // "getting ready" screen: whatever happens, the session is told to move on. Clips
                // that were not pre-generated simply play a procedural stand-in the first time.
                try
                {
                    await PrepareClipsAsync(secrets);
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[HapticAI] Clip preparation failed: {e}");
                    _backendStatus = "preparation failed, see log";
                }
                _session.SetHapticsReady(DescribeSource());
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[HapticAI] Bootstrap failed: {e}");
            }
        }

        // --- Haptics ------------------------------------------------------------------------

        async Task<HapticSecrets> BuildHapticsAsync()
        {
            if (config == null)
            {
                Debug.LogWarning("[HapticAI] No HapticAIConfig assigned — using runtime defaults (mock synth).");
                config = ScriptableObject.CreateInstance<HapticAIConfig>();
            }

            var secrets = await HapticSecrets.LoadAsync(
                allowEditorLocalhostFallback: config.useEditorLocalhostFallback);

            int sampleRate = HapticPlayer.SampleRateHz;
            config.targetSampleRateHz = sampleRate;

            _haptics = HapticPipelineFactory.Build(config, secrets, sampleRate);
            _player = new HapticPlayer();
            return secrets;
        }

        void BuildRelay()
        {
            var head = _rig != null ? _rig.centerEyeAnchor : null;
            var relayGo = new GameObject("FireHapticRelay");
            relayGo.transform.SetParent(transform, false);
            _relay = relayGo.AddComponent<FireHapticRelay>();
            _relay.Initialise(_haptics.Director, _player, head, _fireManager, _sprayer, config.cacheBuckets);
        }

        /// <summary>
        /// The session, started in whatever condition the setup scene last saved on this headset.
        /// <see cref="startingHapticsMode"/> is only the answer for a headset that has never been
        /// set up - otherwise the operator's choice has to win, or a run collects data for a
        /// condition nobody selected.
        /// </summary>
        void BuildSession()
        {
            var head = _rig != null ? _rig.centerEyeAnchor : transform;
            var settings = StudySettings.Current;
            var mode = settings.Configured ? settings.condition : startingHapticsMode;

            var sessionGo = new GameObject("TrainingSession");
            sessionGo.transform.SetParent(transform, false);
            _session = sessionGo.AddComponent<TrainingSession>();
            _session.Initialise(_fireManager, _sprayer, _relay, _player, head, mode, settings);
        }

        /// <summary>
        /// Confirm the backend is really there, then pre-generate the canonical clips so runtime
        /// events are instant hits.
        ///
        /// The health check is not just diagnostics: with an unreachable backend, every warm request
        /// would sit through the full HTTP timeout before falling back, turning a quick start into a
        /// 30-second stall for clips that end up coming from the on-device synth anyway - and the
        /// on-device synth is instant, so there is nothing worth pre-generating. Checking first lets
        /// that case skip straight to the briefing, and say so on the panel instead of hiding it.
        /// </summary>
        async Task PrepareClipsAsync(HapticSecrets secrets)
        {
            if (!_haptics.UsingBackend)
            {
                Debug.LogWarning("[HapticAI] Running without a backend - clips come from the on-device " +
                                 "mock synth. Skipping cache warm (mock generation is instant).");
                return;
            }

            var probe = await HapticSecrets.ProbeAsync(secrets.hapticServiceUrl);
            _backendStatus = probe.Message;
            if (!probe.Reachable)
            {
                Debug.LogWarning("[HapticAI] Skipping cache warm - the backend did not answer.");
                return;
            }

            await _haptics.Director.WarmAsync(WarmSet());
            Debug.Log("[HapticAI] Cache warm complete.");
        }

        string DescribeSource()
        {
            if (!_haptics.UsingBackend) return _haptics.SourceDescription;
            return $"{_haptics.SourceDescription} - {_backendStatus ?? "unknown"}";
        }

        /// <summary>
        /// Canonical events pre-generated so runtime hits play instantly.
        ///
        /// Every runtime fire event carries a distance cue, so warm entries that omit it can never
        /// match one; 3 m lands in the "across the room" bucket, which is what most events hit.
        ///
        /// The spray entries cover every bucket the trigger can actually reach. The old set warmed
        /// 0.3 / 0.7 / 1.0, which at four buckets is 1, 2 and 4 — leaving buckets 0 and 3 cold, so a
        /// light or three-quarter squeeze fell through to a procedural stand-in every time.
        /// </summary>
        IEnumerable<HapticEvent> WarmSet()
        {
            var events = new List<HapticEvent>
            {
                new HapticEvent(HapticEventType.Ignite).WithNumber("intensity", 1f)
                    .WithNumber("distance", 3f).WithTag("surface", "TABLE"),
                new HapticEvent(HapticEventType.Spread).WithNumber("intensity", 0.8f)
                    .WithNumber("distance", 3f).WithTag("surface", "WALL_FACE"),
                new HapticEvent(HapticEventType.Spread).WithNumber("intensity", 0.8f)
                    .WithNumber("distance", 3f).WithTag("surface", "FLOOR"),
                new HapticEvent(HapticEventType.Extinguish).WithNumber("intensity", 0.7f)
                    .WithNumber("distance", 3f),
                HapticEvent.Custom(FireHapticRelay.CompleteEventName).WithNumber("intensity", 1f),
            };

            int buckets = Mathf.Max(1, config.cacheBuckets);
            for (int b = 0; b <= buckets; b++)
            {
                // Midpoint of each bucket, and exactly 1.0 for the top one (a fully pulled trigger
                // is its own bucket because (int)(1.0 * buckets) == buckets).
                float depth = b < buckets ? (b + 0.5f) / buckets : 1f;
                events.Add(new HapticEvent(HapticEventType.SprayTick).WithNumber("triggerDepth", depth));
            }
            return events;
        }

        // --- World --------------------------------------------------------------------------

        void BuildFireManager()
        {
            var managerGo = new GameObject("SurfaceFireManager");
            managerGo.transform.SetParent(transform, false);
            _fireManager = managerGo.AddComponent<SurfaceFireManager>();
        }

        void BuildFoamSprayer()
        {
            var parent = _rig != null && _rig.rightHandAnchor != null ? _rig.rightHandAnchor : transform;
            var nozzle = new GameObject("FoamNozzle");
            nozzle.transform.SetParent(parent, false);
            nozzle.transform.localPosition = nozzleOffset;
            nozzle.transform.localRotation = Quaternion.identity;

            // ParticleSystem is added first so FoamSprayer's RequireComponent is satisfied cleanly.
            var ps = nozzle.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            _sprayer = nozzle.AddComponent<FoamSprayer>();
            _sprayer.Configure();
            _sprayer.Armed = false;   // the session arms it when a run starts.
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
