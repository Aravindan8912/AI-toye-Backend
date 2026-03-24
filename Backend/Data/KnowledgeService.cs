using System.Text;
using JarvisBackend.Models;
using JarvisBackend.Services.Interfaces;
using JarvisBackend.Utils;
using MongoDB.Bson;
using MongoDB.Driver;

namespace JarvisBackend.Data;

/// <summary>Knowledge base with vector embeddings; search by similarity and feed results to Ollama for RAG.</summary>
public class KnowledgeService : IKnowledgeService
{
    private readonly MongoService _mongo;
    private readonly IEmbeddingService _embed;
    private readonly IConfiguration _config;
    private readonly ILogger<KnowledgeService> _logger;

    public KnowledgeService(MongoService mongo, IEmbeddingService embed, IConfiguration config, ILogger<KnowledgeService> logger)
    {
        _mongo = mongo;
        _embed = embed;
        _config = config;
        _logger = logger;
    }

    public async Task SaveAsync(Knowledge doc)
    {
        if (doc.Id == default)
            doc.Id = ObjectId.GenerateNewId();
        await _mongo.KnowledgeCollection.InsertOneAsync(doc);
        _logger.LogDebug("Knowledge saved: {Title}", doc.Title);
    }

    public async Task SaveWithEmbeddingAsync(string title, string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            _logger.LogWarning("SaveWithEmbeddingAsync: empty content for title {Title}", title);
            return;
        }
        // ~200–400 tokens/chunk: use ChunkMaxChars (≈4 chars/token English; default 1200 ≈ 300 tokens)
        var maxChars = _config.GetValue("Knowledge:ChunkMaxChars", 1200);
        var chunks = TextChunker.Chunk(content, maxChars);
        foreach (var chunk in chunks)
        {
            var embedding = await _embed.GetEmbedding(chunk);
            await SaveAsync(new Knowledge
            {
                Id = ObjectId.GenerateNewId(),
                Title = title,
                Content = chunk,
                Embedding = embedding
            });
        }
        _logger.LogInformation("Knowledge saved: {Title}, {Count} chunk(s)", title, chunks.Count);
    }

    public async Task<List<Knowledge>> SearchAsync(float[]? queryEmbedding, int? limit = null)
    {
        var scored = await SearchScoredAsync(queryEmbedding, limit);
        return scored.Select(x => x.Doc).ToList();
    }

    public async Task<List<(Knowledge Doc, float Score)>> SearchScoredAsync(float[]? queryEmbedding, int? limit = null)
    {
        if (queryEmbedding == null || queryEmbedding.Length == 0)
            return new List<(Knowledge Doc, float Score)>();

        var limitVal = limit ?? _config.GetValue("Knowledge:SearchLimit", 3);
        var all = await _mongo.KnowledgeCollection.Find(_ => true).ToListAsync();

        var results = all
            .Where(x => x.Embedding != null && x.Embedding.Length == queryEmbedding.Length)
            .Select(x => (Doc: x, Score: (float)CosineSimilarity.Calculate(queryEmbedding, x.Embedding!)))
            .OrderByDescending(x => x.Score)
            .Take(limitVal)
            .ToList();

        _logger.LogDebug("Knowledge search: {Count} results (limit {Limit})", results.Count, limitVal);
        return results;
    }

    public async Task<List<Knowledge>> GetAllAsync()
    {
        return await _mongo.KnowledgeCollection.Find(_ => true).ToListAsync();
    }

    public async Task<(string Context, int ChunkCount)> BuildKnowledgeContextForQueryAsync(string userText, float[]? queryEmbedding = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userText))
            return ("", 0);

        var emb = queryEmbedding ?? await _embed.GetEmbedding(userText);
        if (emb == null || emb.Length == 0)
            return ("", 0);

        var retrievalLimit = _config.GetValue("Knowledge:SearchLimit", 3);
        var similarityThreshold = _config.GetValue("Knowledge:SimilarityThreshold", 0.45f);
        var maxContextChars = _config.GetValue("Knowledge:MaxContextChars", 1200);

        var scored = await SearchScoredAsync(emb, retrievalLimit * 2);
        var selected = scored
            .Where(x => x.Score >= similarityThreshold)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Doc)
            .DistinctBy(x => $"{x.Title}\n{x.Content}")
            .Take(retrievalLimit)
            .ToList();

        var contextBuilder = new StringBuilder();
        foreach (var item in selected)
        {
            var block = $"[{item.Title}]\n{item.Content}\n\n";
            if (contextBuilder.Length + block.Length > maxContextChars)
                break;
            contextBuilder.Append(block);
        }

        var context = contextBuilder.ToString().Trim();
        _logger.LogInformation("Knowledge RAG: {ChunkCount} chunk(s) after threshold {Threshold}, chars {Chars}", selected.Count, similarityThreshold, context.Length);
        return (context, selected.Count);
    }
}
