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
    EventSubSubscriptionSynchronizer subscriptionSynchronizer,
    IServiceScopeFactory scopeFactory,
    IArchiveJobQueue archiveJobQueue,
    RuntimeStatusStore runtimeStatusStore,
    ILogger<TwitchEventSubHostedService> logger) : IHostedService
{
    private static readonly Uri EventSubEndpoint = new("wss://eventsub.wss.twitch.tv/ws");
    private const int ArchiveJobRetentionLimitPerChannel = 100;
    private readonly SemaphoreSlim _connectSync = new(1, 1);
    private CancellationTokenSource? _backgroundCancellationTokenSource;
    private Task? _monitorTask;
    private volatile bool _isConnected;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        eventSubWebsocketClient.WebsocketConnected += OnWebsocketConnectedAsync;
        eventSubWebsocketClient.WebsocketDisconnected += OnWebsocketDisconnectedAsync;
        eventSubWebsocketClient.WebsocketReconnected += OnWebsocketReconnectedAsync;
        eventSubWebsocketClient.ErrorOccurred += OnErrorOccurredAsync;
        eventSubWebsocketClient.StreamOnline += OnStreamOnlineAsync;
        eventSubWebsocketClient.StreamOffline += OnStreamOfflineAsync;

        _backgroundCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _monitorTask = MonitorConnectionAsync(_backgroundCancellationTokenSource.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_backgroundCancellationTokenSource is not null)
        {
            await _backgroundCancellationTokenSource.CancelAsync();
        }

        if (_monitorTask is not null)
        {
            try
            {
                await _monitorTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }

        eventSubWebsocketClient.WebsocketConnected -= OnWebsocketConnectedAsync;
        eventSubWebsocketClient.WebsocketDisconnected -= OnWebsocketDisconnectedAsync;
        eventSubWebsocketClient.WebsocketReconnected -= OnWebsocketReconnectedAsync;
        eventSubWebsocketClient.ErrorOccurred -= OnErrorOccurredAsync;
        eventSubWebsocketClient.StreamOnline -= OnStreamOnlineAsync;
        eventSubWebsocketClient.StreamOffline -= OnStreamOfflineAsync;
        await eventSubWebsocketClient.DisconnectAsync();
    }

    private async Task MonitorConnectionAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await ConnectIfConfiguredAsync(cancellationToken);
            await EnsureSubscriptionsIfConnectedAsync(cancellationToken);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task ConnectIfConfiguredAsync(CancellationToken cancellationToken)
    {
        if (_isConnected)
        {
            return;
        }

        await _connectSync.WaitAsync(cancellationToken);
        try
        {
            if (_isConnected)
            {
                return;
            }

            var token = await accessTokenProvider.GetUserAccessTokenAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(token))
            {
                runtimeStatusStore.UpdateEventSubConnectionState("awaiting-authorization");
                return;
            }

            runtimeStatusStore.UpdateEventSubConnectionState("connecting");
            await eventSubWebsocketClient.ConnectAsync(EventSubEndpoint);
        }
        catch (Exception ex)
        {
            runtimeStatusStore.UpdateEventSubConnectionState("error");
            logger.LogError(ex, "Failed to initialize the EventSub websocket connection");
        }
        finally
        {
            _connectSync.Release();
        }
    }

    private async Task EnsureSubscriptionsIfConnectedAsync(CancellationToken cancellationToken)
    {
        if (!_isConnected || string.IsNullOrWhiteSpace(eventSubWebsocketClient.SessionId))
        {
            return;
        }

        try
        {
            await subscriptionSynchronizer.EnsureSubscriptionsAsync(eventSubWebsocketClient.SessionId, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reconcile EventSub subscriptions for enabled channels");
        }
    }

    private async Task OnWebsocketConnectedAsync(object? sender, WebsocketConnectedArgs args)
    {
        _isConnected = true;
        runtimeStatusStore.UpdateEventSubConnectionState(args.IsRequestedReconnect ? "reconnected" : "connected");
        logger.LogInformation("EventSub websocket connected with session {SessionId}", eventSubWebsocketClient.SessionId);

        if (args.IsRequestedReconnect)
        {
            return;
        }

        await subscriptionSynchronizer.EnsureSubscriptionsAsync(eventSubWebsocketClient.SessionId, CancellationToken.None);
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
        await dbContext.TrimArchiveJobsForChannelAsync(channel.Id, ArchiveJobRetentionLimitPerChannel, CancellationToken.None);

        await archiveJobQueue.EnqueueAsync(archiveJob.Id, CancellationToken.None);
    }

    private Task OnWebsocketDisconnectedAsync(object? sender, WebsocketDisconnectedArgs args)
    {
        _isConnected = false;
        runtimeStatusStore.UpdateEventSubConnectionState("disconnected");
        logger.LogWarning("EventSub websocket disconnected");
        return Task.CompletedTask;
    }

    private Task OnWebsocketReconnectedAsync(object? sender, WebsocketReconnectedArgs args)
    {
        _isConnected = true;
        runtimeStatusStore.UpdateEventSubConnectionState("reconnected");
        logger.LogInformation("EventSub websocket reconnected");
        return Task.CompletedTask;
    }

    private Task OnErrorOccurredAsync(object? sender, ErrorOccuredArgs args)
    {
        _isConnected = false;
        runtimeStatusStore.UpdateEventSubConnectionState("error");
        logger.LogError(args.Exception, "EventSub websocket error");
        return Task.CompletedTask;
    }
}
