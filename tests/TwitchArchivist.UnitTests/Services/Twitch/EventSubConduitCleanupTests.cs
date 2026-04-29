using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TwitchArchivist.Models;
using TwitchArchivist.Persistence;
using TwitchArchivist.Persistence.Entities;
using TwitchArchivist.Services;
using TwitchArchivist.Services.Twitch;

namespace TwitchArchivist.UnitTests.Services.Twitch;

#pragma warning disable CS0067
public class EventSubConduitCleanupTests
{
    [Fact]
    public async Task ReconcileAsyncReplacesStaleDisconnectedShardSession()
    {
        await using var database = await CreateDatabaseAsync();
        await SeedConduitAsync(database.Services, "conduit-1", 1);
        var helixClient = new RecordingHelixClient(
            [new EventSubConduitRecord("conduit-1", 1, [new EventSubConduitShardRecord("0", "enabled", "session-0")])]);
        var websocketClient = new RecordingEventSubWebsocketClient(["session-0", "session-1"]);
        var coordinator = CreateCoordinator(database.Services, helixClient, websocketClient);

        await coordinator.StartAsync(CancellationToken.None);
        websocketClient.CreatedShardClients.Single().MarkDisconnected();

        await coordinator.ReconcileAsync(CancellationToken.None);

        Assert.Equal("session-1", helixClient.AssignedShards.Single().TransportSessionId);
    }

    [Fact]
    public async Task ReconcileAsyncReassignsDisabledRemoteShard()
    {
        await using var database = await CreateDatabaseAsync();
        await SeedConduitAsync(database.Services, "conduit-1", 1);
        var helixClient = new RecordingHelixClient(
            [new EventSubConduitRecord("conduit-1", 1, [new EventSubConduitShardRecord("0", "websocket_disconnected", "session-old")])]);
        var websocketClient = new RecordingEventSubWebsocketClient(["session-0", "session-1"]);
        var coordinator = CreateCoordinator(database.Services, helixClient, websocketClient);

        await coordinator.StartAsync(CancellationToken.None);
        helixClient.RemoteConduits = [new EventSubConduitRecord("conduit-1", 1, [new EventSubConduitShardRecord("0", "websocket_disconnected", "session-old")])];

        await coordinator.ReconcileAsync(CancellationToken.None);

        Assert.Equal("session-1", helixClient.AssignedShards.Last().TransportSessionId);
    }

    [Fact]
    public async Task RunOnceAsyncDeletesLegacyDirectWebsocketSubscriptions()
    {
        await using var database = await CreateDatabaseAsync();
        await SeedConduitAsync(database.Services, "conduit-1", 1, broadcasterUserId: "123");
        var helixClient = new RecordingHelixClient(
            [new EventSubConduitRecord("conduit-1", 1, [new EventSubConduitShardRecord("0", "enabled", "session-0")])])
        {
            ExistingSubscriptions =
            [
                new EventSubSubscriptionRecord("delete-me", "stream.online", "enabled", "123", "legacy-session"),
                new EventSubSubscriptionRecord("keep-me", "stream.online", "enabled", "999", "legacy-session")
            ]
        };
        var cleanupService = CreateCleanupService(database.Services, helixClient, new NoOpCoordinator(), transportMode: "conduit-websocket");

        await cleanupService.RunOnceAsync(CancellationToken.None);

        Assert.Equal(["delete-me"], helixClient.DeletedSubscriptionIds);
    }

    [Fact]
    public async Task RunOnceAsyncHonorsRetryAfterWhenRateLimited()
    {
        await using var database = await CreateDatabaseAsync();
        var coordinator = new RateLimitedCoordinator();
        var cleanupService = CreateCleanupService(database.Services, new RecordingHelixClient([]), coordinator, transportMode: "conduit-websocket");
        var startedAt = DateTimeOffset.UtcNow;

        await cleanupService.RunOnceAsync(CancellationToken.None);

        var elapsed = DateTimeOffset.UtcNow - startedAt;
        Assert.Equal(2, coordinator.CallCount);
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(35), $"Expected retry delay to be honored. Elapsed={elapsed}");
    }

    private static EventSubConduitCoordinator CreateCoordinator(
        IServiceProvider services,
        RecordingHelixClient helixClient,
        RecordingEventSubWebsocketClient websocketClient)
    {
        return new EventSubConduitCoordinator(
            helixClient,
            websocketClient,
            CreateNotificationProcessor(services),
            services.GetRequiredService<IServiceScopeFactory>(),
            new RuntimeStatusStore(),
            Options.Create(new TwitchOptions
            {
                EventSubTransportMode = "conduit-websocket",
                EventSubConduitShardCount = 1,
                EventSubConduitAssignmentTimeoutSeconds = 1
            }),
            NullLogger<EventSubConduitCoordinator>.Instance,
            TimeProvider.System);
    }

    private static EventSubNotificationProcessor CreateNotificationProcessor(IServiceProvider services)
        => new(
            services.GetRequiredService<IServiceScopeFactory>(),
            new NoOpArchiveJobQueue(),
            NullLogger<EventSubNotificationProcessor>.Instance,
            Options.Create(new TwitchOptions
            {
                EventSubRetryBaseDelaySeconds = 1,
                EventSubRetryMaxDelaySeconds = 2
            }),
            TimeProvider.System);

    private static EventSubConduitCleanupService CreateCleanupService(
        IServiceProvider services,
        ITwitchHelixClient helixClient,
        IEventSubConduitCoordinator coordinator,
        string transportMode)
    {
        return new EventSubConduitCleanupService(
            helixClient,
            coordinator,
            services.GetRequiredService<IServiceScopeFactory>(),
            new RuntimeStatusStore(),
            Options.Create(new TwitchOptions
            {
                EventSubTransportMode = transportMode,
                EventSubRetryBaseDelaySeconds = 1
            }),
            NullLogger<EventSubConduitCleanupService>.Instance,
            TimeProvider.System);
    }

    private static async Task SeedConduitAsync(IServiceProvider services, string conduitId, int shardCount, string broadcasterUserId = "123")
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        var now = DateTimeOffset.UtcNow;
        dbContext.EventSubConduits.Add(new EventSubConduit
        {
            TwitchConduitId = conduitId,
            ShardCount = shardCount,
            CreatedUtc = now,
            UpdatedUtc = now
        });
        dbContext.ChannelConfigurations.Add(new ChannelConfiguration
        {
            TwitchLogin = "channel",
            TwitchUserId = broadcasterUserId,
            OutputDirectory = Path.GetTempPath(),
            IsEnabled = true,
            CreatedUtc = now,
            UpdatedUtc = now
        });
        await dbContext.SaveChangesAsync();
    }

    private static async Task<TestDatabase> CreateDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<TwitchArchivistDbContext>(options => options.UseSqlite(connection));

        var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        await dbContext.Database.EnsureCreatedAsync();

        return new TestDatabase(provider, connection);
    }

    private sealed class RecordingHelixClient(IReadOnlyList<EventSubConduitRecord> remoteConduits) : ITwitchHelixClient
    {
        public IReadOnlyList<EventSubConduitRecord> RemoteConduits { get; set; } = remoteConduits;
        public IReadOnlyList<EventSubSubscriptionRecord> ExistingSubscriptions { get; set; } = [];
        public List<EventSubConduitShardRecord> AssignedShards { get; } = [];
        public List<string> DeletedSubscriptionIds { get; } = [];

        public Task<string?> ResolveUserIdAsync(string twitchLogin, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<TwitchChannelSearchResult>> SearchChannelsAsync(string query, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<TwitchLiveStreamState>> GetLiveStreamsByLoginsAsync(IReadOnlyList<string> twitchLogins, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ArchiveVodRecord?> GetLatestArchiveVodAsync(string broadcasterUserId, DateTimeOffset? createdAfterUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<EventSubSubscriptionRecord>> GetEventSubscriptionsAsync(CancellationToken cancellationToken) => Task.FromResult(ExistingSubscriptions);
        public Task DeleteEventSubscriptionAsync(string subscriptionId, CancellationToken cancellationToken)
        {
            DeletedSubscriptionIds.Add(subscriptionId);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<EventSubConduitRecord>> GetEventSubConduitsAsync(CancellationToken cancellationToken) => Task.FromResult(RemoteConduits);
        public Task<EventSubConduitRecord> CreateEventSubConduitAsync(int shardCount, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EventSubConduitRecord> UpdateEventSubConduitAsync(string conduitId, int shardCount, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<EventSubConduitShardRecord>> UpdateEventSubConduitShardsAsync(string conduitId, IReadOnlyList<EventSubConduitShardRecord> shards, CancellationToken cancellationToken)
        {
            AssignedShards.Clear();
            AssignedShards.AddRange(shards.Select(x => new EventSubConduitShardRecord(x.ShardId, "enabled", x.TransportSessionId)));
            RemoteConduits = [new EventSubConduitRecord(conduitId, shards.Count, AssignedShards.ToArray())];
            return Task.FromResult<IReadOnlyList<EventSubConduitShardRecord>>(AssignedShards);
        }
        public Task<EventSubSubscriptionRecord> CreateStreamSubscriptionAsync(string subscriptionType, string broadcasterUserId, string sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EventSubSubscriptionRecord> CreateConduitSubscriptionAsync(string subscriptionType, string broadcasterUserId, string conduitId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingEventSubWebsocketClient(IReadOnlyList<string> sessionIds) : IEventSubWebsocketClient
    {
        private int _index;
        public List<FakeShardClient> CreatedShardClients { get; } = [];
        public string? SessionId => null;
        public event Func<object?, EventSubConnectedEventArgs, Task>? Connected;
        public event Func<object?, EventSubDisconnectedEventArgs, Task>? Disconnected;
        public event Func<object?, EventSubReconnectedEventArgs, Task>? Reconnected;
        public event Func<object?, EventSubErrorEventArgs, Task>? ErrorOccurred;
        public event Func<object?, EventSubStreamOnlineEventArgs, Task>? StreamOnline;
        public event Func<object?, EventSubStreamOfflineEventArgs, Task>? StreamOffline;

        public IEventSubShardClient CreateShardClient(string shardKey)
        {
            var client = new FakeShardClient(shardKey, sessionIds[_index++]);
            CreatedShardClients.Add(client);
            return client;
        }

        public Task<bool> ConnectAsync(Uri endpoint) => throw new NotSupportedException();
        public Task<bool> ReconnectAsync() => throw new NotSupportedException();
        public Task<bool> DisconnectAsync() => Task.FromResult(true);
    }

    private sealed class FakeShardClient(string shardKey, string connectSessionId) : IEventSubShardClient
    {
        public string ShardKey { get; } = shardKey;
        public string? SessionId { get; private set; }
        public EventSubShardConnectionState ConnectionState { get; private set; } = new(null, false, null, null, null);
        public event Func<object?, EventSubConnectedEventArgs, Task>? Connected;
        public event Func<object?, EventSubDisconnectedEventArgs, Task>? Disconnected;
        public event Func<object?, EventSubReconnectedEventArgs, Task>? Reconnected;
        public event Func<object?, EventSubErrorEventArgs, Task>? ErrorOccurred;
        public event Func<object?, EventSubShardDisabledEventArgs, Task>? ShardDisabled;
        public event Func<object?, EventSubStreamOnlineEventArgs, Task>? StreamOnline;
        public event Func<object?, EventSubStreamOfflineEventArgs, Task>? StreamOffline;

        public async Task<bool> ConnectAsync(Uri endpoint)
        {
            SessionId = connectSessionId;
            ConnectionState = new EventSubShardConnectionState(SessionId, true, DateTimeOffset.UtcNow, null, null);
            if (Connected is not null)
            {
                await Connected.Invoke(this, new EventSubConnectedEventArgs(false, SessionId));
            }
            return true;
        }

        public Task<bool> ReconnectAsync() => Task.FromResult(true);
        public Task<bool> DisconnectAsync() => Task.FromResult(true);

        public void MarkDisconnected()
        {
            ConnectionState = ConnectionState with { IsConnected = false, LastDisconnectedUtc = DateTimeOffset.UtcNow };
        }
    }

    private sealed class NoOpCoordinator : IEventSubConduitCoordinator
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReconcileAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoOpArchiveJobQueue : IArchiveJobQueue
    {
        public ValueTask EnqueueAsync(int archiveJobId, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<int> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class RateLimitedCoordinator : IEventSubConduitCoordinator
    {
        public int CallCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReconcileAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            if (CallCount == 1)
            {
                throw new TwitchHelixRateLimitException("rate limited", TimeSpan.FromMilliseconds(40));
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TestDatabase(IServiceProvider services, SqliteConnection connection) : IAsyncDisposable
    {
        public IServiceProvider Services { get; } = services;

        public async ValueTask DisposeAsync()
        {
            if (Services is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else if (Services is IDisposable disposable)
            {
                disposable.Dispose();
            }

            await connection.DisposeAsync();
        }
    }
}
#pragma warning restore CS0067
