using HapticAI.Core;
using UnityEngine;

namespace HapticAI.Scene
{
    /// <summary>
    /// Plays <see cref="HapticClipData"/> on the Touch controllers. This is the only place that
    /// touches the Oculus buffered-haptics API: it wraps 8-bit amplitude buffers in
    /// <c>OVRHapticsClip</c>s and queues them. Using the buffered API (not
    /// <c>OVRInput.SetControllerVibration</c>) is what lets an arbitrary generated waveform
    /// actually play. OVRManager drives <c>OVRHaptics.Process()</c> each frame.
    ///
    /// It distinguishes two kinds of playback, which the previous Clear-then-Queue-everything
    /// approach could not:
    ///
    ///   * <b>Cues</b> (<see cref="PlayCue"/>) - ignite / spread / extinguish. Discrete, and must
    ///     play to their full length.
    ///   * <b>Sustained</b> (<see cref="SetSustained"/>) - the extinguisher spray. Continuous for
    ///     as long as the trigger is held.
    ///
    /// Why that distinction matters: the spray fires a fresh request every 100 ms. Clearing the
    /// channel and queueing a full 1 s clip each time meant only the first 100 ms of any clip ever
    /// reached the controller - the same attack transient at 10 Hz, which feels like one flat
    /// rumble no matter what was generated - and any fire cue queued while the trigger was held was
    /// wiped within 100 ms. Here the sustained clip is fed as small looping slices, so it plays
    /// seamlessly and stops within one slice of the trigger releasing, while a cue preempts it,
    /// plays in full, and then lets the spray resume.
    /// </summary>
    public sealed class HapticPlayer
    {
        /// <summary>
        /// Length of one sustained slice. Must comfortably exceed a frame (13.9 ms at 72 Hz) so the
        /// queue never runs dry between Ticks, and stay short enough that releasing the trigger
        /// stops the buzz promptly.
        /// </summary>
        const float SliceSeconds = 0.04f;
        /// <summary>How far ahead of realtime to keep the sustained queue topped up.</summary>
        const float LookaheadSeconds = 0.1f;
        /// <summary>Backstop so a bad clock reading can't spin the top-up loop.</summary>
        const int MaxSlicesPerTick = 8;

        /// <summary>The controller haptic sample rate to target when converting waveforms (~320 Hz).</summary>
        public static int SampleRateHz
        {
            get
            {
                int r = OVRHaptics.Config.SampleRateHz;
                return r > 0 ? r : 320;
            }
        }

        /// <summary>
        /// False when the runtime reports no haptics-capable controller. That is the permanent state
        /// under the Meta XR Simulator, and briefly true on a headset before initialisation, so the
        /// UI surfaces it rather than leaving "I feel nothing" ambiguous.
        /// </summary>
        public bool IsAvailable => OVRHaptics.Config.SampleSizeInBytes > 0;

        readonly Output _left = new Output(() => OVRHaptics.LeftChannel);
        readonly Output _right = new Output(() => OVRHaptics.RightChannel);
        bool _warnedNoHaptics;
        bool _loggedAvailable;

        /// <summary>
        /// Feeds the sustained channels. Must be called once per frame from a MonoBehaviour -
        /// <see cref="FireHapticRelay"/> does it - because it reads <c>Time.realtimeSinceStartup</c>.
        /// </summary>
        public void Tick()
        {
            if (!CheckAvailable()) return;
            float now = Time.realtimeSinceStartup;
            _left.TopUp(now);
            _right.TopUp(now);
        }

        /// <summary>
        /// Play a discrete clip in full, interrupting whatever is currently playing.
        /// <paramref name="gain"/> scales the amplitudes (1 = as rendered).
        /// </summary>
        public void PlayCue(HapticClipData data, float gain = 1f, HapticHand hand = HapticHand.Right)
        {
            if (data == null || data.Amplitudes.Length == 0) return;
            if (!CheckAvailable()) return;

            byte[] samples = data.Amplitudes;
            if (gain < 0.999f || gain > 1.001f)
            {
                // OVRHapticsClip keeps the array by reference, so scale into a fresh buffer.
                samples = new byte[data.Amplitudes.Length];
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = Scale(data.Amplitudes[i], gain);
            }

            var output = OutputFor(hand);
            output.Channel.Preempt(new OVRHapticsClip(samples, samples.Length));
            // Hold the sustained loop off until the cue has had time to finish.
            output.BusyUntil = Time.realtimeSinceStartup + data.DurationSeconds;
        }

        /// <summary>
        /// Start (or swap) the continuously looping clip for a hand. Swapping mid-spray keeps the
        /// current read position, so a spray clip upgrading from procedural fallback to the real
        /// generated one does not click. <paramref name="gain"/> is applied per slice, so a gain
        /// change needs no re-render either.
        /// </summary>
        public void SetSustained(HapticClipData data, float gain = 1f, HapticHand hand = HapticHand.Right)
        {
            if (data == null || data.Amplitudes.Length == 0) return;
            if (!CheckAvailable()) return;
            OutputFor(hand).SetLoop(data.Amplitudes, gain);
        }

        static byte Scale(byte amplitude, float gain)
        {
            float v = amplitude * gain;
            return (byte)(v >= 255f ? 255 : v <= 0f ? 0 : (int)(v + 0.5f));
        }

        /// <summary>
        /// Stop looping for a hand. Deliberately does NOT clear the channel: up to one slice
        /// (~40 ms) of already-queued spray drains naturally, which is inaudible, whereas clearing
        /// would also cut short any cue playing at that moment.
        /// </summary>
        public void StopSustained(HapticHand hand = HapticHand.Right)
        {
            OutputFor(hand).ClearLoop();
        }

        /// <summary>Silence everything immediately - used when a run ends or the scene tears down.</summary>
        public void StopAll()
        {
            _left.ClearLoop();
            _right.ClearLoop();
            if (!IsAvailable) return;
            OVRHaptics.LeftChannel.Clear();
            OVRHaptics.RightChannel.Clear();
        }

        Output OutputFor(HapticHand hand) => hand == HapticHand.Left ? _left : _right;

        /// <summary>
        /// OVRHaptics.Config stays zeroed until the runtime reports a haptics-capable controller.
        /// OVRHapticsClip's constructor divides by SampleSizeInBytes, so without this guard every
        /// cue and spray slice throws DivideByZeroException and takes the caller's event handler
        /// down with it. OVRManager reloads the config every frame, so this recovers on its own once
        /// a controller connects.
        /// </summary>
        bool CheckAvailable()
        {
            if (!IsAvailable)
            {
                if (!_warnedNoHaptics)
                {
                    _warnedNoHaptics = true;
                    Debug.LogWarning("[HapticAI] No haptics-capable controller reported by the runtime " +
                                     "(normal in the XR Simulator) - clips will be generated and cached " +
                                     "but not played.");
                }
                return false;
            }

            if (!_loggedAvailable)
            {
                _loggedAvailable = true;
                Debug.Log($"[HapticAI] Controller haptics available: {OVRHaptics.Config.SampleRateHz} Hz, " +
                          $"{OVRHaptics.Config.SampleSizeInBytes} byte(s)/sample.");
            }
            return true;
        }

        /// <summary>
        /// One controller's playback state. Tracks when everything queued so far will have finished
        /// (<see cref="BusyUntil"/>) because OVRHaptics exposes no way to ask how much is pending.
        /// </summary>
        sealed class Output
        {
            readonly System.Func<OVRHaptics.OVRHapticsChannel> _channel;
            byte[] _loop;
            float _gain = 1f;
            int _read;

            public Output(System.Func<OVRHaptics.OVRHapticsChannel> channel) => _channel = channel;

            public OVRHaptics.OVRHapticsChannel Channel => _channel();
            public float BusyUntil { get; set; }

            public void SetLoop(byte[] samples, float gain)
            {
                _loop = samples;
                _gain = gain < 0f ? 0f : gain;
                if (_read >= samples.Length) _read = 0;
            }

            public void ClearLoop()
            {
                _loop = null;
                _read = 0;
            }

            public void TopUp(float now)
            {
                // An idle or underrun channel: anything queued has already played out.
                if (BusyUntil < now) BusyUntil = now;
                if (_loop == null || _loop.Length == 0) return;

                int rate = SampleRateHz;
                int sliceSamples = Mathf.Max(1, Mathf.RoundToInt(SliceSeconds * rate));
                float sliceSeconds = sliceSamples / (float)rate;

                for (int i = 0; i < MaxSlicesPerTick && BusyUntil - now < LookaheadSeconds; i++)
                {
                    // A fresh buffer per slice: OVRHapticsClip keeps the array by reference and
                    // reads it over the following frames, so it cannot be pooled and refilled.
                    // ~25 x 13-byte allocations per second while spraying is not worth optimising.
                    var buffer = new byte[sliceSamples];
                    for (int s = 0; s < sliceSamples; s++)
                    {
                        buffer[s] = Scale(_loop[_read], _gain);
                        _read = (_read + 1) % _loop.Length;
                    }
                    Channel.Queue(new OVRHapticsClip(buffer, sliceSamples));
                    BusyUntil += sliceSeconds;
                }
            }
        }
    }
}
