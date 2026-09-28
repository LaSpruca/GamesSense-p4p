using HapticAI.Net;
using HapticAI.Profiles;
using HapticAI.Scene;
using UnityEngine;

namespace HapticAI.Esp
{
    /// <summary>
    /// Mode B: "the model receives just the force and reed switch, updates the game, and sends
    /// back a PWM." Unlike <see cref="EspTriggerSource"/> (Mode A), the ESP32 reading never touches
    /// the fire sim - it goes straight to the server as a minimal spray reading (no hit-detection
    /// telemetry), Gemini's plan/select loop runs exactly as it does for the real game (see
    /// server/haptic_profiles.py), and its answer both plays the matching profile locally (the
    /// "updates that in game" half) and is sent back to the ESP32 as a 0-100% PWM value over BLE
    /// (the "sends back a pwm" half). Useful for bringing up and testing the physical controller
    /// loop independently of a running fire scene.
    /// </summary>
    public sealed class EspGeminiLoop : MonoBehaviour
    {
        [Tooltip("Raw force reading (0-4095) that counts as a full trigger pull (1.0).")]
        public int forceAtFullTrigger = 3500;
        [Tooltip("Readings per second sent to the server while the reed switch is closed.")]
        public float stateSendHz = 8f;

        EspBleClient _esp;
        HapticSessionClient _client;
        HapticProfileLibrary _library;
        HapticPlayer _player;

        string _loopRole;
        float _loopGain = -1f;
        float _nextStateSendAt;
        bool _sentIdle = true;

        public void Initialise(EspBleClient esp, HapticSessionClient client, HapticProfileLibrary library,
            HapticPlayer player)
        {
            Bind(esp);
            _client = client;
            _library = library;
            _player = player;
            if (_client != null) _client.SelectionReceived += OnSelection;
        }

        void OnDestroy()
        {
            Bind(null);
            if (_client != null) _client.SelectionReceived -= OnSelection;
            _player?.StopSustained();
        }

        void Bind(EspBleClient esp)
        {
            if (_esp != null) _esp.SensorUpdated -= OnSensorUpdated;
            _esp = esp;
            if (_esp != null) _esp.SensorUpdated += OnSensorUpdated;
        }

        void OnSensorUpdated(bool reed, int force)
        {
            if (_client == null) return;
            float trigger = Mathf.Clamp01((float)force / Mathf.Max(1, forceAtFullTrigger));
            float interval = 1f / Mathf.Max(1f, stateSendHz);

            if (reed)
            {
                if (Time.unscaledTime < _nextStateSendAt) return;
                _nextStateSendAt = Time.unscaledTime + interval;
                _sentIdle = false;
                Send(reed: true, trigger);
            }
            else if (!_sentIdle)
            {
                _sentIdle = true;
                Send(reed: false, 0f);
            }
        }

        void Send(bool reed, float trigger)
        {
            // Deliberately just spraying/trigger: everything the fire sim would otherwise supply
            // (hit_rate, hit_quality, ...) is left at its zero value, so the selector's rule falls
            // through to spray_dry/spray_soak - there is no "hitting the fire" without a fire sim.
            _client.Send(new HapticWire.State { spraying = reed, trigger = (float)System.Math.Round(trigger, 2) });
        }

        void OnSelection(HapticWire.Select select)
        {
            if (select == null || select.source != "gemini") return;
            _esp?.SendHaptic(select.pwm);

            if (string.IsNullOrEmpty(select.profile))
            {
                _player?.StopSustained();
                _loopRole = null;
                _loopGain = -1f;
                return;
            }

            var profile = _library?.ProfileFor(select.profile);
            string role = profile != null ? profile.Role : select.profile;
            if (!HapticRoles.IsLoop(role)) return;

            var clip = _library.ClipFor(role, out float profileGain, out _);
            float gain = Mathf.Clamp(select.gain, 0.2f, 1f) * profileGain;
            if (role == _loopRole && Mathf.Abs(gain - _loopGain) <= 0.02f) return;

            _player.SetSustained(clip, gain);
            _loopRole = role;
            _loopGain = gain;
        }
    }
}
