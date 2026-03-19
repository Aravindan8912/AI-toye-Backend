namespace JarvisBackend.Configuration;

/// <summary>MQTT broker and topic settings for ESP32 ↔ .NET flow.</summary>
public class MqttOptions
{
    public const string SectionName = "MQTT";

    public string BrokerHost { get; set; } = "localhost";
    public int BrokerPort { get; set; } = 1883;
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public string ClientId { get; set; } = "jarvis-backend";

    /// <summary>Topic to subscribe to for incoming audio (e.g. jarvis/+/audio/in → clientId from segment).</summary>
    public string TopicIn { get; set; } = "jarvis/+/audio/in";

    /// <summary>Topic template for responses to device: jarvis/{clientId}/audio/out.</summary>
    public string TopicOutTemplate { get; set; } = "jarvis/{0}/audio/out";

    public bool Enabled { get; set; } = true;
}
