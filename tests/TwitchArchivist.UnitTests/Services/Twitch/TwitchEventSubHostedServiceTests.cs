using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TwitchArchivist.Models;
using TwitchArchivist.Persistence;
using TwitchArchivist.Services;
using TwitchArchivist.Services.Twitch;

namespace TwitchArchivist.UnitTests.Services.Twitch;

#pragma warning disable CS0067
public class TwitchEventSubHostedServiceTests
{
    [Fact]
    public async Task ServiceDisconnectsClientBeforeReconnectingAfterDisconnectEvent()
    {
        await using var database = await CreateDatabaseAsync();
        var websocketClient = new FakeEventSubWebsocketClient();
        var subscriptionSynchronizer = new EventSubSubscriptionSynchronizer(
            new StubTwitchHelixClient(),
            database.Services.GetRequiredService<IServiceScopeFactory>());
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
            database.Services.GetRequiredService<IServiceScopeFactory>(),
            new NoOpArchiveJobQueue(),
            new RuntimeStatusStore(),
            NullLogger<TwitchEventSubHostedService>.Instance,
            options,
            TimeProvider.System,
            TimeSpan.FromMilliseconds(50));

        await service.StartAsync(CancellationToken.None);

        await websocketClient.WaitForConnectCountAsync(1, TimeSpan.FromSeconds(3));
        await websocketClient.TriggerDisconnectedAsync();
        await websocketClient.WaitForConnectCountAsync(2, TimeSpan.FromSeconds(5));

        await service.StopAsync(CancellationToken.None);

        var secondConnectIndex = websocketClient.Operations.IndexOf("connect-2");
        var resetIndex = websocketClient.Operations.IndexOf("disconnect");

        Assert.True(secondConnectIndex >= 0, "Expected the service to attempt a second websocket connection.");
        Assert.True(resetIndex >= 0, "Expected the service to reset the websocket client before reconnecting.");
        Assert.True(resetIndex < secondConnectIndex, $"Expected reset before reconnect. Operations: {string.Join(", ", websocketClient.Operations)}");
    }

    [Fact]
    public async Task ServiceUsesRetryBackoffBetweenFailedConnectAttempts()
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
            new EventSubSubscriptionSynchronizer(new StubTwitchHelixClient(), database.Services.GetRequiredService<IServiceScopeFactory>()),
            database.Services.GetRequiredService<IServiceScopeFactory>(),
            new NoOpArchiveJobQueue(),
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
            database.Services.GetRequiredService<IServiceScopeFactory>(),
            new NoOpArchiveJobQueue(),
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
            database.Services.GetRequiredService<IServiceScopeFactory>(),
            new NoOpArchiveJobQueue(),
            new RuntimeStatusStore(),
            NullLogger<TwitchEventSubHostedService>.Instance,
            options,
            TimeProvider.System,
            TimeSpan.FromMilliseconds(50));

        await service.StartAsync(CancellationToken.None);
        await websocketClient.WaitForConnectCountAsync(1, TimeSpan.FromSeconds(3));
        await websocketClient.TriggerDisconnectedAsync();
        await websocketClient.WaitForConnectCountAsync(2, TimeSpan.FromSeconds(3));
        await Task.Delay(100);
        await service.StopAsync(CancellationToken.None);

        Assert.True(subscriptionSynchronizer.CallCount >= 2, $"Expected immediate subscription reconcile on reconnect. Calls: {subscriptionSynchronizer.CallCount}");
        Assert.Contains("session-2", subscriptionSynchronizer.SessionIds);
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
            database.Services.GetRequiredService<IServiceScopeFactory>(),
            new NoOpArchiveJobQueue(),
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

    private sealed class FakeEventSubWebsocketClient : IEventSubWebsocketClient
    {
        private readonly object _gate = new();
        private TaskCompletionSource<bool> _secondConnectTcs = CreateTcs();
        private bool _started;

        public string? SessionId { get; private set; }

        public List<string> Operations { get; } = [];

        public int ConnectCount { get; private set; }

        public event Func<object?, EventSubConnectedEventArgs, Task>? Connected;
        public event Func<object?, EventSubDisconnectedEventArgs, Task>? Disconnected;
        public event Func<object?, EventSubReconnectedEventArgs, Task>? Reconnected;
        public event Func<object?, EventSubErrorEventArgs, Task>? ErrorOccurred;
        public event Func<object?, EventSubStreamOnlineEventArgs, Task>? StreamOnline;
        public event Func<object?, EventSubStreamOfflineEventArgs, Task>? StreamOffline;

        public async Task ConnectAsync(Uri endpoint)
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

            if (ConnectCount >= 2)
            {
                _secondConnectTcs.TrySetResult(true);
            }
        }

        public Task DisconnectAsync()
        {
            lock (_gate)
            {
                _started = false;
                Operations.Add("disconnect");
            }

            return Task.CompletedTask;
        }

        public async Task TriggerDisconnectedAsync()
        {
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
            await _secondConnectTcs.Task.WaitAsync(cancellationTokenSource.Token);
        }

        private static TaskCompletionSource<bool> CreateTcs()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);
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

        public Task ConnectAsync(Uri endpoint)
        {
            ConnectCount++;
            throw new InvalidOperationException("connect failed");
        }

        public Task DisconnectAsync() => Task.CompletedTask;
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
