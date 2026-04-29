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
public class TwitchEventSubConduitHostedServiceTests
{
    [Fact]
    public async Task ConduitShardStreamOfflineCreatesArchiveJob()
    {
        await using var database = await CreateDatabaseAsync();
        var now = DateTimeOffset.UtcNow;

        await using (var scope = database.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
            dbContext.ChannelConfigurations.Add(new ChannelConfiguration
            {
                TwitchLogin = "seretuscumbia",
                TwitchUserId = "29430843",
                OutputDirectory = Path.GetTempPath(),
                IsEnabled = true,
                CreatedUtc = now,
                UpdatedUtc = now
            });
            await dbContext.SaveChangesAsync();
        }

        var helixClient = new RecordingHelixClient();
        var websocketFactory = new FakeEventSubWebsocketClient(["session-0"]);
        var coordinator = CreateCoordinator(database.Services, helixClient, websocketFactory, shardCount: 1);

        await coordinator.StartAsync(CancellationToken.None);
        var shardClient = Assert.Single(websocketFactory.CreatedShardClients);

        await shardClient.TriggerStreamOfflineAsync("seretuscumbia", "29430843");

        await using var verificationScope = database.Services.CreateAsyncScope();
        var verificationDbContext = verificationScope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        var jobs = await verificationDbContext.ArchiveJobs.ToListAsync();

        Assert.Single(jobs);
    }

    [Fact]
    public async Task StartAsyncCreatesConduitAssignmentsAndPersistsShardState()
    {
        await using var database = await CreateDatabaseAsync();
        var helixClient = new RecordingHelixClient();
        var websocketFactory = new FakeEventSubWebsocketClient(["session-0", "session-1"]);
        var coordinator = CreateCoordinator(database.Services, helixClient, websocketFactory, shardCount: 2);
        var service = new TwitchEventSubConduitHostedService(
            coordinator,
            CreateCleanupService(database.Services, helixClient),
            new CountingSubscriptionSynchronizer(),
            NullLogger<TwitchEventSubConduitHostedService>.Instance,
            Options.Create(new TwitchOptions
            {
                EventSubTransportMode = "conduit-websocket",
                EventSubConduitReconcileIntervalSeconds = 60
            }));

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(1, helixClient.CreateConduitCallCount);
        Assert.Equal(0, helixClient.UpdateConduitCallCount);
        Assert.Equal(["session-0", "session-1"], helixClient.AssignedShards.OrderBy(x => x.ShardId).Select(x => x.TransportSessionId).ToArray());

        await using var scope = database.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        var conduit = await dbContext.EventSubConduits.Include(x => x.Shards).SingleAsync();

        Assert.Equal("conduit-1", conduit.TwitchConduitId);
        Assert.Equal(2, conduit.ShardCount);
        Assert.Collection(
            conduit.Shards.OrderBy(x => x.ShardId),
            shard =>
            {
                Assert.Equal(0, shard.ShardId);
                Assert.Equal("session-0", shard.TransportSessionId);
                Assert.Equal("enabled", shard.Status);
                Assert.NotNull(shard.LastWelcomeUtc);
                Assert.NotNull(shard.LastAssignmentUtc);
            },
            shard =>
            {
                Assert.Equal(1, shard.ShardId);
                Assert.Equal("session-1", shard.TransportSessionId);
                Assert.Equal("enabled", shard.Status);
                Assert.NotNull(shard.LastWelcomeUtc);
                Assert.NotNull(shard.LastAssignmentUtc);
            });
    }

    [Fact]
    public async Task StartAsyncScalesExistingConduitToConfiguredShardCount()
    {
        await using var database = await CreateDatabaseAsync();
        await SeedConduitAsync(database.Services, "conduit-1", shardCount: 1);
        var helixClient = new RecordingHelixClient(existingRemoteShardCount: 1);
        var websocketFactory = new FakeEventSubWebsocketClient(["session-0", "session-1", "session-2"]);
        var coordinator = CreateCoordinator(database.Services, helixClient, websocketFactory, shardCount: 3);
        var service = new TwitchEventSubConduitHostedService(
            coordinator,
            CreateCleanupService(database.Services, helixClient),
            new CountingSubscriptionSynchronizer(),
            NullLogger<TwitchEventSubConduitHostedService>.Instance,
            Options.Create(new TwitchOptions
            {
                EventSubTransportMode = "conduit-websocket",
                EventSubConduitReconcileIntervalSeconds = 60
            }));

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(0, helixClient.CreateConduitCallCount);
        Assert.Equal(1, helixClient.UpdateConduitCallCount);
        Assert.Equal(3, helixClient.LastUpdatedShardCount);

        await using var scope = database.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        var conduit = await dbContext.EventSubConduits.Include(x => x.Shards).SingleAsync();

        Assert.Equal(3, conduit.ShardCount);
        Assert.Equal(3, conduit.Shards.Count);
    }

    [Fact]
    public async Task StartAsyncEnsuresConduitSubscriptionsForEnabledChannels()
    {
        await using var database = await CreateDatabaseAsync();
        var now = DateTimeOffset.UtcNow;

        await using (var scope = database.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
            dbContext.ChannelConfigurations.Add(new ChannelConfiguration
            {
                TwitchLogin = "seretuscumbia",
                TwitchUserId = "29430843",
                OutputDirectory = Path.GetTempPath(),
                IsEnabled = true,
                CreatedUtc = now,
                UpdatedUtc = now
            });
            await dbContext.SaveChangesAsync();
        }

        var helixClient = new RecordingHelixClient();
        var websocketFactory = new FakeEventSubWebsocketClient(["session-0"]);
        var subscriptionSynchronizer = new CountingSubscriptionSynchronizer();
        var coordinator = CreateCoordinator(database.Services, helixClient, websocketFactory, shardCount: 1);
        var service = new TwitchEventSubConduitHostedService(
            coordinator,
            CreateCleanupService(database.Services, helixClient),
            subscriptionSynchronizer,
            NullLogger<TwitchEventSubConduitHostedService>.Instance,
            Options.Create(new TwitchOptions
            {
                EventSubTransportMode = "conduit-websocket",
                EventSubConduitReconcileIntervalSeconds = 60
            }));

        await service.StartAsync(CancellationToken.None);

        Assert.True(subscriptionSynchronizer.CallCount >= 1);
        Assert.Contains(string.Empty, subscriptionSynchronizer.SessionIds);
    }

    [Fact]
    public async Task BackgroundLoopContinuesAfterTransientSubscriptionSyncFailure()
    {
        await using var database = await CreateDatabaseAsync();
        var helixClient = new RecordingHelixClient();
        var websocketFactory = new FakeEventSubWebsocketClient(["session-0"]);
        var subscriptionSynchronizer = new FlakySubscriptionSynchronizer();
        var coordinator = CreateCoordinator(database.Services, helixClient, websocketFactory, shardCount: 1);
        var service = new TwitchEventSubConduitHostedService(
            coordinator,
            CreateCleanupService(database.Services, helixClient),
            subscriptionSynchronizer,
            NullLogger<TwitchEventSubConduitHostedService>.Instance,
            Options.Create(new TwitchOptions
            {
                EventSubTransportMode = "conduit-websocket",
                EventSubConduitReconcileIntervalSeconds = 1
            }));

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(1200);
        await service.StopAsync(CancellationToken.None);

        Assert.True(subscriptionSynchronizer.CallCount >= 2);
    }

    private static EventSubConduitCoordinator CreateCoordinator(
        IServiceProvider services,
        ITwitchHelixClient helixClient,
        IEventSubWebsocketClient websocketClient,
        int shardCount)
    {
        return new EventSubConduitCoordinator(
            helixClient,
            websocketClient,
            CreateNotificationProcessor(services),
            services.GetRequiredService<IServiceScopeFactory>(),
            new RuntimeStatusStore(),
            Options.Create(new TwitchOptions
            {
                EventSubConduitShardCount = shardCount,
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

    private static EventSubConduitCleanupService CreateCleanupService(IServiceProvider services, ITwitchHelixClient helixClient)
        => new(
            helixClient,
            new NoOpCoordinator(),
            services.GetRequiredService<IServiceScopeFactory>(),
            new RuntimeStatusStore(),
            Options.Create(new TwitchOptions
            {
                EventSubTransportMode = "conduit-websocket",
                EventSubRetryBaseDelaySeconds = 1
            }),
            NullLogger<EventSubConduitCleanupService>.Instance,
            TimeProvider.System);

    private static async Task SeedConduitAsync(IServiceProvider services, string conduitId, int shardCount)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        dbContext.EventSubConduits.Add(new EventSubConduit
        {
            TwitchConduitId = conduitId,
            ShardCount = shardCount,
            CreatedUtc = DateTimeOffset.UtcNow,
            UpdatedUtc = DateTimeOffset.UtcNow
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

    private sealed class RecordingHelixClient(int existingRemoteShardCount = 0) : ITwitchHelixClient
    {
        public int CreateConduitCallCount { get; private set; }
        public int UpdateConduitCallCount { get; private set; }
        public int LastUpdatedShardCount { get; private set; }
        public List<EventSubConduitShardRecord> AssignedShards { get; } = [];

        public Task<string?> ResolveUserIdAsync(string twitchLogin, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<TwitchChannelSearchResult>> SearchChannelsAsync(string query, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<TwitchLiveStreamState>> GetLiveStreamsByLoginsAsync(IReadOnlyList<string> twitchLogins, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ArchiveVodRecord?> GetLatestArchiveVodAsync(string broadcasterUserId, DateTimeOffset? createdAfterUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<EventSubSubscriptionRecord>> GetEventSubscriptionsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteEventSubscriptionAsync(string subscriptionId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<EventSubConduitRecord>> GetEventSubConduitsAsync(CancellationToken cancellationToken)
        {
            IReadOnlyList<EventSubConduitRecord> result = existingRemoteShardCount > 0
                ? [new EventSubConduitRecord("conduit-1", existingRemoteShardCount, [])]
                : [];
            return Task.FromResult(result);
        }

        public Task<EventSubConduitRecord> CreateEventSubConduitAsync(int shardCount, CancellationToken cancellationToken)
        {
            CreateConduitCallCount++;
            return Task.FromResult(new EventSubConduitRecord("conduit-1", shardCount, []));
        }

        public Task<EventSubConduitRecord> UpdateEventSubConduitAsync(string conduitId, int shardCount, CancellationToken cancellationToken)
        {
            UpdateConduitCallCount++;
            LastUpdatedShardCount = shardCount;
            return Task.FromResult(new EventSubConduitRecord(conduitId, shardCount, []));
        }

        public Task<IReadOnlyList<EventSubConduitShardRecord>> UpdateEventSubConduitShardsAsync(
            string conduitId,
            IReadOnlyList<EventSubConduitShardRecord> shards,
            CancellationToken cancellationToken)
        {
            AssignedShards.Clear();
            AssignedShards.AddRange(shards.Select(x => new EventSubConduitShardRecord(x.ShardId, "enabled", x.TransportSessionId)));
            return Task.FromResult<IReadOnlyList<EventSubConduitShardRecord>>(AssignedShards);
        }

        public Task<EventSubSubscriptionRecord> CreateStreamSubscriptionAsync(string subscriptionType, string broadcasterUserId, string sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EventSubSubscriptionRecord> CreateConduitSubscriptionAsync(string subscriptionType, string broadcasterUserId, string conduitId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class NoOpCoordinator : IEventSubConduitCoordinator
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReconcileAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeEventSubWebsocketClient(IReadOnlyList<string> sessionIds) : IEventSubWebsocketClient
    {
        private int _createIndex;
        public List<FakeEventSubShardClient> CreatedShardClients { get; } = [];

        public string? SessionId => null;
        public event Func<object?, EventSubConnectedEventArgs, Task>? Connected;
        public event Func<object?, EventSubDisconnectedEventArgs, Task>? Disconnected;
        public event Func<object?, EventSubReconnectedEventArgs, Task>? Reconnected;
        public event Func<object?, EventSubErrorEventArgs, Task>? ErrorOccurred;
        public event Func<object?, EventSubShardDisabledEventArgs, Task>? ShardDisabled;
        public event Func<object?, EventSubStreamOnlineEventArgs, Task>? StreamOnline;
        public event Func<object?, EventSubStreamOfflineEventArgs, Task>? StreamOffline;

        public IEventSubShardClient CreateShardClient(string shardKey)
        {
            var sessionId = sessionIds[_createIndex++];
            var shardClient = new FakeEventSubShardClient(shardKey, sessionId);
            CreatedShardClients.Add(shardClient);
            return shardClient;
        }

        public Task<bool> ConnectAsync(Uri endpoint) => throw new NotSupportedException();
        public Task<bool> ReconnectAsync() => throw new NotSupportedException();
        public Task<bool> DisconnectAsync() => Task.FromResult(true);
    }

    private sealed class FakeEventSubShardClient(string shardKey, string sessionId) : IEventSubShardClient
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
            SessionId = sessionId;
            ConnectionState = new EventSubShardConnectionState(SessionId, true, DateTimeOffset.UtcNow, null, null);
            if (Connected is not null)
            {
                await Connected.Invoke(this, new EventSubConnectedEventArgs(false, SessionId));
            }

            return true;
        }

        public Task<bool> ReconnectAsync() => Task.FromResult(true);
        public Task<bool> DisconnectAsync() => Task.FromResult(true);

        public async Task TriggerStreamOfflineAsync(string broadcasterUserLogin, string broadcasterUserId)
        {
            if (StreamOffline is not null)
            {
                await StreamOffline.Invoke(this, new EventSubStreamOfflineEventArgs(broadcasterUserLogin, broadcasterUserId));
            }
        }
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

    private sealed class NoOpArchiveJobQueue : IArchiveJobQueue
    {
        public ValueTask EnqueueAsync(int archiveJobId, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<int> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class CountingSubscriptionSynchronizer : IEventSubSubscriptionSynchronizer
    {
        public int CallCount { get; private set; }
        public List<string> SessionIds { get; } = [];

        public Task EnsureSubscriptionsAsync(string sessionId, CancellationToken cancellationToken)
        {
            CallCount++;
            SessionIds.Add(sessionId);
            return Task.CompletedTask;
        }
    }

    private sealed class FlakySubscriptionSynchronizer : IEventSubSubscriptionSynchronizer
    {
        public int CallCount { get; private set; }

        public Task EnsureSubscriptionsAsync(string sessionId, CancellationToken cancellationToken)
        {
            CallCount++;
            if (CallCount == 1)
            {
                throw new InvalidOperationException("boom");
            }

            return Task.CompletedTask;
        }
    }
}
#pragma warning restore CS0067
