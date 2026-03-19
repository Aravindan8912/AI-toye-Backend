using JarvisBackend.Models;

namespace JarvisBackend.Services.Interfaces;

public interface IMemoryService
{
    Task<List<ChatMemory>> Search(float[]? embedding);
    Task Save(ChatMemory memory, string? clientId = null);
    Task<List<ChatMemory>> GetRecent(int limit = 50);
    /// <summary>Fast recent memory from Redis (per device/session). Returns empty if Redis disabled.</summary>
    Task<List<ChatMemory>> GetRecentByClient(string? clientId, int limit = 20);
}