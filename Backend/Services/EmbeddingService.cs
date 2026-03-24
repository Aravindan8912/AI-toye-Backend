using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JarvisBackend.Data;
using JarvisBackend.Services.Interfaces;

namespace JarvisBackend.Services;

/// <summary>Uses Ollama /api/embed with Redis cache for fast response (same text = cache hit).</summary>
public class EmbeddingService : IEmbeddingService
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly RedisService? _redis;
    private readonly ILogger<EmbeddingService> _logger;

    public EmbeddingService(HttpClient http, IConfiguration config, ILogger<EmbeddingService> logger, RedisService redis)
    {
        _http = http;
        _config = config;
        _redis = redis;
        _logger = logger;
    }

    public async Task<float[]> GetEmbedding(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<float>();

        var normalized = text.Trim();
        var cacheKey = _redis?.InstanceName + "embed:" + GetHashKey(normalized);

        // Redis cache for fast response
        if (_redis?.IsEnabled == true && !string.IsNullOrEmpty(cacheKey))
        {
            var db = _redis.GetDatabase();
            if (db != null)
            {
                var cached = await db.StringGetAsync(cacheKey);
                if (cached.HasValue && !cached.IsNullOrEmpty)
                {
                    try
                    {
                        var cachedVec = JsonSerializer.Deserialize<float[]>(cached!);
                        if (cachedVec != null && cachedVec.Length > 0)
                        {
                            _logger.LogDebug("Embedding: cache hit, dimensions={Dim}", cachedVec.Length);
                            return cachedVec;
                        }
                    }
                    catch { /* fall through to Ollama */ }
                }
            }
        }

        var model = _config["Ollama:EmbeddingModel"] ?? "nomic-embed-text";
        var body = JsonSerializer.Serialize(new { model, input = normalized });
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var res = await _http.PostAsync("/api/embed", content);
        res.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync());
        var root = doc.RootElement;
        if (!root.TryGetProperty("embeddings", out var embeddingsEl) || embeddingsEl.GetArrayLength() == 0)
            throw new InvalidOperationException("Ollama embed response missing or empty 'embeddings'.");

        var first = embeddingsEl[0];
        var list = new List<float>();
        foreach (var e in first.EnumerateArray())
            list.Add((float)e.GetDouble());
        var vec = list.ToArray();
        _logger.LogDebug("Embedding: model={Model}, dimensions={Dim}", model, vec.Length);

        // Store in Redis for fast response next time
        if (_redis?.IsEnabled == true && !string.IsNullOrEmpty(cacheKey) && vec.Length > 0)
        {
            try
            {
                var db = _redis.GetDatabase();
                if (db != null)
                {
                    var ttl = _redis.EmbeddingCacheTtlSeconds > 0 ? TimeSpan.FromSeconds(_redis.EmbeddingCacheTtlSeconds) : (TimeSpan?)null;
                    await db.StringSetAsync(cacheKey, JsonSerializer.Serialize(vec), ttl);
                    _logger.LogDebug("Embedding: cached for {Ttl}s", _redis.EmbeddingCacheTtlSeconds);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Embedding: cache set failed");
            }
        }

        return vec;
    }

    private static string GetHashKey(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }
}