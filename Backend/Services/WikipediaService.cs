using System.Text.Json;

namespace JarvisBackend.Services;

public class WikipediaService
{
    private readonly HttpClient _http;

    public WikipediaService(HttpClient? http = null)
    {
        if (http != null)
        {
            _http = http;
        }
        else
        {
            _http = new HttpClient();
            _http.DefaultRequestHeaders.Add("User-Agent", "JarvisBackend/1.0 (contact@localhost)");
        }
    }

    public async Task<string> GetSummary(string title)
    {
        var safe = Uri.EscapeDataString(title.Trim().Replace(' ', '_'));
        var url = $"https://en.wikipedia.org/api/rest_v1/page/summary/{safe}";
        var json = await _http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("extract", out var e)
            ? (e.GetString() ?? "")
            : "";
    }
}
