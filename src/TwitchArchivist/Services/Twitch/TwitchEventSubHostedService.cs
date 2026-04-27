using Microsoft.EntityFrameworkCore;
using TwitchArchivist.Persistence;
using TwitchArchivist.Persistence.Entities;
using TwitchLib.EventSub.Core.EventArgs.Stream;
using TwitchLib.EventSub.Websockets;
using TwitchLib.EventSub.Websockets.Core.EventArgs;

namespace TwitchArchivist.Services.Twitch;

public class TwitchEventSubHostedService(
    EventSubWebsocketClient eventSubWebsocketClient,
    ITwitchAccessTokenProvider accessTokenProvider,
    ITwitchHelixClient twitchHelixClient,
    IServiceScopeFactory scopeFactory,
    IArchiveJobQueue archiveJobQueue,
    RuntimeStatusStore runtimeStatusStore,
    ILogger<TwitchEventSubHostedService> logger) : IHostedService
{
    private static readonly Uri EventSubEndpoint = new("wss://eventsub.wss.twitch.tv/ws");

    public Task StartAsync(CancellationToken cancellationToken)
    {
        eventSubWebsocketClient.WebsocketConnected += OnWebsocketConnectedAsync;
        eventSubWebsocketClient.WebsocketDisconnected += OnWebsocketDisconnectedAsync;
        eventSubWebsocketClient.WebsocketReconnected += OnWebsocketReconnectedAsync;
        eventSubWebsocketClient.ErrorOccurred += OnErrorOccurredAsync;
        eventSubWebsocketClient.StreamOnline += OnStreamOnlineAsync;
        eventSubWebsocketClient.StreamOffline += OnStreamOfflineAsync;

        _ = ConnectIfConfiguredAsync(cancellationToken);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await eventSubWebsocketClient.DisconnectAsync();
    }

    private async Task ConnectIfConfiguredAsync(CancellationToken cancellationToken)
    {
        var token = await accessTokenProvider.GetAccessTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            runtimeStatusStore.UpdateEventSubConnectionState("not-configured");
            logger.LogInformation("Skipping EventSub websocket connection because Twitch credentials are not configured");
            return;
        }

        runtimeStatusStore.UpdateEventSubConnectionState("connecting");
        await eventSubWebsocketClient.ConnectAsync(EventSubEndpoint);
    }

    private async Task OnWebsocketConnectedAsync(object? sender, WebsocketConnectedArgs args)
    {
        runtimeStatusStore.UpdateEventSubConnectionState(args.IsRequestedReconnect ? "reconnected" : "connected");
        logger.LogInformation("EventSub websocket connected with session {SessionId}", eventSubWebsocketClient.SessionId);

        if (args.IsRequestedReconnect)
        {
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        var channels = await dbContext.ChannelConfigurations.Where(x => x.IsEnabled).ToListAsync();
        var subscriptions = await twitchHelixClient.GetEventSubscriptionsAsync(CancellationToken.None);

        foreach (var channel in channels)
        {
            if (string.IsNullOrWhiteSpace(channel.TwitchUserId))
            {
                channel.TwitchUserId = await twitchHelixClient.ResolveUserIdAsync(channel.TwitchLogin, CancellationToken.None);
                channel.UpdatedUtc = DateTimeOffset.UtcNow;
            }

            if (string.IsNullOrWhiteSpace(channel.TwitchUserId))
            {
                continue;
            }

            await EnsureSubscriptionAsync(dbContext, subscriptions, channel, "stream.online");
            await EnsureSubscriptionAsync(dbContext, subscriptions, channel, "stream.offline");
        }

        await dbContext.SaveChangesAsync();
    }

    private async Task EnsureSubscriptionAsync(
        TwitchArchivistDbContext dbContext,
        IReadOnlyList<EventSubSubscriptionRecord> remoteSubscriptions,
        ChannelConfiguration channel,
        string subscriptionType)
    {
        var existing = remoteSubscriptions.FirstOrDefault(x =>
            string.Equals(x.Type, subscriptionType, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.BroadcasterUserId, channel.TwitchUserId, StringComparison.Ordinal));

        if (existing is null)
        {
            existing = await twitchHelixClient.CreateStreamSubscriptionAsync(
                subscriptionType,
                channel.TwitchUserId!,
                eventSubWebsocketClient.SessionId,
                CancellationToken.None);
        }

        var entity = await dbContext.EventSubscriptionStates
            .SingleOrDefaultAsync(x => x.ChannelConfigurationId == channel.Id && x.SubscriptionType == subscriptionType);

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

    private async Task OnStreamOnlineAsync(object? sender, StreamOnlineArgs args)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        var login = args.Payload.Event.BroadcasterUserLogin.ToLowerInvariant();
        var channel = await dbContext.ChannelConfigurations.SingleOrDefaultAsync(x => x.TwitchLogin == login);
        if (channel is null)
        {
            return;
        }

        channel.TwitchUserId = args.Payload.Event.BroadcasterUserId;
        channel.UpdatedUtc = DateTimeOffset.UtcNow;

        var state = await dbContext.StreamSessionStates.SingleOrDefaultAsync(x => x.ChannelConfigurationId == channel.Id);
        if (state is null)
        {
            state = new StreamSessionState
            {
                ChannelConfigurationId = channel.Id,
                CreatedUtc = DateTimeOffset.UtcNow
            };
            dbContext.StreamSessionStates.Add(state);
        }

        state.LastKnownStreamId = args.Payload.Event.Id;
        state.LastOnlineUtc = args.Payload.Event.StartedAt;
        state.UpdatedUtc = DateTimeOffset.UtcNow;

        await dbContext.SaveChangesAsync();
    }

    private async Task OnStreamOfflineAsync(object? sender, StreamOfflineArgs args)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        var login = args.Payload.Event.BroadcasterUserLogin.ToLowerInvariant();
        var channel = await dbContext.ChannelConfigurations.SingleOrDefaultAsync(x => x.TwitchLogin == login);
        if (channel is null)
        {
            return;
        }

        channel.TwitchUserId = args.Payload.Event.BroadcasterUserId;
        channel.UpdatedUtc = DateTimeOffset.UtcNow;

        var state = await dbContext.StreamSessionStates.SingleOrDefaultAsync(x => x.ChannelConfigurationId == channel.Id);
        if (state is null)
        {
            state = new StreamSessionState
            {
                ChannelConfigurationId = channel.Id,
                CreatedUtc = DateTimeOffset.UtcNow
            };
            dbContext.StreamSessionStates.Add(state);
        }

        var now = DateTimeOffset.UtcNow;
        if (state.LastOfflineUtc.HasValue && now - state.LastOfflineUtc.Value < TimeSpan.FromMinutes(10))
        {
            return;
        }

        var recentPendingJobExists = await dbContext.ArchiveJobs.AnyAsync(x =>
            x.ChannelConfigurationId == channel.Id &&
            (x.Status == ArchiveJobStatus.Pending || x.Status == ArchiveJobStatus.WaitingForVod || x.Status == ArchiveJobStatus.Running) &&
            now - x.CreatedUtc < TimeSpan.FromHours(6));

        if (recentPendingJobExists)
        {
            return;
        }

        state.LastOfflineUtc = now;
        state.LastProcessedOfflineMessageId = $"{channel.TwitchLogin}:{now:O}";
        state.UpdatedUtc = now;

        var archiveJob = new ArchiveJob
        {
            ChannelConfigurationId = channel.Id,
            TriggerSource = "stream.offline",
            Status = ArchiveJobStatus.Pending,
            CreatedUtc = now
        };
        dbContext.ArchiveJobs.Add(archiveJob);
        await dbContext.SaveChangesAsync();

        await archiveJobQueue.EnqueueAsync(archiveJob.Id, CancellationToken.None);
    }

    private Task OnWebsocketDisconnectedAsync(object? sender, WebsocketDisconnectedArgs args)
    {
        runtimeStatusStore.UpdateEventSubConnectionState("disconnected");
        logger.LogWarning("EventSub websocket disconnected");
        return Task.CompletedTask;
    }

    private Task OnWebsocketReconnectedAsync(object? sender, WebsocketReconnectedArgs args)
    {
        runtimeStatusStore.UpdateEventSubConnectionState("reconnected");
        logger.LogInformation("EventSub websocket reconnected");
        return Task.CompletedTask;
    }

    private Task OnErrorOccurredAsync(object? sender, ErrorOccuredArgs args)
    {
        runtimeStatusStore.UpdateEventSubConnectionState("error");
        logger.LogError(args.Exception, "EventSub websocket error");
        return Task.CompletedTask;
    }
}
