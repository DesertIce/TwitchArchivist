using TwitchArchivist.Models;
namespace TwitchArchivist.Services.Twitch;

public class TwitchEventSubHostedService(
    IEventSubWebsocketClient eventSubWebsocketClient,
    ITwitchAccessTokenProvider accessTokenProvider,
    IEventSubSubscriptionSynchronizer subscriptionSynchronizer,
    EventSubNotificationProcessor notificationProcessor,
    RuntimeStatusStore runtimeStatusStore,
    ILogger<TwitchEventSubHostedService> logger,
    Microsoft.Extensions.Options.IOptions<TwitchOptions> twitchOptions,
    TimeProvider timeProvider,
    TimeSpan? monitorInterval = null) : IHostedService
{
    private static readonly Uri EventSubEndpoint = new("wss://eventsub.wss.twitch.tv/ws");
    private readonly SemaphoreSlim _connectSync = new(1, 1);
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
        eventSubWebsocketClient.StreamOnline += notificationProcessor.HandleStreamOnlineAsync;
        eventSubWebsocketClient.StreamOffline += notificationProcessor.HandleStreamOfflineAsync;

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
        eventSubWebsocketClient.StreamOnline -= notificationProcessor.HandleStreamOnlineAsync;
        eventSubWebsocketClient.StreamOffline -= notificationProcessor.HandleStreamOfflineAsync;
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
        if (string.Equals(
            twitchOptions.Value.EventSubTransportMode,
            "conduit-websocket",
            StringComparison.OrdinalIgnoreCase))
        {
            _nextSubscriptionSyncUtc = DateTimeOffset.MaxValue;
            return;
        }

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
        var sessionId = args.SessionId ?? eventSubWebsocketClient.SessionId;
        logger.LogInformation("EventSub websocket connected with session {SessionId}", sessionId);

        if (args.IsRequestedReconnect || string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        await EnsureSubscriptionsIfConnectedAsync(CancellationToken.None);
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
        logger.LogInformation("EventSub websocket reconnected with session {SessionId}", args.SessionId ?? eventSubWebsocketClient.SessionId);
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
