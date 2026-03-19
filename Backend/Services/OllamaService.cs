using System.Text;
using System.Text.Json;
using JarvisBackend.Services.Interfaces;

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
        var model = _config["Ollama:Model"] ?? "llama3";
        _logger.LogInformation("Ollama: generating with model {Model}, prompt length {Length}", model, prompt?.Length ?? 0);

        var numPredict = _config.GetValue("Ollama:NumPredict", 50);
        var temperature = _config.GetValue("Ollama:Temperature", 0.3);
        var topK = _config.GetValue("Ollama:TopK", 20);
        var topP = _config.GetValue("Ollama:TopP", 0.8);
        var request = new
        {
            model,
            prompt = prompt?.Trim() ?? "",
            stream = false,
            options = new
            {
                num_predict = numPredict,
                temperature = temperature,
                top_k = topK,
                top_p = topP
            }
        };

        var content = new StringContent(
            JsonSerializer.Serialize(request),
            Encoding.UTF8,
            "application/json"
        );

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync("api/generate", content);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Ollama connection failed. Is Ollama running? Start with: ollama run {Model}", model);
            throw new InvalidOperationException(
                "Ollama is not running or not reachable at " + (_http.BaseAddress?.ToString() ?? "configured URL") + ". Start Ollama (e.g. ollama run " + model + ") and ensure it listens on the configured address.", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            _logger.LogError("Ollama error {StatusCode}: {Body}", response.StatusCode, body);
            throw new InvalidOperationException($"Ollama error ({(int)response.StatusCode}): {body}");
        }

        var json = await response.Content.ReadAsStringAsync();
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
}
