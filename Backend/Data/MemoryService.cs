using System.Text.Json;
using JarvisBackend.Models;
using JarvisBackend.Services.Interfaces;
using MongoDB.Driver;
using StackExchange.Redis;

namespace JarvisBackend.Data;

/// <summary>Vector-backed memory in MongoDB (long-term); fast recent memory in Redis (per client).</summary>
public class MemoryService : IMemoryService
{
    private readonly MongoService _mongo;
    private readonly RedisService? _redis;
    private readonly IConfiguration _config;
    private readonly ILogger<MemoryService> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public MemoryService(MongoService mongo, IConfiguration config, ILogger<MemoryService> logger, RedisService? redis = null)
    {
        _mongo = mongo;
        _config = config;
        _logger = logger;
        _redis = redis;
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

    public async Task Save(ChatMemory memory, string? clientId = null)
    {
        if (memory == null)
            return;
        memory.Id ??= Guid.NewGuid().ToString();
        memory.Timestamp = memory.Timestamp == default ? DateTime.UtcNow : memory.Timestamp;
        await _mongo.Collection.InsertOneAsync(memory);
        _logger.LogDebug("Memory saved to MongoDB: {Id}", memory.Id);

        if (_redis?.IsEnabled == true && !string.IsNullOrEmpty(clientId))
        {
            var key = _redis.InstanceName + "recent:" + clientId;
            var db = _redis.GetDatabase();
            if (db != null)
            {
                var json = JsonSerializer.Serialize(new { memory.Id, memory.UserText, memory.BotText, memory.Timestamp }, JsonOptions);
                await db.ListRightPushAsync(key, json);
                await db.ListTrimAsync(key, -_redis.RecentMemoryLimit, -1);
                _logger.LogDebug("Memory pushed to Redis recent: {ClientId}", clientId);
            }
        }
    }

    public async Task<List<ChatMemory>> GetRecent(int limit = 50)
    {
        return await _mongo.Collection
            .Find(_ => true)
            .SortByDescending(x => x.Timestamp)
            .Limit(limit)
            .ToListAsync();
    }

    public async Task<List<ChatMemory>> GetRecentByClient(string? clientId, int limit = 20)
    {
        if (string.IsNullOrEmpty(clientId) || _redis?.IsEnabled != true)
            return new List<ChatMemory>();

        var db = _redis.GetDatabase();
        if (db == null)
            return new List<ChatMemory>();

        var key = _redis.InstanceName + "recent:" + clientId;
        var values = await db.ListRangeAsync(key, -limit, -1);
        var list = new List<ChatMemory>();
        foreach (var v in values)
        {
            if (v.IsNullOrEmpty) continue;
            try
            {
                var o = JsonSerializer.Deserialize<JsonElement>(v!);
                list.Add(new ChatMemory
                {
                    Id = o.TryGetProperty("id", out var id) ? id.GetString() : null,
                    UserText = o.TryGetProperty("userText", out var u) ? u.GetString() : null,
                    BotText = o.TryGetProperty("botText", out var b) ? b.GetString() : null,
                    Timestamp = o.TryGetProperty("timestamp", out var t) ? DateTime.Parse(t.GetString()!) : default
                });
            }
            catch { /* skip malformed */ }
        }
        return list;
    }
}