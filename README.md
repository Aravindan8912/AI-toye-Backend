# AI-toye-Backend (Jarvis)

Voice AI backend: WebSocket audio → STT (Whisper) → LLM (Ollama) → TTS (Piper), with **MongoDB** and **vector memory** for context-aware conversation.

## Backend

- **Location**: [Backend/](Backend/)
- **Docs**: [Backend/README.md](Backend/README.md) — run, config, API  
- **Architecture**: [Backend/docs/ARCHITECTURE.md](Backend/docs/ARCHITECTURE.md)  
- **MongoDB & vectors**: [Backend/docs/MONGODB_AND_VECTORS.md](Backend/docs/MONGODB_AND_VECTORS.md)

## Stack

- .NET 8, WebSocket, RabbitMQ  
- Whisper (STT), Ollama (LLM + embeddings), Piper (TTS)  
- **MongoDB** — chat memory storage  
- **Vector search** — Ollama embeddings + cosine similarity (optionally Atlas Vector Search for scale)

## Quick start

1. Start **MongoDB** and **Ollama** (and optionally RabbitMQ).  
2. In `Backend`: copy `appsettings.json` and set paths / connection strings.  
3. Run: `dotnet run` from `Backend/`.  
4. Connect to `ws://host:5000/ws`, send audio chunks, then text `EOR` to process.

See [Backend/README.md](Backend/README.md) for full configuration and usage.
