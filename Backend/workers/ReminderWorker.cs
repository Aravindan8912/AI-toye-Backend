using System.Threading.Channels;
using JarvisBackend.Services.Interfaces;

namespace JarvisBackend.Workers;

/// <summary>Consumes conversation turns from the reminder queue and stores them via IReminderService so the AI can refer to previous conversation.</summary>
public class ReminderWorker : BackgroundService
{
    private readonly ChannelReader<ReminderItem> _reader;
    private readonly IReminderService _reminderService;
    private readonly ILogger<ReminderWorker> _logger;

    public ReminderWorker(ChannelReader<ReminderItem> reader, IReminderService reminderService, ILogger<ReminderWorker> logger)
    {
        _reader = reader;
        _reminderService = reminderService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ReminderWorker: started. Storing conversation turns for AI context.");
        await foreach (var item in _reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await _reminderService.SaveAsync(item.ClientId, item.UserText, item.BotText, stoppingToken);
                _logger.LogDebug("ReminderWorker: stored ClientId={ClientId}", item.ClientId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ReminderWorker: failed to store reminder for ClientId={ClientId}", item.ClientId);
            }
        }
    }
}
