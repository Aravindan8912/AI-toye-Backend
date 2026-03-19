namespace JarvisBackend.Configuration;

/// <summary>RabbitMQ connection and queue settings for MQTT → RabbitMQ → AudioWorker flow.</summary>
public class RabbitMqOptions
{
    public const string SectionName = "RabbitMQ";

    /// <summary>Host name or IP (default: localhost).</summary>
    public string HostName { get; set; } = "localhost";

    /// <summary>Port (default: 5672).</summary>
    public int Port { get; set; } = 5672;

    /// <summary>Virtual host (default: /).</summary>
    public string VirtualHost { get; set; } = "/";

    /// <summary>User name (default: guest).</summary>
    public string UserName { get; set; } = "guest";

    /// <summary>Password (default: guest).</summary>
    public string Password { get; set; } = "guest";

    /// <summary>Queue name for audio messages (default: audio_queue).</summary>
    public string QueueName { get; set; } = "audio_queue";

    /// <summary>When true, the /ws endpoint and AudioWorker are enabled (default: true).</summary>
    public bool Enabled { get; set; } = true;
}
