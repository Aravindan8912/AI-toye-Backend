using System.Collections.Concurrent;
using System.Net.WebSockets;
using JarvisBackend.Services.Interfaces;

namespace JarvisBackend;

/// <summary>Handles WebSocket connections from ESP32-S3: receive audio → Whisper → Ollama → Piper TTS → send WAV back.</summary>
public static class WebSocketServer
{
    public static readonly ConcurrentDictionary<string, WebSocket> Connections = new();

    public static async Task HandleConnectionAsync(WebSocket socket, IServiceProvider services, CancellationToken cancel = default)
    {
        var id = Guid.NewGuid().ToString();
        Connections[id] = socket;
        Serilog.Log.Information("WS Connected: {ConnectionId}", id);

        var buffer = new byte[4096];
        var audioBuffer = new MemoryStream();
        var inputDir = Path.Combine(Directory.GetCurrentDirectory(), "audio", "input");
        Directory.CreateDirectory(inputDir);

        var whisper = services.GetRequiredService<IWhisperService>();
        var ollama = services.GetRequiredService<IOllamaService>();
        var tts = services.GetRequiredService<ITtsService>();

        try
        {
            while (socket.State == WebSocketState.Open && !cancel.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, cancel);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                audioBuffer.Write(buffer.AsSpan(0, result.Count));

                // Process one full message (ESP32 sends one WAV per utterance when EndOfMessage)
                if (!result.EndOfMessage)
                    continue;

                var audioData = audioBuffer.ToArray();
                audioBuffer.SetLength(0);

                // Whisper expects a file path: write received bytes to temp WAV
                var tempPath = Path.Combine(inputDir, $"ws-{Guid.NewGuid():N}.wav");
                await File.WriteAllBytesAsync(tempPath, audioData, cancel);

                string text;
                try
                {
                    text = await whisper.TranscribeAsync(tempPath);
                }
                finally
                {
                    try { File.Delete(tempPath); } catch { /* ignore */ }
                }

                Serilog.Log.Information("WS STT: {Text}", text);

                if (string.IsNullOrWhiteSpace(text))
                    continue;

                var prompt = $"You are a helpful assistant. Keep answers short.\nUser: {text}";
                var reply = await ollama.GenerateAsync(prompt);
                Serilog.Log.Information("WS LLM: {Reply}", reply);

                var wavBytes = await tts.GenerateSpeechAsync(reply ?? "");

                await socket.SendAsync(
                    wavBytes,
                    WebSocketMessageType.Binary,
                    true,
                    cancel);
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "WebSocket error");
        }
        finally
        {
            Connections.TryRemove(id, out _);
            if (socket.State == WebSocketState.Open)
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None);
                }
                catch { /* ignore */ }
            }
            Serilog.Log.Information("WS Disconnected: {ConnectionId}", id);
        }
    }
}
