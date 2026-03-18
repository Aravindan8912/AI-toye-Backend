# MongoDB and Vector Integration

This document describes how the backend uses **MongoDB** for storage and **vector embeddings** (via Ollama) for semantic memory search.

## Overview

- **MongoDB** stores conversation turns in the `memory` collection (configurable).
- Each document includes a **vector embedding** of the user message (from Ollama’s embed API).
- At query time, the app embeds the new user message, then finds the **top-K most similar** stored turns by **cosine similarity** and uses them as context for the LLM.

## Configuration (appsettings.json)

```json
"MongoDB": {
  "ConnectionString": "mongodb://localhost:27017",
  "DatabaseName": "jarvis",
  "MemoryCollectionName": "memory"
},
"Memory": {
  "SearchLimit": 5
},
"Ollama": {
  "BaseUrl": "http://localhost:11434",
  "EmbeddingModel": "nomic-embed-text"
}
```

- **ConnectionString**: Use for local MongoDB, or an Atlas SRV (e.g. `mongodb+srv://user:pass@cluster.mongodb.net/`).
- **DatabaseName** / **MemoryCollectionName**: Database and collection used for chat memory.
- **SearchLimit**: Number of similar memories to pass to the LLM (default 5).
- **EmbeddingModel**: Must be an Ollama model that supports embeddings (e.g. `nomic-embed-text`). Pull with: `ollama run nomic-embed-text`.

## Document shape (ChatMemory)

Stored in MongoDB with BSON field names:

| BSON field | Type | Description |
|------------|------|-------------|
| `_id` | ObjectId | Id (or string from app). |
| `userText` | string | User message for this turn. |
| `botText` | string | Assistant reply. |
| `embedding` | array of doubles | Embedding vector of `userText` (from Ollama). |
| `timestamp` | datetime | UTC time of the turn. |

The C# model is `JarvisBackend.Models.ChatMemory` with `[BsonElement(...)]` for these names.

## Flow

1. **User speaks** → AudioWorker transcribes with Whisper → `text`.
2. **Embed** → `EmbeddingService.GetEmbedding(text)` calls Ollama `POST /api/embed` with `EmbeddingModel` and `input: text` → returns `float[]`.
3. **Search** → `MemoryService.Search(embedding)`:
   - Reads all (or filtered) documents from `MongoDB.MemoryCollectionName`.
   - For each doc with `embedding`, computes **cosine similarity** to the query vector.
   - Returns the top `SearchLimit` documents (by similarity).
4. **Prompt** → Worker builds a context string from those memories (e.g. "User: ... Bot: ...") and calls Ollama to generate a reply.
5. **Save** → New `ChatMemory` with `userText`, `botText`, same `embedding`, and `timestamp` is inserted via `MemoryService.Save` into MongoDB.

## Current implementation: in-memory similarity

- **MemoryService** uses `MongoService.Collection.Find(_ => true).ToListAsync()` and then computes cosine similarity in process.
- **Pros**: Simple, no extra services, works with any MongoDB (local or Atlas).
- **Cons**: Does not scale to very large collections (all vectors in memory, no index).

For production with many documents, consider:

- **MongoDB Atlas Vector Search**: Create a vector index on `embedding` and use `$vectorSearch` in an aggregation pipeline instead of loading all docs and comparing in C#.
- Or an external **vector DB** (e.g. Qdrant, Milvus, or Pinecone) and keep MongoDB only for metadata/full document storage; then you would change `MemoryService` to query the vector DB and optionally load full documents from MongoDB by id.

## Ollama embedding API

- **Endpoint**: `POST /api/embed`
- **Request**: `{ "model": "<EmbeddingModel>", "input": "text" }` (or array of strings).
- **Response**: `{ "embeddings": [ [ float, ... ] ] }` — one vector per input.

The backend uses a single string input and takes `embeddings[0]` as the vector. Model and base URL come from config.

## Indexes (optional)

For non-vector queries (e.g. by time or user later), you can add indexes in MongoDB:

```javascript
db.memory.createIndex({ "timestamp": -1 })
```

For **Atlas Vector Search**, you define a search index on the collection with a vector mapping for the `embedding` field (see Atlas docs). The C# code would then call an aggregation with `$vectorSearch` instead of `Find(_ => true)` + in-memory cosine.

## Summary

| What | Where |
|------|--------|
| Connection / collection | `MongoService` (from `MongoDB:*` config) |
| Embeddings | Ollama `EmbeddingModel` via `EmbeddingService` |
| Similarity | `MemoryService`: cosine in memory over MongoDB documents |
| Persistence | `MemoryService.Save` → `MongoService.Collection.InsertOneAsync` |

For scaling, add a vector index (e.g. Atlas Vector Search) and switch search to use that index instead of in-memory comparison.
