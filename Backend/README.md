# Jarvis Backend

Voice pipeline backend: WebSocket → STT (Whisper) → LLM (Ollama) → TTS (Piper), with **MongoDB** persistence and **vector memory** (embeddings via Ollama) for context-aware replies.

## Features

- **WebSocket** (`/ws`): stream binary audio; send text `EOR` to trigger processing
- **STT**: Whisper
- **LLM**: Ollama (configurable model)
- **TTS**: Piper
- **Memory**: MongoDB + vector search (Ollama embeddings, cosine similarity) for conversation context

## Prerequisites

- .NET 8
- **MongoDB** (local or Atlas) — for chat memory
- **Ollama** — for LLM and embeddings (`llama3`, `nomic-embed-text` or similar)
- **RabbitMQ** — for audio queue (optional; can disable in config)
- Whisper, Piper, FFmpeg — paths in config

## Quick start

1. **MongoDB**  
   - Local: `mongod` (default `mongodb://localhost:27017`)  
   - Or set `MongoDB:ConnectionString` in config.

2. **Ollama**  
   - **Docker (recommended):**
     ```bash
     docker run -d --name ollama -p 11434:11434 -v ollama_data:/root/.ollama ollama/ollama
     ```
     Then pull models inside the container or from the host (Ollama API is on port 11434):
     ```bash
     docker exec -it ollama ollama run llama3
     docker exec -it ollama ollama run nomic-embed-text
     ```
   - Or run Ollama natively and pull models: `ollama run llama3`, `ollama run nomic-embed-text`.  
   - Backend uses `Ollama:BaseUrl` (default `http://localhost:11434`), which works for both Docker and local Ollama.

3. **Config**  
   - Copy/adjust `appsettings.json` (see [Configuration](#configuration)).

4. **Run**
   ```bash
   dotnet run
   ```
   - API: `http://0.0.0.0:5000`  
   - WebSocket: `ws://host:5000/ws`

## Configuration

| Section | Key | Default | Description |
|--------|-----|---------|-------------|
| **MongoDB** | `ConnectionString` | `mongodb://localhost:27017` | MongoDB connection string |
| | `DatabaseName` | `jarvis` | Database name |
| | `MemoryCollectionName` | `memory` | Collection for chat memory |
| **Memory** | `SearchLimit` | `5` | Max number of similar memories returned for context |
| **Ollama** | `BaseUrl` | `http://localhost:11434` | Ollama API base URL |
| | `Model` | `llama3` | LLM model for chat |
| | `EmbeddingModel` | `nomic-embed-text` | Model for `/api/embed` (vector embeddings) |
| **RabbitMQ** | `Enabled` | `true` | Enable/disable queue and WebSocket pipeline |

See `appsettings.json` for full options (RabbitMQ, Whisper, Piper, Serilog, etc.).

## MongoDB and vector memory

- **Storage**: Each turn (user + bot text) is stored in MongoDB with an **embedding** vector (from Ollama `EmbeddingModel`).
- **Search**: On each user message, the app gets an embedding for the message, loads memory from MongoDB, and finds the top-`SearchLimit` items by **cosine similarity** (in-memory). Those are passed to the LLM as context.
- **Scale**: For very large collections, consider **MongoDB Atlas Vector Search** and switch to `$vectorSearch` instead of in-memory similarity (see [docs/MONGODB_AND_VECTORS.md](docs/MONGODB_AND_VECTORS.md)).

Details: [docs/MONGODB_AND_VECTORS.md](docs/MONGODB_AND_VECTORS.md) and [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Project layout

```
Backend/
├── Program.cs              # Host, DI, routes, /ws
├── appsettings.json        # Config (MongoDB, Ollama, RabbitMQ, etc.)
├── Data/
│   ├── MongoService.cs     # MongoDB connection and memory collection
│   └── MemoryService.cs    # Vector search + save (cosine similarity)
├── Models/
│   ├── AudioMessage.cs     # WebSocket → RabbitMQ payload
│   └── ChatMemory.cs       # Memory document (userText, botText, embedding, timestamp)
├── Services/
│   ├── EmbeddingService.cs # Ollama /api/embed
│   ├── OllamaService.cs    # Ollama generate
│   ├── ...
│   └── Interfaces/
├── Workers/
│   └── AudioWorker.cs      # Consumes queue: STT → embed → memory search → LLM → save → TTS
├── WebSockets/
│   └── AudioWebSocketHandler.cs
└── docs/
    ├── ARCHITECTURE.md
    └── MONGODB_AND_VECTORS.md
```

## API and WebSocket

- **GET /** — static files (if present)
- **WebSocket `/ws`**  
  - Connect → receive `Connected. ClientId: <id>...`  
  - Send binary audio chunks, then text `EOR` to run STT → memory + LLM → TTS and get audio back

## Documentation

- [Architecture and data flow](docs/ARCHITECTURE.md)
- [MongoDB and vector integration](docs/MONGODB_AND_VECTORS.md)
- [RabbitMQ queue visibility and pipeline design](docs/RABBITMQ_AND_QUEUE.md) — why the queue often shows 0 and why **Get message(s)** at http://localhost:15672 shows "Queue is empty" (expected when the consumer is running).
