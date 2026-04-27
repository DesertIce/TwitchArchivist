namespace TwitchArchivist.Services.Twitch;

public interface ITwitchHelixClient
{
    Task<string?> ResolveUserIdAsync(string twitchLogin, CancellationToken cancellationToken);

    Task<ArchiveVodRecord?> GetLatestArchiveVodAsync(
        string broadcasterUserId,
        DateTimeOffset? createdAfterUtc,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<EventSubSubscriptionRecord>> GetEventSubscriptionsAsync(CancellationToken cancellationToken);

    Task<EventSubSubscriptionRecord> CreateStreamSubscriptionAsync(
        string subscriptionType,
        string broadcasterUserId,
        string sessionId,
        CancellationToken cancellationToken);
}
