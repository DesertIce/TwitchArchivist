using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Polly;
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
    private static readonly TimeSpan RecentOfflineJobWindow = TimeSpan.FromHours(6);
    private static readonly ArchiveJobStatus[] ActiveArchiveJobStatuses =
    [
        ArchiveJobStatus.Pending,
        ArchiveJobStatus.WaitingForVod,
        ArchiveJobStatus.Running
    ];
    private readonly SemaphoreSlim _connectSync = new(1, 1);
    private readonly SemaphoreSlim _archiveJobCreationSync = new(1, 1);
    private readonly TimeSpan _monitorInterval = monitorInterval ?? TimeSpan.FromSeconds(Math.Max(1, twitchOptions.Value.EventSubMonitorIntervalSeconds));
    private readonly TimeSpan _subscriptionSyncInterval = TimeSpan.FromSeconds(Math.Max(1, twitchOptions.Value.EventSubSubscriptionSyncIntervalSeconds));
    private CancellationTokenSource? _backgroundCancellationTokenSource;
    private Task? _monitorTask;
    private volatile bool _isConnected;
    private volatile bool _reconnectRequired;
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

            var connectOperation = _reconnectRequired ? "reconnecting" : "connecting";
            runtimeStatusStore.UpdateEventSubConnectionState(connectOperation);
            var connectSucceeded = _reconnectRequired
                ? await eventSubWebsocketClient.ReconnectAsync()
                : await eventSubWebsocketClient.ConnectAsync(EventSubEndpoint);

            if (!connectSucceeded)
            {
                throw new InvalidOperationException($"EventSub websocket {connectOperation} returned false.");
            }

            _connectFailureCount = 0;
            _nextConnectAttemptUtc = DateTimeOffset.MaxValue;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already been started", StringComparison.OrdinalIgnoreCase))
        {
            _reconnectRequired = true;
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
        _reconnectRequired = false;
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
        var login = args.BroadcasterUserLogin.ToLowerInvariant();
        logger.LogInformation("Received EventSub stream.offline notification for channel {ChannelLogin}", login);

        await _archiveJobCreationSync.WaitAsync();
        try
        {
            await CreateArchiveJobCreationRetryPolicy(login).ExecuteAsync(async () =>
            {
                await CreateArchiveJobFromOfflineAsync(login, args.BroadcasterUserId);
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                ex,
                "Failed to create archive job from stream.offline for channel {ChannelLogin} and broadcaster user id {BroadcasterUserId}",
                login,
                args.BroadcasterUserId);
        }
        finally
        {
            _archiveJobCreationSync.Release();
        }
    }

    private async Task CreateArchiveJobFromOfflineAsync(string login, string broadcasterUserId)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        var channel = await dbContext.ChannelConfigurations.SingleOrDefaultAsync(x => x.TwitchLogin == login);
        if (channel is null)
        {
            logger.LogWarning("Ignoring stream.offline notification for unknown channel {ChannelLogin}", login);
            return;
        }

        channel.TwitchUserId = broadcasterUserId;
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
        logger.LogInformation(
            "Evaluating archive job creation for channel {ChannelLogin}; broadcaster user id {BroadcasterUserId}, last online at {LastOnlineUtc}, last offline at {LastOfflineUtc}, current stream id {LastKnownStreamId}",
            channel.TwitchLogin,
            broadcasterUserId,
            state.LastOnlineUtc,
            state.LastOfflineUtc,
            state.LastKnownStreamId);

        if (state.LastOfflineUtc.HasValue && now - state.LastOfflineUtc.Value < TimeSpan.FromMinutes(10))
        {
            logger.LogInformation(
                "Ignoring duplicate stream.offline notification for channel {ChannelLogin}; last offline at {LastOfflineUtc}",
                channel.TwitchLogin,
                state.LastOfflineUtc.Value);
            return;
        }

        var recentJobCutoffUtc = now - RecentOfflineJobWindow;
        var recentJobs = await dbContext.ArchiveJobs
            .Where(x => x.ChannelConfigurationId == channel.Id)
            .Select(x => new
            {
                x.Id,
                x.Status,
                x.CreatedUtc
            })
            .ToListAsync();

        var recentPendingJob = recentJobs
            .Where(x =>
                x.CreatedUtc >= recentJobCutoffUtc &&
                ActiveArchiveJobStatuses.Contains(x.Status))
            .OrderByDescending(x => x.CreatedUtc)
            .FirstOrDefault();

        if (recentPendingJob is not null)
        {
            logger.LogInformation(
                "Skipping archive job creation for channel {ChannelLogin} because active job {ArchiveJobId} with status {ArchiveJobStatus} already exists from {ArchiveJobCreatedUtc} within the cutoff {RecentJobCutoffUtc}",
                channel.TwitchLogin,
                recentPendingJob.Id,
                recentPendingJob.Status,
                recentPendingJob.CreatedUtc,
                recentJobCutoffUtc);
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

        try
        {
            await dbContext.TrimArchiveJobsForChannelAsync(channel.Id, ArchiveJobRetentionLimitPerChannel, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Archive job {ArchiveJobId} for channel {ChannelLogin} was created but retention trimming failed",
                archiveJob.Id,
                channel.TwitchLogin);
        }

        logger.LogInformation(
            "Created archive job {ArchiveJobId} for channel {ChannelLogin} from stream.offline at {OfflineDetectedAtUtc}",
            archiveJob.Id,
            channel.TwitchLogin,
            now);

        try
        {
            await CreateArchiveJobEnqueueRetryPolicy(channel.TwitchLogin, archiveJob.Id).ExecuteAsync(async () =>
            {
                await archiveJobQueue.EnqueueAsync(archiveJob.Id, CancellationToken.None);
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                ex,
                "Archive job {ArchiveJobId} for channel {ChannelLogin} was created but could not be enqueued; it will remain pending for recovery",
                archiveJob.Id,
                channel.TwitchLogin);
        }
    }

    private AsyncPolicy CreateArchiveJobCreationRetryPolicy(string channelLogin)
    {
        var maxDelay = TimeSpan.FromSeconds(Math.Max(1, twitchOptions.Value.EventSubRetryMaxDelaySeconds));
        var baseDelay = Math.Max(1, twitchOptions.Value.EventSubRetryBaseDelaySeconds);

        return Policy
            .Handle<DbUpdateException>()
            .Or<SqliteException>()
            .Or<TimeoutException>()
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: retryAttempt =>
                {
                    var delay = TimeSpan.FromSeconds(baseDelay * Math.Pow(2, retryAttempt - 1));
                    return delay <= maxDelay ? delay : maxDelay;
                },
                onRetry: (exception, delay, retryAttempt, _) =>
                {
                    logger.LogWarning(
                        exception,
                        "Retrying archive job creation for channel {ChannelLogin} after transient failure. Attempt {RetryAttempt}/3 in {RetryDelay}",
                        channelLogin,
                        retryAttempt,
                        delay);
                });
    }

    private AsyncPolicy CreateArchiveJobEnqueueRetryPolicy(string channelLogin, int archiveJobId)
    {
        return Policy
            .Handle<InvalidOperationException>()
            .Or<TimeoutException>()
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: retryAttempt => TimeSpan.FromMilliseconds(200 * retryAttempt),
                onRetry: (exception, delay, retryAttempt, _) =>
                {
                    logger.LogWarning(
                        exception,
                        "Retrying archive job enqueue for channel {ChannelLogin} and archive job {ArchiveJobId}. Attempt {RetryAttempt}/3 in {RetryDelay}",
                        channelLogin,
                        archiveJobId,
                        retryAttempt,
                        delay);
                });
    }

    private Task OnWebsocketDisconnectedAsync(object? sender, EventSubDisconnectedEventArgs args)
    {
        _isConnected = false;
        _reconnectRequired = true;
        _socketResetRequired = false;
        ScheduleNextConnectAttempt();
        runtimeStatusStore.UpdateEventSubConnectionState("disconnected");
        logger.LogWarning("EventSub websocket disconnected");
        return Task.CompletedTask;
    }

    private async Task OnWebsocketReconnectedAsync(object? sender, EventSubReconnectedEventArgs args)
    {
        _isConnected = true;
        _reconnectRequired = false;
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
        _reconnectRequired = true;
        _socketResetRequired = false;
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
