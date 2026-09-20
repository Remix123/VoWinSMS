using VoWin.Models;

namespace VoWin.Services;

public interface INotificationService
{
    Task<NotificationSettings> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(NotificationSettings settings, CancellationToken cancellationToken = default);
    Task<NotificationSendResult> SendTestAsync(string channel, NotificationSettings settings, CancellationToken cancellationToken = default);
}

public sealed record NotificationSendResult(bool Success, string Message);
