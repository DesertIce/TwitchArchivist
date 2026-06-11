namespace TwitchArchivist.Services.Twitch;

public interface IArchiveJobTriggerService
{
    Task CreateArchiveJobFromOfflineAsync(
        string channelLogin,
        string broadcasterUserId,
        string triggerSource,
        DateTimeOffset offlineDetectedUtc,
        CancellationToken cancellationToken);
}
