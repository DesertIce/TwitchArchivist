using TwitchArchivist.Services.Twitch;

namespace TwitchArchivist.UnitTests.Services.Twitch;

#pragma warning disable CS0067
public class EventSubShardClientTests
{
    [Fact]
    public async Task AdapterCanCreateMultipleIndependentShardClients()
    {
        var primaryClient = new FakeEventSubShardClient("primary");
        var createdClients = new List<FakeEventSubShardClient>();
        var adapter = new TwitchLibEventSubWebsocketClientAdapter(
            primaryClient,
            shardKey =>
            {
                var client = new FakeEventSubShardClient(shardKey);
                createdClients.Add(client);
                return client;
            });

        var shardZero = adapter.CreateShardClient("0");
        var shardOne = adapter.CreateShardClient("1");

        await shardZero.ConnectAsync(new Uri("wss://eventsub.wss.twitch.tv/ws"));
        await shardOne.ConnectAsync(new Uri("wss://eventsub.wss.twitch.tv/ws"));

        Assert.Equal(2, createdClients.Count);
        Assert.NotSame(shardZero, shardOne);
        Assert.Equal("session-0", shardZero.SessionId);
        Assert.Equal("session-1", shardOne.SessionId);
        Assert.True(shardZero.ConnectionState.IsConnected);
        Assert.True(shardOne.ConnectionState.IsConnected);
    }

    [Fact]
    public async Task PrimaryClientEventsStillFlowThroughAdapter()
    {
        var primaryClient = new FakeEventSubShardClient("primary");
        var adapter = new TwitchLibEventSubWebsocketClientAdapter(
            primaryClient,
            shardKey => new FakeEventSubShardClient(shardKey));
        EventSubConnectedEventArgs? connectedArgs = null;
        EventSubReconnectedEventArgs? reconnectedArgs = null;

        adapter.Connected += (_, args) =>
        {
            connectedArgs = args;
            return Task.CompletedTask;
        };
        adapter.Reconnected += (_, args) =>
        {
            reconnectedArgs = args;
            return Task.CompletedTask;
        };

        await adapter.ConnectAsync(new Uri("wss://eventsub.wss.twitch.tv/ws"));
        await adapter.ReconnectAsync();

        Assert.Equal("session-primary-r1", adapter.SessionId);
        Assert.Equal("session-primary", connectedArgs?.SessionId);
        Assert.Equal("session-primary-r1", reconnectedArgs?.SessionId);
    }

    [Fact]
    public async Task ShardClientCapturesWelcomeAndReconnectState()
    {
        var shardClient = new FakeEventSubShardClient("7");

        await shardClient.ConnectAsync(new Uri("wss://eventsub.wss.twitch.tv/ws"));
        var connectedState = shardClient.ConnectionState;

        await shardClient.ReconnectAsync();
        var reconnectedState = shardClient.ConnectionState;

        Assert.True(connectedState.IsConnected);
        Assert.Equal("session-7", connectedState.SessionId);
        Assert.NotNull(connectedState.LastWelcomeUtc);
        Assert.Equal("session-7-r1", reconnectedState.SessionId);
        Assert.NotNull(reconnectedState.LastReconnectedUtc);
    }

    private sealed class FakeEventSubShardClient(string shardKey) : IEventSubShardClient
    {
        private int _reconnectCount;

        public string ShardKey { get; } = shardKey;

        public string? SessionId { get; private set; }

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

        public async Task<bool> ConnectAsync(Uri endpoint)
        {
            SessionId = $"session-{ShardKey}";
            ConnectionState = ConnectionState with
            {
                SessionId = SessionId,
                IsConnected = true,
                LastWelcomeUtc = DateTimeOffset.UtcNow
            };

            if (Connected is not null)
            {
                await Connected.Invoke(this, new EventSubConnectedEventArgs(false, SessionId));
            }

            return true;
        }

        public async Task<bool> ReconnectAsync()
        {
            _reconnectCount++;
            SessionId = $"session-{ShardKey}-r{_reconnectCount}";
            ConnectionState = ConnectionState with
            {
                SessionId = SessionId,
                IsConnected = true,
                LastReconnectedUtc = DateTimeOffset.UtcNow
            };

            if (Reconnected is not null)
            {
                await Reconnected.Invoke(this, new EventSubReconnectedEventArgs(SessionId));
            }

            return true;
        }

        public async Task<bool> DisconnectAsync()
        {
            ConnectionState = ConnectionState with
            {
                SessionId = SessionId,
                IsConnected = false,
                LastDisconnectedUtc = DateTimeOffset.UtcNow
            };

            if (Disconnected is not null)
            {
                await Disconnected.Invoke(this, new EventSubDisconnectedEventArgs(SessionId));
            }

            return true;
        }
    }
}
#pragma warning restore CS0067
