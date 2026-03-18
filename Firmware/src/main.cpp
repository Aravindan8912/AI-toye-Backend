#include <Arduino.h>
#include <WiFi.h>
#include <ArduinoWebsockets.h>
#include <driver/i2s.h>

using namespace websockets;

// WIFI
#define WIFI_SSID "aravinth"
#define WIFI_PASSWORD "12345678901"

// WS
#define WS_URL "ws://10.17.254.58:5000/ws"

// MIC
#define MIC_BCK 16
#define MIC_WS 17
#define MIC_SD 18

// SPEAKER
#define SPK_BCK 12
#define SPK_WS 13
#define SPK_DOUT 11

#define SAMPLE_RATE 16000
#define TTS_RATE 22050

WebsocketsClient client;
bool connected = false;

#define I2S_MIC I2S_NUM_0
#define I2S_SPK I2S_NUM_1

// Queue: each item is 257 bytes — byte 0 = length (0 means 256), bytes 1..256 = PCM
#define QUEUE_ITEM_SIZE 257
#define QUEUE_LEN 32
QueueHandle_t audioQueue;

// ================= MIC =================
void initMic() {
  i2s_config_t cfg = {
    .mode = (i2s_mode_t)(I2S_MODE_MASTER | I2S_MODE_RX),
    .sample_rate = SAMPLE_RATE,
    .bits_per_sample = I2S_BITS_PER_SAMPLE_32BIT,
    .channel_format = I2S_CHANNEL_FMT_ONLY_LEFT,
    .communication_format = I2S_COMM_FORMAT_STAND_I2S,
    .dma_buf_count = 4,
    .dma_buf_len = 256
  };

  i2s_pin_config_t pin = {
    .bck_io_num = MIC_BCK,
    .ws_io_num = MIC_WS,
    .data_out_num = -1,
    .data_in_num = MIC_SD
  };

  i2s_driver_install(I2S_MIC, &cfg, 0, NULL);
  i2s_set_pin(I2S_MIC, &pin);
}

// ================= SPEAKER =================
void initSpeaker() {
  i2s_config_t cfg = {
    .mode = (i2s_mode_t)(I2S_MODE_MASTER | I2S_MODE_TX),
    .sample_rate = TTS_RATE,
    .bits_per_sample = I2S_BITS_PER_SAMPLE_16BIT,
    .channel_format = I2S_CHANNEL_FMT_ONLY_LEFT,
    .communication_format = I2S_COMM_FORMAT_STAND_I2S,
    .dma_buf_count = 8,
    .dma_buf_len = 512
  };

  i2s_pin_config_t pin = {
    .bck_io_num = SPK_BCK,
    .ws_io_num = SPK_WS,
    .data_out_num = SPK_DOUT,
    .data_in_num = -1
  };

  i2s_driver_install(I2S_SPK, &cfg, 0, NULL);
  i2s_set_pin(I2S_SPK, &pin);
}

// ================= WIFI =================
void connectWiFi() {
  WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
  while (WiFi.status() != WL_CONNECTED) {
    delay(500);
  }
  Serial.println("WiFi OK");
}

// ================= SPEAKER TASK =================
void speakerTask(void *param) {
  uint8_t buffer[QUEUE_ITEM_SIZE];

  while (true) {
    if (xQueueReceive(audioQueue, buffer, portMAX_DELAY)) {
      size_t len = buffer[0] ? (size_t)buffer[0] : 256;
      if (len > 0) {
        size_t written;
        i2s_write(I2S_SPK, buffer + 1, len, &written, portMAX_DELAY);
      }
    }
  }
}

// ================= MIC TASK =================
void micTask(void *param) {
  int32_t micBuf[128];
  uint8_t out[256];
  size_t bytesRead;

  while (true) {
    if (!connected) {
      vTaskDelay(100);
      continue;
    }

    i2s_read(I2S_MIC, micBuf, sizeof(micBuf), &bytesRead, portMAX_DELAY);

    int samples = bytesRead / 4;
    int idx = 0;

    for (int i = 0; i < samples; i++) {
      int16_t s = micBuf[i] >> 11;
      s *= 2;

      out[idx++] = s & 0xFF;
      out[idx++] = (s >> 8) & 0xFF;

      if (idx >= 256) {
        client.sendBinary((const char*)out, idx);
        idx = 0;
      }
    }

    if (idx > 0)
      client.sendBinary((const char*)out, idx);

    delay(1);
  }
}

// ================= WS =================
void connectWS() {

  client.onMessage([](WebsocketsMessage msg) {
    if (msg.isBinary()) {
      auto data = msg.rawData();
      const char* raw = data.c_str();
      size_t len = data.length();

      // Skip WAV header (RIFF....WAVEfmt data)
      if (len > 44 && raw[0]=='R' && raw[1]=='I' && raw[2]=='F' && raw[3]=='F') {
        raw += 44;
        len -= 44;
      }
      Serial.printf("TTS recv %u bytes\n", (unsigned)len);

      const uint8_t* ptr = (const uint8_t*)raw;
      while (len > 0) {
        uint8_t chunk[QUEUE_ITEM_SIZE];
        size_t copy = (len > 256) ? 256 : len;
        chunk[0] = (copy == 256) ? 0 : (uint8_t)copy;
        memcpy(chunk + 1, ptr, copy);

        xQueueSend(audioQueue, chunk, portMAX_DELAY);

        ptr += copy;
        len -= copy;
      }
    }
  });

  connected = client.connect(WS_URL);
}

// ================= SETUP =================
void setup() {
  Serial.begin(115200);

  audioQueue = xQueueCreate(QUEUE_LEN, QUEUE_ITEM_SIZE);

  connectWiFi();
  initMic();
  initSpeaker();
  connectWS();

  xTaskCreatePinnedToCore(micTask, "mic", 4096, NULL, 1, NULL, 0);
  xTaskCreatePinnedToCore(speakerTask, "spk", 4096, NULL, 1, NULL, 1);
}

// ================= LOOP =================
void loop() {
  if (!client.available()) {
    connected = false;
    delay(2000);
    connectWS();
  }

  client.poll();
  delay(10);
}