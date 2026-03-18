namespace JarvisBackend.Services.Interfaces;

public interface IEmbeddingService
{
    Task<float[]> GetEmbedding(string text);
}