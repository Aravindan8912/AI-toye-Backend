using JarvisBackend.Models;

namespace JarvisBackend.Services.Interfaces;

/// <summary>Knowledge base with vector search; used to feed context to Ollama for RAG-style answers.</summary>
public interface IKnowledgeService
{
    Task SaveAsync(Knowledge doc);
    /// <summary>Chunks content if needed, embeds each chunk, and saves. Use this to add e.g. Spider-Man details.</summary>
    Task SaveWithEmbeddingAsync(string title, string content);
    Task<List<Knowledge>> SearchAsync(float[]? queryEmbedding, int? limit = null);
    Task<List<Knowledge>> GetAllAsync();
}
