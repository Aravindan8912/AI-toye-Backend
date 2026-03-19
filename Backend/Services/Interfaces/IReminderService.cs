using JarvisBackend.Models;

namespace JarvisBackend.Services.Interfaces;

/// <summary>Stores and retrieves conversation turns so the AI can refer to previous conversation.</summary>
public interface IReminderService
{
    Task SaveAsync(string clientId, string userText, string botText, CancellationToken cancellationToken = default);
    Task<List<Reminder>> GetRecentByClientAsync(string clientId, int limit = 10, CancellationToken cancellationToken = default);
}
