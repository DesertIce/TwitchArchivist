using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TwitchArchivist.Models;
using TwitchArchivist.Persistence;
using TwitchArchivist.Persistence.Entities;

namespace TwitchArchivist.Services.Twitch;

public class EventSubSubscriptionSynchronizer(
    ITwitchHelixClient twitchHelixClient,
    IServiceScopeFactory scopeFactory,
    ILogger<EventSubSubscriptionSynchronizer> logger,
    IOptions<TwitchOptions> twitchOptions) : IEventSubSubscriptionSynchronizer
{
    public async Task EnsureSubscriptionsAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (IsConduitTransportMode())
        {
            await EnsureConduitSubscriptionsAsync(cancellationToken);
            return;
        }

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
            try
            {
                var updatedUtc = DateTimeOffset.UtcNow;
                await ResolveHelixBroadcasterUserIdAsync(channel, updatedUtc, cancellationToken);

                if (string.IsNullOrWhiteSpace(channel.TwitchUserId) ||
                    !TwitchHelixUserIds.IsHelixUserId(channel.TwitchUserId))
                {
                    continue;
                }

                if (!await EnsureSubscriptionAsync(dbContext, subscriptions, channel, "stream.online", sessionId, cancellationToken))
                {
                    continue;
                }

                if (!await EnsureSubscriptionAsync(dbContext, subscriptions, channel, "stream.offline", sessionId, cancellationToken))
                {
                    continue;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Failed to reconcile EventSub websocket subscriptions for channel {ChannelLogin} and broadcaster user id {BroadcasterUserId}",
                    channel.TwitchLogin,
                    channel.TwitchUserId);
                continue;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> EnsureSubscriptionAsync(
        TwitchArchivistDbContext dbContext,
        IReadOnlyList<EventSubSubscriptionRecord> remoteSubscriptions,
        ChannelConfiguration channel,
        string subscriptionType,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.EventSubscriptionStates
            .SingleOrDefaultAsync(
                x => x.ChannelConfigurationId == channel.Id && x.SubscriptionType == subscriptionType,
                cancellationToken);

        var existing = entity is not null && !string.IsNullOrWhiteSpace(entity.TwitchSubscriptionId)
            ? remoteSubscriptions.FirstOrDefault(x =>
                string.Equals(x.Id, entity.TwitchSubscriptionId, StringComparison.Ordinal) &&
                string.Equals(x.TransportSessionId, sessionId, StringComparison.Ordinal))
            : null;

        existing ??= remoteSubscriptions.FirstOrDefault(x =>
            string.Equals(x.Type, subscriptionType, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.BroadcasterUserId, channel.TwitchUserId, StringComparison.Ordinal) &&
            string.Equals(x.TransportSessionId, sessionId, StringComparison.Ordinal));

        if (existing is null)
        {
            try
            {
                existing = await twitchHelixClient.CreateStreamSubscriptionAsync(
                    subscriptionType,
                    channel.TwitchUserId!,
                    sessionId,
                    cancellationToken);
            }
            catch (HttpRequestException ex) when (IsHelixBroadcasterNotFoundForEventSub(ex))
            {
                logger.LogWarning(
                    ex,
                    "Twitch rejected EventSub websocket subscription for channel {ChannelLogin} because the broadcaster user id is unknown to Helix; clearing stored id for a later resolve.",
                    channel.TwitchLogin);
                channel.TwitchUserId = null;
                channel.UpdatedUtc = DateTimeOffset.UtcNow;
                return false;
            }
        }

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
        entity.TransportSessionId = existing.TransportSessionId ?? sessionId;
        entity.Status = existing.Status;
        entity.LastVerifiedUtc = DateTimeOffset.UtcNow;
        entity.UpdatedUtc = DateTimeOffset.UtcNow;
        return true;
    }

    private async Task EnsureConduitSubscriptionsAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        var now = DateTimeOffset.UtcNow;
        var conduit = await ResolveCurrentConduitAsync(dbContext, cancellationToken);
        if (conduit is null)
        {
            return;
        }

        var channels = await dbContext.ChannelConfigurations.ToListAsync(cancellationToken);
        var remoteSubscriptions = await twitchHelixClient.GetEventSubscriptionsAsync(cancellationToken);

        foreach (var channel in channels.Where(x => x.IsEnabled))
        {
            try
            {
                await ResolveHelixBroadcasterUserIdAsync(channel, now, cancellationToken);

                if (string.IsNullOrWhiteSpace(channel.TwitchUserId) ||
                    !TwitchHelixUserIds.IsHelixUserId(channel.TwitchUserId))
                {
                    continue;
                }

                if (!await EnsureConduitSubscriptionAsync(dbContext, remoteSubscriptions, conduit, channel, "stream.online", cancellationToken))
                {
                    continue;
                }

                if (!await EnsureConduitSubscriptionAsync(dbContext, remoteSubscriptions, conduit, channel, "stream.offline", cancellationToken))
                {
                    continue;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Failed to reconcile EventSub conduit subscriptions for channel {ChannelLogin} and broadcaster user id {BroadcasterUserId}",
                    channel.TwitchLogin,
                    channel.TwitchUserId);
                continue;
            }
        }

        var disabledChannelIds = channels
            .Where(x => !x.IsEnabled)
            .Select(x => x.Id)
            .ToArray();

        if (disabledChannelIds.Length > 0)
        {
            var disabledBindings = await dbContext.EventSubSubscriptionBindings
                .Where(x => disabledChannelIds.Contains(x.ChannelConfigurationId))
                .ToListAsync(cancellationToken);

            foreach (var binding in disabledBindings)
            {
                binding.Status = "disabled-local";
                binding.UpdatedUtc = now;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> EnsureConduitSubscriptionAsync(
        TwitchArchivistDbContext dbContext,
        IReadOnlyList<EventSubSubscriptionRecord> remoteSubscriptions,
        EventSubConduit conduit,
        ChannelConfiguration channel,
        string subscriptionType,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.EventSubSubscriptionBindings
            .SingleOrDefaultAsync(
                x => x.ChannelConfigurationId == channel.Id && x.SubscriptionType == subscriptionType,
                cancellationToken);

        var currentBindingMatchesConduit = entity is null || entity.EventSubConduitId == conduit.Id;
        var existing = entity is not null && currentBindingMatchesConduit && !string.IsNullOrWhiteSpace(entity.TwitchSubscriptionId)
            ? remoteSubscriptions.FirstOrDefault(x => string.Equals(x.Id, entity.TwitchSubscriptionId, StringComparison.Ordinal))
            : null;

        existing ??= remoteSubscriptions.FirstOrDefault(x =>
            string.Equals(x.Type, subscriptionType, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.BroadcasterUserId, channel.TwitchUserId, StringComparison.Ordinal) &&
            string.IsNullOrWhiteSpace(x.TransportSessionId) &&
            string.Equals(x.TransportConduitId, conduit.TwitchConduitId, StringComparison.Ordinal));

        if (existing is null)
        {
            try
            {
                existing = await twitchHelixClient.CreateConduitSubscriptionAsync(
                    subscriptionType,
                    channel.TwitchUserId!,
                    conduit.TwitchConduitId,
                    cancellationToken);
            }
            catch (HttpRequestException ex) when (IsHelixBroadcasterNotFoundForEventSub(ex))
            {
                logger.LogWarning(
                    ex,
                    "Twitch rejected EventSub conduit subscription for channel {ChannelLogin} because the broadcaster user id is unknown to Helix; clearing stored id for a later resolve.",
                    channel.TwitchLogin);
                channel.TwitchUserId = null;
                channel.UpdatedUtc = DateTimeOffset.UtcNow;
                return false;
            }
        }

        if (entity is null)
        {
            entity = new EventSubSubscriptionBinding
            {
                ChannelConfigurationId = channel.Id,
                SubscriptionType = subscriptionType,
                CreatedUtc = DateTimeOffset.UtcNow
            };
            dbContext.EventSubSubscriptionBindings.Add(entity);
        }

        entity.EventSubConduitId = conduit.Id;
        entity.TwitchSubscriptionId = existing.Id;
        entity.Status = existing.Status;
        entity.LastVerifiedUtc = DateTimeOffset.UtcNow;
        entity.UpdatedUtc = DateTimeOffset.UtcNow;
        return true;
    }

    private async Task ResolveHelixBroadcasterUserIdAsync(
        ChannelConfiguration channel,
        DateTimeOffset updatedUtc,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(channel.TwitchUserId) &&
            TwitchHelixUserIds.IsHelixUserId(channel.TwitchUserId))
        {
            return;
        }

        channel.TwitchUserId = await twitchHelixClient.ResolveUserIdAsync(channel.TwitchLogin, cancellationToken);
        channel.UpdatedUtc = updatedUtc;
    }

    private static bool IsHelixBroadcasterNotFoundForEventSub(HttpRequestException ex)
        => ex.StatusCode == HttpStatusCode.BadRequest &&
           ex.Message.Contains("user that does not exist", StringComparison.OrdinalIgnoreCase);

    private async Task<EventSubConduit?> ResolveCurrentConduitAsync(TwitchArchivistDbContext dbContext, CancellationToken cancellationToken)
    {
        var configuredConduitId = twitchOptions.Value.EventSubConduitId;
        if (!string.IsNullOrWhiteSpace(configuredConduitId))
        {
            return await dbContext.EventSubConduits
                .SingleOrDefaultAsync(x => x.TwitchConduitId == configuredConduitId, cancellationToken);
        }

        var conduits = await dbContext.EventSubConduits.ToListAsync(cancellationToken);
        return conduits
            .OrderByDescending(x => x.UpdatedUtc)
            .ThenByDescending(x => x.Id)
            .FirstOrDefault();
    }

    private bool IsConduitTransportMode()
        => string.Equals(
            twitchOptions.Value.EventSubTransportMode,
            "conduit-websocket",
            StringComparison.OrdinalIgnoreCase);
}
