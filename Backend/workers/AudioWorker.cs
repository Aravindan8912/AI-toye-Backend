using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using JarvisBackend.Models;
using JarvisBackend.Services;
using JarvisBackend.Services.Interfaces;
using JarvisBackend.Utils;
using JarvisBackend.WebSockets;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace JarvisBackend.Workers;

/// <summary>
/// Consumes audio from RabbitMQ and runs STT → LLM → TTS in a background processor.
/// Uses manual ack so messages show as Unacked in RabbitMQ UI while processing.
/// Heavy work is offloaded so the consumer thread returns immediately (avoids thread pool starvation).
/// </summary>
public class AudioWorker : BackgroundService
{
    private readonly RabbitMqService _rabbit;
    private readonly IWhisperService _whisper;
    private readonly IOllamaService _ollama;
    private readonly ITtsService _tts;
    private readonly IEmbeddingService _embed;
    private readonly IMemoryService _memory;
    private readonly IKnowledgeService _knowledge;
    private readonly IConfiguration _config;
    private readonly ConnectionManager _manager;
    private readonly ILogger<AudioWorker> _logger;

    public AudioWorker(
        RabbitMqService rabbit,
        IWhisperService whisper,
        IOllamaService ollama,
        ITtsService tts,
        IEmbeddingService embed,
        IMemoryService memory,
        IKnowledgeService knowledge,
        IConfiguration config,
        ConnectionManager manager,
        ILogger<AudioWorker> logger)
    {
        _rabbit = rabbit;
        _whisper = whisper;
        _ollama = ollama;
        _tts = tts;
        _embed = embed;
        _memory = memory;
        _knowledge = knowledge;
        _config = config;
        _manager = manager;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var channel = _rabbit.TryGetChannel();
        if (channel == null)
            return;

        // Prefetch 1: only one message in flight so you see "Unacked = 1" in RabbitMQ UI while processing
        channel.BasicQos(prefetchSize: 0, prefetchCount: 1, global: false);

        var workChannel = Channel.CreateUnbounded<(string Path, string ClientId, ulong DeliveryTag)>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        var ackChannel = Channel.CreateUnbounded<ulong>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        // Dedicated ack loop: only this code touches channel.BasicAck (IModel is not thread-safe)
        var ackTask = RunAckLoopAsync(channel, ackChannel.Reader, stoppingToken);

        // Background processor: heavy work (Whisper, LLM, TTS) runs here so consumer thread is not blocked
        var processTask = RunProcessorLoopAsync(workChannel.Reader, ackChannel.Writer, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.Received += async (_, ea) =>
        {
            byte[] body = ea.Body.ToArray();
            ulong tag = ea.DeliveryTag;
            try
            {
                var message = JsonSerializer.Deserialize<AudioMessage>(Encoding.UTF8.GetString(body));
                if (message?.AudioData == null || message.AudioData.Length == 0)
                {
                    channel.BasicAck(tag, false);
                    return;
                }

                var path = Path.Combine(Path.GetTempPath(), $"audio_{Guid.NewGuid()}.wav");
                var wavBytes = WavHelper.EnsureWav(message.AudioData);
                await File.WriteAllBytesAsync(path, wavBytes, stoppingToken);

                // Enqueue and return immediately — message stays Unacked until processor finishes
                await workChannel.Writer.WriteAsync((path, message.ClientId ?? "", tag), stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AudioWorker: failed to enqueue message");
                try { channel.BasicNack(tag, false, false); } catch { }
            }
        };

        channel.BasicConsume(_rabbit.QueueName, autoAck: false, consumer: consumer);
        _logger.LogInformation("AudioWorker: consumer started (prefetch=1, manual ack). Pipeline runs in background.");

        await Task.WhenAll(ackTask, processTask);
    }

    private async Task RunAckLoopAsync(IModel channel, ChannelReader<ulong> reader, CancellationToken stoppingToken)
    {
        await foreach (var tag in reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                channel.BasicAck(tag, false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AudioWorker: BasicAck failed for tag {Tag}", tag);
            }
        }
    }

    private async Task RunProcessorLoopAsync(
        ChannelReader<(string Path, string ClientId, ulong DeliveryTag)> reader,
        ChannelWriter<ulong> ackWriter,
        CancellationToken stoppingToken)
    {
        await foreach (var (path, clientId, deliveryTag) in reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessOneAsync(path, clientId, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AudioWorker pipeline error for ClientId={ClientId}", clientId);
            }
            finally
            {
                try { File.Delete(path); } catch { }
                try { await ackWriter.WriteAsync(deliveryTag, stoppingToken); } catch { }
            }
        }
    }

    private async Task ProcessOneAsync(string path, string clientId, CancellationToken stoppingToken)
    {
        // Skip Whisper for very small files (fragments); still send a short TTS reply
        var fileLen = new FileInfo(path).Length;
        const long minPayloadBytes = 32000; // ~1 sec at 16 kHz 16-bit
        string text;
        string reply;
        if (fileLen < minPayloadBytes)
        {
            _logger.LogInformation("[Pipeline] Audio too short ({Bytes} bytes). Skipping Whisper/LLM/DB.", fileLen);
            text = "";
            reply = "Recording too short. Please speak for at least a second.";
        }
        else
        {
            text = await _whisper.TranscribeAsync(path);
            _logger.LogInformation("[Pipeline] STT (Whisper): {Text}", string.IsNullOrWhiteSpace(text) ? "(empty)" : text);
            if (string.IsNullOrWhiteSpace(text))
                reply = "I couldn't hear you properly, please say again.";
            else
            {
                // Vector: embed user text, search memory + knowledge for context
                var embedding = await _embed.GetEmbedding(text);
                var memories = await _memory.Search(embedding);
                var knowledgeChunks = await _knowledge.SearchAsync(embedding);
                var memoryContext = string.Join("\n", memories.Select(m => $"User: {m.UserText}\nBot: {m.BotText}"));
                var knowledgeContext = knowledgeChunks.Count > 0
                    ? string.Join("\n\n", knowledgeChunks.Select(k => $"[{k.Title}]\n{k.Content}"))
                    : "";
                var persona = _config["Assistant:Persona"] ?? "";
                var userProfile = _config["Assistant:UserProfile"] ?? "";
                var systemBlock = (string.IsNullOrWhiteSpace(persona) && string.IsNullOrWhiteSpace(userProfile))
                    ? ""
                    : string.Join("\n\n", new[] { persona, userProfile }.Where(s => !string.IsNullOrWhiteSpace(s))) + "\n\n";
                var prompt = $@"{systemBlock}Keep answers short. Answer using the knowledge below when relevant; otherwise use conversation context or general knowledge.

{(string.IsNullOrEmpty(knowledgeContext) ? "" : "Knowledge:\n" + knowledgeContext + "\n\n")}{(string.IsNullOrEmpty(memoryContext) ? "" : "Recent conversation:\n" + memoryContext + "\n\n")}User: {text}
";
                reply = await _ollama.GenerateAsync(prompt) ?? "";
                _logger.LogInformation("[Pipeline] LLM: {Reply}", reply);

                // Store extracted text + response in DB with vector (for future similarity search)
                await _memory.Save(new ChatMemory
                {
                    Id = Guid.NewGuid().ToString(),
                    UserText = text,
                    BotText = reply,
                    Embedding = embedding,
                    Timestamp = DateTime.UtcNow
                });
                _logger.LogInformation("[Pipeline] Stored in DB (vector memory). userText + botText saved.");
            }
        }

        var audioOut = await _tts.GenerateSpeechAsync(reply);
        _logger.LogInformation("[Pipeline] TTS: {Bytes} bytes", audioOut.Length);

        var ttsSampleRate = 22050;
        if (audioOut != null && audioOut.Length >= 28 && WavHelper.IsWav(audioOut))
        {
            ttsSampleRate = BitConverter.ToInt32(audioOut, 24);
            if (ttsSampleRate <= 0 || ttsSampleRate > 48000) ttsSampleRate = 22050;
        }

        var toSend = WavHelper.StripWavHeader(audioOut);
        var socket = _manager.Get(clientId);

        if (socket != null && socket.State == WebSocketState.Open)
        {
            var chatPayload = JsonSerializer.Serialize(new { userText = text ?? "", botText = reply ?? "", ttsSampleRate });
            await socket.SendAsync(
                new ArraySegment<byte>(Encoding.UTF8.GetBytes(chatPayload)),
                WebSocketMessageType.Text,
                true,
                CancellationToken.None);
            await socket.SendAsync(
                new ArraySegment<byte>(toSend),
                WebSocketMessageType.Binary,
                true,
                CancellationToken.None);
        }
    }
}
