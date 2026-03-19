using JarvisBackend.Services.Interfaces;
using StackExchange.Redis;

namespace JarvisBackend.Data;

/// <summary>Stores user facts (birthday, name, last_topic) in Redis. Better than vector search for concrete facts.</summary>
public class ProfileService : IProfileService
{
    private readonly RedisService _redis;
    private readonly ILogger<ProfileService> _logger;

    public ProfileService(RedisService redis, ILogger<ProfileService> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task<Dictionary<string, string>> GetProfileAsync(string clientId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(clientId) || !_redis.IsEnabled)
            return new Dictionary<string, string>();

        var db = _redis.GetDatabase();
        if (db == null)
            return new Dictionary<string, string>();

        var key = _redis.InstanceName + "profile:" + clientId;
        var entries = await db.HashGetAllAsync(key);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
            if (e.Name.HasValue && e.Value.HasValue)
                result[e.Name!] = e.Value!;
        return result;
    }

    public async Task SetAsync(string clientId, string key, string value, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(key) || !_redis.IsEnabled)
            return;

        var db = _redis.GetDatabase();
        if (db == null)
            return;

        var redisKey = _redis.InstanceName + "profile:" + clientId;
        await db.HashSetAsync(redisKey, key, value);
        _logger.LogDebug("Profile set: ClientId={ClientId}, Key={Key}", clientId, key);
    }
}
