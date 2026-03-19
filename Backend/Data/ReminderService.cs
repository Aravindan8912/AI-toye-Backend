using System.Text.Json;
using JarvisBackend.Models;
using JarvisBackend.Services.Interfaces;
using StackExchange.Redis;

namespace JarvisBackend.Data;

/// <summary>Stores conversation turns in Redis so the AI can refer to previous conversation and understand the human.</summary>
public class ReminderService : IReminderService
{
    private readonly RedisService? _redis;
    private readonly ILogger<ReminderService> _logger;
    private const int DefaultReminderLimit = 10;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public ReminderService(RedisService redis, ILogger<ReminderService> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task SaveAsync(string clientId, string userText, string botText, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(clientId) || _redis?.IsEnabled != true)
            return;

        var db = _redis.GetDatabase();
        if (db == null)
            return;

        var key = _redis.InstanceName + "reminders:" + clientId;
        var reminder = new Reminder { ClientId = clientId, UserText = userText ?? "", BotText = botText ?? "", Timestamp = DateTime.UtcNow };
        var json = JsonSerializer.Serialize(new { reminder.ClientId, reminder.UserText, reminder.BotText, reminder.Timestamp }, JsonOptions);
        await db.ListRightPushAsync(key, json);
        await db.ListTrimAsync(key, -DefaultReminderLimit, -1);
        _logger.LogDebug("Reminder saved: ClientId={ClientId}", clientId);
    }

    public async Task<List<Reminder>> GetRecentByClientAsync(string clientId, int limit = 10, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(clientId) || _redis?.IsEnabled != true)
            return new List<Reminder>();

        var db = _redis.GetDatabase();
        if (db == null)
            return new List<Reminder>();

        var key = _redis.InstanceName + "reminders:" + clientId;
        var values = await db.ListRangeAsync(key, -limit, -1);
        var list = new List<Reminder>();
        foreach (var v in values)
        {
            if (v.IsNullOrEmpty) continue;
            try
            {
                var o = JsonSerializer.Deserialize<JsonElement>(v!);
                list.Add(new Reminder
                {
                    ClientId = o.TryGetProperty("clientId", out var c) ? c.GetString() ?? clientId : clientId,
                    UserText = o.TryGetProperty("userText", out var u) ? u.GetString() ?? "" : "",
                    BotText = o.TryGetProperty("botText", out var b) ? b.GetString() ?? "" : "",
                    Timestamp = o.TryGetProperty("timestamp", out var t) ? DateTime.Parse(t.GetString()!) : default
                });
            }
            catch { /* skip malformed */ }
        }
        return list;
    }
}
