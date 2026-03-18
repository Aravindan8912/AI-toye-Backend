namespace JarvisBackend.Services.Interfaces;

public interface IOllamaService
{
    Task<string> GenerateAsync(string prompt);
}