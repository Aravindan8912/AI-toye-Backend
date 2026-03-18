namespace JarvisBackend.Services.Interfaces;

public interface ITtsService
{
    /// <summary>Generates speech and returns WAV bytes. Caller does not need to read from disk.</summary>
    Task<byte[]> GenerateSpeechAsync(string text);
}