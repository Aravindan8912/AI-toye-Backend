namespace JarvisBackend.Models;

public class AudioMessage
{
    public required string ClientId { get; set; }
    public required byte[] AudioData { get; set; }
}