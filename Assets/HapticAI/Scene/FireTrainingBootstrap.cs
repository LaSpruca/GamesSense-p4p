using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FireSim;
using HapticAI.Config;
using HapticAI.Esp;
using HapticAI.Net;
using HapticAI.Profiles;
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
    ///   * MRUK loading the room, plus an EffectMesh producing colliders on every detected
    ///     surface (so foam splashes on them and fire sits on them), rendered only as the light
    ///     the fire throws onto the real room.
    ///   * A SurfaceFireManager that runs the staged drill across those surfaces.
    ///   * A foam sprayer at the tip of the right controller, driven by the index trigger.
    ///   * The haptics stack: a palette of profiles planned for THIS room, played by the relay.
    ///   * A TrainingSession that runs briefing → stage → notification → next stage.
    ///
    /// Haptics start-up is built around never holding the trainee up:
    ///   1. The session socket opens while the room is still loading.
    ///   2. As soon as the room is known, a scene snapshot goes to the server; its plan comes back
    ///      in well under a second when the room has been seen before, a few seconds otherwise.
    ///   3. The briefing appears at that point. Clips stream in behind it (instantly from the
    ///      headset's own store on a warm start) and procedural stand-ins cover any that have not.
    ///   4. If the socket has not connected within the configured timeout, the on-device palette is
    ///      adopted instead - and replaced by the server's if the socket comes up later.
    /// </summary>
    public sealed class FireTrainingBootstrap : MonoBehaviour
    {
        /// <summary>
        /// How the ESP32 custom controller (bracket-mounted FSR402 + reed switch + DRV2605L, see
        /// esp32-controller/README.md) participates, if at all. <see cref="ControllerDrivesGame"/> is
        /// Mode A: its reed+force replace the Quest trigger locally via
        /// <see cref="HapticAI.Esp.EspTriggerSource"/>, and everything downstream - fire sim, Gemini
        /// haptics - is unchanged. <see cref="GeminiDrivesMotor"/> is Mode B: reed+force go straight
        /// to the server via <see cref="HapticAI.Esp.EspGeminiLoop"/>, bypassing the fire sim, and
        /// Gemini's selection drives the ESP32's haptic motor over BLE - useful for bringing up and
        /// testing the physical controller loop on its own.
        /// </summary>
        public enum EspControllerMode { None, ControllerDrivesGame, GeminiDrivesMotor }

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

        [Header("ESP32 controller")]
        [Tooltip("None: use the Quest controller as normal. ControllerDrivesGame / GeminiDrivesMotor " +
                 "require the bracket-mounted ESP32 controller (esp32-controller/) and are Android-only " +
                 "- both are silently ignored in the Editor.")]
        public EspControllerMode espMode = EspControllerMode.None;

        const string FallbackRoomResourcePath = "HapticAI/Rooms";

        OVRCameraRig _rig;
        SurfaceFireManager _fireManager;
        FoamSprayer _sprayer;
        FireHapticRelay _relay;
        TrainingSession _session;
        HapticStack _haptics;
        HapticPlayer _player;
        EspBleClient _esp;
        CancellationTokenSource _cts;
        string _backendStatus = "unknown";
        bool _serverPaletteAdopted;
        bool _offlinePaletteAdopted;
        bool _sceneSent;

        async void Start()
        {
            _cts = new CancellationTokenSource();
            try
            {
                _rig = XrRig.EnsureCameraRig();
                XrRig.ConfigurePassthrough(_rig);

                // Kick the room load off immediately - it is the long pole and needs nothing else.
                BuildFireManager();
                BuildSceneUnderstanding();
                MRUK.Instance.RegisterSceneLoadedCallback(_fireManager.OnSceneReady);

                BuildFoamSprayer();
                _fireManager.AttachSprayer(_sprayer);
                if (espMode == EspControllerMode.ControllerDrivesGame) BuildEspTriggerSource();

                var secrets = await BuildHapticsAsync();
                if (espMode == EspControllerMode.ControllerDrivesGame) MirrorHapticsToEsp();
                BuildRelay();
                if (espMode == EspControllerMode.ControllerDrivesGame) TapOnPinPulled();
                BuildSession();
                if (espMode == EspControllerMode.GeminiDrivesMotor) BuildEspGeminiLoop();

                // Palette preparation must never strand the user on the "getting ready" screen:
                // whatever happens, the session is told to move on.
                try
                {
                    await PreparePaletteAsync(secrets, _cts.Token);
                }
                catch (System.OperationCanceledException) { }
                catch (System.Exception e)
                {
                    Debug.LogError($"[HapticAI] Palette preparation failed: {e}");
                    _backendStatus = "preparation failed, see log";
                    AdoptOfflinePalette();
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[HapticAI] Bootstrap failed: {e}");
            }
        }

        void Update()
        {
            _haptics.Client?.Pump();
        }

        void OnDestroy()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _haptics.Client?.Dispose();
            _esp?.Dispose();
        }

        // --- ESP32 controller -----------------------------------------------------------------

        /// <summary>Mode A: the ESP32's reed+force stand in for the Quest trigger, nothing else changes.</summary>
        void BuildEspTriggerSource()
        {
            _esp = new EspBleClient();
            _esp.Connect();

            var triggerGo = new GameObject("EspTriggerSource");
            triggerGo.transform.SetParent(transform, false);
            var trigger = triggerGo.AddComponent<EspTriggerSource>();
            trigger.Bind(_esp);
            _sprayer.TriggerSource = trigger;
        }

        /// <summary>
        /// Mode A's motor: the ESP32's DRV2605L plays whatever the Quest controller is playing -
        /// the AI profile loop and fire cues in the AI condition, the fixed rumble in the static
        /// one - reduced to a 0-100% drive level. The session keeps exactly one of those two
        /// sources active at a time, so they never fight over the motor.
        /// </summary>
        void MirrorHapticsToEsp()
        {
            _player.RightLevelChanged += level => _esp.SendHaptic(ToMotorPercent(level));
            _sprayer.BuiltInVibration += amplitude => _esp.SendHaptic(ToMotorPercent(amplitude));
        }

        /// <summary>
        /// The reed switch is the extinguisher's pin: its magnet comes away with the pin, and the
        /// handle cannot be squeezed until it has. The moment it reads "pulled" (the state
        /// <see cref="EspTriggerSource"/> treats as able to spray) is a single tap. The first reading
        /// after connecting only sets the baseline, so a pin already out does not tap on connect.
        /// </summary>
        void TapOnPinPulled()
        {
            bool? pinOut = null;
            _esp.SensorUpdated += (reed, _) =>
            {
                if (pinOut == false && reed) _relay.PlayPinPulled();
                pinOut = reed;
            };
            _esp.Disconnected += _ => pinOut = null;
        }

        /// <summary>
        /// The firmware floors any non-zero command to the motor's spin threshold, so a small deadzone
        /// keeps near-silent cue tails from buzzing. It must stay well below the quiet "good technique"
        /// loop (gain 0.2 of a clip whose mean amplitude may be ~0.3), or that loop would go silent.
        /// </summary>
        static int ToMotorPercent(float level) =>
            level < 0.02f ? 0 : Mathf.Clamp(Mathf.RoundToInt(level * 100f), 1, 100);

        /// <summary>Mode B: the ESP32's reed+force go to the server; its selection drives the motor.</summary>
        void BuildEspGeminiLoop()
        {
            _esp = new EspBleClient();
            _esp.Connect();

            var loopGo = new GameObject("EspGeminiLoop");
            loopGo.transform.SetParent(transform, false);
            var loop = loopGo.AddComponent<EspGeminiLoop>();
            loop.Initialise(_esp, _haptics.Client, _haptics.Library, _player);
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
            _relay.Initialise(_haptics.Library, _haptics.Client, _player, head, _fireManager, _sprayer,
                config.stateSendHz);
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
            _session.Initialise(_fireManager, _sprayer, _relay, _haptics.Library, _player, head, mode, settings);
        }

        // --- Palette ------------------------------------------------------------------------

        async Task PreparePaletteAsync(HapticSecrets secrets, CancellationToken ct)
        {
            if (!_haptics.UsingBackend)
            {
                Debug.LogWarning("[HapticAI] Running without a server - the palette comes from the on-device " +
                                 "mock synth.");
                await WaitForRoomAsync(ct);
                AdoptOfflinePalette();
                return;
            }

            var client = _haptics.Client;
            client.Connected += OnSocketConnected;
            client.Disconnected += OnSocketDisconnected;
            client.ProfilesReceived += OnProfilesReceived;
            client.ClipReceived += OnClipReceived;
            client.Start();

            // /health is cheap and answers the operator's real question ("is the laptop really
            // reachable?") on the briefing panel, independently of the socket's own retries.
            var probe = await HapticSecrets.ProbeAsync(secrets.hapticServiceUrl);
            _backendStatus = probe.Message;
            _session.UpdateHapticSource(DescribeSource());

            await WaitForRoomAsync(ct);
            TrySendScene();

            // Give the server its chance; then stop waiting. The socket keeps retrying in the
            // background and the server palette replaces the offline one whenever it arrives.
            float deadline = Time.realtimeSinceStartup + Mathf.Max(2, config.connectTimeoutSeconds);
            while (!_serverPaletteAdopted && Time.realtimeSinceStartup < deadline)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
            }
            if (!_serverPaletteAdopted)
            {
                Debug.LogWarning("[HapticAI] No palette from the server yet - starting on the on-device palette.");
                AdoptOfflinePalette();
            }
        }

        async Task WaitForRoomAsync(CancellationToken ct)
        {
            while (!_fireManager.RoomReady)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
            }
        }

        void OnSocketConnected()
        {
            _sceneSent = false;
            TrySendScene();
            _session?.UpdateHapticSource(DescribeSource());
        }

        void OnSocketDisconnected(string reason)
        {
            _session?.UpdateHapticSource(DescribeSource());
        }

        /// <summary>Send the scene snapshot once per connection, as soon as both are available.</summary>
        void TrySendScene()
        {
            if (_sceneSent || !_fireManager.RoomReady) return;
            var client = _haptics.Client;
            if (client == null || client.State != HapticSessionClient.Status.Connected) return;

            var snapshot = BuildSceneSnapshot();
            // Clips are requested explicitly after the plan arrives, so a set already on this
            // headset costs the server nothing.
            _sceneSent = client.Send(new HapticWire.Scene { scene = snapshot, refresh = false, send_clips = false });
            if (_sceneSent) Debug.Log($"[HapticAI] Scene snapshot sent: {JsonUtility.ToJson(snapshot)}");
        }

        HapticWire.SceneSnapshot BuildSceneSnapshot()
        {
            var surfaces = new List<string>(_fireManager.RoomSurfaceLabels);
            surfaces.Sort(System.StringComparer.Ordinal);
            var size = _fireManager.RoomSize;
            var stages = new HapticWire.StageSnapshot[TrainingStages.Count];
            for (int i = 0; i < stages.Length; i++)
            {
                var s = TrainingStages.All[i];
                stages[i] = new HapticWire.StageSnapshot
                {
                    index = s.Index,
                    name = s.Name,
                    aim = s.AimLabel,
                    spread = s.SpreadEnabled,
                    sweep_sectors = s.SweepSectors,
                    reignite = s.ReigniteEnabled,
                };
            }
            return new HapticWire.SceneSnapshot
            {
                room = new HapticWire.RoomSnapshot
                {
                    surfaces = surfaces.ToArray(),
                    size_m = new[] { Round1(size.x), Round1(size.y), Round1(size.z) },
                    anchors = _fireManager.RoomAnchorCount,
                },
                extinguisher = new HapticWire.ExtinguisherSnapshot(),
                stages = stages,
            };
        }

        static float Round1(float v) => Mathf.Round(v * 10f) / 10f;

        void OnProfilesReceived(HapticWire.Profiles message)
        {
            if (message?.profiles == null || message.profiles.Length == 0)
            {
                Debug.LogWarning("[HapticAI] Server sent an empty palette.");
                return;
            }

            var profiles = new List<HapticProfile>(message.profiles.Length);
            foreach (var p in message.profiles)
                profiles.Add(new HapticProfile(p.id, p.role, p.prompt, p.duration_s, p.loop, p.gain));
            var set = new HapticProfileSet(message.set_id, message.scene_key, message.source, message.notes, profiles);

            var missing = _haptics.Library.AdoptManifest(set);
            _serverPaletteAdopted = true;
            Debug.Log($"[HapticAI] Palette {set.SetId} from {set.Source}: {set.Profiles.Count} profiles, " +
                      $"{missing.Count} clip(s) to fetch. {set.Notes}");

            if (missing.Count > 0)
                _haptics.Client.Send(new HapticWire.ClipsNeeded { set_id = set.SetId, profiles = missing.ToArray() });

            _session.UpdateHapticSource(DescribeSource());
            _session.SetHapticsReady(DescribeSource());
        }

        void OnClipReceived(string setId, string profileId, string source, byte[] wav)
        {
            if (_haptics.Library.ReceiveWav(setId, profileId, source, wav))
                Debug.Log($"[HapticAI] Clip {profileId} ready ({source}); {_haptics.Library.ClipCount}/{_haptics.Library.ProfileCount}.");
        }

        /// <summary>The on-device palette: identical roles, template prompts, mock synth. Never blocks.</summary>
        void AdoptOfflinePalette()
        {
            if (_offlinePaletteAdopted || _serverPaletteAdopted) return;
            _offlinePaletteAdopted = true;

            string surfaces = string.Join(", ", _fireManager.RoomSurfaceLabels);
            var set = OfflineProfileSynth.BuildSet(surfaces);
            _haptics.Library.AdoptManifest(set);
            _session.SetHapticsReady(DescribeSource());
            _ = _haptics.Offline.SynthesiseMissingAsync(_haptics.Library, _cts.Token);
        }

        string DescribeSource()
        {
            if (!_haptics.UsingBackend) return _haptics.SourceDescription;
            var client = _haptics.Client;
            string socket = client == null ? "no socket"
                : client.State == HapticSessionClient.Status.Connected
                    ? $"socket up, gemini {client.ServerTransport}"
                    : $"socket {client.State.ToString().ToLowerInvariant()}";
            return $"{_haptics.SourceDescription} - {_backendStatus}, {socket}";
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
            // Not invisible any more, but not visible either: the room-light material draws nothing
            // of the mesh itself and only adds the flames' light to the passthrough view of the real
            // surfaces. Null (shader missing) puts it back to colliders only.
            effectMesh.MeshMaterial = FireSimVisuals.RoomLightMaterial;
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
