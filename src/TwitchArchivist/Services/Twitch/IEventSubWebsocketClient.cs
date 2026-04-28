namespace TwitchArchivist.Services.Twitch;

public interface IEventSubWebsocketClient
{
    string? SessionId { get; }

    event Func<object?, EventSubConnectedEventArgs, Task>? Connected;

    event Func<object?, EventSubDisconnectedEventArgs, Task>? Disconnected;

    event Func<object?, EventSubReconnectedEventArgs, Task>? Reconnected;

    event Func<object?, EventSubErrorEventArgs, Task>? ErrorOccurred;

    event Func<object?, EventSubStreamOnlineEventArgs, Task>? StreamOnline;

    event Func<object?, EventSubStreamOfflineEventArgs, Task>? StreamOffline;

    Task<bool> ConnectAsync(Uri endpoint);

    Task<bool> ReconnectAsync();

    Task<bool> DisconnectAsync();
}

public sealed record EventSubConnectedEventArgs(bool IsRequestedReconnect);

public sealed record EventSubDisconnectedEventArgs;

public sealed record EventSubReconnectedEventArgs;

public sealed record EventSubErrorEventArgs(Exception Exception);

public sealed record EventSubStreamOnlineEventArgs(
    string BroadcasterUserLogin,
    string BroadcasterUserId,
    string StreamId,
    DateTimeOffset StartedAtUtc);

public sealed record EventSubStreamOfflineEventArgs(
    string BroadcasterUserLogin,
    string BroadcasterUserId);
