namespace JarvisBackend.Services.Interfaces;

/// <summary>Stores user facts (birthday, name, etc.) in Redis for fast, relevant responses.</summary>
public interface IProfileService
{
    Task<Dictionary<string, string>> GetProfileAsync(string clientId, CancellationToken cancellationToken = default);
    Task SetAsync(string clientId, string key, string value, CancellationToken cancellationToken = default);
}
