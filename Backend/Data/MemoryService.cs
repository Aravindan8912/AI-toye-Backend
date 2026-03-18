using JarvisBackend.Models;
using JarvisBackend.Services.Interfaces;
using MongoDB.Driver;

namespace JarvisBackend.Data;

/// <summary>Vector-backed memory: search by embedding (cosine similarity) and persist to MongoDB.</summary>
public class MemoryService : IMemoryService
{
    private readonly MongoService _mongo;
    private readonly IConfiguration _config;
    private readonly ILogger<MemoryService> _logger;

    public MemoryService(MongoService mongo, IConfiguration config, ILogger<MemoryService> logger)
    {
        _mongo = mongo;
        _config = config;
        _logger = logger;
    }

    public static double Cosine(float[] a, float[] b)
    {
        if (a == null || b == null || a.Length != b.Length || a.Length == 0)
            return 0;
        double dot = 0, magA = 0, magB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }
        var denom = Math.Sqrt(magA) * Math.Sqrt(magB);
        return denom == 0 ? 0 : dot / denom;
    }

    public async Task<List<ChatMemory>> Search(float[]? query)
    {
        if (query == null || query.Length == 0)
            return new List<ChatMemory>();

        var limit = _config.GetValue("Memory:SearchLimit", 5);
        var all = await _mongo.Collection.Find(_ => true).ToListAsync();

        var results = all
            .Where(x => x.Embedding != null && x.Embedding.Length == query.Length)
            .Select(x => new { Data = x, Score = Cosine(query!, x.Embedding!) })
            .OrderByDescending(x => x.Score)
            .Take(limit)
            .Select(x => x.Data)
            .ToList();

        _logger.LogDebug("Memory search: {Count} results (limit {Limit})", results.Count, limit);
        return results;
    }

    public async Task Save(ChatMemory memory)
    {
        if (memory == null)
            return;
        memory.Id ??= Guid.NewGuid().ToString();
        memory.Timestamp = memory.Timestamp == default ? DateTime.UtcNow : memory.Timestamp;
        await _mongo.Collection.InsertOneAsync(memory);
        _logger.LogDebug("Memory saved: {Id}", memory.Id);
    }

    public async Task<List<ChatMemory>> GetRecent(int limit = 50)
    {
        return await _mongo.Collection
            .Find(_ => true)
            .SortByDescending(x => x.Timestamp)
            .Limit(limit)
            .ToListAsync();
    }
}