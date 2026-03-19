namespace JarvisBackend.Configuration;

/// <summary>Redis connection and key settings for fast recent memory.</summary>
public class RedisOptions
{
    public const string SectionName = "Redis";

    public string Configuration { get; set; } = "localhost:6379";
    public string InstanceName { get; set; } = "jarvis:";

    /// <summary>Max recent messages per client to keep in Redis (fast context).</summary>
    public int RecentMemoryLimit { get; set; } = 20;

    /// <summary>TTL in seconds for cached embeddings (default 1 hour). 0 = no expiry.</summary>
    public int EmbeddingCacheTtlSeconds { get; set; } = 3600;

    /// <summary>TTL in minutes for LLM response cache (same question = instant). 0 = disabled.</summary>
    public int LlmCacheTtlMinutes { get; set; } = 10;

    public bool Enabled { get; set; } = true;
}
