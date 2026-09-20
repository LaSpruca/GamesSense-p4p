using UnityEngine;

namespace HapticAI.Scene
{
    /// <summary>
    /// Finding the camera rig and putting it into passthrough, shared by the two scenes that need
    /// it (setup and training). Both build their contents in code, and both must look the same way
    /// at the real room, so the wrong place for this is "whichever bootstrap was written first".
    /// </summary>
    public static class XrRig
    {
        /// <summary>
        /// The scene's rig, or one built in code if it has none - which keeps a caller working in
        /// a scene that was never hand-authored.
        /// </summary>
        public static OVRCameraRig EnsureCameraRig()
        {
            var rig = Object.FindFirstObjectByType<OVRCameraRig>();
            if (rig != null) return rig;

            var rigGo = new GameObject("OVRCameraRig");
            rigGo.AddComponent<OVRManager>();
            return rigGo.AddComponent<OVRCameraRig>();
        }

        /// <summary>
        /// Insight Passthrough as an underlay, with the eye cameras cleared to transparent so it
        /// shows through them.
        /// </summary>
        public static void ConfigurePassthrough(OVRCameraRig rig)
        {
            if (rig == null) return;

            var manager = rig.GetComponent<OVRManager>();
            if (manager == null) manager = rig.gameObject.AddComponent<OVRManager>();
            manager.isInsightPassthroughEnabled = true;

            var passthrough = rig.GetComponent<OVRPassthroughLayer>();
            if (passthrough == null) passthrough = rig.gameObject.AddComponent<OVRPassthroughLayer>();
            passthrough.overlayType = OVROverlay.OverlayType.Underlay;
            passthrough.projectionSurfaceType = OVRPassthroughLayer.ProjectionSurfaceType.Reconstructed;

            // The transparent clear is deferred until passthrough is really running. Clearing the
            // eye buffer to nothing when there is no underlay behind it does not give a black room,
            // it gives whatever the compositor had there already - a smeared or frozen image that
            // reads as a crash. Passthrough can fail for reasons that have nothing to do with this
            // code (it is off over Link unless the PC app's beta toggle is on, and it needs a
            // manifest feature on device), so the failure has to degrade into something readable.
            var watcher = rig.GetComponent<PassthroughClear>();
            if (watcher == null) watcher = rig.gameObject.AddComponent<PassthroughClear>();
            watcher.Watch(rig);
        }
    }

    /// <summary>
    /// Holds the eye cameras on an opaque clear until <see cref="OVRManager"/> reports passthrough
    /// initialised, then switches them to transparent so the underlay shows through. Passthrough
    /// reports "pending" for a few frames after it is asked for, and can fail outright, so this
    /// cannot be decided once at startup.
    /// </summary>
    sealed class PassthroughClear : MonoBehaviour
    {
        /// <summary>Punches the hole the passthrough underlay is composited into.</summary>
        static readonly Color ShowPassthrough = new Color(0f, 0f, 0f, 0f);
        /// <summary>Near-black, but deliberately not pure black, so a blank room still looks drawn.</summary>
        static readonly Color NoPassthrough = new Color(0.02f, 0.02f, 0.03f, 1f);

        OVRCameraRig _rig;
        bool _transparent;
        bool _applied;

        public void Watch(OVRCameraRig rig)
        {
            _rig = rig;
            _applied = false;
            Apply(transparent: false);
        }

        void Update()
        {
            if (_rig == null) return;
            if (OVRManager.IsInsightPassthroughInitPending()) return;   // not known yet; stay opaque
            Apply(OVRManager.IsInsightPassthroughInitialized());
        }

        void Apply(bool transparent)
        {
            if (_applied && _transparent == transparent) return;
            _applied = true;
            _transparent = transparent;

            foreach (var cam in _rig.GetComponentsInChildren<Camera>(true))
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = transparent ? ShowPassthrough : NoPassthrough;
            }
        }
    }
}
