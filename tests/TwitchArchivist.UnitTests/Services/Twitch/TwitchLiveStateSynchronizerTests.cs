using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TwitchArchivist.Persistence;
using TwitchArchivist.Persistence.Entities;
using TwitchArchivist.Services.Twitch;

namespace TwitchArchivist.UnitTests.Services.Twitch;

public class TwitchLiveStateSynchronizerTests
{
    [Fact]
    public async Task SynchronizeAsyncUpdatesEnabledChannelLiveStatesFromHelix()
    {
        await using var database = await CreateDatabaseAsync();
        var baseline = new DateTimeOffset(2026, 04, 28, 18, 0, 0, TimeSpan.Zero);

        await using (var scope = database.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
            dbContext.ChannelConfigurations.AddRange(
                new ChannelConfiguration
                {
                    TwitchLogin = "alpha",
                    OutputDirectory = @"D:\archive\alpha",
                    IsEnabled = true,
                    CreatedUtc = baseline.AddDays(-1),
                    UpdatedUtc = baseline.AddDays(-1)
                },
                new ChannelConfiguration
                {
                    TwitchLogin = "beta",
                    OutputDirectory = @"D:\archive\beta",
                    IsEnabled = true,
                    CreatedUtc = baseline.AddDays(-1),
                    UpdatedUtc = baseline.AddDays(-1)
                },
                new ChannelConfiguration
                {
                    TwitchLogin = "gamma",
                    OutputDirectory = @"D:\archive\gamma",
                    IsEnabled = false,
                    CreatedUtc = baseline.AddDays(-1),
                    UpdatedUtc = baseline.AddDays(-1)
                });
            await dbContext.SaveChangesAsync();

            var channels = await dbContext.ChannelConfigurations.OrderBy(x => x.TwitchLogin).ToListAsync();
            dbContext.StreamSessionStates.AddRange(
                new StreamSessionState
                {
                    ChannelConfigurationId = channels.Single(x => x.TwitchLogin == "beta").Id,
                    LastKnownStreamId = "stale-stream",
                    LastOnlineUtc = baseline.AddHours(-3),
                    CreatedUtc = baseline.AddDays(-1),
                    UpdatedUtc = baseline.AddHours(-3)
                },
                new StreamSessionState
                {
                    ChannelConfigurationId = channels.Single(x => x.TwitchLogin == "gamma").Id,
                    LastKnownStreamId = "disabled-stream",
                    LastOnlineUtc = baseline.AddHours(-5),
                    CreatedUtc = baseline.AddDays(-1),
                    UpdatedUtc = baseline.AddHours(-5)
                });
            await dbContext.SaveChangesAsync();
        }

        var helixClient = new StubTwitchHelixClient(
        [
            new TwitchLiveStreamState("user-alpha", "alpha", "stream-alpha", baseline.AddMinutes(-20))
        ]);

        var synchronizer = new TwitchLiveStateSynchronizer(
            helixClient,
            database.Services.GetRequiredService<IServiceScopeFactory>(),
            new FixedTimeProvider(baseline),
            NullLogger<TwitchLiveStateSynchronizer>.Instance);

        await synchronizer.SynchronizeAsync(CancellationToken.None);

        Assert.Equal(["alpha", "beta"], helixClient.RequestedLogins.Single());

        await using var verificationScope = database.Services.CreateAsyncScope();
        var verificationDbContext = verificationScope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        var channelStates = await verificationDbContext.ChannelConfigurations
            .Include(x => x.StreamSessionState)
            .OrderBy(x => x.TwitchLogin)
            .ToListAsync();

        var alpha = channelStates.Single(x => x.TwitchLogin == "alpha");
        Assert.Equal("user-alpha", alpha.TwitchUserId);
        Assert.NotNull(alpha.StreamSessionState);
        Assert.Equal("stream-alpha", alpha.StreamSessionState!.LastKnownStreamId);
        Assert.Equal(baseline.AddMinutes(-20), alpha.StreamSessionState.LastOnlineUtc);
        Assert.Null(alpha.StreamSessionState.LastOfflineUtc);

        var beta = channelStates.Single(x => x.TwitchLogin == "beta");
        Assert.NotNull(beta.StreamSessionState);
        Assert.Equal(baseline, beta.StreamSessionState!.LastOfflineUtc);
        Assert.Equal(baseline.AddHours(-3), beta.StreamSessionState.LastOnlineUtc);

        var gamma = channelStates.Single(x => x.TwitchLogin == "gamma");
        Assert.NotNull(gamma.StreamSessionState);
        Assert.Equal("disabled-stream", gamma.StreamSessionState!.LastKnownStreamId);
        Assert.Equal(baseline.AddHours(-5), gamma.StreamSessionState.LastOnlineUtc);
        Assert.Null(gamma.StreamSessionState.LastOfflineUtc);
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

    private sealed class StubTwitchHelixClient(IReadOnlyList<TwitchLiveStreamState> liveStreams) : ITwitchHelixClient
    {
        public List<string[]> RequestedLogins { get; } = [];

        public Task<EventSubSubscriptionRecord> CreateStreamSubscriptionAsync(string subscriptionType, string broadcasterUserId, string sessionId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<EventSubSubscriptionRecord>> GetEventSubscriptionsAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ArchiveVodRecord?> GetLatestArchiveVodAsync(string broadcasterUserId, DateTimeOffset? createdAfterUtc, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<TwitchLiveStreamState>> GetLiveStreamsByLoginsAsync(IReadOnlyList<string> twitchLogins, CancellationToken cancellationToken)
        {
            RequestedLogins.Add(twitchLogins.ToArray());
            return Task.FromResult<IReadOnlyList<TwitchLiveStreamState>>(liveStreams);
        }

        public Task<string?> ResolveUserIdAsync(string twitchLogin, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<TwitchChannelSearchResult>> SearchChannelsAsync(string query, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
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
