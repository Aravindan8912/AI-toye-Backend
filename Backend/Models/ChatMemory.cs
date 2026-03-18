using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace JarvisBackend.Models;

/// <summary>One turn of conversation stored for vector search (embedding over user + bot text).</summary>
public class ChatMemory
{
    [BsonId]
    public string? Id { get; set; }

    [BsonElement("userText")]
    public string? UserText { get; set; }

    [BsonElement("botText")]
    public string? BotText { get; set; }

    /// <summary>Vector embedding (e.g. from Ollama nomic-embed-text) for similarity search.</summary>
    [BsonElement("embedding")]
    public float[]? Embedding { get; set; }

    [BsonElement("timestamp")]
    public DateTime Timestamp { get; set; }
}