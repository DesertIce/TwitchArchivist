using TwitchLib.EventSub.Core.EventArgs.Stream;
using TwitchLib.EventSub.Core.EventArgs.Conduit;
using TwitchLib.EventSub.Websockets;
using TwitchLib.EventSub.Websockets.Core.EventArgs;

namespace TwitchArchivist.Services.Twitch;

public sealed class TwitchLibEventSubShardClient : IEventSubShardClient
{
    private readonly EventSubWebsocketClient _innerClient;

    public TwitchLibEventSubShardClient(string shardKey, EventSubWebsocketClient innerClient, TimeProvider? timeProvider = null)
    {
        ShardKey = shardKey;
        _innerClient = innerClient;
        _timeProvider = timeProvider ?? TimeProvider.System;
        Attach();
    }

    private readonly TimeProvider _timeProvider;

    public string ShardKey { get; }

    public string? SessionId => _innerClient.SessionId;

    public EventSubShardConnectionState ConnectionState { get; private set; } = new(
        SessionId: null,
        IsConnected: false,
        LastWelcomeUtc: null,
        LastReconnectedUtc: null,
        LastDisconnectedUtc: null);

    public event Func<object?, EventSubConnectedEventArgs, Task>? Connected;

    public event Func<object?, EventSubDisconnectedEventArgs, Task>? Disconnected;

    public event Func<object?, EventSubReconnectedEventArgs, Task>? Reconnected;

    public event Func<object?, EventSubErrorEventArgs, Task>? ErrorOccurred;

    public event Func<object?, EventSubShardDisabledEventArgs, Task>? ShardDisabled;

    public event Func<object?, EventSubStreamOnlineEventArgs, Task>? StreamOnline;

    public event Func<object?, EventSubStreamOfflineEventArgs, Task>? StreamOffline;

    public Task<bool> ConnectAsync(Uri endpoint) => _innerClient.ConnectAsync(endpoint);

    public Task<bool> ReconnectAsync() => _innerClient.ReconnectAsync();

    public Task<bool> DisconnectAsync() => _innerClient.DisconnectAsync();

    private void Attach()
    {
        _innerClient.WebsocketConnected += OnWebsocketConnectedAsync;
        _innerClient.WebsocketDisconnected += OnWebsocketDisconnectedAsync;
        _innerClient.WebsocketReconnected += OnWebsocketReconnectedAsync;
        _innerClient.ErrorOccurred += OnErrorOccurredAsync;
        _innerClient.ConduitShardDisabled += OnConduitShardDisabledAsync;
        _innerClient.StreamOnline += OnStreamOnlineAsync;
        _innerClient.StreamOffline += OnStreamOfflineAsync;
    }

    private Task OnWebsocketConnectedAsync(object? sender, WebsocketConnectedArgs args)
    {
        ConnectionState = ConnectionState with
        {
            SessionId = SessionId,
            IsConnected = true,
            LastWelcomeUtc = _timeProvider.GetUtcNow()
        };

        return Connected?.Invoke(sender, new EventSubConnectedEventArgs(args.IsRequestedReconnect, SessionId)) ?? Task.CompletedTask;
    }

    private Task OnWebsocketDisconnectedAsync(object? sender, WebsocketDisconnectedArgs args)
    {
        ConnectionState = ConnectionState with
        {
            SessionId = SessionId,
            IsConnected = false,
            LastDisconnectedUtc = _timeProvider.GetUtcNow()
        };

        return Disconnected?.Invoke(sender, new EventSubDisconnectedEventArgs(SessionId)) ?? Task.CompletedTask;
    }

    private Task OnWebsocketReconnectedAsync(object? sender, WebsocketReconnectedArgs args)
    {
        ConnectionState = ConnectionState with
        {
            SessionId = SessionId,
            IsConnected = true,
            LastReconnectedUtc = _timeProvider.GetUtcNow()
        };

        return Reconnected?.Invoke(sender, new EventSubReconnectedEventArgs(SessionId)) ?? Task.CompletedTask;
    }

    private Task OnErrorOccurredAsync(object? sender, ErrorOccuredArgs args)
        => ErrorOccurred?.Invoke(sender, new EventSubErrorEventArgs(args.Exception)) ?? Task.CompletedTask;

    private Task OnConduitShardDisabledAsync(object? sender, ConduitShardDisabledArgs args)
        => ShardDisabled?.Invoke(sender, new EventSubShardDisabledEventArgs(ShardKey, SessionId)) ?? Task.CompletedTask;

    private Task OnStreamOnlineAsync(object? sender, StreamOnlineArgs args)
        => StreamOnline?.Invoke(sender, new EventSubStreamOnlineEventArgs(
            args.Payload.Event.BroadcasterUserLogin,
            args.Payload.Event.BroadcasterUserId,
            args.Payload.Event.Id,
            args.Payload.Event.StartedAt)) ?? Task.CompletedTask;

    private Task OnStreamOfflineAsync(object? sender, StreamOfflineArgs args)
        => StreamOffline?.Invoke(sender, new EventSubStreamOfflineEventArgs(
            args.Payload.Event.BroadcasterUserLogin,
            args.Payload.Event.BroadcasterUserId)) ?? Task.CompletedTask;
}
