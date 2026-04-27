namespace TwitchArchivist.Services.Twitch;

public interface ITwitchHelixClient
{
    Task<string?> ResolveUserIdAsync(string twitchLogin, CancellationToken cancellationToken);

    Task<string?> GetLatestArchiveVodIdAsync(string broadcasterUserId, CancellationToken cancellationToken);

    Task<IReadOnlyList<EventSubSubscriptionRecord>> GetEventSubscriptionsAsync(CancellationToken cancellationToken);

    Task<EventSubSubscriptionRecord> CreateStreamSubscriptionAsync(
        string subscriptionType,
        string broadcasterUserId,
        string sessionId,
        CancellationToken cancellationToken);
}
