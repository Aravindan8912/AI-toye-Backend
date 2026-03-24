namespace JarvisBackend.Models;

/// <summary>One conversation turn stored so the AI can refer to previous conversation and understand the human.</summary>
public class Reminder
{
    public string ClientId { get; set; } = "";
    public string UserText { get; set; } = "";
    public string BotText { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
