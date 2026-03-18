using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace JarvisBackend.Models;

/// <summary>Stored knowledge chunk (e.g. Spider-Man details) with vector embedding for RAG.</summary>
public class Knowledge
{
    [BsonId]
    public ObjectId Id { get; set; }

    [BsonElement("title")]
    public string Title { get; set; } = "";

    [BsonElement("content")]
    public string Content { get; set; } = "";

    [BsonElement("embedding")]
    public float[]? Embedding { get; set; }
}