namespace TwitchArchivist.Services.Twitch;

public interface IEventSubShardClient
{
    string ShardKey { get; }

    string? SessionId { get; }

    EventSubShardConnectionState ConnectionState { get; }

    event Func<object?, EventSubConnectedEventArgs, Task>? Connected;

    event Func<object?, EventSubDisconnectedEventArgs, Task>? Disconnected;

    event Func<object?, EventSubReconnectedEventArgs, Task>? Reconnected;

    event Func<object?, EventSubErrorEventArgs, Task>? ErrorOccurred;

    event Func<object?, EventSubShardDisabledEventArgs, Task>? ShardDisabled;

    event Func<object?, EventSubStreamOnlineEventArgs, Task>? StreamOnline;

    event Func<object?, EventSubStreamOfflineEventArgs, Task>? StreamOffline;

    Task<bool> ConnectAsync(Uri endpoint);

    Task<bool> ReconnectAsync();

    Task<bool> DisconnectAsync();
}

public sealed record EventSubShardDisabledEventArgs(string ShardKey, string? SessionId);
