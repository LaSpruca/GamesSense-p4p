# ESP32 custom controller

Firmware for the bracket-mounted ESP32-S3 controller: a Quest controller rides alongside it for
motion tracking, while this board supplies the squeeze/trigger input (FSR402 + reed switch) and
the haptic motor (DRV2605L). It never touches Wi-Fi - it's a BLE peripheral, and the Quest headset
(running Unity) is the BLE central that connects to it.

```
ESP32 (BLE peripheral) <---- BLE ----> Quest headset (Unity, BLE central) <---- WebSocket ----> Python server (Gemini)
```

## Wiring

| Part | Pin |
|---|---|
| FSR402 (with 100k pulldown to GND) | GPIO4 (ADC) |
| Reed switch module (DO) | GPIO5 (digital in) |
| DRV2605L SDA | GPIO8 |
| DRV2605L SCL | GPIO9 |

Board package: **esp32 by Espressif** in the Arduino IDE / arduino-cli. BLE (`BLEDevice`, `BLE2902`,
...) ships with that package - nothing extra to install.

If a closed reed switch reads as 0 instead of 1, flip `REED_ACTIVE_LOW` in `esp32_controller.ino`.

## BLE protocol

One GATT service, two characteristics. UUIDs are shared with the Unity side
(`Assets/HapticAI/Esp/EspBleClient.cs`) and must match exactly if you change them here.

| | UUID | Properties | Payload |
|---|---|---|---|
| Service | `6f6a0001-6873-4de8-8c1f-3a5b6d1e2f00` | - | - |
| Sensor characteristic | `6f6a0002-6873-4de8-8c1f-3a5b6d1e2f00` | Notify, Read | 3 bytes: `[reed, force_lo, force_hi]` |
| Command characteristic | `6f6a0003-6873-4de8-8c1f-3a5b6d1e2f00` | Write, Write-without-response | 2 bytes: `[cmd, value]` |

**Sensor packet** (ESP32 -> headset), sent ~25 Hz or immediately on a large change:
- `reed` (byte 0): `0` open / `1` closed.
- `force` (bytes 1-2, uint16 little-endian): raw FSR reading, `0`-`4095`.

**Command packet** (headset -> ESP32):
- `cmd` (byte 0): `0x01` = set haptic strength, `0x00` = stop.
- `value` (byte 1): `0`-`100`, percent motor drive. Rescaled to the DRV2605L's RTP register
  (open-loop ERM real-time-playback mode - see `drv2605Init()`/`drv2605SetPercent()` in the
  sketch), floored at `DRIVE_MIN` (default 35) rather than mapped linearly from 0: most small ERMs
  simply don't spin below some minimum RTP value, so a naive 0-100% -> 0-127 map goes silent for
  most of its low range. Recalibrate `DRIVE_MIN`/`DRIVE_MAX` from the serial monitor for your motor.
  If your motor is an LRA rather than an ERM, set the feedback-control bit and resonant frequency
  per the DRV2605L datasheet before shipping.

The DRV2605L is reset to 0% and the reed/force stream stops notifying automatically on BLE
disconnect, so a dropped link never leaves the motor buzzing.

## How the two game modes use this

Two Unity components consume the sensor stream differently - see
`Assets/HapticAI/Esp/`:

- **`EspTriggerSource`** (Mode A - the controller drives the game): reed + force are read as a
  drop-in replacement for the Quest's analog trigger (`FoamSprayer.TriggerSource`). Everything
  downstream - the fire sim, profile selection, Gemini haptics - runs unchanged; only where the
  trigger value comes from is different. The motor mirrors whatever the Quest controller plays
  (AI loop and fire cues, or the fixed rumble in the static condition) as a 0-100% level.
- **`EspGeminiLoop`** (Mode B - the model drives the motor): reed + force are sent to the Python
  server as a minimal spray reading (bypassing the fire sim's hit-detection telemetry). Gemini's
  existing plan/select loop (`server/haptic_profiles.py`) picks a profile and gain as it always
  does; the resulting `pwm` field (gain rescaled to 0-100, added to the `select` message in
  `server/haptic_profiles.py`'s `Selection.pwm`) is written straight back to the command
  characteristic, driving the physical motor from Gemini's decision.

Pick the mode with **Esp Mode** on the `FireTrainingBootstrap` component in
`Assets/Scenes/FireTraining.unity`. The first launch asks for the Nearby devices (Bluetooth)
permission; allow it. Connection progress is logged under the `EspBle`/`EspBlePlugin` tags
(`adb logcat -s Unity EspBlePlugin`).
