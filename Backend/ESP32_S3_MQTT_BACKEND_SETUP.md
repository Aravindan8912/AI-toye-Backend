# ESP32-S3 to Backend MQTT Connection Guide

This guide explains how to connect an ESP32-S3 to your Jarvis backend through MQTT.

## 1) MQTT contract used by this backend

From current backend code and config:

- Incoming audio topic (ESP32 -> backend): `jarvis/{clientId}/audio/in`
- Role topic (ESP32 -> backend): `jarvis/{clientId}/mode`
- Text response topic (backend -> ESP32): `jarvis/{clientId}/audio/out`
- Raw TTS audio topic (backend -> ESP32): `jarvis/{clientId}/audio/out/wav`

`{clientId}` can be any device ID string, for example: `esp32s3-livingroom`.

## 2) Configure backend MQTT settings

Edit `Backend/appsettings.json`:

```json
"MQTT": {
  "Enabled": true,
  "BrokerHost": "192.168.1.20",
  "BrokerPort": 1883,
  "UserName": "mqtt_user",
  "Password": "mqtt_password",
  "ClientId": "jarvis-backend",
  "TopicIn": "jarvis/+/audio/in",
  "TopicOutTemplate": "jarvis/{0}/audio/out"
}
```

Important:

- Do not use `localhost` for devices on Wi-Fi LAN.
- `BrokerHost` must be reachable from both backend machine and ESP32-S3.
- `TopicIn` should stay `jarvis/+/audio/in` so one backend handles many devices.

## 3) Run an MQTT broker

You need a broker (for example Mosquitto).

- Broker host: same IP you put in `MQTT:BrokerHost`
- Port: `1883` (default)
- If auth enabled, use matching username/password in backend and ESP32

## 4) ESP32-S3 topic mapping

For `clientId = esp32s3-01`:

- Publish mic bytes to: `jarvis/esp32s3-01/audio/in`
- Publish role text to: `jarvis/esp32s3-01/mode` with payload:
  - `ironman`, or
  - `spiderman`, or
  - `captain`
- Subscribe to:
  - `jarvis/esp32s3-01/audio/out` (JSON metadata)
  - `jarvis/esp32s3-01/audio/out/wav` (raw PCM bytes from backend TTS)

## 5) ESP32-S3 Arduino example

This minimal sketch uses `WiFi.h` + `PubSubClient`.
It demonstrates connect, subscribe, publish mode, and publish test audio bytes.

```cpp
#include <WiFi.h>
#include <PubSubClient.h>

// Wi-Fi
const char* WIFI_SSID = "YOUR_WIFI";
const char* WIFI_PASS = "YOUR_PASS";

// MQTT
const char* MQTT_HOST = "192.168.1.20";
const int   MQTT_PORT = 1883;
const char* MQTT_USER = "mqtt_user";      // set "" if no auth
const char* MQTT_PASS = "mqtt_password";  // set "" if no auth

const char* DEVICE_ID = "esp32s3-01";

WiFiClient wifiClient;
PubSubClient mqtt(wifiClient);

String topicIn;
String topicMode;
String topicOut;
String topicWav;

void mqttCallback(char* topic, byte* payload, unsigned int length) {
  String t = String(topic);
  if (t == topicOut) {
    // JSON payload: {"userText":"...","botText":"...","ttsSampleRate":22050}
    String json;
    json.reserve(length + 1);
    for (unsigned int i = 0; i < length; i++) json += (char)payload[i];
    Serial.println("[MQTT] JSON OUT:");
    Serial.println(json);
  } else if (t == topicWav) {
    // Raw PCM bytes (WAV header removed by backend).
    // Send to I2S/DAC playback pipeline in your firmware.
    Serial.printf("[MQTT] WAV bytes: %u\n", length);
  }
}

void connectWifi() {
  WiFi.mode(WIFI_STA);
  WiFi.begin(WIFI_SSID, WIFI_PASS);
  Serial.print("Connecting WiFi");
  while (WiFi.status() != WL_CONNECTED) {
    delay(500);
    Serial.print(".");
  }
  Serial.println();
  Serial.print("WiFi IP: ");
  Serial.println(WiFi.localIP());
}

void connectMqtt() {
  mqtt.setServer(MQTT_HOST, MQTT_PORT);
  mqtt.setCallback(mqttCallback);

  while (!mqtt.connected()) {
    Serial.print("Connecting MQTT...");
    bool ok;
    if (strlen(MQTT_USER) == 0) {
      ok = mqtt.connect(DEVICE_ID);
    } else {
      ok = mqtt.connect(DEVICE_ID, MQTT_USER, MQTT_PASS);
    }

    if (ok) {
      Serial.println("connected");
      mqtt.subscribe(topicOut.c_str());
      mqtt.subscribe(topicWav.c_str());
      mqtt.publish(topicMode.c_str(), "spiderman"); // set role once on connect
    } else {
      Serial.printf("failed, rc=%d retry in 2s\n", mqtt.state());
      delay(2000);
    }
  }
}

void setup() {
  Serial.begin(115200);

  topicIn   = "jarvis/" + String(DEVICE_ID) + "/audio/in";
  topicMode = "jarvis/" + String(DEVICE_ID) + "/mode";
  topicOut  = "jarvis/" + String(DEVICE_ID) + "/audio/out";
  topicWav  = topicOut + "/wav";

  connectWifi();
  connectMqtt();
}

void loop() {
  if (WiFi.status() != WL_CONNECTED) {
    connectWifi();
  }
  if (!mqtt.connected()) {
    connectMqtt();
  }
  mqtt.loop();

  // Example: publish fake audio every 5 seconds.
  // Replace this with actual microphone chunk bytes.
  static unsigned long lastMs = 0;
  if (millis() - lastMs > 5000) {
    lastMs = millis();
    uint8_t fakeAudio[320];
    memset(fakeAudio, 0, sizeof(fakeAudio));
    mqtt.publish(topicIn.c_str(), fakeAudio, sizeof(fakeAudio));
    Serial.printf("[MQTT] Sent %u bytes to %s\n", sizeof(fakeAudio), topicIn.c_str());
  }
}
```

## 6) Backend run and quick test

1. Start broker.
2. Start backend (`dotnet run`).
3. Start ESP32 firmware.
4. Check backend logs for:
   - `MQTT listener connected`
   - `MQTT received audio from <clientId>`
   - `MQTT published to jarvis/<clientId>/audio/out and .../wav`

## 7) Troubleshooting

- ESP32 cannot connect MQTT:
  - Verify broker IP/port and Wi-Fi network.
  - If broker auth is enabled, verify user/pass on both backend and ESP32.
- Backend connects but ESP32 receives nothing:
  - Ensure same `clientId` is used in both publish and subscribe topics.
  - Ensure backend MQTT `Enabled` is true.
- Everything uses `localhost`:
  - Replace with LAN IP address. `localhost` works only on the same machine.

## 8) Payload notes

- `audio/in` payload: binary audio chunk bytes from ESP32.
- `audio/out` payload: JSON text with `userText`, `botText`, `ttsSampleRate`.
- `audio/out/wav` payload: raw PCM bytes for playback.

