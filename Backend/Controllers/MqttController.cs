using System.Text.Json;
using JarvisBackend.Models;
using JarvisBackend.Services;
using JarvisBackend.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace JarvisBackend.Controllers;

[ApiController]
[Route("api/mqtt")]
public class MqttController : ControllerBase
{
    private const int QueueTestPcmBytes = 32000; // ~1s PCM @ 16k mono (matches backend expectations)

    private readonly IMqttResponsePublisher _mqttPublisher;
    private readonly RabbitMqService _rabbitMq;
    private readonly ILogger<MqttController> _logger;

    public MqttController(IMqttResponsePublisher mqttPublisher, RabbitMqService rabbitMq, ILogger<MqttController> logger)
    {
        _mqttPublisher = mqttPublisher;
        _rabbitMq = rabbitMq;
        _logger = logger;
    }

    /// <summary>
    /// Sends a test hello message to ESP32 over MQTT.
    /// </summary>
    /// <param name="clientId">ESP32 device id used in topic template jarvis/{clientId}/audio/out.</param>
    [HttpPost("hello")]
    public async Task<IActionResult> SendHello([FromQuery] string? clientId)
    {
        var targetClientId = string.IsNullOrWhiteSpace(clientId) ? "esp32-board" : clientId.Trim();

        var payload = JsonSerializer.Serialize(new
        {
            type = "connection_test",
            message = "hello from backend",
            connected = true,
            sentAtUtc = DateTime.UtcNow
        });

        await _mqttPublisher.PublishAsync(targetClientId, payload, Array.Empty<byte>(), HttpContext.RequestAborted);
        _logger.LogInformation("MQTT hello test sent to client {ClientId}", targetClientId);

        return Ok(new
        {
            status = "sent",
            clientId = targetClientId,
            note = "If ESP32 is subscribed, it should receive hello on jarvis/{clientId}/audio/out"
        });
    }

    /// <summary>
    /// Sends a queue test message through RabbitMQ so AudioWorker processes it and publishes back to MQTT.
    /// </summary>
    /// <param name="clientId">ESP32 device id used by queue pipeline and MQTT response topics.</param>
    [HttpPost("hello/queue")]
    public IActionResult SendHelloViaQueue([FromQuery] string? clientId) => EnqueueQueueHelloTest(clientId);

    /// <summary>Same as <see cref="SendHelloViaQueue"/> — hyphen path avoids proxy/path quirks.</summary>
    [HttpPost("hello-queue")]
    public IActionResult SendHelloViaQueueHyphen([FromQuery] string? clientId) => EnqueueQueueHelloTest(clientId);

    private IActionResult EnqueueQueueHelloTest(string? clientId)
    {
        var targetClientId = string.IsNullOrWhiteSpace(clientId) ? "esp32-board" : clientId.Trim();

        // Queue contract carries audio bytes; this synthetic PCM validates RabbitMQ->AudioWorker->MQTT response flow.
        var message = new AudioMessage
        {
            ClientId = targetClientId,
            AudioData = new byte[QueueTestPcmBytes]
        };

        _rabbitMq.Publish(message);
        _logger.LogInformation("Queue hello test enqueued for client {ClientId}", targetClientId);

        return Ok(new
        {
            status = "queued",
            clientId = targetClientId,
            queue = _rabbitMq.QueueName,
            note = "AudioWorker will consume this queue message and publish response on jarvis/{clientId}/audio/out and /wav"
        });
    }
}
