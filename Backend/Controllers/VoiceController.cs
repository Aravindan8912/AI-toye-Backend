using JarvisBackend.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace JarvisBackend.Controllers;

[ApiController]
[Route("api/voice")]
public class VoiceController : ControllerBase
{
    private readonly IWhisperService _whisper;
    private readonly IOllamaService _ollama;
    private readonly ITtsService _tts;
    private readonly IVoicePipelineLogger _pipelineLog;
    private readonly IConfiguration _config;
    private readonly ILogger<VoiceController> _logger;

    public VoiceController(IWhisperService whisper, IOllamaService ollama, ITtsService tts, IVoicePipelineLogger pipelineLog, IConfiguration config, ILogger<VoiceController> logger)
    {
        _whisper = whisper;
        _ollama = ollama;
        _tts = tts;
        _pipelineLog = pipelineLog;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Audio → Whisper → Ollama → Piper TTS. Returns response as WAV. Send multipart/form-data with file (any field name).
    /// </summary>
    [HttpPost("ask")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> Ask()
    {
        if (!Request.HasFormContentType || string.IsNullOrEmpty(Request.ContentType) || !Request.ContentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Ask: missing or invalid Content-Type. Expected multipart/form-data.");
            return BadRequest(new { error = "Send POST with Content-Type: multipart/form-data and a form field containing the audio file (e.g. field name 'file')." });
        }
        var file = Request.Form.Files.GetFile("file") ?? Request.Form.Files.FirstOrDefault();
        if (file == null || file.Length == 0)
        {
            _logger.LogWarning("Ask: no audio file in request");
            return BadRequest(new { error = "No audio file. Send multipart/form-data with a file (field name 'file' or any)." });
        }

        _logger.LogInformation("Ask: received audio file {FileName}, {Size} bytes", file.FileName, file.Length);

        var dir = Path.Combine(Directory.GetCurrentDirectory(), "audio", "input");
        Directory.CreateDirectory(dir);
        var fileName = $"{Guid.NewGuid()}.wav";
        var path = Path.Combine(dir, fileName);

        await using (var stream = new FileStream(path, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }

        // 🎤 1. STT
        var userText = await _whisper.TranscribeAsync(path);
        _logger.LogInformation("Ask: Whisper transcript length {Length}", userText?.Length ?? 0);
        _pipelineLog.LogWhisperResponse(userText, path);

        // 🧠 2. LLM
        var persona = _config["Assistant:Persona"] ?? "";
        var userProfile = _config["Assistant:UserProfile"] ?? "";
        var systemBlock = (string.IsNullOrWhiteSpace(persona) && string.IsNullOrWhiteSpace(userProfile))
            ? ""
            : string.Join("\n\n", new[] { persona, userProfile }.Where(s => !string.IsNullOrWhiteSpace(s))) + "\n\n";
        var prompt = $"{systemBlock}Keep answers short.\nUser: {userText ?? ""}";
        string responseText;
        try
        {
            responseText = await _ollama.GenerateAsync(prompt);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Ollama", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(ex, "Ask: Ollama unavailable");
            return StatusCode(503, new { error = "Ollama is not available. Start Ollama (e.g. ollama run llama3) and ensure it is running on http://localhost:11434" });
        }
        _logger.LogInformation("Ask: Ollama response length {Length}", responseText?.Length ?? 0);
        _pipelineLog.LogOllamaResponse(responseText, null);

        // 🔊 3. TTS
        var bytes = await _tts.GenerateSpeechAsync(responseText ?? "");
        _pipelineLog.LogPiperResponse(responseText, bytes.Length);
        return File(bytes, "audio/wav", "response.wav");
    }

    /// <summary>
    /// ESP32 flow step 1: GET beep audio to play before recording.
    /// </summary>
    [HttpGet("beep")]
    public IActionResult Beep()
    {
        _logger.LogDebug("Beep: returning beep WAV");

        const int sampleRate = 16000;
        const double frequency = 440;
        const double durationSec = 0.3;
        int numSamples = (int)(sampleRate * durationSec);
        var bytes = new byte[44 + numSamples * 2]; // 44-byte WAV header + 16-bit samples

        // WAV header (minimal)
        System.Text.Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0);
        BitConverter.GetBytes(bytes.Length - 8).CopyTo(bytes, 4);
        System.Text.Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(bytes, 8);
        BitConverter.GetBytes(16).CopyTo(bytes, 16);   // fmt chunk size
        BitConverter.GetBytes((short)1).CopyTo(bytes, 20);  // PCM
        BitConverter.GetBytes((short)1).CopyTo(bytes, 22);  // mono
        BitConverter.GetBytes(sampleRate).CopyTo(bytes, 24);
        BitConverter.GetBytes(sampleRate * 2).CopyTo(bytes, 28); // byte rate
        BitConverter.GetBytes((short)2).CopyTo(bytes, 32);  // block align
        BitConverter.GetBytes((short)16).CopyTo(bytes, 34); // bits per sample
        System.Text.Encoding.ASCII.GetBytes("data").CopyTo(bytes, 36);
        BitConverter.GetBytes(numSamples * 2).CopyTo(bytes, 40);

        for (int i = 0; i < numSamples; i++)
        {
            double t = (double)i / sampleRate;
            short sample = (short)(Math.Sin(2 * Math.PI * frequency * t) * 8000);
            BitConverter.GetBytes(sample).CopyTo(bytes, 44 + i * 2);
        }

        return File(bytes, "audio/wav", "beep.wav");
    }

    /// <summary>
    /// ESP32 flow step: after recording ~10 sec, POST audio here. Backend runs Whisper and returns text.
    /// Accepts multipart/form-data with any file field name (e.g. "file", "audio", "recording").
    /// </summary>
    [HttpPost("stt")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> SpeechToText()
    {
        if (!Request.HasFormContentType || string.IsNullOrEmpty(Request.ContentType) || !Request.ContentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("STT: missing or invalid Content-Type. Expected multipart/form-data.");
            return BadRequest(new { error = "Send POST with Content-Type: multipart/form-data and a form field containing the audio file (e.g. field name 'file')." });
        }
        var file = Request.Form.Files.GetFile("file") ?? Request.Form.Files.FirstOrDefault();
        if (file == null || file.Length == 0)
        {
            _logger.LogWarning("STT: no audio file in request");
            return BadRequest(new { error = "No audio file. Send multipart/form-data with a file (field name 'file' or any)." });
        }

        _logger.LogInformation("STT: received audio file {FileName}, {Size} bytes", file.FileName, file.Length);

        var dir = Path.Combine(Directory.GetCurrentDirectory(), "audio", "input");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "input.wav");

        await using (var stream = new FileStream(path, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }

        var text = await _whisper.TranscribeAsync(path);
        _logger.LogInformation("STT: transcript length {Length}", text?.Length ?? 0);
        _pipelineLog.LogWhisperResponse(text, path);
        return Ok(new { text });
    }
}