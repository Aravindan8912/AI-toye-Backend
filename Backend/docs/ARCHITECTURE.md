# Architecture

## High-level flow

```
Client (e.g. ESP32)
    │
    │  WebSocket (binary audio + "EOR")
    ▼
┌─────────────────────┐
│  AudioWebSocketHandler
│  - Assign ClientId
│  - Buffer chunks until EOR
│  - Publish to RabbitMQ
└──────────┬──────────┘
           │
           ▼
┌─────────────────────┐     ┌──────────────┐
│  RabbitMQ           │     │  MongoDB     │
│  audio_queue        │     │  jarvis.memory│
└──────────┬──────────┘     └──────▲───────┘
           │                       │
           ▼                       │
┌─────────────────────┐            │
│  AudioWorker        │            │
│  Consumer: enqueue   │            │
│  → background       │            │
│  Processor:         │            │
│  1. Whisper (STT)   │            │
│  2. Embedding       │────────────┤
│  3. Memory.Search   │◄───────────┘
│  4. Ollama (LLM)    │
│  5. Memory.Save     │────────────►
│  6. Piper (TTS)     │
│  7. Send audio → WS │            │
│  Ack after done     │            │
└─────────────────────┘
```

## Components

| Component | Responsibility |
|-----------|-----------------|
| **AudioWebSocketHandler** | Accepts WebSocket, assigns `ClientId`, buffers binary audio; on `EOR` publishes `AudioMessage` to RabbitMQ. |
| **RabbitMQ** | Decouples WebSocket from processing; queue name from `RabbitMQ:QueueName`. |
| **AudioWorker** | Consumer (prefetch 1, manual ack) receives message, writes WAV to temp file, enqueues to in-memory channel and returns. A background processor runs Whisper → embed → memory search → Ollama → save memory → Piper → send audio to WebSocket; then a dedicated ack loop acks the message. This keeps the RabbitMQ consumer thread free and avoids thread pool starvation. See [RABBITMQ_AND_QUEUE.md](RABBITMQ_AND_QUEUE.md). |
| **MongoService** | MongoDB connection and `IMongoCollection<ChatMemory>` for `jarvis.memory` (or configured DB/collection). |
| **MemoryService** | Loads memory from MongoDB, runs **cosine similarity** in memory, returns top-K; persists new `ChatMemory` (with embedding). |
| **EmbeddingService** | Calls Ollama `POST /api/embed` with `Ollama:EmbeddingModel` to get vector for a string. |
| **OllamaService** | Calls Ollama `api/generate` for LLM reply. |

## Data: ChatMemory (MongoDB)

Each document represents one turn and is used for vector search:

| Field | Type | Description |
|-------|------|-------------|
| `_id` | ObjectId | Set by MongoDB or `Id` as string. |
| `userText` | string | User utterance. |
| `botText` | string | Bot reply. |
| `embedding` | float[] | Vector from Ollama embed model (e.g. nomic-embed-text). |
| `timestamp` | DateTime | When the turn was stored. |

Embedding is computed from the **user** message only; the same vector is stored with that turn so similar user questions retrieve relevant past turns.

## Vector search (current)

- **Storage**: Every turn is saved with its embedding in MongoDB.
- **Query**: For each new user message we:
  1. Get embedding of the message via `EmbeddingService`.
  2. Load all memory documents from MongoDB (or a filtered set).
  3. Compute cosine similarity between query embedding and each document’s embedding.
  4. Take top `Memory:SearchLimit` (default 5) and pass them as context to the LLM.

This is **in-memory** similarity over MongoDB data. For very large collections, use **MongoDB Atlas Vector Search** (or another vector index) and query with `$vectorSearch`; see [MONGODB_AND_VECTORS.md](MONGODB_AND_VECTORS.md).

## Configuration summary

- **MongoDB**: `ConnectionString`, `DatabaseName`, `MemoryCollectionName`
- **Memory**: `SearchLimit`
- **Ollama**: `BaseUrl`, `Model` (LLM), `EmbeddingModel` (embeddings)
- **RabbitMQ**: `Enabled`, host, queue, etc.

All config is in `appsettings.json` (and environment/overrides as per ASP.NET Core).
