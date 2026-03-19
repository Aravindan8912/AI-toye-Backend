namespace JarvisBackend.Workers;

/// <summary>One conversation turn to be stored by ReminderWorker.</summary>
public record ReminderItem(string ClientId, string UserText, string BotText);
