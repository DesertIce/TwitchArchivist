using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TwitchArchivist.Persistence;
using TwitchArchivist.Persistence.Entities;
using TwitchArchivist.Services.Twitch;

namespace TwitchArchivist.UnitTests.Services.Twitch;

public class EventSubSubscriptionSynchronizerTests
{
    [Fact]
    public async Task EnsureSubscriptionsAsyncCreatesMissingSubscriptionsForEnabledChannels()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"twitcharchivist-sync-{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddDbContext<TwitchArchivistDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
        await using var provider = services.BuildServiceProvider();

        try
        {
            await using (var scope = provider.CreateAsyncScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
                await dbContext.Database.EnsureCreatedAsync();
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
            var synchronizer = new EventSubSubscriptionSynchronizer(
                helixClient,
                provider.GetRequiredService<IServiceScopeFactory>());

            await synchronizer.EnsureSubscriptionsAsync("session-123", CancellationToken.None);

            Assert.Equal(2, helixClient.CreatedSubscriptions.Count);
            Assert.Contains(helixClient.CreatedSubscriptions, x => x.SubscriptionType == "stream.online");
            Assert.Contains(helixClient.CreatedSubscriptions, x => x.SubscriptionType == "stream.offline");

            await using var verificationScope = provider.CreateAsyncScope();
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
        finally
        {
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

    private sealed class StubTwitchHelixClient : ITwitchHelixClient
    {
        public List<(string SubscriptionType, string BroadcasterUserId, string SessionId)> CreatedSubscriptions { get; } = [];

        public Task<EventSubSubscriptionRecord> CreateStreamSubscriptionAsync(string subscriptionType, string broadcasterUserId, string sessionId, CancellationToken cancellationToken)
        {
            CreatedSubscriptions.Add((subscriptionType, broadcasterUserId, sessionId));
            return Task.FromResult(new EventSubSubscriptionRecord($"sub-{subscriptionType}", subscriptionType, "enabled", broadcasterUserId));
        }

        public Task<IReadOnlyList<EventSubSubscriptionRecord>> GetEventSubscriptionsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<EventSubSubscriptionRecord>>([]);

        public Task<ArchiveVodRecord?> GetLatestArchiveVodAsync(string broadcasterUserId, DateTimeOffset? createdAfterUtc, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<string?> ResolveUserIdAsync(string twitchLogin, CancellationToken cancellationToken)
            => Task.FromResult<string?>("29430843");

        public Task<IReadOnlyList<TwitchChannelSearchResult>> SearchChannelsAsync(string query, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
