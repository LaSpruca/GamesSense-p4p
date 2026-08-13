using HapticAI.Core;
using UnityEngine;

namespace HapticAI.Scene
{
    /// <summary>
    /// Plays a <see cref="HapticClipData"/> on a Touch controller. This is the only place that
    /// touches the Oculus buffered-haptics API: it wraps the 8-bit amplitude buffer in an
    /// <c>OVRHapticsClip</c> and queues it on the appropriate channel. Using the buffered API
    /// (not <c>OVRInput.SetControllerVibration</c>) is what lets an arbitrary generated waveform
    /// actually play. OVRManager drives <c>OVRHaptics.Process()</c> each frame.
    /// </summary>
    public sealed class HapticPlayer
    {
        /// <summary>The controller haptic sample rate to target when converting waveforms (~320 Hz).</summary>
        public static int SampleRateHz
        {
            get
            {
                int r = OVRHaptics.Config.SampleRateHz;
                return r > 0 ? r : 320;
            }
        }

        bool _warnedNoHaptics;

        public void Play(HapticClipData data, HapticHand hand = HapticHand.Right)
        {
            if (data == null || data.Amplitudes.Length == 0) return;

            // OVRHaptics.Config stays zeroed until the runtime reports a haptics-capable controller.
            // That is the permanent state under the Meta XR Simulator, and briefly true on a headset
            // before initialisation. OVRHapticsClip's constructor divides by SampleSizeInBytes, so
            // without this guard every ignite/spread/spray tick throws DivideByZeroException — once
            // per frame while spraying — and takes the caller's event handler down with it.
            if (OVRHaptics.Config.SampleSizeInBytes <= 0)
            {
                if (!_warnedNoHaptics)
                {
                    _warnedNoHaptics = true;
                    Debug.LogWarning("[HapticAI] No haptics-capable controller reported by the runtime " +
                                     "(normal in the XR Simulator) — clips will be generated and cached " +
                                     "but not played.");
                }
                return;
            }

            var clip = new OVRHapticsClip(data.Amplitudes, data.Amplitudes.Length);
            var channel = hand == HapticHand.Left ? OVRHaptics.LeftChannel : OVRHaptics.RightChannel;
            // Clear + Queue replaces whatever is currently playing with this clip, so discrete cues
            // and rapid spray ticks stay responsive instead of piling up in the queue.
            channel.Clear();
            channel.Queue(clip);
        }
    }
}
