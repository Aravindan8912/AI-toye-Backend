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
        var chunks = TextChunker.Chunk(content, 500);
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
        if (queryEmbedding == null || queryEmbedding.Length == 0)
            return new List<Knowledge>();

        var limitVal = limit ?? _config.GetValue("Knowledge:SearchLimit", 5);
        var all = await _mongo.KnowledgeCollection.Find(_ => true).ToListAsync();

        var results = all
            .Where(x => x.Embedding != null && x.Embedding.Length == queryEmbedding.Length)
            .Select(x => new { Doc = x, Score = CosineSimilarity.Calculate(queryEmbedding, x.Embedding!) })
            .OrderByDescending(x => x.Score)
            .Take(limitVal)
            .Select(x => x.Doc)
            .ToList();

        _logger.LogDebug("Knowledge search: {Count} results (limit {Limit})", results.Count, limitVal);
        return results;
    }

    public async Task<List<Knowledge>> GetAllAsync()
    {
        return await _mongo.KnowledgeCollection.Find(_ => true).ToListAsync();
    }
}
