# ESP32-S3 + INMP441 → WebSocket Backend

Simple firmware for **ESP32-S3** and **INMP441** (I2S mic): connect over WiFi, record **5 seconds** of audio, send raw PCM over **WebSocket** to the AI-Toy backend (`ws://<SERVER_IP>:5000/ws`), then send **EOR**. The backend runs Whisper → Ollama → Piper TTS and logs the **Piper TTS response** to the console.

---

## How to upload the firmware

### 1. Install PlatformIO

- **Option A – VS Code:** Install the [PlatformIO IDE](https://platformio.org/install/ide?install=vscode) extension, then open the `AI-toy` folder (or just the `Firmware` folder).
- **Option B – CLI:** `pip install platformio` (or see [platformio.org/install/cli](https://platformio.org/install/cli)).

### 2. Set WiFi and backend IP

Edit **`Firmware/src/main.cpp`** and set:

```cpp
#define WIFI_SSID       "YOUR_WIFI_SSID"
#define WIFI_PASSWORD   "YOUR_WIFI_PASSWORD"
#define WS_HOST         "10.17.254.58"   // Your PC's IP (where the backend runs)
```

### 3. Connect the board

- Connect **ESP32-S3** to your PC with a **USB cable** (data-capable).
- **Windows:** If the board appears as a new COM port, you may need the [CP210x](https://www.silabs.com/developers/usb-to-uart-bridge-vcp-drivers) or **CH340** driver depending on your board.

### 4. Build and upload

Open a terminal in the **Firmware** folder (or repo root), then:

```bash
cd Firmware
pio run -t upload
```

- If you have only one device connected, upload starts automatically.
- If you have several COM ports, set the port:
  ```bash
  pio run -t upload --upload-port COM3
  ```
  (Replace `COM3` with your port, e.g. from Device Manager or `pio device list`.)

### 5. Open serial monitor (optional)

To see logs (WiFi, WebSocket, recording):

```bash
pio device monitor -b 115200
```

To list connected serial ports:

```bash
pio device list
```

---

## Wiring (INMP441 → ESP32-S3)

| INMP441 pin | ESP32-S3 GPIO | Notes |
|-------------|----------------|--------|
| **SCK** (BCK) | **16** (I2S_BCK) | Bit clock |
| **WS** (LRCK) | **17** (I2S_WS) | Word select / LRCLK |
| **SD** (DATA) | **18** (I2S_DIN) | Serial data **IN** to ESP32 |
| **L/R** | **GND** | Left channel (use GND) |
| **VDD** | **3.3V** | Power |
| **GND** | **GND** | Ground |

- **L/R to GND** = left channel (required for correct sample format).
- Default pins in code: **16, 17, 18**. To use others, edit in `src/main.cpp`: `I2S_BCK`, `I2S_WS`, `I2S_DIN`.

---

## Config (edit before upload)

In `Firmware/src/main.cpp` set:

```cpp
#define WIFI_SSID       "YOUR_WIFI_SSID"
#define WIFI_PASSWORD   "YOUR_WIFI_PASSWORD"
#define WS_HOST         "192.168.1.10"   // Your backend PC IP (same WiFi)
#define WS_PORT         5000
#define WS_PATH         "/ws"
```

- **WS_HOST**: IP of the PC running the backend (e.g. `192.168.1.10`). Find it with `ipconfig` (Windows) or `ifconfig` (Mac/Linux).

---

## Build and upload (PlatformIO)

1. **Install PlatformIO** (VS Code extension or CLI): https://platformio.org/

2. **Open project**  
   From repo root:
   ```bash
   cd Firmware
   pio run
   ```

3. **Upload**  
   Connect ESP32-S3 via USB, then:
   ```bash
   pio run -t upload
   ```

4. **Serial monitor** (optional):
   ```bash
   pio device monitor -b 115200
   ```

---

## Flow

1. ESP32-S3 boots → connects to WiFi → connects to `ws://WS_HOST:5000/ws`.
2. Records **5 seconds** from INMP441 (16 kHz, 16-bit mono, raw PCM).
3. Sends PCM in **4096-byte chunks**, then the text **`EOR`**.
4. Backend accumulates chunks until `EOR`, prepends a WAV header (if needed), runs Whisper → Ollama → Piper, and logs **Piper TTS response** to the backend console. TTS WAV is sent back over the same WebSocket (ESP32 prints "binary N bytes" in serial).

---

## Backend

- Backend must be running (`cd Backend && dotnet run`), with **RabbitMQ** and **Ollama** running.
- Backend accepts **raw PCM** (16 kHz, 16-bit mono) and prepends a WAV header before passing to Whisper.

---

## Troubleshooting

| Symptom | Check |
|--------|--------|
| No I2S data / zeros | INMP441 wiring (SCK, WS, SD, L/R=GND); try different GPIOs if your board uses other I2S pins. |
| WS connect failed | Backend running? Correct `WS_HOST` and same WiFi? Firewall allows port 5000? |
| Backend doesn’t process | RabbitMQ running? `RabbitMQ:Enabled: true` in `appsettings.json`? |
