using Microsoft.EntityFrameworkCore;
using TwitchArchivist.Models;
using TwitchArchivist.Persistence;
using TwitchArchivist.Persistence.Entities;
namespace TwitchArchivist.Services.Twitch;

public class TwitchEventSubHostedService(
    IEventSubWebsocketClient eventSubWebsocketClient,
    ITwitchAccessTokenProvider accessTokenProvider,
    IEventSubSubscriptionSynchronizer subscriptionSynchronizer,
    IServiceScopeFactory scopeFactory,
    IArchiveJobQueue archiveJobQueue,
    RuntimeStatusStore runtimeStatusStore,
    ILogger<TwitchEventSubHostedService> logger,
    Microsoft.Extensions.Options.IOptions<TwitchOptions> twitchOptions,
    TimeProvider timeProvider,
    TimeSpan? monitorInterval = null) : IHostedService
{
    private static readonly Uri EventSubEndpoint = new("wss://eventsub.wss.twitch.tv/ws");
    private const int ArchiveJobRetentionLimitPerChannel = 100;
    private readonly SemaphoreSlim _connectSync = new(1, 1);
    private readonly TimeSpan _monitorInterval = monitorInterval ?? TimeSpan.FromSeconds(Math.Max(1, twitchOptions.Value.EventSubMonitorIntervalSeconds));
    private readonly TimeSpan _subscriptionSyncInterval = TimeSpan.FromSeconds(Math.Max(1, twitchOptions.Value.EventSubSubscriptionSyncIntervalSeconds));
    private CancellationTokenSource? _backgroundCancellationTokenSource;
    private Task? _monitorTask;
    private volatile bool _isConnected;
    private volatile bool _socketResetRequired;
    private int _connectFailureCount;
    private int _subscriptionFailureCount;
    private DateTimeOffset _nextConnectAttemptUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _nextSubscriptionSyncUtc = DateTimeOffset.MinValue;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        eventSubWebsocketClient.Connected += OnWebsocketConnectedAsync;
        eventSubWebsocketClient.Disconnected += OnWebsocketDisconnectedAsync;
        eventSubWebsocketClient.Reconnected += OnWebsocketReconnectedAsync;
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

        eventSubWebsocketClient.Connected -= OnWebsocketConnectedAsync;
        eventSubWebsocketClient.Disconnected -= OnWebsocketDisconnectedAsync;
        eventSubWebsocketClient.Reconnected -= OnWebsocketReconnectedAsync;
        eventSubWebsocketClient.ErrorOccurred -= OnErrorOccurredAsync;
        eventSubWebsocketClient.StreamOnline -= OnStreamOnlineAsync;
        eventSubWebsocketClient.StreamOffline -= OnStreamOfflineAsync;
        await eventSubWebsocketClient.DisconnectAsync();
    }

    private async Task MonitorConnectionAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var now = timeProvider.GetUtcNow();
            if (now >= _nextConnectAttemptUtc)
            {
                await ConnectIfConfiguredAsync(cancellationToken);
            }

            now = timeProvider.GetUtcNow();
            if (now >= _nextSubscriptionSyncUtc)
            {
                await EnsureSubscriptionsIfConnectedAsync(cancellationToken);
            }

            try
            {
                await Task.Delay(_monitorInterval, cancellationToken);
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
            _nextConnectAttemptUtc = DateTimeOffset.MaxValue;
            return;
        }

        await _connectSync.WaitAsync(cancellationToken);
        try
        {
            if (_isConnected)
            {
                _nextConnectAttemptUtc = DateTimeOffset.MaxValue;
                return;
            }

            var token = await accessTokenProvider.GetUserAccessTokenAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(token))
            {
                runtimeStatusStore.UpdateEventSubConnectionState("awaiting-authorization");
                _nextConnectAttemptUtc = timeProvider.GetUtcNow() + _monitorInterval;
                return;
            }

            if (_socketResetRequired)
            {
                await ResetSocketStateAsync(cancellationToken);
            }

            runtimeStatusStore.UpdateEventSubConnectionState("connecting");
            await eventSubWebsocketClient.ConnectAsync(EventSubEndpoint);
            _connectFailureCount = 0;
            _nextConnectAttemptUtc = DateTimeOffset.MaxValue;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already been started", StringComparison.OrdinalIgnoreCase))
        {
            _socketResetRequired = true;
            ScheduleNextConnectAttempt();
            runtimeStatusStore.UpdateEventSubConnectionState("error");
            logger.LogWarning(ex, "EventSub websocket reconnect requires a client reset before the next connect attempt");
        }
        catch (Exception ex)
        {
            ScheduleNextConnectAttempt();
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
            _subscriptionFailureCount = 0;
            _nextSubscriptionSyncUtc = timeProvider.GetUtcNow() + _subscriptionSyncInterval;
        }
        catch (Exception ex)
        {
            ScheduleNextSubscriptionSyncAttempt();
            logger.LogError(ex, "Failed to reconcile EventSub subscriptions for enabled channels");
        }
    }

    private async Task OnWebsocketConnectedAsync(object? sender, EventSubConnectedEventArgs args)
    {
        _isConnected = true;
        _socketResetRequired = false;
        _connectFailureCount = 0;
        _nextConnectAttemptUtc = DateTimeOffset.MaxValue;
        _nextSubscriptionSyncUtc = timeProvider.GetUtcNow();
        runtimeStatusStore.UpdateEventSubConnectionState(args.IsRequestedReconnect ? "reconnected" : "connected");
        var sessionId = eventSubWebsocketClient.SessionId;
        logger.LogInformation("EventSub websocket connected with session {SessionId}", sessionId);

        if (args.IsRequestedReconnect || string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        await EnsureSubscriptionsIfConnectedAsync(CancellationToken.None);
    }

    private async Task OnStreamOnlineAsync(object? sender, EventSubStreamOnlineEventArgs args)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        var login = args.BroadcasterUserLogin.ToLowerInvariant();
        var channel = await dbContext.ChannelConfigurations.SingleOrDefaultAsync(x => x.TwitchLogin == login);
        if (channel is null)
        {
            return;
        }

        channel.TwitchUserId = args.BroadcasterUserId;
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

        state.LastKnownStreamId = args.StreamId;
        state.LastOnlineUtc = args.StartedAtUtc;
        state.UpdatedUtc = DateTimeOffset.UtcNow;

        await dbContext.SaveChangesAsync();
    }

    private async Task OnStreamOfflineAsync(object? sender, EventSubStreamOfflineEventArgs args)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        var login = args.BroadcasterUserLogin.ToLowerInvariant();
        var channel = await dbContext.ChannelConfigurations.SingleOrDefaultAsync(x => x.TwitchLogin == login);
        if (channel is null)
        {
            return;
        }

        channel.TwitchUserId = args.BroadcasterUserId;
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

    private Task OnWebsocketDisconnectedAsync(object? sender, EventSubDisconnectedEventArgs args)
    {
        _isConnected = false;
        _socketResetRequired = true;
        ScheduleNextConnectAttempt();
        runtimeStatusStore.UpdateEventSubConnectionState("disconnected");
        logger.LogWarning("EventSub websocket disconnected");
        return Task.CompletedTask;
    }

    private async Task OnWebsocketReconnectedAsync(object? sender, EventSubReconnectedEventArgs args)
    {
        _isConnected = true;
        _socketResetRequired = false;
        _connectFailureCount = 0;
        _nextConnectAttemptUtc = DateTimeOffset.MaxValue;
        _nextSubscriptionSyncUtc = timeProvider.GetUtcNow();
        runtimeStatusStore.UpdateEventSubConnectionState("reconnected");
        logger.LogInformation("EventSub websocket reconnected");
        await EnsureSubscriptionsIfConnectedAsync(CancellationToken.None);
    }

    private Task OnErrorOccurredAsync(object? sender, EventSubErrorEventArgs args)
    {
        _isConnected = false;
        _socketResetRequired = true;
        ScheduleNextConnectAttempt();
        runtimeStatusStore.UpdateEventSubConnectionState("error");
        logger.LogError(args.Exception, "EventSub websocket error");
        return Task.CompletedTask;
    }

    private async Task ResetSocketStateAsync(CancellationToken cancellationToken)
    {
        try
        {
            await eventSubWebsocketClient.DisconnectAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to reset the EventSub websocket client before reconnecting");
        }
        finally
        {
            _socketResetRequired = false;
        }
    }

    private void ScheduleNextConnectAttempt()
    {
        _connectFailureCount++;
        _nextConnectAttemptUtc = timeProvider.GetUtcNow() + ComputeRetryDelay(_connectFailureCount);
    }

    private void ScheduleNextSubscriptionSyncAttempt()
    {
        _subscriptionFailureCount++;
        _nextSubscriptionSyncUtc = timeProvider.GetUtcNow() + ComputeRetryDelay(_subscriptionFailureCount);
    }

    private TimeSpan ComputeRetryDelay(int failureCount)
    {
        var baseDelaySeconds = Math.Max(1, twitchOptions.Value.EventSubRetryBaseDelaySeconds);
        var maxDelaySeconds = Math.Max(baseDelaySeconds, twitchOptions.Value.EventSubRetryMaxDelaySeconds);
        var exponent = Math.Max(0, failureCount - 1);
        var scaledDelaySeconds = baseDelaySeconds * Math.Pow(2, exponent);
        var delaySeconds = Math.Min(maxDelaySeconds, scaledDelaySeconds);
        return TimeSpan.FromSeconds(delaySeconds);
    }
}
