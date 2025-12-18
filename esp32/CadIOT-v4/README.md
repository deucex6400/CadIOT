
# ESP32 + Azure SDK for Embedded C (Arduino) — activateRelay

This sketch connects an ESP32 to Azure IoT Hub using the **Azure SDK for C (Arduino)** and implements:

- **Direct Method** `activateRelay` (turns relay ON, returns `{ "status": "relay_on" }` with 200)
- **Cloud-to-Device (C2D) fallback** when payload contains `{ "cmd": "activateRelay" }`

## Files
- `AlertRelay_ESP32_AZSDK.ino` — main sketch
- `iot_configs.h` — fill with Wi‑Fi & IoT Hub details
- `AzIoTSasToken.h/.cpp` — SAS token helper (HMAC + base64), used by the official Arduino Embedded C samples

## Prerequisites
1. **Arduino IDE** with ESP32 board support.
2. Install **Azure SDK for C** from *Library Manager* (search "Azure SDK for C").
3. An **Azure IoT Hub** with a device registered (Device ID + Primary Key).

## Configure
Open `iot_configs.h` and set:
```c
#define IOT_CONFIG_WIFI_SSID       "YOUR_WIFI_SSID"
#define IOT_CONFIG_WIFI_PASSWORD   "YOUR_WIFI_PASSWORD"
#define IOT_CONFIG_IOTHUB_FQDN     "BGVfd-IOT-Hub.azure-devices.net"
#define IOT_CONFIG_DEVICE_ID       "AlertV1-Dev2"
#define IOT_CONFIG_DEVICE_KEY      "<DEVICE PRIMARY KEY>"
```
> Do **not** commit secrets to source control.

## Build & Upload
1. Open the folder in Arduino IDE.
2. Select your ESP32 board and COM port.
3. `Sketch → Upload`.

## Test the direct method
Using Azure CLI (with the `azure-iot` extension):
```bash
az iot hub invoke-device-method   --hub-name BGVfd-IOT-Hub   --device-id AlertV1-Dev2   --method-name activateRelay   --method-payload '{}'
```
Expect status **200** and payload `{ "status": "relay_on" }`.

## Notes
- SAS token renews hourly in samples; this sketch generates a SAS on connect. You can regenerate and reconnect when `IsExpired()` returns true.
- Direct Methods arrive on `$iothub/methods/POST/{method}/?$rid={rid}`; responses publish to `$iothub/methods/res/{status}/?$rid={rid}`.
- C2D messages subscribe to `devices/{deviceId}/messages/devicebound/#`.

## Hardware
Relay is on **GPIO 23**. Sketch drives it `HIGH` for ON. Adjust as needed.

## License
MIT — see headers.
