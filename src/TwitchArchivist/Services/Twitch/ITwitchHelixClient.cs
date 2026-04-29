namespace TwitchArchivist.Services.Twitch;

public interface ITwitchHelixClient
{
    Task<string?> ResolveUserIdAsync(string twitchLogin, CancellationToken cancellationToken);

    Task<IReadOnlyList<TwitchChannelSearchResult>> SearchChannelsAsync(string query, CancellationToken cancellationToken);

    Task<IReadOnlyList<TwitchLiveStreamState>> GetLiveStreamsByLoginsAsync(IReadOnlyList<string> twitchLogins, CancellationToken cancellationToken);

    Task<ArchiveVodRecord?> GetLatestArchiveVodAsync(
        string broadcasterUserId,
        DateTimeOffset? createdAfterUtc,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<EventSubSubscriptionRecord>> GetEventSubscriptionsAsync(CancellationToken cancellationToken);

    Task DeleteEventSubscriptionAsync(string subscriptionId, CancellationToken cancellationToken)
        => Task.FromException(new NotSupportedException());

    Task<IReadOnlyList<EventSubConduitRecord>> GetEventSubConduitsAsync(CancellationToken cancellationToken)
        => Task.FromException<IReadOnlyList<EventSubConduitRecord>>(new NotSupportedException());

    Task<EventSubConduitRecord> CreateEventSubConduitAsync(int shardCount, CancellationToken cancellationToken)
        => Task.FromException<EventSubConduitRecord>(new NotSupportedException());

    Task<EventSubConduitRecord> UpdateEventSubConduitAsync(string conduitId, int shardCount, CancellationToken cancellationToken)
        => Task.FromException<EventSubConduitRecord>(new NotSupportedException());

    Task<IReadOnlyList<EventSubConduitShardRecord>> UpdateEventSubConduitShardsAsync(
        string conduitId,
        IReadOnlyList<EventSubConduitShardRecord> shards,
        CancellationToken cancellationToken)
        => Task.FromException<IReadOnlyList<EventSubConduitShardRecord>>(new NotSupportedException());

    Task<EventSubSubscriptionRecord> CreateStreamSubscriptionAsync(
        string subscriptionType,
        string broadcasterUserId,
        string sessionId,
        CancellationToken cancellationToken);

    Task<EventSubSubscriptionRecord> CreateConduitSubscriptionAsync(
        string subscriptionType,
        string broadcasterUserId,
        string conduitId,
        CancellationToken cancellationToken)
        => Task.FromException<EventSubSubscriptionRecord>(new NotSupportedException());
}
