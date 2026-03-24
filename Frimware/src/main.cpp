#include <Arduino.h>
#include <WiFi.h>
#include <PubSubClient.h>
#include <Adafruit_NeoPixel.h>
#include <driver/i2s.h>

const char *WIFI_SSID = "aravinth";
const char *WIFI_PASSWORD = "12345678901";
const char *MQTT_BROKER = "172.16.74.58";
const uint16_t MQTT_PORT = 1883;
const char *DEVICE_ID = "esp32s3-1";
const char *TOY_NAME = "Spiderman Toy";
constexpr uint8_t RGB_PIN = 48;
constexpr uint16_t PIXEL_COUNT = 1;
constexpr uint8_t BUTTON_PIN = 12; 
Adafruit_NeoPixel pixel(PIXEL_COUNT, RGB_PIN, NEO_GRB + NEO_KHZ800);

WiFiClient wifiClient;
PubSubClient mqtt(wifiClient);

char topicAudioIn[48];
char topicMode[48];
char topicAudioOut[56];
char topicAudioOutWav[64];
bool g_buttonPressed = false;

constexpr int I2S_WS = 4;
constexpr int I2S_SCK = 5;
constexpr int I2S_AMP_DIN = 6; 
constexpr int I2S_MIC_SD = 7; 
constexpr int SAMPLE_RATE_REC = 16000;
constexpr int BUFFER_SAMPLES = 256;
constexpr size_t MAX_RECORD_PCM_BYTES = 48000;
constexpr size_t MAX_PLAY_PCM_BYTES = 65536;
constexpr int MIC_DIGITAL_GAIN = 2;
constexpr int MIC_SAMPLE_SHIFT = 13;
constexpr i2s_comm_format_t MIC_COMM_FORMAT = I2S_COMM_FORMAT_STAND_I2S;
constexpr i2s_channel_fmt_t MIC_CHANNEL_FORMAT = I2S_CHANNEL_FMT_ONLY_LEFT;

constexpr size_t MIN_PCM_BYTES_TO_SEND = 6400;       
constexpr int MIN_PEAK_ABS = 300;                    
constexpr uint32_t MIN_MEAN_SAMPLE_SQ = 800;         
constexpr int CLIP_PEAK_THRESHOLD = 31000;           

uint8_t *audioBuffer = nullptr;
size_t audioLen = 0;
int16_t maxAbsSample = 0;
uint64_t recordSumSqPcm = 0;
bool recording = false;
bool i2sInstalled = false;
uint8_t *playBuffer = nullptr;
size_t playLen = 0;
bool playPending = false;
uint32_t currentTtsSampleRate = 22050;

void setLedColor(uint8_t r, uint8_t g, uint8_t b)
{
  pixel.setPixelColor(0, pixel.Color(r, g, b));
  pixel.show();
}

void setupI2SMic()
{
  if (i2sInstalled)
  {
    i2s_driver_uninstall(I2S_NUM_0);
    i2sInstalled = false;
  }

  i2s_config_t cfg = {
      .mode = (i2s_mode_t)(I2S_MODE_MASTER | I2S_MODE_RX),
      .sample_rate = SAMPLE_RATE_REC,
      .bits_per_sample = I2S_BITS_PER_SAMPLE_32BIT,
      .channel_format = MIC_CHANNEL_FORMAT,
      .communication_format = MIC_COMM_FORMAT,
      .intr_alloc_flags = 0,
      .dma_buf_count = 4,
      .dma_buf_len = 512,
      .use_apll = false,
      .tx_desc_auto_clear = false,
      .fixed_mclk = 0};

  i2s_pin_config_t pin = {
      .bck_io_num = I2S_SCK,
      .ws_io_num = I2S_WS,
      .data_out_num = -1,
      .data_in_num = I2S_MIC_SD};

  i2s_driver_install(I2S_NUM_0, &cfg, 0, nullptr);
  i2s_set_pin(I2S_NUM_0, &pin);
  i2sInstalled = true;
}

void setupI2SSpeaker(uint32_t sampleRate)
{
  if (i2sInstalled)
  {
    i2s_driver_uninstall(I2S_NUM_0);
    i2sInstalled = false;
  }

  i2s_config_t cfg = {
      .mode = (i2s_mode_t)(I2S_MODE_MASTER | I2S_MODE_TX),
      .sample_rate = sampleRate,
      .bits_per_sample = I2S_BITS_PER_SAMPLE_16BIT,
      .channel_format = I2S_CHANNEL_FMT_ONLY_LEFT,
      .communication_format = I2S_COMM_FORMAT_STAND_I2S,
      .intr_alloc_flags = 0,
      .dma_buf_count = 4,
      .dma_buf_len = 512,
      .use_apll = false,
      .tx_desc_auto_clear = true,
      .fixed_mclk = 0};

  i2s_pin_config_t pin = {
      .bck_io_num = I2S_SCK,
      .ws_io_num = I2S_WS,
      .data_out_num = I2S_AMP_DIN,
      .data_in_num = -1};

  i2s_driver_install(I2S_NUM_0, &cfg, 0, nullptr);
  i2s_set_pin(I2S_NUM_0, &pin);
  i2sInstalled = true;
}

void playQueuedPcm()
{
  if (!playPending || !playBuffer || playLen == 0)
    return;

  playPending = false;
  Serial.print("Playing queued audio, bytes=");
  Serial.print(playLen);
  Serial.print(", rate=");
  Serial.println(currentTtsSampleRate);

  setupI2SSpeaker(currentTtsSampleRate);
  size_t written = 0;
  size_t pos = 0;
  while (pos < playLen)
  {
    const size_t chunk = (playLen - pos > 1024) ? 1024 : (playLen - pos);
    i2s_write(I2S_NUM_0, playBuffer + pos, chunk, &written, portMAX_DELAY);
    pos += written;
  }
  if (i2sInstalled)
  {
    i2s_driver_uninstall(I2S_NUM_0);
    i2sInstalled = false;
  }
}

void recordStart()
{
  if (recording || !audioBuffer)
    return;
  audioLen = 0;
  maxAbsSample = 0;
  recordSumSqPcm = 0;
  setupI2SMic();
  {
    int32_t warmup[BUFFER_SAMPLES];
    for (int w = 0; w < 40; w++)
    {
      size_t br = 0;
      (void)i2s_read(I2S_NUM_0, warmup, sizeof(warmup), &br, pdMS_TO_TICKS(80));
    }
  }
  recording = true;
  setLedColor(0, 255, 0);
  Serial.println("Button pushed, recording start (green)");
}

void recordPoll()
{
  if (!recording || !audioBuffer)
    return;

  int32_t raw[BUFFER_SAMPLES];
  size_t bytesRead = 0;
  if (i2s_read(I2S_NUM_0, raw, sizeof(raw), &bytesRead, pdMS_TO_TICKS(40)) == ESP_OK && bytesRead > 0)
  {
    const size_t samples = bytesRead / sizeof(int32_t);
    for (size_t i = 0; i < samples; i++)
    {
      int32_t s = (raw[i] >> MIC_SAMPLE_SHIFT) * MIC_DIGITAL_GAIN;
      if (s > 32767)
        s = 32767;
      else if (s < -32768)
        s = -32768;
      const int16_t pcm = static_cast<int16_t>(s);
      const int32_t ps = static_cast<int32_t>(pcm);
      recordSumSqPcm += static_cast<uint64_t>(ps * ps);
      const int16_t absPcm = pcm >= 0 ? pcm : static_cast<int16_t>(-pcm);
      if (absPcm > maxAbsSample)
        maxAbsSample = absPcm;

      if (audioLen + sizeof(int16_t) <= MAX_RECORD_PCM_BYTES)
      {
        audioBuffer[audioLen++] = static_cast<uint8_t>(pcm & 0xFF);
        audioBuffer[audioLen++] = static_cast<uint8_t>((pcm >> 8) & 0xFF);
      }
      else
      {
        break;
      }
    }
  }
}

void recordStopAndSend()
{
  if (!recording)
    return;

  if (i2sInstalled)
  {
    i2s_driver_uninstall(I2S_NUM_0);
    i2sInstalled = false;
  }
  recording = false;
  setLedColor(255, 0, 0);

  const uint32_t sampleCount = static_cast<uint32_t>(audioLen / 2);
  const uint32_t meanSq = (sampleCount > 0) ? static_cast<uint32_t>(recordSumSqPcm / sampleCount) : 0;

  Serial.print("Button released, default red. bytes=");
  Serial.print(audioLen);
  Serial.print(", samples=");
  Serial.print(sampleCount);
  Serial.print(", peak=");
  Serial.print(maxAbsSample);
  Serial.print(", meanSq=");
  Serial.println(meanSq);

  if (!mqtt.connected())
  {
    Serial.println("Send skipped: MQTT not connected");
    return;
  }
  if (audioLen == 0)
  {
    Serial.println("Send skipped: no audio captured");
    return;
  }
  if (audioLen < MIN_PCM_BYTES_TO_SEND)
  {
    Serial.printf("Send skipped: clip too short (%u bytes, need >= %u)\n",
                  static_cast<unsigned>(audioLen), static_cast<unsigned>(MIN_PCM_BYTES_TO_SEND));
    return;
  }
  if (maxAbsSample < MIN_PEAK_ABS || meanSq < MIN_MEAN_SAMPLE_SQ)
  {
    Serial.println("Send skipped: audio looks SILENT (low peak or meanSq). Check MIC wiring, L/R pin, MIC_COMM_FORMAT / MIC_CHANNEL_FORMAT / MIC_SAMPLE_SHIFT in main.cpp.");
    return;
  }
  if (maxAbsSample >= CLIP_PEAK_THRESHOLD)
  {
    Serial.println("Warning: audio is clipping (peak near 32767). Lower MIC_DIGITAL_GAIN or increase MIC_SAMPLE_SHIFT; fix may improve Whisper.");
  }

  if (mqtt.publish(topicAudioIn, audioBuffer, audioLen, false))
    Serial.println("Audio OK — sent to MQTT (jarvis/.../audio/in)");
  else
    Serial.println("Audio publish failed (MQTT buffer size / connection?)");
}

void buildTopics()
{
  snprintf(topicAudioIn, sizeof(topicAudioIn), "jarvis/%s/audio/in", DEVICE_ID);
  snprintf(topicMode, sizeof(topicMode), "jarvis/%s/mode", DEVICE_ID);
  snprintf(topicAudioOut, sizeof(topicAudioOut), "jarvis/%s/audio/out", DEVICE_ID);
  snprintf(topicAudioOutWav, sizeof(topicAudioOutWav), "jarvis/%s/audio/out/wav", DEVICE_ID);
}

void mqttCallback(char *topic, byte *payload, unsigned int length)
{
  Serial.printf("[MQTT] topic=%s len=%u\n", topic, length);

  if (strstr(topic, "/audio/out") != nullptr && strstr(topic, "/wav") == nullptr)
  {
    char buf[512];
    if (length < sizeof(buf))
    {
      memcpy(buf, payload, length);
      buf[length] = '\0';
      Serial.print("[MQTT] JSON: ");
      Serial.println(buf);

      if (strstr(buf, "\"type\":\"connection_test\"") != nullptr)
      {
        Serial.println("========== HELLO RECEIVED FROM BACKEND ==========");
      }

      const char *sr = strstr(buf, "\"ttsSampleRate\":");
      if (sr != nullptr)
      {
        const uint32_t parsed = static_cast<uint32_t>(atoi(sr + 16));
        if (parsed >= 8000 && parsed <= 48000)
          currentTtsSampleRate = parsed;
      }
    }
    else
    {
      Serial.println("[MQTT] JSON larger than 512 bytes; check serial or parse in chunks");
    }
    return;
  }

  if (strstr(topic, "/audio/out/wav") != nullptr)
  {
    if (!playBuffer)
    {
      Serial.println("[MQTT] No play buffer allocated");
      return;
    }
    if (length > MAX_PLAY_PCM_BYTES)
    {
      Serial.print("[MQTT] TTS too large for play buffer (max ");
      Serial.print(MAX_PLAY_PCM_BYTES);
      Serial.print("): ");
      Serial.println(length);
      return;
    }

    memcpy(playBuffer, payload, length);
    playLen = length;
    playPending = true;
    Serial.printf("[MQTT] queued PCM: %u bytes\n", length);
    return;
  }
}

bool mqttConnect()
{
  String clientId = String("esp32-") + String((uint32_t)ESP.getEfuseMac(), HEX);

  if (mqtt.connected())
    return true;

  Serial.print("MQTT connecting to ");
  Serial.print(MQTT_BROKER);
  Serial.print(":");
  Serial.println(MQTT_PORT);

  if (mqtt.connect(clientId.c_str()))
  {
    Serial.println("MQTT connected.");

    if (mqtt.subscribe(topicAudioOut) && mqtt.subscribe(topicAudioOutWav))
    {
      Serial.println("Subscribed to audio/out and audio/out/wav");
    }
    else
    {
      Serial.println("MQTT subscribe failed");
      mqtt.disconnect();
      return false;
    }

    const char *role = "spiderman";
    mqtt.publish(topicMode, reinterpret_cast<const uint8_t *>(role), strlen(role), false);
    Serial.println("Published mode=spiderman");

    return true;
  }

  const int st = mqtt.state();
  Serial.print("MQTT connect failed, rc=");
  Serial.print(st);
  Serial.println(" (-2=TCP failed: wrong IP, different subnet, broker down, or firewall blocking 1883)");
  if (WiFi.status() == WL_CONNECTED)
  {
    Serial.print("ESP32 IP (must route to broker): ");
    Serial.println(WiFi.localIP());
    Serial.print("Trying broker: ");
    Serial.println(MQTT_BROKER);
    Serial.println("Fix: On your PC run `ipconfig`, use that IPv4 as MQTT_BROKER when PC and ESP are on same Wi‑Fi.");
  }
  return false;
}

void publishSilenceOneSecondTest()
{
  static constexpr size_t kBytes = 32000;
  static uint8_t silence[kBytes];
  if (mqtt.publish(topicAudioIn, silence, kBytes, false))
    Serial.println("Published 1s silence PCM to audio/in (pipeline test)");
  else
    Serial.println("Publish failed (MQTT buffer / connection?)");
}

void setup()
{
  Serial.begin(115200);
  const unsigned long serialWaitStart = millis();
  while (!Serial && millis() - serialWaitStart < 5000)
  {
    delay(10);
  }
  delay(300);

  buildTopics();

  Serial.println("=== Toy + MQTT (Jarvis backend) ===");
  Serial.print("Toy Name: ");
  Serial.println(TOY_NAME);
  Serial.print("DEVICE_ID: ");
  Serial.println(DEVICE_ID);

  pixel.begin();
  pixel.clear();
  pinMode(BUTTON_PIN, INPUT_PULLUP);
  setLedColor(255, 0, 0);
  audioBuffer = static_cast<uint8_t *>(malloc(MAX_RECORD_PCM_BYTES));
  playBuffer = static_cast<uint8_t *>(malloc(MAX_PLAY_PCM_BYTES));
  if (!audioBuffer)
  {
    Serial.println("FATAL: audioBuffer alloc failed");
    while (true)
      delay(1000);
  }
  if (!playBuffer)
  {
    Serial.println("FATAL: playBuffer alloc failed");
    while (true)
      delay(1000);
  }

  WiFi.mode(WIFI_STA);
  WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
  Serial.print("WiFi connecting");

  const unsigned long start = millis();
  while (WiFi.status() != WL_CONNECTED && millis() - start < 20000)
  {
    Serial.print(".");
    delay(400);
  }
  Serial.println();

  if (WiFi.status() != WL_CONNECTED)
  {
    Serial.println("WiFi failed — fix SSID/password or MQTT_BROKER later.");
    return;
  }

  Serial.print("WiFi OK, IP: ");
  Serial.println(WiFi.localIP());

  mqtt.setServer(MQTT_BROKER, MQTT_PORT);
  if (!mqtt.setBufferSize(65535))
    Serial.println("WARN: mqtt.setBufferSize(65535) failed — TTS may not arrive.");
  mqtt.setCallback(mqttCallback);

  mqttConnect();

  Serial.println("Serial: send 't' to publish 1s silence to audio/in (end-to-end test).");
}

void loop()
{
  const bool pressed = (digitalRead(BUTTON_PIN) == LOW);
  if (pressed != g_buttonPressed && !recording)
  {
    g_buttonPressed = pressed;
    if (g_buttonPressed)
    {
      recordStart();
    }
    else
    {
      Serial.println("Button released, default light (red)");
    }
  }

  if (WiFi.status() == WL_CONNECTED)
  {
    if (!mqtt.connected())
    {
      delay(2000);
      mqttConnect();
    }
    else
    {
      mqtt.loop();
    }
  }

  if (recording)
  {
    recordPoll();
    if (!pressed)
    {
      g_buttonPressed = false;
      recordStopAndSend();
    }
  }

  if (!recording && playPending)
    playQueuedPcm();

  if (Serial.available())
  {
    char c = static_cast<char>(Serial.read());
    if (c == 't' || c == 'T')
      publishSilenceOneSecondTest();
  }

  static unsigned long lastBeat = 0;
  const unsigned long now = millis();
  if (now - lastBeat >= 5000)
  {
    lastBeat = now;
    Serial.print("HB | WiFi ");
    Serial.print(WiFi.status() == WL_CONNECTED ? "OK" : "down");
    Serial.print(" | MQTT ");
    Serial.println(mqtt.connected() ? "OK" : "down");
  }

  delay(10);
}
