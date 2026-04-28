using Microsoft.EntityFrameworkCore;
using TwitchArchivist.Persistence;
using TwitchArchivist.Persistence.Entities;

namespace TwitchArchivist.Services.Twitch;

public class EventSubSubscriptionSynchronizer(
    ITwitchHelixClient twitchHelixClient,
    IServiceScopeFactory scopeFactory) : IEventSubSubscriptionSynchronizer
{
    public async Task EnsureSubscriptionsAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        var channels = await dbContext.ChannelConfigurations.Where(x => x.IsEnabled).ToListAsync(cancellationToken);
        var subscriptions = await twitchHelixClient.GetEventSubscriptionsAsync(cancellationToken);

        foreach (var channel in channels)
        {
            if (string.IsNullOrWhiteSpace(channel.TwitchUserId))
            {
                channel.TwitchUserId = await twitchHelixClient.ResolveUserIdAsync(channel.TwitchLogin, cancellationToken);
                channel.UpdatedUtc = DateTimeOffset.UtcNow;
            }

            if (string.IsNullOrWhiteSpace(channel.TwitchUserId))
            {
                continue;
            }

            await EnsureSubscriptionAsync(dbContext, subscriptions, channel, "stream.online", sessionId, cancellationToken);
            await EnsureSubscriptionAsync(dbContext, subscriptions, channel, "stream.offline", sessionId, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureSubscriptionAsync(
        TwitchArchivistDbContext dbContext,
        IReadOnlyList<EventSubSubscriptionRecord> remoteSubscriptions,
        ChannelConfiguration channel,
        string subscriptionType,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var existing = remoteSubscriptions.FirstOrDefault(x =>
            string.Equals(x.Type, subscriptionType, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.BroadcasterUserId, channel.TwitchUserId, StringComparison.Ordinal));

        if (existing is null)
        {
            existing = await twitchHelixClient.CreateStreamSubscriptionAsync(
                subscriptionType,
                channel.TwitchUserId!,
                sessionId,
                cancellationToken);
        }

        var entity = await dbContext.EventSubscriptionStates
            .SingleOrDefaultAsync(
                x => x.ChannelConfigurationId == channel.Id && x.SubscriptionType == subscriptionType,
                cancellationToken);

        if (entity is null)
        {
            entity = new EventSubscriptionState
            {
                ChannelConfigurationId = channel.Id,
                SubscriptionType = subscriptionType,
                CreatedUtc = DateTimeOffset.UtcNow
            };
            dbContext.EventSubscriptionStates.Add(entity);
        }

        entity.TwitchSubscriptionId = existing.Id;
        entity.Status = existing.Status;
        entity.LastVerifiedUtc = DateTimeOffset.UtcNow;
        entity.UpdatedUtc = DateTimeOffset.UtcNow;
    }
}
