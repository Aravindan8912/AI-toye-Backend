using System.Text;
using JarvisBackend.Configuration;
using JarvisBackend.Services.Interfaces;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;

namespace JarvisBackend.Services;

/// <summary>Publishes chat JSON and TTS WAV to MQTT topics for ESP32 (jarvis/{clientId}/audio/out and .../wav).</summary>
public class MqttResponsePublisher : IMqttResponsePublisher
{
    /// <summary>
    /// ESP32 PubSubClient uses a uint16_t buffer; build flag MQTT_MAX_PACKET_SIZE=65535 is typical.
    /// One MQTT PUBLISH must fit topic + payload + headers — keep PCM under this cap or the device never receives audio.
    /// </summary>
    private const int MaxTtsPcmPayloadBytes = 62_000;

    private readonly MqttOptions _options;
    private readonly ILogger<MqttResponsePublisher> _logger;
    private IMqttClient? _client;
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    public MqttResponsePublisher(IOptions<MqttOptions> options, ILogger<MqttResponsePublisher> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    private async Task<IMqttClient> GetClientAsync(CancellationToken cancellationToken)
    {
        if (_client?.IsConnected == true)
            return _client;
        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            if (_client?.IsConnected == true)
                return _client!;
            var factory = new MqttFactory();
            _client = factory.CreateMqttClient();
            var builder = new MqttClientOptionsBuilder()
                .WithTcpServer(_options.BrokerHost, _options.BrokerPort)
                .WithClientId(_options.ClientId + "-pub");
            if (!string.IsNullOrEmpty(_options.UserName))
                builder.WithCredentials(_options.UserName, _options.Password);
            var result = await _client.ConnectAsync(builder.Build(), cancellationToken);
            if (result.ResultCode != MqttClientConnectResultCode.Success)
            {
                _logger.LogWarning("MQTT publish client connect failed: {Reason}", result.ResultCode);
                throw new InvalidOperationException($"MQTT connect failed: {result.ResultCode}");
            }
            _logger.LogInformation("MQTT publish client connected to {Host}:{Port}", _options.BrokerHost, _options.BrokerPort);
            return _client;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task PublishAsync(string clientId, string chatJson, byte[] ttsAudioBytes, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || string.IsNullOrEmpty(clientId))
            return;
        try
        {
            var client = await GetClientAsync(cancellationToken);
            var topicOut = string.Format(_options.TopicOutTemplate, clientId);
            var topicWav = topicOut.TrimEnd('/') + "/wav";

            await client.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic(topicOut)
                .WithPayload(Encoding.UTF8.GetBytes(chatJson))
                .WithRetainFlag(false)
                .Build(), cancellationToken);

            if (ttsAudioBytes != null && ttsAudioBytes.Length > 0)
            {
                var pcm = ttsAudioBytes;
                if (pcm.Length > MaxTtsPcmPayloadBytes)
                {
                    _logger.LogWarning(
                        "TTS PCM {Bytes} bytes exceeds MQTT single-message limit for ESP32 PubSubClient ({MaxBytes} bytes). Keeping first {MaxBytes} bytes (start of phrase).",
                        pcm.Length, MaxTtsPcmPayloadBytes);
                    pcm = pcm.AsSpan(0, MaxTtsPcmPayloadBytes).ToArray();
                }

                await client.PublishAsync(new MqttApplicationMessageBuilder()
                    .WithTopic(topicWav)
                    .WithPayload(pcm)
                    .WithRetainFlag(false)
                    .Build(), cancellationToken);
            }

            _logger.LogInformation("MQTT published to {TopicOut} and {TopicWav} for ClientId={ClientId}", topicOut, topicWav, clientId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MQTT Publish failed for ClientId={ClientId}", clientId);
        }
    }
}
