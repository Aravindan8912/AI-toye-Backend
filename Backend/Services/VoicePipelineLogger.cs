using System.Text.Json;
using JarvisBackend.Services.Interfaces;

namespace JarvisBackend.Services;

public class VoicePipelineLogger : IVoicePipelineLogger
{
    private readonly IConfiguration _config;
    private readonly string _logFilePath;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public VoicePipelineLogger(IConfiguration config)
    {
        _config = config;
        var logDir = config["VoicePipeline:LogDirectory"] ?? "logs";
        var baseDir = Directory.GetCurrentDirectory();
        var dir = Path.Combine(baseDir, logDir);
        Directory.CreateDirectory(dir);
        _logFilePath = Path.Combine(dir, "voice-responses.jsonl");
    }

    public void LogWhisperResponse(string? transcript, string? inputPath)
    {
        var entry = new
        {
            Timestamp = DateTime.UtcNow,
            Service = "Whisper",
            Response = new
            {
                Transcript = transcript ?? "",
                TranscriptLength = transcript?.Length ?? 0,
                InputPath = inputPath
            }
        };
        AppendJsonLine(entry);
    }

    public void LogOllamaResponse(string? responseText, string? model)
    {
        var resolvedModel = model ?? _config["Ollama:Model"] ?? "";
        var entry = new
        {
            Timestamp = DateTime.UtcNow,
            Service = "Ollama",
            Response = new
            {
                Text = responseText ?? "",
                TextLength = responseText?.Length ?? 0,
                Model = resolvedModel
            }
        };
        AppendJsonLine(entry);
    }

    public void LogPiperResponse(string? inputText, int audioBytesLength)
    {
        var entry = new
        {
            Timestamp = DateTime.UtcNow,
            Service = "PiperTts",
            Response = new
            {
                InputTextLength = inputText?.Length ?? 0,
                AudioBytesLength = audioBytesLength
            }
        };
        AppendJsonLine(entry);
    }

    private void AppendJsonLine(object entry)
    {
        try
        {
            var line = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;
            File.AppendAllText(_logFilePath, line);
        }
        catch
        {
            // avoid breaking the pipeline if log write fails
        }
    }
}
