using JarvisBackend.Services.Interfaces;
using JarvisBackend.Utils;

namespace JarvisBackend.Services;

public class WhisperService : IWhisperService
{
    private readonly IConfiguration _config;
    private readonly ILogger<WhisperService> _logger;

    public WhisperService(IConfiguration config, ILogger<WhisperService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task<string> TranscribeAsync(string filePath)
    {
        _logger.LogInformation("Whisper: transcribing {FilePath}", filePath);

        var whisperPath = _config["WhisperPath"] ?? "../AI/whisper";
        if (!Path.IsPathRooted(whisperPath))
            whisperPath = Path.Combine(Directory.GetCurrentDirectory(), whisperPath);

        var scriptPath = Path.Combine(whisperPath, "whisper_script.py");
        var absoluteAudioPath = Path.IsPathRooted(filePath) ? filePath : Path.GetFullPath(filePath);

        var python = OperatingSystem.IsWindows() ? "python" : "python3";
        var command = $"{python} \"{scriptPath}\" \"{absoluteAudioPath}\"";

        var (stdout, stderr, exitCode) = await ProcessHelper.RunProcessWithDetailsAsync(command);
        if (exitCode != 0)
        {
            _logger.LogError("Whisper: python exit {ExitCode}. stderr: {Stderr}", exitCode, stderr.Trim());
            return "";
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            if (string.IsNullOrWhiteSpace(stdout.Trim()))
                _logger.LogWarning("Whisper: empty transcript, stderr: {Stderr}", stderr.Trim());
            else
                _logger.LogDebug("Whisper stderr: {Stderr}", stderr.Trim());
        }

        // Full Whisper output — no truncation; stored as userText in MongoDB
        var text = stdout.Trim();
        _logger.LogInformation("Whisper: done, result length {Length}, text: {Preview}", text.Length, text.Length > 0 ? (text.Length > 80 ? text.Substring(0, 80) + "…" : text) : "(empty)");
        return text;
    }
}