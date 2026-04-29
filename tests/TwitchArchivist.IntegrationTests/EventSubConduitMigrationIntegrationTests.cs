using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using TwitchArchivist.Persistence;
using TwitchArchivist.Persistence.Entities;
using TwitchArchivist.Services.Twitch;

namespace TwitchArchivist.IntegrationTests;

#pragma warning disable CS0067
public class EventSubConduitMigrationIntegrationTests
{
    [Fact]
    public async Task DirectWebsocketModeStillCreatesSessionBoundSubscriptions()
    {
        var helixClient = new RecordingHelixClient();
        var websocketClient = new RecordingEventSubWebsocketClient(["unused-session"]);

        await using var factory = CreateFactory(
            helixClient,
            websocketClient,
            configurationOverrides: new Dictionary<string, string?>
            {
                ["Twitch:EventSubTransportMode"] = "websocket"
            });

        await InitializeDatabaseAsync(factory.Services);
        await SeedChannelAsync(factory.Services, "alpha", "user-123");

        var synchronizer = factory.Services.GetRequiredService<IEventSubSubscriptionSynchronizer>();
        await synchronizer.EnsureSubscriptionsAsync("session-direct", CancellationToken.None);

        Assert.Equal(2, helixClient.CreatedDirectSubscriptions.Count);
        Assert.Empty(helixClient.CreatedConduitSubscriptions);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        Assert.Equal(2, dbContext.EventSubscriptionStates.Count());
        Assert.All(dbContext.EventSubscriptionStates, x => Assert.Equal("session-direct", x.TransportSessionId));
        Assert.Empty(dbContext.EventSubSubscriptionBindings);
    }

    [Fact]
    public async Task ConduitModeStartsAndExposesShardDiagnostics()
    {
        var helixClient = new RecordingHelixClient();
        var websocketClient = new RecordingEventSubWebsocketClient(["session-0", "session-1"]);

        await using var factory = CreateFactory(
            helixClient,
            websocketClient,
            configurationOverrides: new Dictionary<string, string?>
            {
                ["Twitch:EventSubTransportMode"] = "conduit-websocket",
                ["Twitch:EventSubConduitShardCount"] = "2"
            });

        await InitializeDatabaseAsync(factory.Services);
        var coordinator = factory.Services.GetRequiredService<IEventSubConduitCoordinator>();
        await coordinator.StartAsync(CancellationToken.None);

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, helixClient.CreatedConduitCount);
        Assert.Single(helixClient.AssignmentHistory);

        await using var payload = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(payload);
        var root = document.RootElement;

        Assert.Equal("conduit-websocket", root.GetProperty("eventSubTransportMode").GetString());
        Assert.Equal("conduit-1", root.GetProperty("eventSubConduitId").GetString());
        Assert.Equal(2, root.GetProperty("eventSubConfiguredShardCount").GetInt32());
        Assert.Equal(2, root.GetProperty("eventSubActiveShardCount").GetInt32());
        Assert.Equal(0, root.GetProperty("eventSubDisabledShardCount").GetInt32());
    }

    [Fact]
    public async Task RestartInConduitModePreservesSubscriptionOwnership()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "TwitchArchivist.IntegrationTests", $"{Guid.NewGuid():N}.db");
        var helixClient = new RecordingHelixClient();

        await using (var firstFactory = CreateFactory(
            helixClient,
            new RecordingEventSubWebsocketClient(["session-a"]),
            databasePath,
            new Dictionary<string, string?>
            {
                ["Twitch:EventSubTransportMode"] = "conduit-websocket",
                ["Twitch:EventSubConduitShardCount"] = "1"
            }))
        {
            await InitializeDatabaseAsync(firstFactory.Services);
            var coordinator = firstFactory.Services.GetRequiredService<IEventSubConduitCoordinator>();
            await coordinator.StartAsync(CancellationToken.None);
            await SeedChannelAsync(firstFactory.Services, "alpha", "user-123");

            var synchronizer = firstFactory.Services.GetRequiredService<IEventSubSubscriptionSynchronizer>();
            await synchronizer.EnsureSubscriptionsAsync(string.Empty, CancellationToken.None);
        }

        Assert.Equal(2, helixClient.CreatedConduitSubscriptions.Count);

        await using (var secondFactory = CreateFactory(
            helixClient,
            new RecordingEventSubWebsocketClient(["session-b"]),
            databasePath,
            new Dictionary<string, string?>
            {
                ["Twitch:EventSubTransportMode"] = "conduit-websocket",
                ["Twitch:EventSubConduitShardCount"] = "1"
            }))
        {
            await InitializeDatabaseAsync(secondFactory.Services);
            var coordinator = secondFactory.Services.GetRequiredService<IEventSubConduitCoordinator>();
            await coordinator.StartAsync(CancellationToken.None);
            var synchronizer = secondFactory.Services.GetRequiredService<IEventSubSubscriptionSynchronizer>();
            await synchronizer.EnsureSubscriptionsAsync(string.Empty, CancellationToken.None);

            using var scope = secondFactory.Services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
            Assert.Equal(2, dbContext.EventSubSubscriptionBindings.Count());
        }

        Assert.Equal(2, helixClient.CreatedConduitSubscriptions.Count);
        Assert.Equal(2, helixClient.AssignmentHistory.Count);
        Assert.Equal("session-b", helixClient.AssignmentHistory.Last().Single().TransportSessionId);

        DeleteDatabaseFiles(databasePath);
    }

    [Fact]
    public async Task ReconnectInConduitModeReassignsShardWithoutRecreatingSubscriptions()
    {
        var helixClient = new RecordingHelixClient();
        var websocketClient = new RecordingEventSubWebsocketClient(["session-0", "session-1"]);

        await using var factory = CreateFactory(
            helixClient,
            websocketClient,
            configurationOverrides: new Dictionary<string, string?>
            {
                ["Twitch:EventSubTransportMode"] = "conduit-websocket",
                ["Twitch:EventSubConduitShardCount"] = "1"
            });

        await InitializeDatabaseAsync(factory.Services);
        var coordinator = factory.Services.GetRequiredService<IEventSubConduitCoordinator>();
        await coordinator.StartAsync(CancellationToken.None);
        await SeedChannelAsync(factory.Services, "alpha", "user-123");

        var synchronizer = factory.Services.GetRequiredService<IEventSubSubscriptionSynchronizer>();
        await synchronizer.EnsureSubscriptionsAsync(string.Empty, CancellationToken.None);
        Assert.Equal(2, helixClient.CreatedConduitSubscriptions.Count);

        websocketClient.CreatedShardClients.Single().MarkDisconnected();

        var cleanupService = factory.Services.GetRequiredService<EventSubConduitCleanupService>();
        await cleanupService.RunOnceAsync(CancellationToken.None);
        await synchronizer.EnsureSubscriptionsAsync(string.Empty, CancellationToken.None);

        Assert.Equal(2, helixClient.CreatedConduitSubscriptions.Count);
        Assert.Equal(2, websocketClient.CreatedShardClients.Count);
        Assert.Equal(2, helixClient.AssignmentHistory.Count);
        Assert.Equal("session-1", helixClient.AssignmentHistory.Last().Single().TransportSessionId);
    }

    private static IntegrationTestWebApplicationFactory CreateFactory(
        RecordingHelixClient helixClient,
        RecordingEventSubWebsocketClient websocketClient,
        string? databasePath = null,
        IReadOnlyDictionary<string, string?>? configurationOverrides = null)
    {
        var resolvedDatabasePath = string.IsNullOrWhiteSpace(databasePath)
            ? Path.Combine(Path.GetTempPath(), "TwitchArchivist.IntegrationTests", $"{Guid.NewGuid():N}.db")
            : databasePath;

        return new IntegrationTestWebApplicationFactory(
            builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IHostedService>();
                    services.RemoveAll<DbContextOptions<TwitchArchivistDbContext>>();
                    services.RemoveAll<TwitchArchivistDbContext>();
                    services.RemoveAll<ITwitchHelixClient>();
                    services.RemoveAll<IEventSubWebsocketClient>();
                    services.RemoveAll<ITwitchAccessTokenProvider>();
                    services.RemoveAll<ITwitchLiveStateSynchronizer>();

                    Directory.CreateDirectory(Path.GetDirectoryName(resolvedDatabasePath)!);
                    services.AddDbContext<TwitchArchivistDbContext>(options => options.UseSqlite($"Data Source={resolvedDatabasePath}"));
                    services.AddSingleton<ITwitchHelixClient>(helixClient);
                    services.AddSingleton<IEventSubWebsocketClient>(websocketClient);
                    services.AddSingleton<ITwitchAccessTokenProvider>(new StubAccessTokenProvider());
                    services.AddSingleton<ITwitchLiveStateSynchronizer>(new NoOpLiveStateSynchronizer());
                });
            },
            resolvedDatabasePath,
            configurationOverrides);
    }

    private static async Task InitializeDatabaseAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        await dbContext.Database.MigrateAsync();
        scope.ServiceProvider.GetRequiredService<Services.RuntimeStatusStore>().MarkDatabaseReady();
    }

    private static async Task SeedChannelAsync(IServiceProvider services, string login, string userId)
    {
        using var scope = services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        if (await dbContext.ChannelConfigurations.AnyAsync(x => x.TwitchLogin == login))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        dbContext.ChannelConfigurations.Add(new ChannelConfiguration
        {
            TwitchLogin = login,
            TwitchUserId = userId,
            OutputDirectory = Path.Combine(Path.GetTempPath(), "TwitchArchivistTests", login),
            IsEnabled = true,
            CreatedUtc = now,
            UpdatedUtc = now
        });
        await dbContext.SaveChangesAsync();
    }

    private static void DeleteDatabaseFiles(string databasePath)
    {
        TryDelete(databasePath);
        TryDelete($"{databasePath}-wal");
        TryDelete($"{databasePath}-shm");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class RecordingHelixClient : ITwitchHelixClient
    {
        private EventSubConduitRecord? _conduit;
        private readonly List<EventSubSubscriptionRecord> _subscriptions = [];

        public int CreatedConduitCount { get; private set; }
        public List<EventSubConduitShardRecord[]> AssignmentHistory { get; } = [];
        public List<EventSubSubscriptionRecord> CreatedDirectSubscriptions { get; } = [];
        public List<EventSubSubscriptionRecord> CreatedConduitSubscriptions { get; } = [];

        public Task<string?> ResolveUserIdAsync(string twitchLogin, CancellationToken cancellationToken)
            => Task.FromResult<string?>($"{twitchLogin}-resolved");

        public Task<IReadOnlyList<TwitchChannelSearchResult>> SearchChannelsAsync(string query, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<TwitchChannelSearchResult>>([]);

        public Task<IReadOnlyList<TwitchLiveStreamState>> GetLiveStreamsByLoginsAsync(IReadOnlyList<string> twitchLogins, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<TwitchLiveStreamState>>([]);

        public Task<ArchiveVodRecord?> GetLatestArchiveVodAsync(string broadcasterUserId, DateTimeOffset? createdAfterUtc, CancellationToken cancellationToken)
            => Task.FromResult<ArchiveVodRecord?>(null);

        public Task<IReadOnlyList<EventSubSubscriptionRecord>> GetEventSubscriptionsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<EventSubSubscriptionRecord>>(_subscriptions.ToArray());

        public Task DeleteEventSubscriptionAsync(string subscriptionId, CancellationToken cancellationToken)
        {
            _subscriptions.RemoveAll(x => string.Equals(x.Id, subscriptionId, StringComparison.Ordinal));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<EventSubConduitRecord>> GetEventSubConduitsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<EventSubConduitRecord>>(_conduit is null ? [] : [_conduit]);

        public Task<EventSubConduitRecord> CreateEventSubConduitAsync(int shardCount, CancellationToken cancellationToken)
        {
            CreatedConduitCount++;
            _conduit = new EventSubConduitRecord("conduit-1", shardCount, []);
            return Task.FromResult(_conduit);
        }

        public Task<EventSubConduitRecord> UpdateEventSubConduitAsync(string conduitId, int shardCount, CancellationToken cancellationToken)
        {
            _conduit = new EventSubConduitRecord(conduitId, shardCount, _conduit?.Shards ?? []);
            return Task.FromResult(_conduit);
        }

        public Task<IReadOnlyList<EventSubConduitShardRecord>> UpdateEventSubConduitShardsAsync(
            string conduitId,
            IReadOnlyList<EventSubConduitShardRecord> shards,
            CancellationToken cancellationToken)
        {
            var assignedShards = shards
                .Select(x => new EventSubConduitShardRecord(x.ShardId, "enabled", x.TransportSessionId))
                .ToArray();
            AssignmentHistory.Add(assignedShards);
            _conduit = new EventSubConduitRecord(conduitId, assignedShards.Length, assignedShards);
            return Task.FromResult<IReadOnlyList<EventSubConduitShardRecord>>(assignedShards);
        }

        public Task<EventSubSubscriptionRecord> CreateStreamSubscriptionAsync(
            string subscriptionType,
            string broadcasterUserId,
            string sessionId,
            CancellationToken cancellationToken)
        {
            var subscription = new EventSubSubscriptionRecord(
                $"direct-{CreatedDirectSubscriptions.Count + 1}",
                subscriptionType,
                "enabled",
                broadcasterUserId,
                sessionId,
                null);
            CreatedDirectSubscriptions.Add(subscription);
            _subscriptions.Add(subscription);
            return Task.FromResult(subscription);
        }

        public Task<EventSubSubscriptionRecord> CreateConduitSubscriptionAsync(
            string subscriptionType,
            string broadcasterUserId,
            string conduitId,
            CancellationToken cancellationToken)
        {
            var subscription = new EventSubSubscriptionRecord(
                $"conduit-{CreatedConduitSubscriptions.Count + 1}",
                subscriptionType,
                "enabled",
                broadcasterUserId,
                null,
                conduitId);
            CreatedConduitSubscriptions.Add(subscription);
            _subscriptions.Add(subscription);
            return Task.FromResult(subscription);
        }
    }

    private sealed class RecordingEventSubWebsocketClient(IReadOnlyList<string> sessionIds) : IEventSubWebsocketClient
    {
        private int _sessionIndex;

        public List<FakeShardClient> CreatedShardClients { get; } = [];
        public string? SessionId { get; private set; }

        public event Func<object?, EventSubConnectedEventArgs, Task>? Connected;
        public event Func<object?, EventSubDisconnectedEventArgs, Task>? Disconnected;
        public event Func<object?, EventSubReconnectedEventArgs, Task>? Reconnected;
        public event Func<object?, EventSubErrorEventArgs, Task>? ErrorOccurred;
        public event Func<object?, EventSubStreamOnlineEventArgs, Task>? StreamOnline;
        public event Func<object?, EventSubStreamOfflineEventArgs, Task>? StreamOffline;

        public IEventSubShardClient CreateShardClient(string shardKey)
        {
            if (_sessionIndex >= sessionIds.Count)
            {
                throw new InvalidOperationException("No more fake EventSub shard sessions are available.");
            }

            var shardClient = new FakeShardClient(shardKey, sessionIds[_sessionIndex++]);
            CreatedShardClients.Add(shardClient);
            return shardClient;
        }

        public async Task<bool> ConnectAsync(Uri endpoint)
        {
            SessionId = sessionIds.Count > 0 ? sessionIds[0] : "session-direct";
            if (Connected is not null)
            {
                await Connected.Invoke(this, new EventSubConnectedEventArgs(false, SessionId));
            }

            return true;
        }

        public Task<bool> ReconnectAsync() => Task.FromResult(true);

        public Task<bool> DisconnectAsync()
        {
            SessionId = null;
            return Task.FromResult(true);
        }
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

        public Task<bool> DisconnectAsync()
        {
            ConnectionState = ConnectionState with { IsConnected = false, LastDisconnectedUtc = DateTimeOffset.UtcNow };
            return Task.FromResult(true);
        }

        public void MarkDisconnected()
        {
            ConnectionState = ConnectionState with { IsConnected = false, LastDisconnectedUtc = DateTimeOffset.UtcNow };
        }
    }

    private sealed class StubAccessTokenProvider : ITwitchAccessTokenProvider
    {
        private static readonly TwitchUserAuthorizationState MissingAuthorization = new(
            false,
            false,
            "missing",
            "not configured",
            false,
            null,
            null,
            null,
            DateTimeOffset.UtcNow);

        public string? BuildUserAuthorizationUrl(string state, string redirectUri) => null;

        public Task ExchangeAuthorizationCodeAsync(string code, string redirectUri, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string?> GetAppAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult<string?>("app-token");

        public Task<string?> GetUserAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<TwitchUserAuthorizationState> GetUserAuthorizationStateAsync(CancellationToken cancellationToken)
            => Task.FromResult(MissingAuthorization);

        public Task<TwitchUserAuthorizationState> ValidateUserAuthorizationAsync(CancellationToken cancellationToken)
            => Task.FromResult(MissingAuthorization);
    }

    private sealed class NoOpLiveStateSynchronizer : ITwitchLiveStateSynchronizer
    {
        public Task SynchronizeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
#pragma warning restore CS0067
