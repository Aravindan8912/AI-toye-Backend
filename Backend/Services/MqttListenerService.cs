using System.Text;
using System.Threading.Channels;
using JarvisBackend.Configuration;
using JarvisBackend.Data;
using JarvisBackend.Models;
using JarvisBackend.Services;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace JarvisBackend.Services;

/// <summary>Subscribes to MQTT topic (e.g. jarvis/+/audio/in), extracts clientId from topic, forwards payload to RabbitMQ for AudioWorker.</summary>
public class MqttListenerService : BackgroundService
{
    /// <summary>Bounded queue: under burst load oldest items are dropped so the MQTT library thread never blocks on backpressure.</summary>
    private const int InboundQueueCapacity = 512;

    private readonly MqttOptions _options;
    private readonly RabbitMqService _rabbit;
    private readonly RedisService _redis;
    private readonly ILogger<MqttListenerService> _logger;
    private IMqttClient? _client;
    private ChannelWriter<MqttInboundItem>? _inboundWriter;

    public MqttListenerService(
        IOptions<MqttOptions> options,
        RabbitMqService rabbit,
        RedisService redis,
        ILogger<MqttListenerService> logger)
    {
        _options = options.Value;
        _rabbit = rabbit;
        _redis = redis;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("MQTT listener disabled.");
            return;
        }

        var inboundChannel = Channel.CreateBounded<MqttInboundItem>(new BoundedChannelOptions(InboundQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _inboundWriter = inboundChannel.Writer;
        var drainTask = DrainInboundAsync(inboundChannel.Reader, stoppingToken);

        try
        {
            var factory = new MqttFactory();
            _client = factory.CreateMqttClient();

            var builder = new MqttClientOptionsBuilder()
                .WithTcpServer(_options.BrokerHost, _options.BrokerPort)
                .WithClientId(_options.ClientId);
            if (!string.IsNullOrEmpty(_options.UserName))
                builder.WithCredentials(_options.UserName, _options.Password);

            _client.ApplicationMessageReceivedAsync += OnMessageReceived;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var result = await _client.ConnectAsync(builder.Build(), stoppingToken).ConfigureAwait(false);
                    if (result.ResultCode != MqttClientConnectResultCode.Success)
                    {
                        _logger.LogWarning("MQTT connect failed: {Reason}. Retry in 5s.", result.ResultCode);
                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    _logger.LogInformation("MQTT listener connected to {Host}:{Port}, subscribing to {Topic} and jarvis/+/mode", _options.BrokerHost, _options.BrokerPort, _options.TopicIn);

                    await _client.SubscribeAsync(
                        new MqttClientSubscribeOptionsBuilder()
                            .WithTopicFilter(f => f.WithTopic(_options.TopicIn).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                            .WithTopicFilter(f => f.WithTopic("jarvis/+/mode").WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                            .Build(),
                        stoppingToken).ConfigureAwait(false);

                    while (_client.IsConnected && !stoppingToken.IsCancellationRequested)
                        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);

                    if (stoppingToken.IsCancellationRequested)
                        break;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "MQTT listener error. Reconnecting in 5s.");
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                }
            }

            if (_client?.IsConnected == true)
                await _client.DisconnectAsync().ConfigureAwait(false);
        }
        finally
        {
            if (_client != null)
                _client.ApplicationMessageReceivedAsync -= OnMessageReceived;
            _inboundWriter?.TryComplete();
            try
            {
                await drainTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MQTT inbound drain task ended with error.");
            }
            _inboundWriter = null;
        }
    }

    /// <summary>MQTTnet invokes this on the library's receive path; keep it synchronous and non-blocking (no I/O, no blocking waits).</summary>
    private Task OnMessageReceived(MqttApplicationMessageReceivedEventArgs e)
    {
        var writer = _inboundWriter;
        if (writer is null)
            return Task.CompletedTask;

        try
        {
            var topic = e.ApplicationMessage.Topic;
            var segments = topic.Split('/');
            var clientId = segments.Length >= 2 ? segments[1] : Guid.NewGuid().ToString();
            var payload = e.ApplicationMessage.PayloadSegment;

            // Role selection: jarvis/{clientId}/mode → payload = "ironman" | "spiderman" | "captain"
            if (topic.Contains("/mode", StringComparison.Ordinal))
            {
                var role = (payload.Array != null && payload.Count > 0)
                    ? Encoding.UTF8.GetString(payload.Array, payload.Offset, payload.Count).Trim().ToLowerInvariant()
                    : "";
                if (string.IsNullOrEmpty(role))
                    return Task.CompletedTask;

                if (!writer.TryWrite(new MqttInboundItem(MqttInboundKind.Role, clientId, role, null, topic)))
                    _logger.LogWarning("MQTT inbound queue saturated; dropped mode update for {ClientId}", clientId);
                return Task.CompletedTask;
            }

            // Audio: jarvis/{clientId}/audio/in
            if (payload.Array == null || payload.Count == 0)
            {
                _logger.LogWarning("MQTT empty payload on {Topic}", topic);
                return Task.CompletedTask;
            }

            var audioData = payload.Array.AsSpan(payload.Offset, payload.Count).ToArray();
            if (!writer.TryWrite(new MqttInboundItem(MqttInboundKind.Audio, clientId, null, audioData, topic)))
                _logger.LogWarning("MQTT inbound queue saturated; dropped audio chunk for {ClientId}, {Bytes} bytes", clientId, audioData.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MQTT message enqueue failed");
        }

        return Task.CompletedTask;
    }

    private async Task DrainInboundAsync(ChannelReader<MqttInboundItem> reader, CancellationToken stoppingToken)
    {
        try
        {
            while (await reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var item))
                {
                    try
                    {
                        switch (item.Kind)
                        {
                            case MqttInboundKind.Role:
                                await _redis.SetRoleAsync(item.ClientId, item.Role ?? "").ConfigureAwait(false);
                                _logger.LogInformation("Role set: {ClientId} → {Role}", item.ClientId, item.Role);
                                break;
                            case MqttInboundKind.Audio:
                                if (item.AudioData is { Length: > 0 })
                                {
                                    _rabbit.Publish(new AudioMessage { ClientId = item.ClientId, AudioData = item.AudioData });
                                    _logger.LogInformation("MQTT received audio from {ClientId}, {Bytes} bytes → RabbitMQ", item.ClientId, item.AudioData.Length);
                                }
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "MQTT inbound processing failed for topic {Topic}", item.Topic);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
    }

    private enum MqttInboundKind { Role, Audio }

    private readonly struct MqttInboundItem
    {
        public MqttInboundItem(MqttInboundKind kind, string clientId, string? role, byte[]? audioData, string topic)
        {
            Kind = kind;
            ClientId = clientId;
            Role = role;
            AudioData = audioData;
            Topic = topic;
        }

        public MqttInboundKind Kind { get; }
        public string ClientId { get; }
        public string? Role { get; }
        public byte[]? AudioData { get; }
        public string Topic { get; }
    }
}
