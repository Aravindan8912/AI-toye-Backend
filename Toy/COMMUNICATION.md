# How the Toy (ESP32) Communicates with the Jarvis Backend

## Overview

The Toy talks to your backend over **one WebSocket connection** to `ws://<YOUR_PC_IP>:5000/ws`. All communication is on that single socket: connection, audio upload, and audio download.

---

## 1. Connection

| Who   | What |
|-------|------|
| **Toy** | Connects to `ws://WS_HOST:5000/ws` (e.g. `ws://192.168.1.63:5000/ws`) after WiFi is up. |
| **Backend** | Accepts the WebSocket, creates a `clientId`, stores the socket in `ConnectionManager`, and **sends one text message** right away. |

**First message from backend (text):**
```
Connected. ClientId: <guid>. Send binary (audio) chunks, then text 'EOR' to process.
```

The Toy logs this as “server welcome”. The backend uses `clientId` to know which socket to use when sending the reply later.

---

## 2. Toy → Backend (sending your voice)

When you **release the button**, the Toy sends:

1. **Binary messages** — raw PCM audio in chunks  
   - Format: **16-bit mono, 16 kHz** (same as your I2S mic).  
   - Chunk size: **4096 bytes** (defined by `SEND_CHUNK` in the Toy).  
   - Backend **appends** every binary frame into one buffer (it does not care about chunk boundaries).

2. **One text message** — exactly the string: **`EOR`**  
   - **EOR** = “End of Recording”.  
   - Backend only runs the pipeline (Whisper → LLM → TTS) when it receives **EOR**, using all binary data received so far.

So: **all binary chunks first, then one "EOR" text**. No JSON or other format for the upload.

```
[Toy]  --binary chunk 1-->  [Backend]  (append to buffer)
[Toy]  --binary chunk 2-->  [Backend]  (append)
[Toy]  --binary chunk N-->  [Backend]  (append)
[Toy]  --text "EOR" ------>  [Backend]  (run pipeline: Whisper → Ollama → Piper)
```

Backend code that receives this: `AudioWebSocketHandler.cs` (binary → `audioBuffer`, text `"EOR"` → publish to RabbitMQ with that buffer).

---

## 3. Backend pipeline (what happens after EOR)

- **AudioWebSocketHandler** takes the accumulated binary, publishes it to **RabbitMQ** with the same `clientId`.
- **AudioWorker** consumes the message:
  - Saves audio to a temp file.
  - **Whisper** → text.
  - **Embedding + memory + knowledge** → context.
  - **Ollama** → reply text.
  - **Piper TTS** → WAV.
- Worker then sends the reply **back on the same WebSocket** (looks up socket by `clientId` in `ConnectionManager`).

So the Toy does **not** use HTTP or a second connection for the reply; it uses the **same WebSocket** it used to send audio.

---

## 4. Backend → Toy (receiving the reply)

The backend sends **two** messages on the same WebSocket, in this order:

| Order | Type   | Content |
|-------|--------|---------|
| 1     | **Text**  | JSON: `{ "userText": "...", "botText": "...", "ttsSampleRate": 22050 }` |
| 2     | **Binary** | Raw PCM of the TTS (WAV header already removed), usually **22 050 Hz, 16‑bit mono**. |

- **userText** = what Whisper heard (your speech).  
- **botText** = what the LLM (Spider-Man) replied.  
- **ttsSampleRate** = sample rate of the following binary (e.g. 22050 for Piper).  
- **Binary** = the audio to play on the Toy’s I2S speaker.

Toy code that handles this: `webSocketEvent()` — on **WStype_TEXT** it parses JSON and sets `ttsSampleRate` (and logs user/bot text); on **WStype_BIN** it plays the PCM at that rate.

---

## 5. Summary diagram

```
  TOY (ESP32)                          JARVIS BACKEND
  -----------                          ---------------

  1) Connect WebSocket  ------------>  Accept, send "Connected. ClientId: ..."

  2) [You release button]
     Send binary chunks  ----------->  Append to audioBuffer
     Send "EOR" (text)   ----------->  Publish to RabbitMQ (audio + clientId)

  3)                                   AudioWorker: Whisper → Ollama → Piper
                                       Look up socket by clientId

  4)  <-----------  Text (JSON: userText, botText, ttsSampleRate)
  <-----------  Binary (PCM TTS)

  5) Play PCM on I2S speaker
```

---

## 6. Where it’s defined in code

| Step              | Toy (ESP32)              | Backend                          |
|-------------------|--------------------------|----------------------------------|
| Connect           | `webSocket.begin(...)` in `setup()` | `Program.cs` maps `/ws` → `AudioWebSocketHandler` |
| Send audio        | `sendAudioToServer()` → `sendBIN()` then `sendTXT("EOR")` | `AudioWebSocketHandler.cs`: receive binary → list, text `"EOR"` → publish |
| Process audio     | —                        | `AudioWorker.cs`: RabbitMQ → Whisper → Ollama → Piper |
| Send reply        | —                        | `AudioWorker.cs`: JSON then binary to socket from `_manager.Get(clientId)` |
| Receive reply     | `webSocketEvent()` WStype_TEXT / WStype_BIN | Same WebSocket connection        |

So: **one WebSocket, same connection for upload (binary + EOR) and download (JSON + PCM).** That’s how the Toy communicates with your backend.
