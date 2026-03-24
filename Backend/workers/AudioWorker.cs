using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using JarvisBackend.Data;
using JarvisBackend.Models;
using JarvisBackend.Services;
using JarvisBackend.Services.Interfaces;
using JarvisBackend.Utils;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace JarvisBackend.Workers;

/// <summary>
/// Consumes audio from RabbitMQ (from MQTT listener) and runs STT → LLM → TTS.
/// Writes recent memory to Redis (fast) and long-term vector memory to MongoDB.
/// Sends responses back via MQTT to ESP32.
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
    private readonly IMqttResponsePublisher _mqttPublisher;
    private readonly IReminderService _reminderService;
    private readonly IProfileService _profileService;
    private readonly IRoleService _roleService;
    private readonly ChannelWriter<ReminderItem> _reminderWriter;
    private readonly RedisService _redis;
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
        IMqttResponsePublisher mqttPublisher,
        IReminderService reminderService,
        IProfileService profileService,
        IRoleService roleService,
        ChannelWriter<ReminderItem> reminderWriter,
        RedisService redis,
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
        _mqttPublisher = mqttPublisher;
        _reminderService = reminderService;
        _profileService = profileService;
        _roleService = roleService;
        _reminderWriter = reminderWriter;
        _redis = redis;
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
                var json = Encoding.UTF8.GetString(body).Trim();
                if (json.Length == 0)
                {
                    _logger.LogWarning(
                        "AudioWorker: RabbitMQ message has empty body (deliveryTag={Tag}). Acking — purge stale/non-JSON messages from queue {Queue}.",
                        tag, _rabbit.QueueName);
                    channel.BasicAck(tag, false);
                    return;
                }

                AudioMessage? message;
                try
                {
                    message = JsonSerializer.Deserialize<AudioMessage>(json);
                }
                catch (JsonException jex)
                {
                    _logger.LogWarning(
                        jex,
                        "AudioWorker: RabbitMQ body is not valid JSON (deliveryTag={Tag}, bytes={Bytes}). Preview: {Preview}. Acking to drop poison message.",
                        tag,
                        body.Length,
                        json.Length <= 200 ? json : json[..200] + "…");
                    channel.BasicAck(tag, false);
                    return;
                }

                if (message?.AudioData == null || message.AudioData.Length == 0)
                {
                    channel.BasicAck(tag, false);
                    return;
                }

                var path = Path.Combine(Path.GetTempPath(), $"audio_{Guid.NewGuid()}.wav");
                var wavBytes = WavHelper.EnsureWav(message.AudioData);
                await File.WriteAllBytesAsync(path, wavBytes, stoppingToken);

                var (sampleCount, peakAbs, rms, nearSilenceFrac) = PcmLevelDiagnostics.AnalyzeRawOrWavPcm(message.AudioData);
                _logger.LogInformation(
                    "[Pipeline] Mic PCM check: samples={Samples}, peakAbs={Peak}, rms={Rms:F1}, nearSilence={NearSilence:P0}",
                    sampleCount, peakAbs, rms, nearSilenceFrac);
                if (sampleCount > 0 && peakAbs < 400 && rms < 80)
                    _logger.LogWarning(
                        "[Pipeline] PCM looks very quiet (likely silence or dead mic path on ESP32). Check I2S pins, MIC_SAMPLE_SHIFT / MIC_DIGITAL_GAIN, try I2S_COMM_FORMAT_STAND_MSB or ONLY_RIGHT.");
                if (sampleCount > 0 && nearSilenceFrac > 0.92 && peakAbs < 2000)
                    _logger.LogWarning(
                        "[Pipeline] PCM is mostly near-zero samples — recording may be silent or gain too low.");

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
                // Store user facts when they say things like "my birthday is X" (reminder-style memory)
                await TryStoreUserFactsAsync(clientId, text, stoppingToken);

                var embedding = await _embed.GetEmbedding(text);

                // RAG: same rules as POST /api/knowledge/ask (threshold + SearchLimit + MaxContextChars).
                // MinUserTextCharsForRag: 0 = never skip by length (old code skipped when text.Length < 20, e.g. "Spider-Man Enemie").
                var minCharsForRag = _config.GetValue("Knowledge:MinUserTextCharsForRag", 0);
                var skipKnowledge = string.IsNullOrWhiteSpace(text)
                    || (minCharsForRag > 0 && text.Trim().Length < minCharsForRag);

                var recentTurns = _config.GetValue("Memory:RecentTurnsInPrompt", 12);
                var similarTurns = _config.GetValue("Memory:SimilarTurnsInPrompt", 3);
                var recentFetch = Math.Max(recentTurns + 5, 25);

                var recentTask = _memory.GetRecentByClient(clientId, recentFetch);
                var similarTask = _memory.Search(embedding, clientId);
                Task<(string Context, int ChunkCount)>? knowledgeTask = skipKnowledge
                    ? null
                    : _knowledge.BuildKnowledgeContextForQueryAsync(text, embedding, stoppingToken);

                await Task.WhenAll(
                    recentTask,
                    similarTask,
                    knowledgeTask ?? Task.FromResult(("", 0)));

                var recent = (await recentTask).TakeLast(recentTurns).ToList();
                var similar = (await similarTask).Take(similarTurns).ToList();
                var previousBlock = string.Join("\n", recent.Select(m => $"U:{m.UserText}\nB:{m.BotText}"));
                var relevantBlock = similar.Count > 0
                    ? string.Join("\n\n", similar.Select(m => $"U:{m.UserText}\nB:{m.BotText}"))
                    : "";

                string knowledgeContext = "";
                if (knowledgeTask != null)
                {
                    try
                    {
                        var (ctx, chunkCount) = await knowledgeTask;
                        knowledgeContext = ctx;
                        if (chunkCount > 0)
                            _logger.LogInformation("[Pipeline] Knowledge RAG applied: {ChunkCount} chunk(s)", chunkCount);
                        else
                            _logger.LogInformation("[Pipeline] Knowledge RAG: no chunk above SimilarityThreshold (see Knowledge:SimilarityThreshold)");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[Pipeline] Knowledge RAG failed");
                    }
                }
                else if (minCharsForRag > 0)
                {
                    _logger.LogDebug("[Pipeline] Knowledge RAG skipped (transcript shorter than MinUserTextCharsForRag={Min})", minCharsForRag);
                }

                // User facts from Redis (birthday, name, etc.) - better than vector for concrete facts
                var profile = await _profileService.GetProfileAsync(clientId, stoppingToken);
                var userFactsBlock = profile.Count > 0
                    ? "User facts: " + string.Join(", ", profile.Select(p => $"{p.Key}={p.Value}"))
                    : "";

                // Role key from Redis (e.g. ironman); fetch full role from DB (name, style, maxLength)
                var roleKey = await _redis.GetRoleAsync(clientId) ?? "default";
                var roleData = await _roleService.GetRoleAsync(roleKey, stoppingToken);

                var context = new System.Text.StringBuilder();
                if (!string.IsNullOrEmpty(userFactsBlock)) context.AppendLine(userFactsBlock).AppendLine();
                if (!string.IsNullOrEmpty(knowledgeContext)) context.AppendLine("Knowledge:").AppendLine(knowledgeContext).AppendLine();
                context.AppendLine("Previous (earlier in this session — use when the user asks what they said before or refers to past messages):").AppendLine(string.IsNullOrEmpty(previousBlock) ? "(none)" : previousBlock).AppendLine();
                context.AppendLine("Relevant (similar past turns):").AppendLine(string.IsNullOrEmpty(relevantBlock) ? "(none)" : relevantBlock);

                var prompt = PromptBuilder.Build(roleData, text, context.ToString());
                _logger.LogInformation("[Pipeline] Prompt length: {Chars} chars (voice: trim Knowledge/Memory in appsettings if too slow)", prompt.Length);

                // LLM cache: same question = instant response
                reply = "";
                var llmCacheKey = _redis.InstanceName + "llm:" + clientId + ":" + HashText(text);
                if (_redis.IsEnabled && _redis.LlmCacheTtlMinutes > 0)
                {
                    var db = _redis.GetDatabase();
                    if (db != null)
                    {
                        var cached = await db.StringGetAsync(llmCacheKey);
                        if (cached.HasValue && !cached.IsNullOrEmpty)
                        {
                            reply = cached!;
                            _logger.LogInformation("[Pipeline] LLM: cache hit (instant)");
                        }
                    }
                }

                if (string.IsNullOrEmpty(reply))
                {
                    try
                    {
                        reply = await _ollama.GenerateAsync(prompt) ?? "";
                        _logger.LogInformation("[Pipeline] LLM: {Reply}", reply);
                    }
                    catch (TaskCanceledException ex)
                    {
                        _logger.LogWarning(ex,
                            "[Pipeline] Ollama request timed out or was canceled (large prompt / slow model). Increase Ollama:RequestTimeoutSeconds or reduce Knowledge/Memory context.");
                        reply = "Sorry, that took too long. Try a shorter question, or reduce knowledge chunks in appsettings.";
                    }
                    catch (OperationCanceledException ex)
                    {
                        _logger.LogWarning(ex, "[Pipeline] Ollama canceled.");
                        reply = "Sorry, the AI request was canceled. Please try again.";
                    }

                    if (_redis.IsEnabled && _redis.LlmCacheTtlMinutes > 0 && !string.IsNullOrEmpty(reply))
                    {
                        try
                        {
                            var db = _redis.GetDatabase();
                            if (db != null)
                                await db.StringSetAsync(llmCacheKey, reply, TimeSpan.FromMinutes(_redis.LlmCacheTtlMinutes));
                        }
                        catch { /* ignore */ }
                    }
                }

                // MongoDB (long-term vector) + Redis (fast recent)
                await _memory.Save(new ChatMemory
                {
                    Id = Guid.NewGuid().ToString(),
                    UserText = text,
                    BotText = reply,
                    Embedding = embedding,
                    Timestamp = DateTime.UtcNow,
                    ClientId = clientId
                }, clientId);
                _logger.LogInformation("[Pipeline] Stored: MongoDB (vector) + Redis (recent).");

                // Enqueue for ReminderWorker to store so next time AI can refer to this conversation
                try { await _reminderWriter.WriteAsync(new ReminderItem(clientId, text, reply), stoppingToken); } catch (Exception ex) { _logger.LogDebug(ex, "Reminder enqueue skipped"); }
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
        var chatPayload = JsonSerializer.Serialize(new { userText = text ?? "", botText = reply ?? "", ttsSampleRate });
        await _mqttPublisher.PublishAsync(clientId, chatPayload, toSend ?? Array.Empty<byte>(), stoppingToken);
    }

    private static string HashText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text.Trim()));
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    private async Task TryStoreUserFactsAsync(string clientId, string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var t = text.Trim();
        try
        {
            if (t.Contains("birthday", StringComparison.OrdinalIgnoreCase))
            {
                var m = System.Text.RegularExpressions.Regex.Match(t, @"(?:my\s+)?birthday\s+is\s+(.+?)(?:\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success && m.Groups.Count > 1)
                {
                    var value = m.Groups[1].Value.Trim();
                    if (value.Length > 0 && value.Length < 100)
                        await _profileService.SetAsync(clientId, "birthday", value, cancellationToken);
                }
            }
            if (t.Contains("call me", StringComparison.OrdinalIgnoreCase) || t.Contains("my name is", StringComparison.OrdinalIgnoreCase))
            {
                var m = System.Text.RegularExpressions.Regex.Match(t, @"(?:call me|my name is)\s+(.+?)(?:\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success && m.Groups.Count > 1)
                {
                    var value = m.Groups[1].Value.Trim();
                    if (value.Length > 0 && value.Length < 80)
                        await _profileService.SetAsync(clientId, "name", value, cancellationToken);
                }
            }
            if (t.StartsWith("remember", StringComparison.OrdinalIgnoreCase))
            {
                var value = t.Substring(8).Trim();
                if (value.Length > 0 && value.Length < 200)
                    await _profileService.SetAsync(clientId, "last_topic", value, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Profile fact extraction skipped");
        }
    }
}
