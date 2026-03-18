using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using JarvisBackend.Models;
using JarvisBackend.Services;
using JarvisBackend.Services.Interfaces;
using JarvisBackend.Utils;
using JarvisBackend.WebSockets;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace JarvisBackend.Workers;

/// <summary>Background consumer: RabbitMQ audio_queue → Whisper → Ollama → TTS → send WAV back over WebSocket.</summary>
public class AudioWorker : BackgroundService
{
    private readonly RabbitMqService _rabbit;
    private readonly IWhisperService _whisper;
    private readonly IOllamaService _ollama;
    private readonly ITtsService _tts;
    private readonly ConnectionManager _manager;
    private readonly ILogger<AudioWorker> _logger;

    public AudioWorker(
        RabbitMqService rabbit,
        IWhisperService whisper,
        IOllamaService ollama,
        ITtsService tts,
        ConnectionManager manager,
        ILogger<AudioWorker> logger)
    {
        _rabbit = rabbit;
        _whisper = whisper;
        _ollama = ollama;
        _tts = tts;
        _manager = manager;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var channel = _rabbit.TryGetChannel();
        if (channel == null)
            return Task.CompletedTask; // RabbitMQ not available; legacy /ws disabled

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.Received += async (_, ea) =>
        {
            var json = Encoding.UTF8.GetString(ea.Body.ToArray());
            var message = JsonSerializer.Deserialize<AudioMessage>(json);
            if (message?.AudioData == null || message.AudioData.Length == 0)
                return;

            var path = Path.Combine(Path.GetTempPath(), $"audio_{Guid.NewGuid()}.wav");
            try
            {
                var wavBytes = WavHelper.EnsureWav(message.AudioData);
                await File.WriteAllBytesAsync(path, wavBytes, stoppingToken);

                var text = await _whisper.TranscribeAsync(path);
                _logger.LogInformation("[Pipeline] STT (Whisper): {Text}", string.IsNullOrWhiteSpace(text) ? "(empty)" : text);

                string reply;
                if (string.IsNullOrWhiteSpace(text))
                {
                    reply = "I couldn't hear you properly, please say again.";
                    _logger.LogInformation("[Pipeline] No speech detected → TTS: \"{Reply}\"", reply);
                }
                else
                {
                    var prompt = $"You are a helpful assistant. Keep answers short.\nUser: {text}";
                    reply = await _ollama.GenerateAsync(prompt) ?? "";
                    _logger.LogInformation("[Pipeline] LLM (Ollama): {Reply}", reply);
                }

                var audioOut = await _tts.GenerateSpeechAsync(reply);
                _logger.LogInformation("[Pipeline] Piper TTS response: {ByteCount} bytes (text: {Text})", audioOut.Length, reply);

                // Send raw PCM to ESP32 (strip WAV header) so binary payload has no null bytes that could truncate on the client
                var toSend = WavHelper.StripWavHeader(audioOut);

                var socket = _manager.Get(message.ClientId ?? "");
                if (socket != null && socket.State == WebSocketState.Open)
                {
                    await socket.SendAsync(
                        new ArraySegment<byte>(toSend),
                        WebSocketMessageType.Binary,
                        true,
                        CancellationToken.None);
                }
            }
            finally
            {
                try { File.Delete(path); } catch { /* ignore */ }
            }
        };

        channel.BasicConsume(_rabbit.QueueName, autoAck: true, consumer: consumer);
        return Task.CompletedTask;
    }
}
