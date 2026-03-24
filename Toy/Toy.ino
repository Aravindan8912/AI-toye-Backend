/*
 * ESP32-S3 Toy — Jarvis Voice (same flow as Frontend)
 * Connect → Record (button) → Send PCM + EOR → Receive JSON then PCM → Play TTS
 *
 * NETWORK (both PC and Toy must use same WiFi):
 *   - PC: Connect to the WiFi where ipconfig shows 192.168.1.63 (e.g. your home router).
 *   - Toy: Set WIFI_SSID and WIFI_PASS below to that SAME network name and password.
 *   - Then Toy will get 192.168.1.xxx and can reach PC at WS_HOST 192.168.1.63.
 *   If Toy shows IP 10.17.x.x, it is on a different WiFi — change WIFI_SSID to the PC's network.
 *
 * Libraries: ArduinoWebSockets by Links2004, ArduinoJson, Adafruit NeoPixel. Board: ESP32S3 Dev Module.
 */

#include "WiFi.h"
#include <WebSocketsClient.h>
#include "SPIFFS.h"
#include <driver/i2s.h>
#include <ArduinoJson.h>
#include <Adafruit_NeoPixel.h>

// ----------------- I2S MIC -----------------
#define I2S_WS   4
#define I2S_SD   7
#define I2S_SCK  5
// Speaker uses same I2S bus when playing (driver switched mic ↔ spk in code)
#define I2S_DOUT I2S_SD

// ----------------- CONTROL -----------------
#define BUTTON_PIN 2

// ----------------- RGB LED (NeoPixel) -----------------
#define LED_PIN 48
Adafruit_NeoPixel pixel(1, LED_PIN, NEO_GRB + NEO_KHZ800);

// ----------------- BATTERY -----------------
#define BATTERY_PIN 1

// ----------------- AUDIO -----------------
#define SAMPLE_RATE 16000
#define SAMPLE_RATE_REC  SAMPLE_RATE
#define SAMPLE_RATE_PLAY 22050   // Piper default; overwritten by JSON
#define BUFFER_SIZE      256
 #define SEND_CHUNK       4096    // send to server in 4K chunks
 
 // ----------------- WIFI / SERVER -----------------
// Use the SAME WiFi as your PC (the one where PC gets 192.168.1.63). Not a different network.
#ifndef WIFI_SSID
#define WIFI_SSID "aravinth"   // e.g. same SSID as the router where PC is 192.168.1.63
#endif
#ifndef WIFI_PASS
#define WIFI_PASS "12345678901"
#endif
#ifndef WS_HOST
#define WS_HOST "10.17.254.58"   // Your PC IP (ipconfig) — Jarvis backend
#endif
#define WS_PORT 5000
#define WS_PATH "/ws"

#ifndef TOY_NAME
#define TOY_NAME "Jarvis Toy"
#endif

WebSocketsClient webSocket;

bool wsConnected = false;
bool didLogWsNotConnected = false;  // log "WS not connected" only once per button-hold
bool recording = false;
bool expectBinary = false;
bool i2sInstalled = false;   // avoid i2s_driver_uninstall when not installed
uint32_t ttsSampleRate = 22050;
String lastUserText;
String lastBotText;
 
 // Audio buffer for recording (accumulate then send)
 #define MAX_AUDIO_BYTES (16000 * 2 * 30)  // 30 sec at 16kHz 16bit
 uint8_t* audioBuffer = nullptr;
 size_t audioLen = 0;
 
 // Playback buffer (received PCM)
 uint8_t* playBuffer = nullptr;
 size_t playLen = 0;
 
 // ----------------- SERIAL LOG HELPERS -----------------
 void logStep(const char* step, const char* detail = "") {
   Serial.print("[");
   Serial.print(millis());
   Serial.print("] ");
   Serial.print(step);
   if (detail[0]) {
     Serial.print(" ");
     Serial.print(detail);
   }
   Serial.println();
 }
 
 void logBattery() {
   float v = (BATTERY_PIN >= 0) ? (analogRead(BATTERY_PIN) / 4095.0f * 3.3f * 2.0f) : 0.0f;
   int pct = (v >= 4.2f) ? 100 : (v <= 3.0f) ? 0 : (int)((v - 3.0f) * 100.0f / 1.2f);
   Serial.print("[Battery] ");
   Serial.print(v, 2);
   Serial.print(" V, ");
   Serial.print(pct);
   Serial.println("%");
 }
 
 // ----------------- BATTERY -----------------
 float getBatteryVoltage() {
   int adc = analogRead(BATTERY_PIN);
   return (adc / 4095.0f) * 3.3f * 2.0f;
 }
 
 int getBatteryPercentage(float voltage) {
   if (voltage >= 4.2f) return 100;
   if (voltage <= 3.0f) return 0;
   return (int)((voltage - 3.0f) * 100.0f / (4.2f - 3.0f));
 }
 
// ----------------- I2S MIC (record) -----------------
void setupI2SMic() {
  if (i2sInstalled) {
    i2s_driver_uninstall(I2S_NUM_0);
    i2sInstalled = false;
  }
  i2s_config_t cfg = {
     .mode = (i2s_mode_t)(I2S_MODE_MASTER | I2S_MODE_RX),
     .sample_rate = SAMPLE_RATE_REC,
     .bits_per_sample = I2S_BITS_PER_SAMPLE_16BIT,
     .channel_format = I2S_CHANNEL_FMT_ONLY_LEFT,
     .communication_format = I2S_COMM_FORMAT_STAND_MSB,
     .dma_buf_count = 4,
     .dma_buf_len = 512
   };
   i2s_pin_config_t pin = {
     .bck_io_num = I2S_SCK,
     .ws_io_num = I2S_WS,
     .data_out_num = -1,
     .data_in_num = I2S_SD
   };
  i2s_driver_install(I2S_NUM_0, &cfg, 0, NULL);
  i2s_set_pin(I2S_NUM_0, &pin);
  i2sInstalled = true;
  logStep("I2S MIC", "16kHz 16bit");
}

// ----------------- I2S SPEAKER (play) -----------------
void setupI2SSpeaker(uint32_t sampleRate) {
  if (i2sInstalled) {
    i2s_driver_uninstall(I2S_NUM_0);
    i2sInstalled = false;
  }
  i2s_config_t cfg = {
     .mode = (i2s_mode_t)(I2S_MODE_MASTER | I2S_MODE_TX),
     .sample_rate = sampleRate,
     .bits_per_sample = I2S_BITS_PER_SAMPLE_16BIT,
     .channel_format = I2S_CHANNEL_FMT_ONLY_LEFT,
     .communication_format = I2S_COMM_FORMAT_STAND_MSB,
     .dma_buf_count = 4,
     .dma_buf_len = 512
   };
   i2s_pin_config_t pin = {
     .bck_io_num = I2S_SCK,
     .ws_io_num = I2S_WS,
     .data_out_num = I2S_DOUT,
     .data_in_num = -1
   };
  i2s_driver_install(I2S_NUM_0, &cfg, 0, NULL);
  i2s_set_pin(I2S_NUM_0, &pin);
  i2sInstalled = true;
  logStep("I2S SPK", String(sampleRate).c_str());
}
 
 // ----------------- RECORD (non-blocking: start / stop in loop) -----------------
void recordStart() {
  if (!audioBuffer) return;
  if (!wsConnected || !webSocket.isConnected()) {
    if (!didLogWsNotConnected) {
      didLogWsNotConnected = true;
      logStep("RECORD", "abort (WS not connected)");
      Serial.println("  -> Wait for \"WS connected\" before holding button. Is backend running?");
    }
    return;
  }
  didLogWsNotConnected = false;
  audioLen = 0;
  setupI2SMic();
  pixel.setPixelColor(0, pixel.Color(0, 255, 0));  // green = recording
  pixel.show();
  recording = true;
  logStep("RECORD", "start (hold = green on, release = green off & send)");
}
 
void recordStop() {
  if (!recording) return;
  if (i2sInstalled) {
    i2s_driver_uninstall(I2S_NUM_0);
    i2sInstalled = false;
  }
  pixel.clear();
  pixel.show();
  recording = false;
  logStep("RECORD", "stop");
  Serial.print("  -> bytes: ");
  Serial.println(audioLen);
}
 
 void recordPoll() {
   if (!recording || !audioBuffer) return;
   int16_t buf[BUFFER_SIZE];
   size_t bytesRead;
   if (i2s_read(I2S_NUM_0, buf, sizeof(buf), &bytesRead, 0) == ESP_OK && bytesRead > 0) {
     if (audioLen + bytesRead <= MAX_AUDIO_BYTES) {
       memcpy(audioBuffer + audioLen, buf, bytesRead);
       audioLen += bytesRead;
     }
   }
 }
 
 // ----------------- SEND AUDIO OVER WEBSOCKET -----------------
void sendAudioToServer() {
  if (!wsConnected || !webSocket.isConnected()) {
    logStep("SEND", "abort (not connected)");
    Serial.println("  -> Is Jarvis backend running? Start: dotnet run (in Backend folder)");
    Serial.print("  -> Then connect to ws://");
    Serial.print(WS_HOST);
    Serial.println(":5000/ws");
    return;
  }
  if (audioLen == 0) {
    logStep("SEND", "abort (no audio)");
    return;
  }
 
   logStep("SEND", "start");
   Serial.print("  -> total bytes: ");
   Serial.println(audioLen);
 
   for (size_t i = 0; i < audioLen; i += SEND_CHUNK) {
     size_t n = (i + SEND_CHUNK <= audioLen) ? SEND_CHUNK : (audioLen - i);
     webSocket.sendBIN(audioBuffer + i, n);
     Serial.print("  -> chunk ");
     Serial.print(i / SEND_CHUNK + 1);
     Serial.print(" sent ");
     Serial.print(n);
     Serial.println(" bytes");
   }
 
   webSocket.sendTXT("EOR");
   logStep("SEND", "EOR sent");
 }
 
 // ----------------- PLAY PCM FROM MEMORY -----------------
 void playPCM(uint8_t* data, size_t len, uint32_t sampleRate) {
   if (!data || len == 0) return;
   setupI2SSpeaker(sampleRate);
   logStep("PLAY", "start");
   Serial.print("  -> bytes: ");
   Serial.print(len);
   Serial.print(", rate: ");
   Serial.println(sampleRate);
 
   size_t written;
   size_t pos = 0;
   while (pos < len) {
     size_t toWrite = (len - pos > 1024) ? 1024 : (len - pos);
     i2s_write(I2S_NUM_0, data + pos, toWrite, &written, portMAX_DELAY);
     pos += written;
   }
 
  if (i2sInstalled) {
    i2s_driver_uninstall(I2S_NUM_0);
    i2sInstalled = false;
  }
  logStep("PLAY", "done");
}
 
 // ----------------- WEBSOCKET EVENT -----------------
 void webSocketEvent(WStype_t type, uint8_t* payload, size_t length) {
   switch (type) {
    case WStype_DISCONNECTED:
      wsConnected = false;
      logStep("WS", "disconnected");
      break;
 
    case WStype_CONNECTED:
      wsConnected = true;
      didLogWsNotConnected = false;
      logStep("WS", "connected");
      break;
 
     case WStype_TEXT: {
       if (length == 0) break;
       logStep("WS RX TEXT", String(length).c_str());
 
       if (length >= 20 && strncmp((char*)payload, "Connected. ClientId:", 20) == 0) {
         Serial.println("  -> server welcome (ready for audio)");
         break;
       }
 
       StaticJsonDocument<1024> doc;
       if (deserializeJson(doc, payload, length) == DeserializationError::Ok) {
         lastUserText = doc["userText"] | "";
         lastBotText = doc["botText"] | "";
         ttsSampleRate = doc["ttsSampleRate"] | 22050;
         if (ttsSampleRate < 8000 || ttsSampleRate > 48000) ttsSampleRate = 22050;
         Serial.println("  -> userText: " + lastUserText);
         Serial.println("  -> botText: " + lastBotText);
         Serial.print("  -> ttsSampleRate: ");
         Serial.println(ttsSampleRate);
         Serial.println("  -> Playing on ESP32-S3 speaker...");
         expectBinary = true;
       }
       break;
     }
 
     case WStype_BIN:
       logStep("WS RX BIN", String(length).c_str());
       if (expectBinary && length > 0) {
         if (playLen < length) {
           if (playBuffer) free(playBuffer);
           playBuffer = (uint8_t*)malloc(length);
           playLen = playBuffer ? length : 0;
         }
         if (playBuffer && playLen >= length) {
           memcpy(playBuffer, payload, length);
           playLen = length;
           expectBinary = false;
           playPCM(playBuffer, playLen, ttsSampleRate);
         } else if (!playBuffer && length > 0) {
           Serial.println("  !! PLAY buffer alloc failed (TTS too long?). Need PSRAM or shorter response.");
           expectBinary = false;
         }
       }
       break;

     case WStype_ERROR:
       logStep("WS", "error");
       break;
 
     default:
       break;
   }
 }
 
// ----------------- SETUP -----------------
// Order: 1) Toy name  2) Battery  3) WS connect. Then: hold = green + record, release = off + send.
void setup() {
  Serial.begin(115200);
  delay(500);

  Serial.println();
  Serial.println("================");
  Serial.print("Toy: ");
  Serial.println(TOY_NAME);
  Serial.println("================");
  logBattery();

  pinMode(BUTTON_PIN, INPUT_PULLUP);
  pixel.begin();
  pixel.setBrightness(48);
  pixel.clear();
  pixel.show();

  audioBuffer = (uint8_t*)malloc(MAX_AUDIO_BYTES);
  playBuffer = (uint8_t*)malloc(32000);
  playLen = 32000;
  if (!audioBuffer) {
    Serial.println("FATAL: audioBuffer alloc failed");
    return;
  }

  logStep("WIFI", "connecting");
  WiFi.mode(WIFI_STA);
  WiFi.begin(WIFI_SSID, WIFI_PASS);
  int w = 0;
  while (WiFi.status() != WL_CONNECTED && w < 30) {
    delay(500);
    Serial.print(".");
    w++;
  }
  Serial.println();
  if (WiFi.status() != WL_CONNECTED) {
    logStep("WIFI", "failed");
    return;
  }
  logStep("WIFI", "OK");
  Serial.print("  -> IP: ");
  Serial.println(WiFi.localIP());

  Serial.print("  -> Backend must be at ");
  Serial.print(WS_HOST);
  Serial.println(" (same WiFi / same subnet as this device)");
  if (WiFi.localIP()[0] != 192 || WiFi.localIP()[1] != 168) {
    Serial.println("  !! This device is not on 192.168.x.x — connect to same WiFi as PC to get 192.168.1.x");
  }

  logStep("WS", "connecting...");
  webSocket.begin(WS_HOST, WS_PORT, WS_PATH);
  webSocket.onEvent(webSocketEvent);
  webSocket.setReconnectInterval(3000);

  Serial.println("\n--- Ready: HOLD button = green on & record, RELEASE = green off & send ---\n");
}
 
// ----------------- LOOP -----------------
// 1) Toy name, 2) Battery, 3) WS connect — then: HOLD = green on & record, RELEASE = green off & send.
void loop() {
  webSocket.loop();

  if (WiFi.status() != WL_CONNECTED) {
    delay(1000);
    return;
  }

  bool pressed = (digitalRead(BUTTON_PIN) == LOW);
  if (!pressed) didLogWsNotConnected = false;

  if (pressed && !recording) {
    recordStart();
  }

  if (recording) {
    recordPoll();
    if (!pressed) {
      recordStop();
      sendAudioToServer();
    }
  }

  delay(20);
}
 