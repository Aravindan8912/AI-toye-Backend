namespace JarvisBackend.Services.Interfaces;

public interface IOllamaService
{
    Task<string> GenerateAsync(string prompt);
    Task<string> GenerateAsync(string prompt, string? model, int? numPredict, CancellationToken cancellationToken = default);
    IAsyncEnumerable<string> GenerateStreamAsync(string prompt, string? model = null, int? numPredict = null, CancellationToken cancellationToken = default);
}