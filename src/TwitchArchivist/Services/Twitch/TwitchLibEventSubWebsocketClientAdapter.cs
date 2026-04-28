using TwitchLib.EventSub.Core.EventArgs.Stream;
using TwitchLib.EventSub.Websockets;
using TwitchLib.EventSub.Websockets.Core.EventArgs;

namespace TwitchArchivist.Services.Twitch;

public sealed class TwitchLibEventSubWebsocketClientAdapter(EventSubWebsocketClient innerClient) : IEventSubWebsocketClient
{
    public string? SessionId => innerClient.SessionId;

    public event Func<object?, EventSubConnectedEventArgs, Task>? Connected;

    public event Func<object?, EventSubDisconnectedEventArgs, Task>? Disconnected;

    public event Func<object?, EventSubReconnectedEventArgs, Task>? Reconnected;

    public event Func<object?, EventSubErrorEventArgs, Task>? ErrorOccurred;

    public event Func<object?, EventSubStreamOnlineEventArgs, Task>? StreamOnline;

    public event Func<object?, EventSubStreamOfflineEventArgs, Task>? StreamOffline;

    public Task ConnectAsync(Uri endpoint) => innerClient.ConnectAsync(endpoint);

    public Task DisconnectAsync() => innerClient.DisconnectAsync();

    public void Attach()
    {
        innerClient.WebsocketConnected += OnWebsocketConnectedAsync;
        innerClient.WebsocketDisconnected += OnWebsocketDisconnectedAsync;
        innerClient.WebsocketReconnected += OnWebsocketReconnectedAsync;
        innerClient.ErrorOccurred += OnErrorOccurredAsync;
        innerClient.StreamOnline += OnStreamOnlineAsync;
        innerClient.StreamOffline += OnStreamOfflineAsync;
    }

    public void Detach()
    {
        innerClient.WebsocketConnected -= OnWebsocketConnectedAsync;
        innerClient.WebsocketDisconnected -= OnWebsocketDisconnectedAsync;
        innerClient.WebsocketReconnected -= OnWebsocketReconnectedAsync;
        innerClient.ErrorOccurred -= OnErrorOccurredAsync;
        innerClient.StreamOnline -= OnStreamOnlineAsync;
        innerClient.StreamOffline -= OnStreamOfflineAsync;
    }

    private Task OnWebsocketConnectedAsync(object? sender, WebsocketConnectedArgs args)
        => Connected?.Invoke(sender, new EventSubConnectedEventArgs(args.IsRequestedReconnect)) ?? Task.CompletedTask;

    private Task OnWebsocketDisconnectedAsync(object? sender, WebsocketDisconnectedArgs args)
        => Disconnected?.Invoke(sender, new EventSubDisconnectedEventArgs()) ?? Task.CompletedTask;

    private Task OnWebsocketReconnectedAsync(object? sender, WebsocketReconnectedArgs args)
        => Reconnected?.Invoke(sender, new EventSubReconnectedEventArgs()) ?? Task.CompletedTask;

    private Task OnErrorOccurredAsync(object? sender, ErrorOccuredArgs args)
        => ErrorOccurred?.Invoke(sender, new EventSubErrorEventArgs(args.Exception)) ?? Task.CompletedTask;

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
