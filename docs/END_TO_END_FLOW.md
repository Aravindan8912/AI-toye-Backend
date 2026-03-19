# End-to-End Flow: Backend (MQTT + Redis + MongoDB)

This document explains **how the Jarvis voice pipeline works** for **ESP32** (and other MQTT clients): MQTT in → RabbitMQ → AudioWorker → Redis (fast recent) + MongoDB (vector) → MQTT out.

---

## 1. High-Level Picture

```
   ESP32
      ↓ MQTT (publish audio to jarvis/{deviceId}/audio/in)
   MQTT Broker
      ↓
   .NET MQTT Listener (MqttListenerService)
      ↓ publish AudioMessage
   RabbitMQ (audio_queue)
      ↓ consume
   AudioWorker
      ↓
   ┌───────────────┬────────────────┐
   │ Redis (FAST)  │ MongoDB (LONG) │
   │ Recent memory │ Vector memory  │
   └───────────────┴────────────────┘
      ↓
   MQTT Response Publisher → jarvis/{deviceId}/audio/out (JSON) + .../wav (PCM)
      ↓
   ESP32 🔊
```

- **WebSocket has been removed.** Clients use MQTT: publish audio to `jarvis/{deviceId}/audio/in`; subscribe to `jarvis/{deviceId}/audio/out` (chat JSON) and `jarvis/{deviceId}/audio/out/wav` (TTS PCM).
- **Redis**: fast recent conversation per device (session). **MongoDB**: long-term vector memory and search.

---

## 2. MQTT Topics and Config

| Topic / config | Purpose |
|----------------|--------|
| **jarvis/+/audio/in** | Backend subscribes (e.g. `jarvis/esp32-living-room/audio/in`). Second segment = **deviceId** (clientId). Payload = raw audio bytes (WAV or PCM). |
| **jarvis/{deviceId}/audio/out** | Backend publishes chat JSON: `{ userText, botText, ttsSampleRate }`. |
| **jarvis/{deviceId}/audio/out/wav** | Backend publishes TTS raw PCM (WAV header stripped). |
| **appsettings.json → MQTT** | `BrokerHost`, `BrokerPort`, `TopicIn`, `TopicOutTemplate`, `Enabled`. |
| **appsettings.json → Redis** | `Configuration`, `InstanceName`, `RecentMemoryLimit`, `Enabled`. |

---

## 3. Backend README — Explained

### 3.1 Features (README)

| Feature | Where in code |
|--------|---------------|
| **MQTT in** | **MqttListenerService**: subscribes to `jarvis/+/audio/in`, extracts deviceId from topic, publishes **AudioMessage** to RabbitMQ. |
| **STT** | **AudioWorker** calls `_whisper.TranscribeAsync(path)`. **WhisperService** uses `WhisperPath` and runs Whisper on the WAV file. |
| **LLM** | **OllamaService**: HTTP POST to Ollama `api/generate`. **AudioWorker** builds prompt with persona, user profile, **Redis recent** + **MongoDB vector** memory, knowledge, and user text. |
| **TTS** | **PiperTtsService** runs Piper. **AudioWorker** calls `_tts.GenerateSpeechAsync(reply)` and publishes JSON + PCM via **MqttResponsePublisher** to MQTT. |
| **Memory** | **Redis**: fast recent per device (**GetRecentByClient**, **Save(..., clientId)**). **MongoDB**: long-term vector (**Search**, **Save**). **AudioWorker** uses both for context. |

### 3.2 Prerequisites (README)

- **.NET 8**: project targets .NET 8.
- **MongoDB**: **MongoService** uses `MongoDB:ConnectionString`, `DatabaseName`, `MemoryCollectionName` (and knowledge collection).
- **Ollama**: **OllamaService** and **EmbeddingService** use `Ollama:BaseUrl`; model and embedding model from config.
- **RabbitMQ**: **RabbitMqService** and **AudioWorker**; can be turned off with `RabbitMQ:Enabled`. **MQTT**: **MqttOptions**; **Redis**: **RedisOptions** (optional, for fast recent memory).
- **Whisper, Piper, FFmpeg**: paths and options in **appsettings.json**; used by **WhisperService** and **PiperTtsService**.

### 3.3 Quick Start (README)

- **MongoDB**: `mongod` or set `MongoDB:ConnectionString` — used by **MongoService**.
- **Ollama**: Docker or native; pull `llama3` and `nomic-embed-text` — used for generate and embed.
- **Config**: **appsettings.json** (and **Program.cs** options binding) — see Configuration table.
- **Run**: `dotnet run` — **Program.cs** uses `UseUrls("http://0.0.0.0:5000")`; API at http://0.0.0.0:5000. ESP32 uses MQTT broker (configurable host/port).

### 3.4 Configuration (README)

All listed keys exist in **appsettings.json** and are read in:

- **MongoDB**: **MongoService** (ConnectionString, DatabaseName, MemoryCollectionName; plus KnowledgeCollectionName for RAG).
- **Memory**: **MemoryService** (`Memory:SearchLimit`).
- **Ollama**: **OllamaService** (BaseUrl, Model), **EmbeddingService** (BaseUrl, EmbeddingModel).
- **RabbitMQ**: **RabbitMqOptions** (Enabled, HostName, Port, etc.). **MQTT**: **MqttOptions** (BrokerHost, TopicIn, TopicOutTemplate). **Redis**: **RedisOptions** (Configuration, RecentMemoryLimit).

**Assistant** (Persona, UserProfile) are in config and injected into the LLM prompt in **AudioWorker**.

### 3.5 Redis (fast) + MongoDB (long) Memory

- **Redis**: Per-device recent turns (key `jarvis:recent:{clientId}`, list, trimmed to **RecentMemoryLimit**). **MemoryService.GetRecentByClient(clientId)** returns these for fast context. **Save(memory, clientId)** pushes to both MongoDB and Redis.
- **MongoDB**: **ChatMemory** (userText, botText, embedding, timestamp). **MemoryService.Search(embedding)** uses **cosine similarity** over MongoDB documents → top-K for LLM.
- **AudioWorker** combines **GetRecentByClient(clientId)** (Redis) and **Search(embedding)** (MongoDB) into "Recent conversation" in the prompt, then **Save(..., clientId)** to both stores.

### 3.6 Project Layout (README)

| Path | Purpose |
|------|--------|
| **Program.cs** | Host, DI (RabbitMQ, MQTT, Redis, workers, MongoDB, memory, embedding, knowledge), CORS, static files, no /ws. |
| **appsettings.json** | All config (MongoDB, Ollama, RabbitMQ, **MQTT**, **Redis**, Whisper, Piper, Memory, Knowledge, Assistant, Serilog). |
| **Data/MongoService.cs** | Mongo client, **memory** and **knowledge** collections. |
| **Data/RedisService.cs** | Optional Redis connection; **MemoryService** uses it for recent per client. |
| **Data/MemoryService.cs** | Vector search (MongoDB) + **GetRecentByClient** (Redis) + **Save(memory, clientId)** to both. |
| **Models/AudioMessage.cs** | `ClientId` + `AudioData` — payload from MQTT listener to RabbitMQ. |
| **Models/ChatMemory.cs** | Document: Id, userText, botText, embedding, timestamp. |
| **Services/MqttListenerService.cs** | Subscribes to `jarvis/+/audio/in`, forwards to RabbitMQ. |
| **Services/MqttResponsePublisher.cs** | Publishes chat JSON + TTS PCM to `jarvis/{clientId}/audio/out` and `.../wav`. |
| **Workers/AudioWorker.cs** | Consumes RabbitMQ: STT → embed → Redis recent + MongoDB vector + knowledge → LLM → save to both → TTS → MQTT publish. |

### 3.7 API (no WebSocket)

- **GET /** — **UseDefaultFiles** + **UseStaticFiles** serve wwwroot.
- **ESP32**: Publish audio to **jarvis/{deviceId}/audio/in**; subscribe to **jarvis/{deviceId}/audio/out** (JSON) and **jarvis/{deviceId}/audio/out/wav** (PCM).

---

## 4. End-to-End Flow (Step by Step) — ESP32 + MQTT

### 4.1 ESP32 Publishes Audio

1. Device (e.g. ESP32) uses a **deviceId** (e.g. `esp32-living-room`). It publishes **raw audio bytes** (WAV or PCM) to topic **jarvis/{deviceId}/audio/in** (e.g. `jarvis/esp32-living-room/audio/in`).
2. **MqttListenerService** (backend) is subscribed to **jarvis/+/audio/in**. It receives the message, takes **clientId** from the second topic segment (deviceId), and publishes **AudioMessage** { ClientId = deviceId, AudioData = payload } to **RabbitMQ** (**audio_queue**).

### 4.2 Backend Processes the Message

1. **AudioWorker** (consumer, prefetch 1, manual ack):
   - **Received** handler: deserializes **AudioMessage**, writes bytes to a temp WAV file (**WavHelper.EnsureWav**), enqueues `(path, clientId, deliveryTag)` to **workChannel**, returns (message stays Unacked).
2. **RunProcessorLoopAsync** reads from workChannel; for each item calls **ProcessOneAsync(path, clientId)** then deletes temp file and acks.
3. **ProcessOneAsync**:
   - If file too small: skip Whisper/LLM; set `reply = "Recording too short. Please speak for at least a second."`
   - Else: **STT** (Whisper) → **Embed** (Ollama) → **GetRecentByClient(clientId)** (Redis, fast) + **Search(embedding)** (MongoDB, vector) + **Knowledge.SearchAsync** → build prompt → **LLM** (Ollama) → **Save(memory, clientId)** (MongoDB + Redis) → **TTS** (Piper).
   - **Send back**: **MqttResponsePublisher.PublishAsync(clientId, chatJson, ttsPcmBytes)** → publishes to **jarvis/{clientId}/audio/out** (JSON) and **jarvis/{clientId}/audio/out/wav** (PCM).

### 4.3 ESP32 Receives the Reply

1. Device subscribes to **jarvis/{deviceId}/audio/out** and **jarvis/{deviceId}/audio/out/wav**.
2. On **out**: payload is JSON `{ userText, botText, ttsSampleRate }` (for display/log).
3. On **out/wav**: payload is raw PCM (no WAV header); play with **ttsSampleRate** (e.g. 22050).

### 4.4 Chat History (API)

- **GET /api/chat/history?limit=50**: **ChatController.GetHistory** uses **IMemoryService.GetRecent(limit)** (MongoDB) and returns `{ userText, botText, timestamp }[]`. Optional for web/admin UIs.

---

## 5. Summary Table

| Stage | ESP32 / Client | Backend |
|-------|----------------|--------|
| **Send** | Publish audio to **jarvis/{deviceId}/audio/in** | MqttListenerService → RabbitMQ (AudioMessage) |
| **Process** | — | AudioWorker: Whisper → Embed → Redis recent + MongoDB vector + Knowledge → Ollama → Save (Redis + MongoDB) → Piper |
| **Reply** | Subscribe **jarvis/{deviceId}/audio/out** and **.../wav** | MqttResponsePublisher: JSON + PCM to MQTT |
| **History** | Optional: GET /api/chat/history?limit=50 | ChatController → MemoryService.GetRecent (MongoDB) |

---

## 6. Files to Read for Each Part

- **Backend entry**: **Backend/Program.cs** (host, DI: MQTT, Redis, RabbitMQ, workers, memory).
- **MQTT**: **Backend/Services/MqttListenerService.cs**, **MqttResponsePublisher.cs**; **Configuration/MqttOptions.cs**.
- **Pipeline**: **Backend/workers/AudioWorker.cs** (STT → embed → Redis + MongoDB memory → LLM → save both → TTS → MQTT publish).
- **Memory**: **Backend/Data/MemoryService.cs** (Redis recent + MongoDB vector), **RedisService.cs**, **MongoService.cs**.
- **Config**: **Backend/appsettings.json** (MQTT, Redis, RabbitMQ, MongoDB, Ollama, Whisper, Piper).

This is the full end-to-end flow for the MQTT + Redis + MongoDB backend.
