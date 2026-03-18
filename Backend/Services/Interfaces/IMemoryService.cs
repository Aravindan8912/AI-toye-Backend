using JarvisBackend.Models;

namespace JarvisBackend.Services.Interfaces;

public interface IMemoryService
{
    Task<List<ChatMemory>> Search(float[]? embedding);
    Task Save(ChatMemory memory);
    Task<List<ChatMemory>> GetRecent(int limit = 50);
}