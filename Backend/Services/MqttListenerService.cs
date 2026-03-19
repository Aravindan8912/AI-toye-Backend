using System.Text;
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
    private readonly MqttOptions _options;
    private readonly RabbitMqService _rabbit;
    private readonly RedisService _redis;
    private readonly ILogger<MqttListenerService> _logger;
    private IMqttClient? _client;

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
                var result = await _client.ConnectAsync(builder.Build(), stoppingToken);
                if (result.ResultCode != MqttClientConnectResultCode.Success)
                {
                    _logger.LogWarning("MQTT connect failed: {Reason}. Retry in 5s.", result.ResultCode);
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                _logger.LogInformation("MQTT listener connected to {Host}:{Port}, subscribing to {Topic} and jarvis/+/mode", _options.BrokerHost, _options.BrokerPort, _options.TopicIn);

                await _client.SubscribeAsync(
                    new MqttClientSubscribeOptionsBuilder()
                        .WithTopicFilter(f => f.WithTopic(_options.TopicIn).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                        .WithTopicFilter(f => f.WithTopic("jarvis/+/mode").WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                        .Build(),
                    stoppingToken);

                while (_client.IsConnected && !stoppingToken.IsCancellationRequested)
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);

                if (stoppingToken.IsCancellationRequested)
                    break;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MQTT listener error. Reconnecting in 5s.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        if (_client?.IsConnected == true)
            await _client.DisconnectAsync();
    }

    private Task OnMessageReceived(MqttApplicationMessageReceivedEventArgs e)
    {
        try
        {
            var topic = e.ApplicationMessage.Topic;
            var segments = topic.Split('/');
            var clientId = segments.Length >= 2 ? segments[1] : Guid.NewGuid().ToString();
            var payload = e.ApplicationMessage.PayloadSegment;

            // Role selection: jarvis/{clientId}/mode → payload = "ironman" | "spiderman" | "captain"
            if (topic.Contains("/mode"))
            {
                var role = (payload.Array != null && payload.Count > 0)
                    ? Encoding.UTF8.GetString(payload.Array!, payload.Offset, payload.Count).Trim().ToLowerInvariant()
                    : "";
                if (!string.IsNullOrEmpty(role))
                {
                    _redis.SetRoleAsync(clientId, role).GetAwaiter().GetResult();
                    _logger.LogInformation("Role set: {ClientId} → {Role}", clientId, role);
                }
                return Task.CompletedTask;
            }

            // Audio: jarvis/{clientId}/audio/in
            if (payload.Array == null || payload.Count == 0)
            {
                _logger.LogWarning("MQTT empty payload on {Topic}", topic);
                return Task.CompletedTask;
            }

            var audioData = payload.Array!.AsSpan(0, payload.Count).ToArray();
            _rabbit.Publish(new AudioMessage { ClientId = clientId, AudioData = audioData });
            _logger.LogInformation("MQTT received audio from {ClientId}, {Bytes} bytes → RabbitMQ", clientId, audioData.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MQTT message handling failed");
        }

        return Task.CompletedTask;
    }
}
