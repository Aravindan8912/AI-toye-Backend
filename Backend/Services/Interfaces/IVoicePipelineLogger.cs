namespace JarvisBackend.Services.Interfaces;

/// <summary>Writes Whisper, Ollama, and Piper TTS responses as JSON lines to a log file.</summary>
public interface IVoicePipelineLogger
{
    void LogWhisperResponse(string? transcript, string? inputPath);
    void LogOllamaResponse(string? responseText, string? model);
    void LogPiperResponse(string? inputText, int audioBytesLength);
}
