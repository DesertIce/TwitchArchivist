using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TwitchArchivist.Models;
using TwitchArchivist.Persistence;
using TwitchArchivist.Persistence.Entities;
using TwitchArchivist.Services;
using TwitchArchivist.Services.Twitch;

namespace TwitchArchivist.UnitTests.Services.Twitch;

#pragma warning disable CS0067
public class TwitchEventSubHostedServiceTests
{
    [Fact]
    public async Task StreamOfflineCreatesArchiveJobAndQueuesIt()
    {
        await using var database = await CreateDatabaseAsync();
        await SeedChannelAsync(database.Services, "k3lsb3lls", "1234");
        var websocketClient = new FakeEventSubWebsocketClient();
        var archiveJobQueue = new RecordingArchiveJobQueue();
        var logger = new ListLogger<TwitchEventSubHostedService>();
        var service = CreateService(
            database.Services,
            websocketClient,
            archiveJobQueue: archiveJobQueue,
            subscriptionSynchronizer: new CountingSubscriptionSynchronizer(),
            logger: logger);

        await service.StartAsync(CancellationToken.None);
        await websocketClient.WaitForConnectCountAsync(1, TimeSpan.FromSeconds(3));

        await websocketClient.TriggerStreamOfflineAsync("k3lsb3lls", "1234");

        await using var scope = database.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        var jobs = await dbContext.ArchiveJobs.ToListAsync();
        Assert.True(jobs.Count == 1, string.Join(Environment.NewLine, logger.Messages));
        var job = jobs.Single();

        await service.StopAsync(CancellationToken.None);

        Assert.Equal("stream.offline", job.TriggerSource);
        Assert.Equal(ArchiveJobStatus.Pending, job.Status);
        Assert.Equal("1234", (await dbContext.ChannelConfigurations.SingleAsync()).TwitchUserId);
        Assert.Equal([job.Id], archiveJobQueue.EnqueuedIds);
    }

    [Fact]
    public async Task StreamOfflineDoesNotCreateDuplicateArchiveJobWhenRecentPendingJobExists()
    {
        await using var database = await CreateDatabaseAsync();
        var channelId = await SeedChannelAsync(database.Services, "k3lsb3lls", "1234");
        await SeedArchiveJobAsync(database.Services, channelId, ArchiveJobStatus.Pending, DateTimeOffset.UtcNow.AddMinutes(-5));
        var websocketClient = new FakeEventSubWebsocketClient();
        var archiveJobQueue = new RecordingArchiveJobQueue();
        var service = CreateService(
            database.Services,
            websocketClient,
            archiveJobQueue: archiveJobQueue,
            subscriptionSynchronizer: new CountingSubscriptionSynchronizer());

        await service.StartAsync(CancellationToken.None);
        await websocketClient.WaitForConnectCountAsync(1, TimeSpan.FromSeconds(3));

        await websocketClient.TriggerStreamOfflineAsync("k3lsb3lls", "1234");

        await using var scope = database.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        var jobs = await dbContext.ArchiveJobs.OrderBy(x => x.Id).ToListAsync();

        await service.StopAsync(CancellationToken.None);

        Assert.Single(jobs);
        Assert.Empty(archiveJobQueue.EnqueuedIds);
    }

    [Fact]
    public async Task ServiceUsesReconnectPathAfterDisconnectEvent()
    {
        await using var database = await CreateDatabaseAsync();
        var websocketClient = new FakeEventSubWebsocketClient();
        var subscriptionSynchronizer = new EventSubSubscriptionSynchronizer(
            new StubTwitchHelixClient(),
            database.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EventSubSubscriptionSynchronizer>.Instance,
            Options.Create(new TwitchOptions()));
        var options = Options.Create(new TwitchOptions
        {
            EventSubMonitorIntervalSeconds = 1,
            EventSubRetryBaseDelaySeconds = 1,
            EventSubRetryMaxDelaySeconds = 2,
            EventSubSubscriptionSyncIntervalSeconds = 60
        });
        var service = new TwitchEventSubHostedService(
            websocketClient,
            new StubAccessTokenProvider(),
            subscriptionSynchronizer,
            CreateNotificationProcessor(database.Services, new NoOpArchiveJobQueue()),
            new RuntimeStatusStore(),
            NullLogger<TwitchEventSubHostedService>.Instance,
            options,
            TimeProvider.System,
            TimeSpan.FromMilliseconds(50));

        await service.StartAsync(CancellationToken.None);

        await websocketClient.WaitForConnectCountAsync(1, TimeSpan.FromSeconds(3));
        await websocketClient.TriggerDisconnectedAsync();
        await websocketClient.WaitForReconnectCountAsync(1, TimeSpan.FromSeconds(5));

        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, websocketClient.ConnectCount);
        Assert.Equal(1, websocketClient.ReconnectCount);
        Assert.Contains("reconnect-1", websocketClient.Operations);
    }

    [Fact]
    public async Task ServiceDisconnectsClientBeforeRetryingWhenReconnectHitsStartedSocket()
    {
        await using var database = await CreateDatabaseAsync();
        var websocketClient = new FakeEventSubWebsocketClient
        {
            FailNextReconnectWithAlreadyStarted = true
        };
        var subscriptionSynchronizer = new EventSubSubscriptionSynchronizer(
            new StubTwitchHelixClient(),
            database.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EventSubSubscriptionSynchronizer>.Instance,
            Options.Create(new TwitchOptions()));
        var options = Options.Create(new TwitchOptions
        {
            EventSubMonitorIntervalSeconds = 1,
            EventSubRetryBaseDelaySeconds = 1,
            EventSubRetryMaxDelaySeconds = 2,
            EventSubSubscriptionSyncIntervalSeconds = 60
        });
        var service = new TwitchEventSubHostedService(
            websocketClient,
            new StubAccessTokenProvider(),
            subscriptionSynchronizer,
            CreateNotificationProcessor(database.Services, new NoOpArchiveJobQueue()),
            new RuntimeStatusStore(),
            NullLogger<TwitchEventSubHostedService>.Instance,
            options,
            TimeProvider.System,
            TimeSpan.FromMilliseconds(50));

        await service.StartAsync(CancellationToken.None);

        await websocketClient.WaitForConnectCountAsync(1, TimeSpan.FromSeconds(3));
        await websocketClient.TriggerDisconnectedAsync();
        await websocketClient.WaitForReconnectCountAsync(2, TimeSpan.FromSeconds(5));

        await service.StopAsync(CancellationToken.None);

        var failedReconnectIndex = websocketClient.Operations.IndexOf("reconnect-failed");
        var resetIndex = websocketClient.Operations.IndexOf("disconnect");
        var successfulReconnectIndex = websocketClient.Operations.IndexOf("reconnect-2");

        Assert.True(failedReconnectIndex >= 0, $"Expected a failed reconnect before reset. Operations: {string.Join(", ", websocketClient.Operations)}");
        Assert.True(resetIndex > failedReconnectIndex, $"Expected reset after the stale-socket failure. Operations: {string.Join(", ", websocketClient.Operations)}");
        Assert.True(successfulReconnectIndex > resetIndex, $"Expected successful reconnect after reset. Operations: {string.Join(", ", websocketClient.Operations)}");
    }

    [Fact]
    public async Task ServiceUsesRetryBackoffBetweenFailedInitialConnectAttempts()
    {
        await using var database = await CreateDatabaseAsync();
        var websocketClient = new AlwaysFailingEventSubWebsocketClient();
        var options = Options.Create(new TwitchOptions
        {
            EventSubMonitorIntervalSeconds = 1,
            EventSubRetryBaseDelaySeconds = 1,
            EventSubRetryMaxDelaySeconds = 2,
            EventSubSubscriptionSyncIntervalSeconds = 60
        });
        var service = new TwitchEventSubHostedService(
            websocketClient,
            new StubAccessTokenProvider(),
            new EventSubSubscriptionSynchronizer(
                new StubTwitchHelixClient(),
                database.Services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<EventSubSubscriptionSynchronizer>.Instance,
                Options.Create(new TwitchOptions())),
            CreateNotificationProcessor(database.Services, new NoOpArchiveJobQueue()),
            new RuntimeStatusStore(),
            NullLogger<TwitchEventSubHostedService>.Instance,
            options,
            TimeProvider.System,
            TimeSpan.FromMilliseconds(50));

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(300);
        var earlyConnectAttempts = websocketClient.ConnectCount;
        await Task.Delay(1100);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, earlyConnectAttempts);
        Assert.True(websocketClient.ConnectCount >= 2, $"Expected retry after backoff. Attempts: {websocketClient.ConnectCount}");
    }

    [Fact]
    public async Task ServiceUsesRetryBackoffBetweenFailedSubscriptionSyncAttempts()
    {
        await using var database = await CreateDatabaseAsync();
        var websocketClient = new FakeEventSubWebsocketClient();
        var subscriptionSynchronizer = new FailingSubscriptionSynchronizer();
        var options = Options.Create(new TwitchOptions
        {
            EventSubMonitorIntervalSeconds = 1,
            EventSubRetryBaseDelaySeconds = 1,
            EventSubRetryMaxDelaySeconds = 2,
            EventSubSubscriptionSyncIntervalSeconds = 60
        });
        var service = new TwitchEventSubHostedService(
            websocketClient,
            new StubAccessTokenProvider(),
            subscriptionSynchronizer,
            CreateNotificationProcessor(database.Services, new NoOpArchiveJobQueue()),
            new RuntimeStatusStore(),
            NullLogger<TwitchEventSubHostedService>.Instance,
            options,
            TimeProvider.System,
            TimeSpan.FromMilliseconds(50));

        await service.StartAsync(CancellationToken.None);
        await websocketClient.WaitForConnectCountAsync(1, TimeSpan.FromSeconds(3));
        await Task.Delay(300);
        var earlySyncAttempts = subscriptionSynchronizer.CallCount;
        await Task.Delay(1100);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, earlySyncAttempts);
        Assert.True(subscriptionSynchronizer.CallCount >= 2, $"Expected retry after backoff. Calls: {subscriptionSynchronizer.CallCount}");
    }

    [Fact]
    public async Task ServiceImmediatelyReconcilesSubscriptionsAfterReconnectStyleConnection()
    {
        await using var database = await CreateDatabaseAsync();
        var websocketClient = new FakeEventSubWebsocketClient();
        var subscriptionSynchronizer = new CountingSubscriptionSynchronizer();
        var options = Options.Create(new TwitchOptions
        {
            EventSubMonitorIntervalSeconds = 10,
            EventSubRetryBaseDelaySeconds = 1,
            EventSubRetryMaxDelaySeconds = 2,
            EventSubSubscriptionSyncIntervalSeconds = 60
        });
        var service = new TwitchEventSubHostedService(
            websocketClient,
            new StubAccessTokenProvider(),
            subscriptionSynchronizer,
            CreateNotificationProcessor(database.Services, new NoOpArchiveJobQueue()),
            new RuntimeStatusStore(),
            NullLogger<TwitchEventSubHostedService>.Instance,
            options,
            TimeProvider.System,
            TimeSpan.FromMilliseconds(50));

        await service.StartAsync(CancellationToken.None);
        await websocketClient.WaitForConnectCountAsync(1, TimeSpan.FromSeconds(3));
        await websocketClient.TriggerDisconnectedAsync();
        await websocketClient.WaitForReconnectCountAsync(1, TimeSpan.FromSeconds(3));
        await Task.Delay(100);
        await service.StopAsync(CancellationToken.None);

        Assert.True(subscriptionSynchronizer.CallCount >= 2, $"Expected immediate subscription reconcile on reconnect. Calls: {subscriptionSynchronizer.CallCount}");
        Assert.Contains("session-1-r1", subscriptionSynchronizer.SessionIds);
    }

    [Fact]
    public async Task ServiceImmediatelyReconcilesSubscriptionsOnReconnectedEvent()
    {
        await using var database = await CreateDatabaseAsync();
        var websocketClient = new FakeEventSubWebsocketClient();
        var subscriptionSynchronizer = new CountingSubscriptionSynchronizer();
        var options = Options.Create(new TwitchOptions
        {
            EventSubMonitorIntervalSeconds = 10,
            EventSubRetryBaseDelaySeconds = 1,
            EventSubRetryMaxDelaySeconds = 2,
            EventSubSubscriptionSyncIntervalSeconds = 60
        });
        var service = new TwitchEventSubHostedService(
            websocketClient,
            new StubAccessTokenProvider(),
            subscriptionSynchronizer,
            CreateNotificationProcessor(database.Services, new NoOpArchiveJobQueue()),
            new RuntimeStatusStore(),
            NullLogger<TwitchEventSubHostedService>.Instance,
            options,
            TimeProvider.System,
            TimeSpan.FromSeconds(10));

        await service.StartAsync(CancellationToken.None);
        await websocketClient.WaitForConnectCountAsync(1, TimeSpan.FromSeconds(3));
        subscriptionSynchronizer.CallCount = 0;
        subscriptionSynchronizer.SessionIds.Clear();

        await websocketClient.TriggerReconnectedAsync();
        await Task.Delay(100);
        await service.StopAsync(CancellationToken.None);

        Assert.True(subscriptionSynchronizer.CallCount >= 1, "Expected immediate subscription reconcile on WebsocketReconnected event.");
        Assert.Contains("session-1", subscriptionSynchronizer.SessionIds);
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

    private static TwitchEventSubHostedService CreateService(
        IServiceProvider services,
        IEventSubWebsocketClient websocketClient,
        IArchiveJobQueue? archiveJobQueue = null,
        IEventSubSubscriptionSynchronizer? subscriptionSynchronizer = null,
        ITwitchAccessTokenProvider? accessTokenProvider = null,
        ILogger<TwitchEventSubHostedService>? logger = null,
        TimeSpan? monitorInterval = null)
    {
        var options = Options.Create(new TwitchOptions
        {
            EventSubMonitorIntervalSeconds = 1,
            EventSubRetryBaseDelaySeconds = 1,
            EventSubRetryMaxDelaySeconds = 2,
            EventSubSubscriptionSyncIntervalSeconds = 60
        });

        return new TwitchEventSubHostedService(
            websocketClient,
            accessTokenProvider ?? new StubAccessTokenProvider(),
            subscriptionSynchronizer ?? new EventSubSubscriptionSynchronizer(
                new StubTwitchHelixClient(),
                services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<EventSubSubscriptionSynchronizer>.Instance,
                Options.Create(new TwitchOptions())),
            CreateNotificationProcessor(services, archiveJobQueue ?? new NoOpArchiveJobQueue()),
            new RuntimeStatusStore(),
            logger ?? NullLogger<TwitchEventSubHostedService>.Instance,
            options,
            TimeProvider.System,
            monitorInterval ?? TimeSpan.FromMilliseconds(50));
    }

    private static EventSubNotificationProcessor CreateNotificationProcessor(IServiceProvider services, IArchiveJobQueue archiveJobQueue)
    {
        var archiveJobTriggerService = new ArchiveJobTriggerService(
            services.GetRequiredService<IServiceScopeFactory>(),
            archiveJobQueue,
            NullLogger<ArchiveJobTriggerService>.Instance,
            Options.Create(new TwitchOptions
            {
                EventSubRetryBaseDelaySeconds = 1,
                EventSubRetryMaxDelaySeconds = 2
            }),
            TimeProvider.System);

        return new EventSubNotificationProcessor(
            services.GetRequiredService<IServiceScopeFactory>(),
            archiveJobTriggerService,
            NullLogger<EventSubNotificationProcessor>.Instance,
            TimeProvider.System);
    }

    private static async Task<int> SeedChannelAsync(IServiceProvider services, string login, string twitchUserId)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        var now = DateTimeOffset.UtcNow;
        var channel = new TwitchArchivist.Persistence.Entities.ChannelConfiguration
        {
            TwitchLogin = login,
            TwitchUserId = twitchUserId,
            OutputDirectory = @"D:\archive\" + login,
            IsEnabled = true,
            CreatedUtc = now,
            UpdatedUtc = now
        };
        dbContext.ChannelConfigurations.Add(channel);
        await dbContext.SaveChangesAsync();
        return channel.Id;
    }

    private static async Task SeedArchiveJobAsync(IServiceProvider services, int channelId, ArchiveJobStatus status, DateTimeOffset createdUtc)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        dbContext.ArchiveJobs.Add(new TwitchArchivist.Persistence.Entities.ArchiveJob
        {
            ChannelConfigurationId = channelId,
            TriggerSource = "seed",
            Status = status,
            CreatedUtc = createdUtc
        });
        await dbContext.SaveChangesAsync();
    }

    private sealed class FakeEventSubWebsocketClient : IEventSubWebsocketClient
    {
        private readonly object _gate = new();
        private bool _started;

        public string? SessionId { get; private set; }

        public List<string> Operations { get; } = [];

        public int ConnectCount { get; private set; }
        public int ReconnectCount { get; private set; }
        public bool FailNextReconnectWithAlreadyStarted { get; set; }

        public event Func<object?, EventSubConnectedEventArgs, Task>? Connected;
        public event Func<object?, EventSubDisconnectedEventArgs, Task>? Disconnected;
        public event Func<object?, EventSubReconnectedEventArgs, Task>? Reconnected;
        public event Func<object?, EventSubErrorEventArgs, Task>? ErrorOccurred;
        public event Func<object?, EventSubStreamOnlineEventArgs, Task>? StreamOnline;
        public event Func<object?, EventSubStreamOfflineEventArgs, Task>? StreamOffline;

        public IEventSubShardClient CreateShardClient(string shardKey) => throw new NotSupportedException();

        public async Task<bool> ConnectAsync(Uri endpoint)
        {
            lock (_gate)
            {
                if (_started)
                {
                    throw new InvalidOperationException("The WebSocket has already been started.");
                }

                _started = true;
                ConnectCount++;
                SessionId = $"session-{ConnectCount}";
                Operations.Add($"connect-{ConnectCount}");
            }

            if (Connected is not null)
            {
                await Connected.Invoke(this, new EventSubConnectedEventArgs(IsRequestedReconnect: ConnectCount > 1));
            }

            return true;
        }

        public async Task<bool> ReconnectAsync()
        {
            lock (_gate)
            {
                ReconnectCount++;
                if (FailNextReconnectWithAlreadyStarted)
                {
                    FailNextReconnectWithAlreadyStarted = false;
                    Operations.Add("reconnect-failed");
                    throw new InvalidOperationException("The WebSocket has already been started.");
                }

                if (_started)
                {
                    throw new InvalidOperationException("The WebSocket has already been started.");
                }

                _started = true;
                SessionId = $"session-{ConnectCount}-r{ReconnectCount}";
                Operations.Add($"reconnect-{ReconnectCount}");
            }

            if (Reconnected is not null)
            {
                await Reconnected.Invoke(this, new EventSubReconnectedEventArgs());
            }

            return true;
        }

        public Task<bool> DisconnectAsync()
        {
            lock (_gate)
            {
                _started = false;
                Operations.Add("disconnect");
            }

            return Task.FromResult(true);
        }

        public async Task TriggerDisconnectedAsync()
        {
            lock (_gate)
            {
                _started = false;
            }

            if (Disconnected is not null)
            {
                await Disconnected.Invoke(this, new EventSubDisconnectedEventArgs());
            }
        }

        public async Task TriggerReconnectedAsync()
        {
            if (Reconnected is not null)
            {
                await Reconnected.Invoke(this, new EventSubReconnectedEventArgs());
            }
        }

        public async Task TriggerStreamOfflineAsync(string broadcasterUserLogin, string broadcasterUserId)
        {
            if (StreamOffline is not null)
            {
                await StreamOffline.Invoke(this, new EventSubStreamOfflineEventArgs(broadcasterUserLogin, broadcasterUserId));
            }
        }

        public async Task WaitForConnectCountAsync(int expectedCount, TimeSpan timeout)
        {
            if (expectedCount <= 1)
            {
                var startedAt = DateTime.UtcNow;
                while (DateTime.UtcNow - startedAt < timeout)
                {
                    if (ConnectCount >= expectedCount)
                    {
                        return;
                    }

                    await Task.Delay(50);
                }

                throw new TimeoutException($"Timed out waiting for connect count {expectedCount}.");
            }

            using var cancellationTokenSource = new CancellationTokenSource(timeout);
            try
            {
                await Task.Run(async () =>
                {
                    while (!cancellationTokenSource.IsCancellationRequested)
                    {
                        if (ConnectCount >= expectedCount)
                        {
                            return;
                        }

                        await Task.Delay(50, cancellationTokenSource.Token);
                    }
                }, cancellationTokenSource.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"Timed out waiting for connect count {expectedCount}.");
            }
        }

        public async Task WaitForReconnectCountAsync(int expectedCount, TimeSpan timeout)
        {
            if (ReconnectCount >= expectedCount)
            {
                return;
            }

            using var cancellationTokenSource = new CancellationTokenSource(timeout);
            try
            {
                await Task.Run(async () =>
                {
                    while (!cancellationTokenSource.IsCancellationRequested)
                    {
                        if (ReconnectCount >= expectedCount)
                        {
                            return;
                        }

                        await Task.Delay(50, cancellationTokenSource.Token);
                    }
                }, cancellationTokenSource.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"Timed out waiting for reconnect count {expectedCount}.");
            }
        }
    }

    private sealed class StubAccessTokenProvider : ITwitchAccessTokenProvider
    {
        public string? BuildUserAuthorizationUrl(string state, string redirectUri) => null;
        public Task ExchangeAuthorizationCodeAsync(string code, string redirectUri, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<string?> GetAppAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult<string?>("app-token");
        public Task<string?> GetUserAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult<string?>("user-token");
        public Task<TwitchUserAuthorizationState> GetUserAuthorizationStateAsync(CancellationToken cancellationToken)
            => Task.FromResult(new TwitchUserAuthorizationState(false, false, "missing", null, false, null, null, null, null));
        public Task<TwitchUserAuthorizationState> ValidateUserAuthorizationAsync(CancellationToken cancellationToken)
            => Task.FromResult(new TwitchUserAuthorizationState(false, false, "missing", null, false, null, null, null, null));
    }

    private sealed class StubTwitchHelixClient : ITwitchHelixClient
    {
        public Task<EventSubSubscriptionRecord> CreateStreamSubscriptionAsync(string subscriptionType, string broadcasterUserId, string sessionId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<EventSubSubscriptionRecord>> GetEventSubscriptionsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<EventSubSubscriptionRecord>>([]);

        public Task<ArchiveVodRecord?> GetLatestArchiveVodAsync(string broadcasterUserId, DateTimeOffset? createdAfterUtc, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<TwitchLiveStreamState>> GetLiveStreamsByLoginsAsync(IReadOnlyList<string> twitchLogins, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<string?> ResolveUserIdAsync(string twitchLogin, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<TwitchChannelSearchResult>> SearchChannelsAsync(string query, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class FailingSubscriptionSynchronizer : IEventSubSubscriptionSynchronizer
    {
        public int CallCount { get; private set; }

        public Task EnsureSubscriptionsAsync(string sessionId, CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("boom");
        }
    }

    private sealed class CountingSubscriptionSynchronizer : IEventSubSubscriptionSynchronizer
    {
        public int CallCount { get; set; }
        public List<string> SessionIds { get; } = [];

        public Task EnsureSubscriptionsAsync(string sessionId, CancellationToken cancellationToken)
        {
            CallCount++;
            SessionIds.Add(sessionId);
            return Task.CompletedTask;
        }
    }

    private sealed class AlwaysFailingEventSubWebsocketClient : IEventSubWebsocketClient
    {
        public string? SessionId => null;
        public int ConnectCount { get; private set; }
        public event Func<object?, EventSubConnectedEventArgs, Task>? Connected;
        public event Func<object?, EventSubDisconnectedEventArgs, Task>? Disconnected;
        public event Func<object?, EventSubReconnectedEventArgs, Task>? Reconnected;
        public event Func<object?, EventSubErrorEventArgs, Task>? ErrorOccurred;
        public event Func<object?, EventSubStreamOnlineEventArgs, Task>? StreamOnline;
        public event Func<object?, EventSubStreamOfflineEventArgs, Task>? StreamOffline;

        public IEventSubShardClient CreateShardClient(string shardKey) => throw new NotSupportedException();

        public Task<bool> ConnectAsync(Uri endpoint)
        {
            ConnectCount++;
            throw new InvalidOperationException("connect failed");
        }

        public Task<bool> ReconnectAsync()
        {
            ConnectCount++;
            throw new InvalidOperationException("reconnect failed");
        }

        public Task<bool> DisconnectAsync() => Task.FromResult(true);
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

    private sealed class RecordingArchiveJobQueue : IArchiveJobQueue
    {
        public List<int> EnqueuedIds { get; } = [];

        public ValueTask EnqueueAsync(int archiveJobId, CancellationToken cancellationToken)
        {
            EnqueuedIds.Add(archiveJobId);
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<int> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add($"{logLevel}: {formatter(state, exception)}{(exception is null ? string.Empty : Environment.NewLine + exception)}");
        }

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();

            public void Dispose()
            {
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
}
#pragma warning restore CS0067
