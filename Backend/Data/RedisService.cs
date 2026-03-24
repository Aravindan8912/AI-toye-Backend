using JarvisBackend.Configuration;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace JarvisBackend.Data;

/// <summary>Optional Redis connection for fast recent memory. GetDatabase() returns null when Redis is disabled.</summary>
public class RedisService
{
    private readonly RedisOptions _options;
    private readonly ILogger<RedisService> _logger;
    private ConnectionMultiplexer? _connection;

    public RedisService(IOptions<RedisOptions> options, ILogger<RedisService> logger)
    {
        _options = options.Value;
        _logger = logger;
        if (_options.Enabled)
        {
            try
            {
                _connection = ConnectionMultiplexer.Connect(_options.Configuration);
                _logger.LogInformation("Redis connected: {Configuration}", _options.Configuration);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Redis connection failed. Recent memory will use in-memory only.");
            }
        }
    }

    public IDatabase? GetDatabase() => _connection?.GetDatabase();

    public bool IsEnabled => _options.Enabled && _connection != null;
    public int RecentMemoryLimit => _options.RecentMemoryLimit;
    public string InstanceName => _options.InstanceName;
    public int EmbeddingCacheTtlSeconds => _options.EmbeddingCacheTtlSeconds;
    public int LlmCacheTtlMinutes => _options.LlmCacheTtlMinutes;

    /// <summary>Store selected character role (ironman, spiderman, captain, etc.) per client.</summary>
    public async Task SetRoleAsync(string clientId, string role)
    {
        if (string.IsNullOrEmpty(clientId) || !IsEnabled) return;
        var db = GetDatabase();
        if (db == null) return;
        var key = InstanceName + "mode:" + clientId;
        await db.StringSetAsync(key, (role ?? "").Trim().ToLowerInvariant());
    }

    /// <summary>Get current character role for client; null if not set.</summary>
    public async Task<string?> GetRoleAsync(string clientId)
    {
        if (string.IsNullOrEmpty(clientId) || !IsEnabled) return null;
        var db = GetDatabase();
        if (db == null) return null;
        var key = InstanceName + "mode:" + clientId;
        var val = await db.StringGetAsync(key);
        return val.HasValue && !val.IsNullOrEmpty ? val.ToString() : null;
    }
}
