# Toy — ESP32-S3 Jarvis Voice (same flow as Frontend)

Same flow as the web Frontend: **Connect → Record (button) → Send PCM + EOR → Receive JSON then PCM → Play TTS.**  
No local record-then-play loop; all audio goes to the Jarvis backend.

## Hardware (from your reference)

| Pin  | Use        |
|------|------------|
| 4    | I2S WS     |
| 5    | I2S SCK    |
| 6    | I2S SD (mic in) |
| 7    | I2S DOUT (speaker out) |
| 2    | Button (INPUT_PULLUP)   |
| 8    | LED        |
| 1    | Battery ADC (optional)  |

## Arduino IDE

1. **Board:** ESP32S3 Dev Module (or your ESP32-S3 board).
2. **Libraries** (Library Manager):
   - **ArduinoWebSockets** by Links2004
   - **ArduinoJson** by Benoit Blanchon (v6 or v7)

## Config (before upload)

Edit in `Toy_Jarvis.ino` or use build flags:

```cpp
#define WIFI_SSID "YOUR_SSID"
#define WIFI_PASS "YOUR_PASSWORD"
#define WS_HOST "192.168.1.63"   // Your PC IP (Jarvis backend) — same Wi-Fi as ESP32
#define WS_PORT 5000
#define WS_PATH "/ws"
```

## Serial (115200)

Every step is logged with timestamp and battery (V and %):

- `[ms] STEP detail | Battery: X.XX V (YY%)`
- Steps: WIFI, WS connected, RECORD start/stop, SEND/EOR, WS RX TEXT/BIN, PLAY start/done, etc.
- After JSON: `userText`, `botText`, `ttsSampleRate`.
- After BIN: bytes received and play start/done.

## Usage

1. Upload sketch, open Serial Monitor 115200.
2. Wait for WiFi and WebSocket connected.
3. **1st press:** start recording (LED on); **2nd press:** stop and send to server (LED off).
4. Backend returns JSON then PCM; ESP32 plays TTS on I2S and logs everything.

## Protocol (matches Backend)

- **Send:** binary chunks (raw PCM 16 kHz 16-bit mono), then text `EOR`.
- **Receive:** (1) text = `Connected. ClientId: ...` or JSON `{ userText, botText, ttsSampleRate }`, (2) binary = PCM (e.g. 22050 Hz from Piper); play at `ttsSampleRate`.
