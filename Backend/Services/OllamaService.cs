using System.Text;
using System.Text.Json;
using JarvisBackend.Services.Interfaces;
using System.Runtime.CompilerServices;
using System.Net.Http;

namespace JarvisBackend.Services;

public class OllamaService : IOllamaService
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<OllamaService> _logger;

    public OllamaService(HttpClient http, IConfiguration config, ILogger<OllamaService> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
    }

    public async Task<string> GenerateAsync(string prompt)
    {
        return await GenerateAsync(prompt, null, null);
    }

    public async Task<string> GenerateAsync(string prompt, string? model, int? numPredict, CancellationToken cancellationToken = default)
    {
        var resolvedModel = model ?? _config["Ollama:Model"] ?? "llama3";
        _logger.LogInformation("Ollama: generating with model {Model}, prompt length {Length}", resolvedModel, prompt?.Length ?? 0);

        var resolvedNumPredict = numPredict ?? _config.GetValue("Ollama:NumPredict", 50);
        var temperature = _config.GetValue("Ollama:Temperature", 0.3);
        var topK = _config.GetValue("Ollama:TopK", 20);
        var topP = _config.GetValue("Ollama:TopP", 0.8);
        var request = BuildRequest(prompt ?? "", resolvedModel, false, resolvedNumPredict, temperature, topK, topP);

        using var content = new StringContent(
            JsonSerializer.Serialize(request),
            Encoding.UTF8,
            "application/json"
        );

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync("api/generate", content, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Ollama connection failed. Is Ollama running? Start with: ollama run {Model}", resolvedModel);
            throw new InvalidOperationException(
                "Ollama is not running or not reachable at " + (_http.BaseAddress?.ToString() ?? "configured URL") + ". Start Ollama (e.g. ollama run " + resolvedModel + ") and ensure it listens on the configured address.", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Ollama error {StatusCode}: {Body}", response.StatusCode, body);
            throw new InvalidOperationException($"Ollama error ({(int)response.StatusCode}): {body}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("response", out var responseEl))
        {
            var responseText = responseEl.GetString() ?? "";
            _logger.LogInformation("Ollama: response length {Length}", responseText.Length);
            return responseText;
        }

        _logger.LogError("Ollama response missing 'response' field");
        throw new InvalidOperationException("Ollama response missing 'response' field.");
    }

    public async IAsyncEnumerable<string> GenerateStreamAsync(
        string prompt,
        string? model = null,
        int? numPredict = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var resolvedModel = model ?? _config["Ollama:Model"] ?? "llama3";
        var resolvedNumPredict = numPredict ?? _config.GetValue("Ollama:NumPredict", 50);
        var temperature = _config.GetValue("Ollama:Temperature", 0.3);
        var topK = _config.GetValue("Ollama:TopK", 20);
        var topP = _config.GetValue("Ollama:TopP", 0.8);
        var request = BuildRequest(prompt ?? "", resolvedModel, true, resolvedNumPredict, temperature, topK, topP);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "api/generate")
        {
            Content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json")
        };

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Ollama streaming connection failed for model {Model}", resolvedModel);
            throw new InvalidOperationException(
                "Ollama is not running or not reachable at " + (_http.BaseAddress?.ToString() ?? "configured URL") + ". Start Ollama (e.g. ollama run " + resolvedModel + ") and ensure it listens on the configured address.", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Ollama error {StatusCode}: {Body}", response.StatusCode, body);
            throw new InvalidOperationException($"Ollama error ({(int)response.StatusCode}): {body}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line)) continue;

            using var chunk = JsonDocument.Parse(line);
            var root = chunk.RootElement;

            if (root.TryGetProperty("response", out var tokenEl))
            {
                var token = tokenEl.GetString();
                if (!string.IsNullOrEmpty(token))
                    yield return token!;
            }

            if (root.TryGetProperty("done", out var doneEl) && doneEl.GetBoolean())
                break;
        }
    }

    private static object BuildRequest(string prompt, string model, bool stream, int numPredict, double temperature, int topK, double topP)
    {
        return new
        {
            model,
            prompt = prompt?.Trim() ?? "",
            stream,
            options = new
            {
                num_predict = numPredict,
                temperature,
                top_k = topK,
                top_p = topP
            }
        };
    }
}
