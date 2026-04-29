using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Polly;
using TwitchArchivist.Models;
using TwitchArchivist.Persistence;

namespace TwitchArchivist.Services.Twitch;

public class EventSubConduitCleanupService(
    ITwitchHelixClient twitchHelixClient,
    IEventSubConduitCoordinator conduitCoordinator,
    IServiceScopeFactory scopeFactory,
    RuntimeStatusStore runtimeStatusStore,
    IOptions<TwitchOptions> twitchOptions,
    ILogger<EventSubConduitCleanupService> logger,
    TimeProvider timeProvider)
{
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!string.Equals(twitchOptions.Value.EventSubTransportMode, "conduit-websocket", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await ExecuteWithRateLimitBackoffAsync(
            async token =>
            {
                await conduitCoordinator.ReconcileAsync(token);
                await CleanupLegacySubscriptionsAsync(token);
            },
            cancellationToken);
    }

    private async Task CleanupLegacySubscriptionsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        var trackedBroadcasterIds = await dbContext.ChannelConfigurations
            .Select(x => x.TwitchUserId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToListAsync(cancellationToken);

        if (trackedBroadcasterIds.Count == 0)
        {
            return;
        }

        var subscriptions = await twitchHelixClient.GetEventSubscriptionsAsync(cancellationToken);
        var legacySubscriptions = subscriptions
            .Where(x =>
                !string.IsNullOrWhiteSpace(x.TransportSessionId) &&
                trackedBroadcasterIds.Contains(x.BroadcasterUserId) &&
                (string.Equals(x.Type, "stream.online", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(x.Type, "stream.offline", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        foreach (var subscription in legacySubscriptions)
        {
            await twitchHelixClient.DeleteEventSubscriptionAsync(subscription.Id, cancellationToken);
        }
    }

    private async Task ExecuteWithRateLimitBackoffAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        var fallbackDelay = TimeSpan.FromSeconds(Math.Max(1, twitchOptions.Value.EventSubRetryBaseDelaySeconds));

        var policy = Policy
            .Handle<TwitchHelixRateLimitException>()
            .WaitAndRetryAsync(
                retryCount: 2,
                sleepDurationProvider: (retryAttempt, exception, _) =>
                {
                    var rateLimitException = (TwitchHelixRateLimitException)exception;
                    var delay = rateLimitException.RetryAfter ?? fallbackDelay;
                    runtimeStatusStore.UpdateEventSubConduitStatus(
                        runtimeStatusStore.EventSubTransportMode,
                        runtimeStatusStore.EventSubConduitId,
                        runtimeStatusStore.EventSubConfiguredShardCount,
                        runtimeStatusStore.EventSubActiveShardCount,
                        runtimeStatusStore.EventSubDisabledShardCount,
                        runtimeStatusStore.EventSubLastShardAssignmentError,
                        runtimeStatusStore.EventSubLastSubscriptionReconcileError,
                        timeProvider.GetUtcNow());
                    return delay;
                },
                onRetryAsync: (exception, delay, retryAttempt, _) =>
                {
                    logger.LogWarning(exception, "Rate limited during EventSub conduit cleanup. Retry {RetryAttempt} in {RetryDelay}", retryAttempt, delay);
                    return Task.CompletedTask;
                });

        await policy.ExecuteAsync(operation, cancellationToken);
    }
}
