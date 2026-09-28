package com.gamessense.espble;

import android.bluetooth.BluetoothAdapter;
import android.bluetooth.BluetoothDevice;
import android.bluetooth.BluetoothGatt;
import android.bluetooth.BluetoothGattCallback;
import android.bluetooth.BluetoothGattCharacteristic;
import android.bluetooth.BluetoothGattDescriptor;
import android.bluetooth.BluetoothGattService;
import android.bluetooth.BluetoothManager;
import android.bluetooth.BluetoothProfile;
import android.bluetooth.le.BluetoothLeScanner;
import android.bluetooth.le.ScanCallback;
import android.bluetooth.le.ScanFilter;
import android.bluetooth.le.ScanResult;
import android.bluetooth.le.ScanSettings;
import android.content.Context;
import android.os.Handler;
import android.os.Looper;
import android.os.ParcelUuid;
import android.util.Log;

import java.util.Collections;
import java.util.UUID;

import com.unity3d.player.UnityPlayer;

/**
 * BLE central for the ESP32 custom controller. Unity has no built-in BLE central API, so this
 * small plugin does the actual GATT work: scan for the service UUID, connect, subscribe to the
 * sensor characteristic's notifications, and write the haptic command characteristic.
 *
 * One connection at a time, matching the one physical controller a session uses. Called from C#
 * via EspBleClient (Assets/HapticAI/Esp/EspBleClient.cs); the wire format on the ESP32 side is
 * documented in esp32-controller/README.md.
 *
 * Android allows only one outstanding GATT operation per connection, and a write issued while
 * another is in flight is silently dropped. Haptic commands are therefore coalesced: only the most
 * recent one is kept, and it is written as soon as the previous write completes, so a burst of
 * level changes can never leave the motor stuck on a stale value.
 */
@SuppressWarnings({"deprecation", "MissingPermission"})
public class EspBlePlugin {
    private static final String TAG = "EspBlePlugin";
    // The Client Characteristic Configuration Descriptor UUID is fixed by the Bluetooth spec.
    private static final UUID CCCD_UUID = UUID.fromString("00002902-0000-1000-8000-00805f9b34fb");
    private static final long RESCAN_DELAY_MS = 1500;

    /** Primitive-only signatures: AndroidJavaProxy marshals ints/strings reliably, arrays less so. */
    public interface Callback {
        void onSensorData(int reed, int force);
        void onConnected();
        void onDisconnected(String reason);
        void onStatus(String message);
    }

    private final Handler main = new Handler(Looper.getMainLooper());
    private final Object lock = new Object();

    private UUID serviceUuid;
    private UUID sensorCharUuid;
    private UUID commandCharUuid;
    private Callback callback;

    private BluetoothLeScanner scanner;
    private boolean scanning;
    private volatile BluetoothGatt gatt;
    private volatile BluetoothGattCharacteristic commandChar;
    private volatile boolean stopping;

    // Guarded by lock.
    private boolean ready;          // notifications enabled; commands may be written
    private boolean writeInFlight;
    private byte[] pendingCommand;

    public void startScanAndConnect(String serviceUuidStr, String sensorCharUuidStr,
                                    String commandCharUuidStr, Callback cb) {
        this.serviceUuid = UUID.fromString(serviceUuidStr);
        this.sensorCharUuid = UUID.fromString(sensorCharUuidStr);
        this.commandCharUuid = UUID.fromString(commandCharUuidStr);
        this.callback = cb;
        this.stopping = false;
        main.post(this::startScan);
    }

    public void sendCommand(byte[] data) {
        if (data == null) return;
        synchronized (lock) {
            pendingCommand = data.clone();
            if (!ready || writeInFlight) return;
        }
        flushCommand();
    }

    public void disconnect() {
        stopping = true;
        main.removeCallbacksAndMessages(null);
        main.post(this::stopScan);
        synchronized (lock) {
            ready = false;
            writeInFlight = false;
            pendingCommand = null;
        }
        BluetoothGatt g = gatt;
        gatt = null;
        commandChar = null;
        if (g != null) {
            try {
                g.disconnect();
                g.close();
            } catch (SecurityException ignored) {
            }
        }
    }

    // --- Scanning (main looper only) ------------------------------------------------------------

    private void startScan() {
        if (stopping || scanning || gatt != null) return;
        Context ctx = UnityPlayer.currentActivity;
        BluetoothManager manager = ctx != null
                ? (BluetoothManager) ctx.getSystemService(Context.BLUETOOTH_SERVICE) : null;
        BluetoothAdapter adapter = manager != null ? manager.getAdapter() : null;
        if (adapter == null || !adapter.isEnabled()) {
            fail("Bluetooth is off or unavailable");
            scheduleRescan();
            return;
        }
        scanner = adapter.getBluetoothLeScanner();
        if (scanner == null) {
            fail("No BLE scanner on this device");
            scheduleRescan();
            return;
        }

        ScanFilter filter = new ScanFilter.Builder().setServiceUuid(new ParcelUuid(serviceUuid)).build();
        ScanSettings settings = new ScanSettings.Builder()
                .setScanMode(ScanSettings.SCAN_MODE_LOW_LATENCY)
                .build();
        try {
            scanner.startScan(Collections.singletonList(filter), settings, scanCallback);
            scanning = true;
            status("Scanning for ESP32 (service " + serviceUuid + ")");
        } catch (SecurityException e) {
            fail("Missing BLUETOOTH_SCAN permission: " + e.getMessage());
        }
    }

    private void stopScan() {
        if (!scanning || scanner == null) return;
        scanning = false;
        try {
            scanner.stopScan(scanCallback);
        } catch (SecurityException | IllegalStateException ignored) {
        }
    }

    private void scheduleRescan() {
        if (stopping) return;
        main.postDelayed(this::startScan, RESCAN_DELAY_MS);
    }

    private final ScanCallback scanCallback = new ScanCallback() {
        @Override
        public void onScanResult(int callbackType, ScanResult result) {
            if (!scanning || gatt != null) return;
            stopScan();
            BluetoothDevice device = result.getDevice();
            status("Found " + device.getAddress() + " (rssi " + result.getRssi() + "); connecting");
            try {
                gatt = device.connectGatt(UnityPlayer.currentActivity, false, gattCallback,
                        BluetoothDevice.TRANSPORT_LE);
            } catch (SecurityException e) {
                fail("Missing BLUETOOTH_CONNECT permission: " + e.getMessage());
                scheduleRescan();
            }
        }

        @Override
        public void onScanFailed(int errorCode) {
            scanning = false;
            fail("Scan failed, code " + errorCode);
            scheduleRescan();
        }
    };

    // --- GATT (binder thread) -------------------------------------------------------------------

    private final BluetoothGattCallback gattCallback = new BluetoothGattCallback() {
        @Override
        public void onConnectionStateChange(BluetoothGatt g, int status, int newState) {
            if (newState == BluetoothProfile.STATE_CONNECTED) {
                status("Connected; discovering services");
                try {
                    g.discoverServices();
                } catch (SecurityException e) {
                    fail("Missing BLUETOOTH_CONNECT permission: " + e.getMessage());
                }
            } else if (newState == BluetoothProfile.STATE_DISCONNECTED) {
                synchronized (lock) {
                    ready = false;
                    writeInFlight = false;
                }
                try {
                    g.close();
                } catch (SecurityException ignored) {
                }
                if (g == gatt) {
                    gatt = null;
                    commandChar = null;
                }
                if (callback != null) {
                    callback.onDisconnected(stopping ? "stopped" : "link lost (status " + status + "), rescanning");
                }
                main.post(EspBlePlugin.this::scheduleRescan);
            }
        }

        @Override
        public void onServicesDiscovered(BluetoothGatt g, int status) {
            if (status != BluetoothGatt.GATT_SUCCESS) {
                fail("Service discovery failed, status " + status);
                dropLink(g);
                return;
            }
            BluetoothGattService service = g.getService(serviceUuid);
            BluetoothGattCharacteristic sensorChar = service != null ? service.getCharacteristic(sensorCharUuid) : null;
            BluetoothGattCharacteristic cmd = service != null ? service.getCharacteristic(commandCharUuid) : null;
            if (sensorChar == null || cmd == null) {
                fail("ESP32 service or characteristic missing - check the firmware UUIDs");
                dropLink(g);
                return;
            }
            commandChar = cmd;
            try {
                g.setCharacteristicNotification(sensorChar, true);
                BluetoothGattDescriptor cccd = sensorChar.getDescriptor(CCCD_UUID);
                if (cccd == null) {
                    markReady();
                    return;
                }
                cccd.setValue(BluetoothGattDescriptor.ENABLE_NOTIFICATION_VALUE);
                if (!g.writeDescriptor(cccd)) {
                    fail("Could not enable sensor notifications");
                    dropLink(g);
                }
            } catch (SecurityException e) {
                fail("Missing BLUETOOTH_CONNECT permission: " + e.getMessage());
            }
        }

        @Override
        public void onDescriptorWrite(BluetoothGatt g, BluetoothGattDescriptor descriptor, int status) {
            if (!CCCD_UUID.equals(descriptor.getUuid())) return;
            if (status != BluetoothGatt.GATT_SUCCESS) {
                fail("Enabling sensor notifications failed, status " + status);
                dropLink(g);
                return;
            }
            markReady();
        }

        @Override
        public void onCharacteristicWrite(BluetoothGatt g, BluetoothGattCharacteristic characteristic, int status) {
            synchronized (lock) {
                writeInFlight = false;
                if (pendingCommand == null) return;
            }
            flushCommand();
        }

        // API 33+ calls this overload; older releases call the one below. Overriding both covers either.
        @Override
        public void onCharacteristicChanged(BluetoothGatt g, BluetoothGattCharacteristic characteristic, byte[] value) {
            onSensor(characteristic, value);
        }

        @Override
        public void onCharacteristicChanged(BluetoothGatt g, BluetoothGattCharacteristic characteristic) {
            onSensor(characteristic, characteristic.getValue());
        }
    };

    private void onSensor(BluetoothGattCharacteristic characteristic, byte[] data) {
        if (!sensorCharUuid.equals(characteristic.getUuid())) return;
        // [reed (0/1), force_lo, force_hi] little-endian uint16, per esp32_controller.ino.
        if (data == null || data.length < 3 || callback == null) return;
        int reed = data[0] != 0 ? 1 : 0;
        int force = (data[1] & 0xFF) | ((data[2] & 0xFF) << 8);
        callback.onSensorData(reed, force);
    }

    private void markReady() {
        synchronized (lock) {
            ready = true;
            writeInFlight = false;
        }
        status("Sensor notifications enabled");
        if (callback != null) callback.onConnected();
        flushCommand();
    }

    private void flushCommand() {
        BluetoothGatt g = gatt;
        BluetoothGattCharacteristic cmd = commandChar;
        byte[] data;
        synchronized (lock) {
            if (!ready || writeInFlight || pendingCommand == null || g == null || cmd == null) return;
            data = pendingCommand;
            pendingCommand = null;
            writeInFlight = true;
        }
        boolean started;
        try {
            cmd.setValue(data);
            cmd.setWriteType(BluetoothGattCharacteristic.WRITE_TYPE_NO_RESPONSE);
            started = g.writeCharacteristic(cmd);
        } catch (SecurityException e) {
            Log.w(TAG, "writeCharacteristic denied: " + e.getMessage());
            started = false;
        }
        if (!started) {
            // Stack busy: put it back unless a newer command has already replaced it, and retry.
            synchronized (lock) {
                writeInFlight = false;
                if (pendingCommand == null) pendingCommand = data;
            }
            main.postDelayed(this::flushCommand, 10);
        }
    }

    private void dropLink(BluetoothGatt g) {
        try {
            g.disconnect();
        } catch (SecurityException ignored) {
        }
    }

    private void status(String message) {
        Log.i(TAG, message);
        if (callback != null) callback.onStatus(message);
    }

    private void fail(String reason) {
        Log.e(TAG, reason);
        if (callback != null) callback.onDisconnected(reason);
    }
}
