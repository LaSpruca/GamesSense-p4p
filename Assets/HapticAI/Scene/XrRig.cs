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

            foreach (var cam in rig.GetComponentsInChildren<Camera>(true))
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            }
        }
    }
}
