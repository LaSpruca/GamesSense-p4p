using System;
using System.Threading;
using UnityEngine;
using UnityEngine.Android;

namespace HapticAI.Esp
{
    /// <summary>
    /// Bridges to the ESP32 custom controller over Bluetooth LE: one GATT service with a sensor
    /// characteristic (reed switch + force, notified by the ESP32) and a command characteristic
    /// (haptic PWM, written by us). The wire format is documented in
    /// <c>esp32-controller/README.md</c> and implemented on the other end by
    /// <c>esp32-controller/esp32_controller.ino</c>.
    ///
    /// BLE central on Android has no Unity built-in, so the actual GATT work happens in a small
    /// native Android plugin (Assets/Plugins/Android/EspBle) and this class is just the C# face of
    /// it - callbacks arrive via AndroidJavaProxy and get forwarded as C# events on the main thread.
    /// The plugin keeps scanning and reconnects on its own if the link drops.
    /// In the Editor (no Android BLE stack), Connect() fails softly: SensorUpdated never fires and
    /// SendHaptic() is a no-op, so game code doesn't need an #if UNITY_ANDROID at every call site.
    /// </summary>
    public sealed class EspBleClient : IDisposable
    {
        public const string ServiceUuid = "6f6a0001-6873-4de8-8c1f-3a5b6d1e2f00";
        public const string SensorCharUuid = "6f6a0002-6873-4de8-8c1f-3a5b6d1e2f00";
        public const string CommandCharUuid = "6f6a0003-6873-4de8-8c1f-3a5b6d1e2f00";

        const byte CmdSetHaptic = 0x01;
        const byte CmdStop = 0x00;

        /// <summary>Reed switch state (true = closed/magnet present) and force reading, 0-4095.</summary>
        public event Action<bool, int> SensorUpdated;
        public event Action Connected;
        public event Action<string> Disconnected;

        public bool IsConnected { get; private set; }

        /// <summary>Latest one-line state for diagnostics, e.g. "scanning...", "connected".</summary>
        public string Status { get; private set; } = "not started";

        AndroidJavaObject _plugin;
        bool _connectStarted;
        int _lastPercent = -1;
        readonly SynchronizationContext _mainThread = SynchronizationContext.Current;

        public void Connect()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // BLUETOOTH_SCAN/BLUETOOTH_CONNECT are runtime-dangerous permissions from Android 12
            // (API 31) on - declaring them in the manifest is not enough, the user has to grant
            // them once the app is running, or every GATT call throws SecurityException.
            Debug.Log($"[EspBle] Connect() called; permissions granted: {HasAllPermissions()}");
            if (HasAllPermissions())
            {
                ConnectNative();
                return;
            }

            Status = "waiting for Bluetooth permission";
            var callbacks = new PermissionCallbacks();
            // Fires once per permission; only the grant that completes the set connects.
            callbacks.PermissionGranted += _ => Post(() =>
            {
                if (HasAllPermissions()) ConnectNative();
            });
            callbacks.PermissionDenied += permission => Post(() =>
            {
                Debug.LogWarning($"[EspBle] Permission denied: {permission}");
                Status = "Bluetooth permission denied";
                Disconnected?.Invoke($"Permission denied: {permission}");
            });
            Permission.RequestUserPermissions(RequiredPermissions, callbacks);
#else
            Status = "Android only";
            Debug.LogWarning("[EspBle] BLE is Android-only; running without the ESP32 controller.");
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        static readonly string[] RequiredPermissions =
        {
            "android.permission.BLUETOOTH_SCAN",
            "android.permission.BLUETOOTH_CONNECT",
        };

        static bool HasAllPermissions()
        {
            foreach (var permission in RequiredPermissions)
                if (!Permission.HasUserAuthorizedPermission(permission)) return false;
            return true;
        }

        void ConnectNative()
        {
            if (_connectStarted) return;
            _connectStarted = true;
            try
            {
                _plugin = new AndroidJavaObject("com.gamessense.espble.EspBlePlugin");
                _plugin.Call("startScanAndConnect", ServiceUuid, SensorCharUuid, CommandCharUuid,
                    new Callback(this));
            }
            catch (Exception e)
            {
                _connectStarted = false;
                _plugin = null;
                Status = "plugin failed to start";
                Debug.LogError($"[EspBle] Connect failed: {e}");
            }
        }
#endif

        public void Disconnect()
        {
            if (_plugin != null)
            {
                // Leave the motor off rather than at whatever it was last told.
                _plugin.Call("sendCommand", new byte[] { CmdStop, 0 });
                _plugin.Call("disconnect");
            }
            _plugin = null;
            _connectStarted = false;
            _lastPercent = -1;
            IsConnected = false;
        }

        /// <summary>
        /// Sets the haptic motor's drive strength, 0-100%. Repeats of the same value are skipped;
        /// the plugin coalesces the rest so only the newest level is ever in flight.
        /// </summary>
        public void SendHaptic(int percent)
        {
            percent = Mathf.Clamp(percent, 0, 100);
            if (percent == _lastPercent) return;
            _lastPercent = percent;
            if (_plugin == null) return;
            _plugin.Call("sendCommand", new byte[] { percent > 0 ? CmdSetHaptic : CmdStop, (byte)percent });
        }

        public void Dispose() => Disconnect();

        // --- Native callbacks, marshalled onto the Unity main thread -------------------------

        void OnSensorData(int reed, int force)
        {
            Post(() => SensorUpdated?.Invoke(reed != 0, force));
        }

        void OnConnected()
        {
            Debug.Log("[EspBle] Connected to ESP32");
            Post(() =>
            {
                IsConnected = true;
                Status = "connected";
                // The ESP32 zeroes its motor on disconnect, so resend whatever level is current.
                int level = _lastPercent;
                _lastPercent = -1;
                if (level > 0) SendHaptic(level);
                Connected?.Invoke();
            });
        }

        void OnDisconnected(string reason)
        {
            Debug.LogWarning($"[EspBle] Disconnected: {reason}");
            Post(() =>
            {
                IsConnected = false;
                Status = reason;
                Disconnected?.Invoke(reason);
            });
        }

        void OnStatus(string message)
        {
            Debug.Log($"[EspBle] {message}");
            Post(() => { if (!IsConnected) Status = message; });
        }

        void Post(Action action)
        {
            if (_mainThread != null) _mainThread.Post(_ => action(), null);
            else action();
        }

        /// <summary>Implements the Java-side callback interface; forwards straight back to the client.</summary>
        sealed class Callback :
#if UNITY_ANDROID && !UNITY_EDITOR
            AndroidJavaProxy
#else
            object
#endif
        {
            readonly EspBleClient _owner;

#if UNITY_ANDROID && !UNITY_EDITOR
            public Callback(EspBleClient owner) : base("com.gamessense.espble.EspBlePlugin$Callback")
            {
                _owner = owner;
            }

            // Names/signatures must match EspBlePlugin.Callback in the Android plugin exactly;
            // AndroidJavaProxy dispatches by reflection.
            public void onSensorData(int reed, int force) => _owner.OnSensorData(reed, force);
            public void onConnected() => _owner.OnConnected();
            public void onDisconnected(string reason) => _owner.OnDisconnected(reason);
            public void onStatus(string message) => _owner.OnStatus(message);
#else
            public Callback(EspBleClient owner) { _owner = owner; }
#endif
        }
    }
}
