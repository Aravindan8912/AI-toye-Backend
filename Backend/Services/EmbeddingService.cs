using System.Text.Json;
using JarvisBackend.Services.Interfaces;

namespace JarvisBackend.Services;

/// <summary>Uses Ollama /api/embed to produce vector embeddings for memory search.</summary>
public class EmbeddingService : IEmbeddingService
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<EmbeddingService> _logger;

    public EmbeddingService(HttpClient http, IConfiguration config, ILogger<EmbeddingService> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
    }

    public async Task<float[]> GetEmbedding(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<float>();

        var model = _config["Ollama:EmbeddingModel"] ?? "nomic-embed-text";
        var body = JsonSerializer.Serialize(new { model, input = text.Trim() });
        var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

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
        return vec;
    }
}