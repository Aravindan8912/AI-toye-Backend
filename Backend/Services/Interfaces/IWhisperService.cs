namespace JarvisBackend.Services.Interfaces;

public interface IWhisperService
{
    Task<string> TranscribeAsync(string filePath);
}