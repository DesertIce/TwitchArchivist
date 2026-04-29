using Microsoft.Extensions.Logging.Abstractions;
using TwitchLib.EventSub.Websockets;

namespace TwitchArchivist.Services.Twitch;

public sealed class TwitchLibEventSubWebsocketClientAdapter : IEventSubWebsocketClient
{
    private readonly IEventSubShardClient _primaryShardClient;
    private readonly Func<string, IEventSubShardClient> _shardClientFactory;

    public TwitchLibEventSubWebsocketClientAdapter(EventSubWebsocketClient innerClient)
        : this(
            new TwitchLibEventSubShardClient("primary", innerClient),
            shardKey => new TwitchLibEventSubShardClient(
                shardKey,
                new EventSubWebsocketClient(NullLoggerFactory.Instance)))
    {
    }

    public TwitchLibEventSubWebsocketClientAdapter(
        IEventSubShardClient primaryShardClient,
        Func<string, IEventSubShardClient> shardClientFactory)
    {
        _primaryShardClient = primaryShardClient;
        _shardClientFactory = shardClientFactory;
    }

    public string? SessionId => _primaryShardClient.SessionId;

    public event Func<object?, EventSubConnectedEventArgs, Task>? Connected
    {
        add => _primaryShardClient.Connected += value;
        remove => _primaryShardClient.Connected -= value;
    }

    public event Func<object?, EventSubDisconnectedEventArgs, Task>? Disconnected
    {
        add => _primaryShardClient.Disconnected += value;
        remove => _primaryShardClient.Disconnected -= value;
    }

    public event Func<object?, EventSubReconnectedEventArgs, Task>? Reconnected
    {
        add => _primaryShardClient.Reconnected += value;
        remove => _primaryShardClient.Reconnected -= value;
    }

    public event Func<object?, EventSubErrorEventArgs, Task>? ErrorOccurred
    {
        add => _primaryShardClient.ErrorOccurred += value;
        remove => _primaryShardClient.ErrorOccurred -= value;
    }

    public event Func<object?, EventSubStreamOnlineEventArgs, Task>? StreamOnline
    {
        add => _primaryShardClient.StreamOnline += value;
        remove => _primaryShardClient.StreamOnline -= value;
    }

    public event Func<object?, EventSubStreamOfflineEventArgs, Task>? StreamOffline
    {
        add => _primaryShardClient.StreamOffline += value;
        remove => _primaryShardClient.StreamOffline -= value;
    }

    public IEventSubShardClient CreateShardClient(string shardKey) => _shardClientFactory(shardKey);

    public Task<bool> ConnectAsync(Uri endpoint) => _primaryShardClient.ConnectAsync(endpoint);

    public Task<bool> ReconnectAsync() => _primaryShardClient.ReconnectAsync();

    public Task<bool> DisconnectAsync() => _primaryShardClient.DisconnectAsync();

    public void Attach()
    {
    }

    public void Detach()
    {
    }
}
