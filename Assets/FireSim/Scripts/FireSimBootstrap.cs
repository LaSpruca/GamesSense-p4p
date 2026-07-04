using UnityEngine;
using Meta.XR.MRUtilityKit;

namespace FireSim
{
    /// <summary>
    /// Single entry point for the fire-extinguisher training scene. The scene ships with an
    /// OVRCameraRig prefab instance; this component finds it, switches it into Insight
    /// Passthrough, and builds the rest of the simulation in code so nothing needs hand-wiring:
    ///
    ///   * Insight Passthrough as an underlay, with the eye cameras cleared to transparent so
    ///     the real room shows through.
    ///   * MRUK loading the room from the device, plus an EffectMesh that produces invisible
    ///     colliders on every detected surface (so foam splashes on them and fire sits on them).
    ///   * A SurfaceFireManager that ignites and spreads fire across detected surfaces.
    ///   * A foam sprayer at the tip of the right controller, driven by the index trigger.
    ///
    /// Runs in Start() so the rig's hand/eye anchors have been created by OVRCameraRig.Awake.
    /// </summary>
    public class FireSimBootstrap : MonoBehaviour
    {
        [Header("Foam nozzle")]
        [Tooltip("Offset of the foam nozzle from the right controller, in local controller space.")]
        public Vector3 nozzleOffset = new Vector3(0f, -0.02f, 0.08f);

        OVRCameraRig _rig;
        SurfaceFireManager _fireManager;

        void Start()
        {
            _rig = EnsureCameraRig();
            ConfigurePassthrough(_rig);
            BuildSceneUnderstanding();
            BuildFireManager();
            BuildFoamSprayer();
        }

        OVRCameraRig EnsureCameraRig()
        {
            var rig = FindFirstObjectByType<OVRCameraRig>();
            if (rig != null) return rig;

            // Fallback: build a rig in code if the scene has none (keeps the component self-sufficient).
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

            // Clear the eye cameras to fully transparent so passthrough shows through them.
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

            // Generate invisible colliders for detected surfaces. Leaving MeshMaterial null means
            // EffectMesh attaches a MeshCollider but no MeshRenderer, so nothing is drawn over passthrough.
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

        void BuildFireManager()
        {
            var managerGo = new GameObject("SurfaceFireManager");
            _fireManager = managerGo.AddComponent<SurfaceFireManager>();
            // Ignite + start spreading once the room geometry is available.
            MRUK.Instance.RegisterSceneLoadedCallback(_fireManager.OnSceneReady);
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
            var sprayer = nozzle.AddComponent<FoamSprayer>();
            sprayer.Configure();
        }
    }
}
