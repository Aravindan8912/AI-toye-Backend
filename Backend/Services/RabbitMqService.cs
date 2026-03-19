using System.Text;
using System.Text.Json;
using JarvisBackend.Configuration;
using JarvisBackend.Models;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace JarvisBackend.Services;

/// <summary>Publishes audio messages to RabbitMQ (from MQTT listener or other publishers) for the AudioWorker pipeline.</summary>
public class RabbitMqService
{
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqService>? _logger;
    private IConnection? _connection;
    private IModel? _channel;
    private readonly object _lock = new();

    public RabbitMqService(IOptions<RabbitMqOptions> options, ILogger<RabbitMqService>? logger = null)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Queue name used for publish and consume (from configuration).</summary>
    public string QueueName => _options.QueueName;

    private ConnectionFactory CreateFactory()
    {
        return new ConnectionFactory
        {
            HostName = _options.HostName,
            Port = _options.Port,
            VirtualHost = _options.VirtualHost,
            UserName = _options.UserName,
            Password = _options.Password,
            DispatchConsumersAsync = true
        };
    }

    private void EnsureConnected()
    {
        if (!_options.Enabled)
            throw new InvalidOperationException("RabbitMQ is disabled. Set RabbitMQ:Enabled to true in appsettings.");
        if (_channel != null) return;
        lock (_lock)
        {
            if (_channel != null) return;
            var factory = CreateFactory();
            _connection = factory.CreateConnection();
            _channel = _connection.CreateModel();
            _channel.QueueDeclare(
                queue: _options.QueueName,
                durable: false,
                exclusive: false,
                autoDelete: false
            );
            _logger?.LogInformation(
                "RabbitMQ connected to {HostName}:{Port}, queue: {QueueName}",
                _options.HostName, _options.Port, _options.QueueName);
        }
    }

    /// <summary>Publish an audio message to the configured queue (e.g. MqttListenerService calls this).</summary>
    public void Publish(AudioMessage message)
    {
        if (message == null)
        {
            _logger?.LogWarning("RabbitMQ Publish called with null message.");
            return;
        }
        var audioBytes = message.AudioData?.Length ?? 0;
        EnsureConnected();
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        _channel!.BasicPublish(exchange: "", routingKey: _options.QueueName, body: body);
        _logger?.LogInformation("RabbitMQ: message published to {QueueName}, bodySize={BodySize}, audioBytes={AudioBytes}", _options.QueueName, body.Length, audioBytes);
    }

    /// <summary>Returns the channel for consuming, or null if RabbitMQ is not available or disabled.</summary>
    public IModel? TryGetChannel()
    {
        if (!_options.Enabled) return null;
        try
        {
            EnsureConnected();
            return _channel;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "RabbitMQ connection failed (HostName={HostName}, Port={Port})", _options.HostName, _options.Port);
            return null;
        }
    }
}
