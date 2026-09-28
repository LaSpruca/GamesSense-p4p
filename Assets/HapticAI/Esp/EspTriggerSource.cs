using FireSim;
using UnityEngine;

namespace HapticAI.Esp
{
    /// <summary>
    /// Mode A: "the controller controls what happens in-game." The ESP32's reed switch stands in
    /// for the trigger being held at all, and its force sensor (0-4095) stands in for how hard - the
    /// same two numbers <see cref="FoamSprayer"/> would otherwise read from the Quest's analog
    /// trigger. Nothing else changes: the existing fire sim, profile selection and Gemini haptics
    /// keep running exactly as they do with the real controller.
    ///
    /// Attach this next to the FoamSprayer, assign an <see cref="EspBleClient"/>, and set
    /// <c>foamSprayer.TriggerSource = this</c> (e.g. from a small bootstrap script that also decides
    /// which of the two ESP modes is active).
    /// </summary>
    public sealed class EspTriggerSource : MonoBehaviour, IExtinguisherTriggerSource
    {
        [Tooltip("Raw force reading (0-4095) that counts as a full trigger pull (1.0).")]
        public int forceAtFullTrigger = 3500;

        EspBleClient _esp;
        bool _reed;
        int _force;
        bool _hasReading;

        public void Bind(EspBleClient esp)
        {
            if (_esp != null) _esp.SensorUpdated -= OnSensorUpdated;
            _esp = esp;
            if (_esp != null) _esp.SensorUpdated += OnSensorUpdated;
        }

        void OnDestroy() => Bind(null);

        float _nextLogAt;

        void OnSensorUpdated(bool reed, int force)
        {
            _reed = reed;
            _force = force;
            _hasReading = true;

            if (Time.unscaledTime < _nextLogAt) return;
            _nextLogAt = Time.unscaledTime + 1f;
            TryGetTrigger(out float trigger);
            Debug.Log($"[EspBle] sensor: reed={(reed ? 1 : 0)} force={force} -> trigger {trigger:0.00}");
        }

        public bool TryGetTrigger(out float trigger)
        {
            if (!_hasReading || !_reed)
            {
                trigger = 0f;
                return _hasReading;   // a real "released" reading beats falling back to OVRInput.
            }
            trigger = Mathf.Clamp01((float)_force / Mathf.Max(1, forceAtFullTrigger));
            return true;
        }
    }
}
