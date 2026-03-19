namespace JarvisBackend.Services.Interfaces;

/// <summary>Publishes voice pipeline response (chat JSON + TTS audio) to MQTT for ESP32.</summary>
public interface IMqttResponsePublisher
{
    Task PublishAsync(string clientId, string chatJson, byte[] ttsAudioBytes, CancellationToken cancellationToken = default);
}
