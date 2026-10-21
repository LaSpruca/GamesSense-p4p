/*
 * ESP32-S3 custom fire-extinguisher controller.
 *
 * Reads an FSR402 (squeeze force, 0-4095) and a reed switch (0/1), and drives a DRV2605L haptic
 * driver over I2C. Talks to the Quest headset (Unity) as a BLE peripheral - see README.md in this
 * folder for the exact GATT layout and byte formats; the C# side is
 * Assets/HapticAI/Scripts/Esp/EspBleClient.cs and the Android BLE plugin under
 * Assets/Plugins/Android/EspBle.
 *
 * Wiring (from the project owner):
 *   FSR402 (with 100k pulldown to GND) -> GPIO4  (ADC)
 *   Reed switch module (DO)            -> GPIO5  (digital in)
 *   DRV2605L SDA                       -> GPIO8
 *   DRV2605L SCL                       -> GPIO9
 *
 * Board package: esp32 by Espressif (BLE support is built in - no extra library to install).
 */

#include <Wire.h>
#include <BLEDevice.h>
#include <BLEServer.h>
#include <BLEUtils.h>
#include <BLE2902.h>

// --- Pins --------------------------------------------------------------------------------------

static const int PIN_FSR = 4;
static const int PIN_REED = 5;
static const int PIN_SDA = 8;
static const int PIN_SCL = 9;

// Reed modules commonly report LOW when the magnet is present (active-low DO). Flip this if a
// held/closed reed reads as 0 instead of 1 on your module.
static const bool REED_ACTIVE_LOW = false;

// --- BLE GATT layout (must match EspBleClient.cs) -----------------------------------------------
// Sensor characteristic (notify): 3 bytes [reed (0/1), force_lo, force_hi] - force is uint16 LE, 0-4095.
// Command characteristic (write): 2 bytes [cmd, value] - cmd 0x01 = set haptic %, 0x00 = stop.

#define SERVICE_UUID       "6f6a0001-6873-4de8-8c1f-3a5b6d1e2f00"
#define SENSOR_CHAR_UUID   "6f6a0002-6873-4de8-8c1f-3a5b6d1e2f00"
#define COMMAND_CHAR_UUID  "6f6a0003-6873-4de8-8c1f-3a5b6d1e2f00"

static const uint8_t CMD_STOP = 0x00;
static const uint8_t CMD_SET_HAPTIC = 0x01;

// How often the sensor notification is sent while nothing has changed much.
static const uint32_t SENSOR_PERIOD_MS = 40;   // ~25 Hz
// Force must move by at least this much to trigger an early notification (keeps the radio quiet
// on a steady squeeze, but still feels responsive on a fast one).
static const int FORCE_CHANGE_THRESHOLD = 24;

// --- DRV2605L (minimal register-level driver; no external library needed) ----------------------
//
// Matches the register sequence the Adafruit_DRV2605 library uses for begin()+useERM()+
// setMode(REALTIME), since that sequence is confirmed working on this board; nothing here is
// guessed. DRIVE_MIN/DRIVE_MAX mirror the calibration from the earlier working sketch - below
// DRIVE_MIN the motor doesn't spin at all, so a naive 0-100% -> 0-127 linear map goes silent for
// most of its low range.

static const uint8_t DRV2605_ADDR = 0x5A;
enum Drv2605Reg : uint8_t {
    REG_STATUS = 0x00,
    REG_MODE = 0x01,
    REG_RTP = 0x02,
    REG_FEEDBACK_CONTROL = 0x1A,
    REG_CONTROL3 = 0x1D,
};

// Real-time drive range (0-127). Recalibrate DRIVE_MIN from serial output if your motor differs.
static const uint8_t DRIVE_MIN = 35;   // lowest RTP value that actually spins this ERM
static const uint8_t DRIVE_MAX = 127;

bool drv2605Write(uint8_t reg, uint8_t value) {
    Wire.beginTransmission(DRV2605_ADDR);
    Wire.write(reg);
    Wire.write(value);
    return Wire.endTransmission() == 0;
}

bool drv2605Init() {
    Wire.beginTransmission(DRV2605_ADDR);
    if (Wire.endTransmission() != 0) {
        Serial.println("DRV2605L not found. Check VIN, GND, SDA (8), SCL (9).");
        return false;
    }
    drv2605Write(REG_MODE, 0x00);              // out of standby
    drv2605Write(REG_FEEDBACK_CONTROL, 0x36);  // POR default with ERM selected (bit7=0)
    drv2605Write(REG_CONTROL3, 0x80);          // POR default (0xA0) with ERM_OPEN_LOOP cleared
    drv2605Write(REG_MODE, 0x05);              // real-time playback (RTP) mode
    drv2605Write(REG_RTP, 0);                  // start silent
    return true;
}

int drv2605SetPercent(uint8_t percent) {
    if (percent > 100) percent = 100;
    // 0% stays fully off; anything above 0 is floored at DRIVE_MIN so it's actually felt.
    uint8_t magnitude = percent == 0 ? 0
        : (uint8_t)map(percent, 1, 100, DRIVE_MIN, DRIVE_MAX);
    drv2605Write(REG_RTP, magnitude);
    return magnitude;
}

// --- BLE plumbing --------------------------------------------------------------------------------

BLECharacteristic *sensorChar;
volatile bool bleConnected = false;

class ServerCallbacks : public BLEServerCallbacks {
    void onConnect(BLEServer *server) override { bleConnected = true; }
    void onDisconnect(BLEServer *server) override {
        bleConnected = false;
        drv2605SetPercent(0);           // don't leave the motor buzzing if the link drops
        server->getAdvertising()->start();
    }
};

class CommandCallbacks : public BLECharacteristicCallbacks {
    void onWrite(BLECharacteristic *characteristic) override {
        // getValue() returns std::string on core 2.x and Arduino String on core 3.x; `auto` works
        // with both (length() and operator[] exist on each).
        auto value = characteristic->getValue();
        if (value.length() < 2) {
            Serial.printf("command write ignored: %d byte(s), need 2\n", (int)value.length());
            return;
        }
        uint8_t cmd = (uint8_t)value[0];
        uint8_t level = (uint8_t)value[1];
        if (cmd == CMD_SET_HAPTIC) {
            int magnitude = drv2605SetPercent(level);
            Serial.printf("haptic: %d%% -> RTP %d\n", level, magnitude);
        } else if (cmd == CMD_STOP) {
            drv2605SetPercent(0);
            Serial.println("haptic: stop");
        } else {
            Serial.printf("command write ignored: unknown cmd 0x%02X\n", cmd);
        }
    }
};

void setup() {
    Serial.begin(115200);
    delay(500);
    pinMode(PIN_FSR, INPUT);
    pinMode(PIN_REED, REED_ACTIVE_LOW ? INPUT_PULLUP : INPUT);
    analogReadResolution(12);   // 0-4095, matches the FSR reading the protocol promises

    Wire.begin(PIN_SDA, PIN_SCL);
    if (!drv2605Init()) {
        while (1) delay(100);   // halt: nothing downstream works without the haptic driver
    }

    BLEDevice::init("HapticAI-ESP32");
    BLEServer *server = BLEDevice::createServer();
    server->setCallbacks(new ServerCallbacks());

    BLEService *service = server->createService(SERVICE_UUID);

    sensorChar = service->createCharacteristic(
        SENSOR_CHAR_UUID, BLECharacteristic::PROPERTY_NOTIFY | BLECharacteristic::PROPERTY_READ);
    sensorChar->addDescriptor(new BLE2902());

    BLECharacteristic *commandChar = service->createCharacteristic(
        COMMAND_CHAR_UUID, BLECharacteristic::PROPERTY_WRITE | BLECharacteristic::PROPERTY_WRITE_NR);
    commandChar->setCallbacks(new CommandCallbacks());

    service->start();
    BLEAdvertising *advertising = BLEDevice::getAdvertising();
    advertising->addServiceUUID(SERVICE_UUID);
    advertising->start();

    Serial.println("HapticAI-ESP32 advertising");
}

void loop() {
    static uint32_t nextSendAt = 0;
    static int lastForce = -1000;
    static bool lastReed = false;

    int rawReed = digitalRead(PIN_REED);
    bool reed = REED_ACTIVE_LOW ? (rawReed == LOW) : (rawReed == HIGH);
    int force = analogRead(PIN_FSR);   // 0-4095 on the ESP32-S3's 12-bit ADC

    uint32_t now = millis();
    bool changedEnough = (reed != lastReed) || (abs(force - lastForce) >= FORCE_CHANGE_THRESHOLD);
    if (bleConnected && (changedEnough || now >= nextSendAt)) {
        uint8_t packet[3] = {(uint8_t)(reed ? 1 : 0), (uint8_t)(force & 0xFF), (uint8_t)((force >> 8) & 0xFF)};
        sensorChar->setValue(packet, sizeof(packet));
        sensorChar->notify();
        lastForce = force;
        lastReed = reed;
        nextSendAt = now + SENSOR_PERIOD_MS;
    }

    delay(5);
}