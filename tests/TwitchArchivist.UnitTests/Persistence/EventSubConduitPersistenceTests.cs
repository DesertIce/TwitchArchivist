using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TwitchArchivist.Persistence;
using TwitchArchivist.Persistence.Entities;

namespace TwitchArchivist.UnitTests.Persistence;

public class EventSubConduitPersistenceTests
{
    [Fact]
    public async Task PersistsConduitWithShardsAndSubscriptionBindings()
    {
        await using var database = await CreateDatabaseAsync();
        var now = new DateTimeOffset(2026, 04, 28, 22, 15, 0, TimeSpan.Zero);

        await using (var scope = database.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
            var channel = new ChannelConfiguration
            {
                TwitchLogin = "k3lsb3lls",
                TwitchUserId = "1234",
                OutputDirectory = @"D:\archive\k3lsb3lls",
                CreatedUtc = now,
                UpdatedUtc = now
            };
            var conduit = new EventSubConduit
            {
                TwitchConduitId = "conduit-1",
                ShardCount = 3,
                CreatedUtc = now,
                UpdatedUtc = now,
                Shards =
                [
                    new EventSubConduitShard
                    {
                        ShardId = 0,
                        TransportSessionId = "session-a",
                        Status = "enabled",
                        LastWelcomeUtc = now,
                        LastAssignmentUtc = now,
                        CreatedUtc = now,
                        UpdatedUtc = now
                    },
                    new EventSubConduitShard
                    {
                        ShardId = 1,
                        TransportSessionId = "session-b",
                        Status = "enabled",
                        LastWelcomeUtc = now.AddSeconds(1),
                        LastAssignmentUtc = now.AddSeconds(2),
                        CreatedUtc = now,
                        UpdatedUtc = now
                    }
                ],
                SubscriptionBindings =
                [
                    new EventSubSubscriptionBinding
                    {
                        ChannelConfiguration = channel,
                        SubscriptionType = "stream.offline",
                        TwitchSubscriptionId = "sub-1",
                        Status = "enabled",
                        LastVerifiedUtc = now.AddMinutes(1),
                        CreatedUtc = now,
                        UpdatedUtc = now
                    }
                ]
            };

            dbContext.EventSubConduits.Add(conduit);
            await dbContext.SaveChangesAsync();
        }

        await using (var scope = database.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
            var conduit = await dbContext.EventSubConduits
                .Include(x => x.Shards.OrderBy(shard => shard.ShardId))
                .Include(x => x.SubscriptionBindings)
                .SingleAsync();

            Assert.Equal("conduit-1", conduit.TwitchConduitId);
            Assert.Equal(3, conduit.ShardCount);

            var shard = Assert.Single(conduit.Shards.Where(x => x.ShardId == 1));
            Assert.Equal("session-b", shard.TransportSessionId);
            Assert.Equal("enabled", shard.Status);
            Assert.Equal(now.AddSeconds(1), shard.LastWelcomeUtc);
            Assert.Equal(now.AddSeconds(2), shard.LastAssignmentUtc);

            var binding = Assert.Single(conduit.SubscriptionBindings);
            Assert.Equal("stream.offline", binding.SubscriptionType);
            Assert.Equal("sub-1", binding.TwitchSubscriptionId);
            Assert.Equal("enabled", binding.Status);
            Assert.Equal(now.AddMinutes(1), binding.LastVerifiedUtc);
            Assert.Equal("1234", (await dbContext.ChannelConfigurations.SingleAsync()).TwitchUserId);
        }
    }

    [Fact]
    public async Task EnforcesConduitAndBindingUniquenessConstraints()
    {
        await using var database = await CreateDatabaseAsync();
        var now = new DateTimeOffset(2026, 04, 28, 22, 30, 0, TimeSpan.Zero);

        await using var scope = database.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        var channel = new ChannelConfiguration
        {
            TwitchLogin = "duplicate-test",
            TwitchUserId = "5678",
            OutputDirectory = @"D:\archive\duplicate-test",
            CreatedUtc = now,
            UpdatedUtc = now
        };
        dbContext.ChannelConfigurations.Add(channel);

        var conduit = new EventSubConduit
        {
            TwitchConduitId = "conduit-unique",
            ShardCount = 2,
            CreatedUtc = now,
            UpdatedUtc = now
        };
        dbContext.EventSubConduits.Add(conduit);
        await dbContext.SaveChangesAsync();

        dbContext.EventSubConduits.Add(new EventSubConduit
        {
            TwitchConduitId = "conduit-unique",
            ShardCount = 4,
            CreatedUtc = now,
            UpdatedUtc = now
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());

        dbContext.ChangeTracker.Clear();

        await using var verificationScope = database.Services.CreateAsyncScope();
        var verificationDbContext = verificationScope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        var existingChannel = await verificationDbContext.ChannelConfigurations.SingleAsync();
        var existingConduit = await verificationDbContext.EventSubConduits.SingleAsync();

        verificationDbContext.EventSubConduitShards.AddRange(
            new EventSubConduitShard
            {
                EventSubConduitId = existingConduit.Id,
                ShardId = 0,
                Status = "enabled",
                CreatedUtc = now,
                UpdatedUtc = now
            },
            new EventSubConduitShard
            {
                EventSubConduitId = existingConduit.Id,
                ShardId = 0,
                Status = "enabled",
                CreatedUtc = now,
                UpdatedUtc = now
            });

        await Assert.ThrowsAsync<DbUpdateException>(() => verificationDbContext.SaveChangesAsync());

        verificationDbContext.ChangeTracker.Clear();

        verificationDbContext.EventSubSubscriptionBindings.AddRange(
            new EventSubSubscriptionBinding
            {
                EventSubConduitId = existingConduit.Id,
                ChannelConfigurationId = existingChannel.Id,
                SubscriptionType = "stream.online",
                Status = "enabled",
                CreatedUtc = now,
                UpdatedUtc = now
            },
            new EventSubSubscriptionBinding
            {
                EventSubConduitId = existingConduit.Id,
                ChannelConfigurationId = existingChannel.Id,
                SubscriptionType = "stream.online",
                Status = "enabled",
                CreatedUtc = now,
                UpdatedUtc = now
            });

        await Assert.ThrowsAsync<DbUpdateException>(() => verificationDbContext.SaveChangesAsync());
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
