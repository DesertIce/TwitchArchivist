using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TwitchArchivist.Models;
using TwitchArchivist.Persistence;
using TwitchArchivist.Persistence.Entities;
using TwitchArchivist.Services.Twitch;

namespace TwitchArchivist.UnitTests.Services.Twitch;

public class EventSubSubscriptionSynchronizerTests
{
    [Fact]
    public async Task EnsureSubscriptionsAsyncCreatesMissingSubscriptionsForEnabledChannelsInDirectMode()
    {
        await using var database = await CreateDatabaseAsync();

        await using (var scope = database.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
            dbContext.ChannelConfigurations.Add(new ChannelConfiguration
            {
                TwitchLogin = "seretuscumbia",
                OutputDirectory = Path.GetTempPath(),
                IsEnabled = true,
                CreatedUtc = DateTimeOffset.UtcNow,
                UpdatedUtc = DateTimeOffset.UtcNow
            });
            await dbContext.SaveChangesAsync();
        }

        var helixClient = new StubTwitchHelixClient();
        var synchronizer = CreateSynchronizer(database.Services, helixClient, transportMode: "websocket");

        await synchronizer.EnsureSubscriptionsAsync("session-123", CancellationToken.None);

        Assert.Equal(2, helixClient.CreatedWebsocketSubscriptions.Count);
        Assert.Contains(helixClient.CreatedWebsocketSubscriptions, x => x.SubscriptionType == "stream.online");
        Assert.Contains(helixClient.CreatedWebsocketSubscriptions, x => x.SubscriptionType == "stream.offline");
        Assert.Empty(helixClient.CreatedConduitSubscriptions);

        await using var verificationScope = database.Services.CreateAsyncScope();
        var verificationDbContext = verificationScope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        var channel = await verificationDbContext.ChannelConfigurations.SingleAsync();
        Assert.Equal("29430843", channel.TwitchUserId);

        var states = await verificationDbContext.EventSubscriptionStates
            .OrderBy(x => x.SubscriptionType)
            .ToListAsync();

        Assert.Collection(
            states,
            state =>
            {
                Assert.Equal("stream.offline", state.SubscriptionType);
                Assert.Equal("enabled", state.Status);
                Assert.Equal("sub-stream.offline", state.TwitchSubscriptionId);
            },
            state =>
            {
                Assert.Equal("stream.online", state.SubscriptionType);
                Assert.Equal("enabled", state.Status);
                Assert.Equal("sub-stream.online", state.TwitchSubscriptionId);
            });
    }

    [Fact]
    public async Task EnsureSubscriptionsAsyncCreatesNewSubscriptionsWhenExistingOnesBelongToDifferentSessionInDirectMode()
    {
        await using var database = await CreateDatabaseAsync();

        await using (var scope = database.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
            dbContext.ChannelConfigurations.Add(new ChannelConfiguration
            {
                TwitchLogin = "seretuscumbia",
                OutputDirectory = Path.GetTempPath(),
                IsEnabled = true,
                CreatedUtc = DateTimeOffset.UtcNow,
                UpdatedUtc = DateTimeOffset.UtcNow
            });
            await dbContext.SaveChangesAsync();
        }

        var helixClient = new StubTwitchHelixClient
        {
            ExistingSubscriptions =
            [
                new EventSubSubscriptionRecord("old-online", "stream.online", "enabled", "29430843", "old-session"),
                new EventSubSubscriptionRecord("old-offline", "stream.offline", "enabled", "29430843", "old-session")
            ]
        };
        var synchronizer = CreateSynchronizer(database.Services, helixClient, transportMode: "websocket");

        await synchronizer.EnsureSubscriptionsAsync("current-session", CancellationToken.None);

        Assert.Equal(2, helixClient.CreatedWebsocketSubscriptions.Count);
        Assert.All(helixClient.CreatedWebsocketSubscriptions, x => Assert.Equal("current-session", x.SessionId));
    }

    [Fact]
    public async Task EnsureSubscriptionsAsyncReusesConduitSubscriptionsAcrossReconnects()
    {
        await using var database = await CreateDatabaseAsync();

        await SeedConduitScenarioAsync(database.Services);

        var helixClient = new StubTwitchHelixClient
        {
            ExistingSubscriptions =
            [
                new EventSubSubscriptionRecord("sub-stream.online", "stream.online", "enabled", "29430843", null),
                new EventSubSubscriptionRecord("sub-stream.offline", "stream.offline", "enabled", "29430843", null)
            ]
        };
        var synchronizer = CreateSynchronizer(database.Services, helixClient, transportMode: "conduit-websocket");

        await synchronizer.EnsureSubscriptionsAsync("shard-session-a", CancellationToken.None);
        await synchronizer.EnsureSubscriptionsAsync("shard-session-b", CancellationToken.None);

        Assert.Empty(helixClient.CreatedConduitSubscriptions);
        Assert.Empty(helixClient.CreatedWebsocketSubscriptions);

        await using var scope = database.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        var bindings = await dbContext.EventSubSubscriptionBindings
            .OrderBy(x => x.SubscriptionType)
            .ToListAsync();

        Assert.Collection(
            bindings,
            binding => Assert.Equal("sub-stream.offline", binding.TwitchSubscriptionId),
            binding => Assert.Equal("sub-stream.online", binding.TwitchSubscriptionId));
    }

    [Fact]
    public async Task EnsureSubscriptionsAsyncCreatesMissingConduitSubscriptionsAndPersistsBindings()
    {
        await using var database = await CreateDatabaseAsync();

        await SeedConduitScenarioAsync(database.Services);

        var helixClient = new StubTwitchHelixClient();
        var synchronizer = CreateSynchronizer(database.Services, helixClient, transportMode: "conduit-websocket");

        await synchronizer.EnsureSubscriptionsAsync("ignored-shard-session", CancellationToken.None);

        Assert.Equal(2, helixClient.CreatedConduitSubscriptions.Count);
        Assert.Empty(helixClient.CreatedWebsocketSubscriptions);
        Assert.All(helixClient.CreatedConduitSubscriptions, x => Assert.Equal("conduit-1", x.ConduitId));

        await using var scope = database.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        var bindings = await dbContext.EventSubSubscriptionBindings
            .OrderBy(x => x.SubscriptionType)
            .ToListAsync();

        Assert.Collection(
            bindings,
            binding =>
            {
                Assert.Equal("stream.offline", binding.SubscriptionType);
                Assert.Equal("conduit-sub-stream.offline", binding.TwitchSubscriptionId);
                Assert.Equal("enabled", binding.Status);
            },
            binding =>
            {
                Assert.Equal("stream.online", binding.SubscriptionType);
                Assert.Equal("conduit-sub-stream.online", binding.TwitchSubscriptionId);
                Assert.Equal("enabled", binding.Status);
            });
    }

    [Fact]
    public async Task EnsureSubscriptionsAsyncMarksDisabledChannelBindingsStaleWithoutTouchingEnabledChannelBindings()
    {
        await using var database = await CreateDatabaseAsync();
        var now = DateTimeOffset.UtcNow;

        await using (var scope = database.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
            var conduit = new EventSubConduit
            {
                TwitchConduitId = "conduit-1",
                ShardCount = 2,
                CreatedUtc = now,
                UpdatedUtc = now
            };
            var enabledChannel = new ChannelConfiguration
            {
                TwitchLogin = "enabled-channel",
                TwitchUserId = "111",
                OutputDirectory = Path.GetTempPath(),
                IsEnabled = true,
                CreatedUtc = now,
                UpdatedUtc = now
            };
            var disabledChannel = new ChannelConfiguration
            {
                TwitchLogin = "disabled-channel",
                TwitchUserId = "222",
                OutputDirectory = Path.GetTempPath(),
                IsEnabled = false,
                CreatedUtc = now,
                UpdatedUtc = now
            };

            dbContext.AddRange(conduit, enabledChannel, disabledChannel);
            await dbContext.SaveChangesAsync();

            dbContext.EventSubSubscriptionBindings.AddRange(
                new EventSubSubscriptionBinding
                {
                    EventSubConduitId = conduit.Id,
                    ChannelConfigurationId = enabledChannel.Id,
                    SubscriptionType = "stream.online",
                    TwitchSubscriptionId = "enabled-online",
                    Status = "enabled",
                    CreatedUtc = now,
                    UpdatedUtc = now
                },
                new EventSubSubscriptionBinding
                {
                    EventSubConduitId = conduit.Id,
                    ChannelConfigurationId = disabledChannel.Id,
                    SubscriptionType = "stream.online",
                    TwitchSubscriptionId = "disabled-online",
                    Status = "enabled",
                    CreatedUtc = now,
                    UpdatedUtc = now
                });
            await dbContext.SaveChangesAsync();
        }

        var helixClient = new StubTwitchHelixClient
        {
            ExistingSubscriptions =
            [
                new EventSubSubscriptionRecord("enabled-online", "stream.online", "enabled", "111", null),
                new EventSubSubscriptionRecord("enabled-offline", "stream.offline", "enabled", "111", null)
            ]
        };
        var synchronizer = CreateSynchronizer(database.Services, helixClient, transportMode: "conduit-websocket");

        await synchronizer.EnsureSubscriptionsAsync("ignored", CancellationToken.None);

        await using var verificationScope = database.Services.CreateAsyncScope();
        var verificationDbContext = verificationScope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        var bindings = await verificationDbContext.EventSubSubscriptionBindings
            .OrderBy(x => x.ChannelConfigurationId)
            .ThenBy(x => x.SubscriptionType)
            .ToListAsync();

        Assert.Equal("disabled-local", bindings.Single(x => x.TwitchSubscriptionId == "disabled-online").Status);
        Assert.Equal("enabled", bindings.Single(x => x.TwitchSubscriptionId == "enabled-online").Status);
        Assert.Equal("enabled", bindings.Single(x => x.TwitchSubscriptionId == "enabled-offline").Status);
        Assert.Empty(helixClient.CreatedConduitSubscriptions);
    }

    private static EventSubSubscriptionSynchronizer CreateSynchronizer(
        IServiceProvider services,
        ITwitchHelixClient helixClient,
        string transportMode)
    {
        return new EventSubSubscriptionSynchronizer(
            helixClient,
            services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new TwitchOptions
            {
                EventSubTransportMode = transportMode
            }));
    }

    private static async Task SeedConduitScenarioAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        var now = DateTimeOffset.UtcNow;

        dbContext.EventSubConduits.Add(new EventSubConduit
        {
            TwitchConduitId = "conduit-1",
            ShardCount = 2,
            CreatedUtc = now,
            UpdatedUtc = now
        });
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

    private static async Task<TestDatabase> CreateDatabaseAsync()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"twitcharchivist-sync-{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddDbContext<TwitchArchivistDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
        var provider = services.BuildServiceProvider();

        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        await dbContext.Database.EnsureCreatedAsync();

        return new TestDatabase(provider, databasePath);
    }

    private sealed class StubTwitchHelixClient : ITwitchHelixClient
    {
        public IReadOnlyList<EventSubSubscriptionRecord> ExistingSubscriptions { get; init; } = [];
        public List<(string SubscriptionType, string BroadcasterUserId, string SessionId)> CreatedWebsocketSubscriptions { get; } = [];
        public List<(string SubscriptionType, string BroadcasterUserId, string ConduitId)> CreatedConduitSubscriptions { get; } = [];

        public Task<EventSubSubscriptionRecord> CreateStreamSubscriptionAsync(string subscriptionType, string broadcasterUserId, string sessionId, CancellationToken cancellationToken)
        {
            CreatedWebsocketSubscriptions.Add((subscriptionType, broadcasterUserId, sessionId));
            return Task.FromResult(new EventSubSubscriptionRecord($"sub-{subscriptionType}", subscriptionType, "enabled", broadcasterUserId, sessionId));
        }

        public Task<EventSubSubscriptionRecord> CreateConduitSubscriptionAsync(string subscriptionType, string broadcasterUserId, string conduitId, CancellationToken cancellationToken)
        {
            CreatedConduitSubscriptions.Add((subscriptionType, broadcasterUserId, conduitId));
            return Task.FromResult(new EventSubSubscriptionRecord($"conduit-sub-{subscriptionType}", subscriptionType, "enabled", broadcasterUserId, null));
        }

        public Task<IReadOnlyList<EventSubSubscriptionRecord>> GetEventSubscriptionsAsync(CancellationToken cancellationToken)
            => Task.FromResult(ExistingSubscriptions);

        public Task<ArchiveVodRecord?> GetLatestArchiveVodAsync(string broadcasterUserId, DateTimeOffset? createdAfterUtc, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<EventSubConduitRecord>> GetEventSubConduitsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<EventSubConduitRecord>>([]);

        public Task<EventSubConduitRecord> CreateEventSubConduitAsync(int shardCount, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<EventSubConduitRecord> UpdateEventSubConduitAsync(string conduitId, int shardCount, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<EventSubConduitShardRecord>> UpdateEventSubConduitShardsAsync(string conduitId, IReadOnlyList<EventSubConduitShardRecord> shards, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<TwitchLiveStreamState>> GetLiveStreamsByLoginsAsync(IReadOnlyList<string> twitchLogins, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<string?> ResolveUserIdAsync(string twitchLogin, CancellationToken cancellationToken)
            => Task.FromResult<string?>("29430843");

        public Task<IReadOnlyList<TwitchChannelSearchResult>> SearchChannelsAsync(string query, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class TestDatabase(IServiceProvider services, string databasePath) : IAsyncDisposable
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

            TryDelete(databasePath);
            TryDelete($"{databasePath}-wal");
            TryDelete($"{databasePath}-shm");
        }
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
}
