using JarvisBackend.Models;

namespace JarvisBackend.Services.Interfaces;

/// <summary>Knowledge base with vector search; used to feed context to Ollama for RAG-style answers.</summary>
public interface IKnowledgeService
{
    Task SaveAsync(Knowledge doc);
    /// <summary>Chunks content if needed, embeds each chunk, and saves. Use this to add e.g. Spider-Man details.</summary>
    Task SaveWithEmbeddingAsync(string title, string content);
    Task<List<Knowledge>> SearchAsync(float[]? queryEmbedding, int? limit = null);
    Task<List<(Knowledge Doc, float Score)>> SearchScoredAsync(float[]? queryEmbedding, int? limit = null);
    Task<List<Knowledge>> GetAllAsync();

    /// <summary>Same retrieval as POST /api/knowledge/ask: embed query, score chunks, filter by SimilarityThreshold, cap by MaxContextChars. Pass <paramref name="queryEmbedding"/> to avoid a second embed call.</summary>
    Task<(string Context, int ChunkCount)> BuildKnowledgeContextForQueryAsync(string userText, float[]? queryEmbedding = null, CancellationToken cancellationToken = default);
}
