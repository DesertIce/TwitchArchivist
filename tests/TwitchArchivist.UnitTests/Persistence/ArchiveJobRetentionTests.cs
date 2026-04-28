using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TwitchArchivist.Persistence;
using TwitchArchivist.Persistence.Entities;

namespace TwitchArchivist.UnitTests.Persistence;

public class ArchiveJobRetentionTests
{
    [Fact]
    public async Task TrimArchiveJobsForChannelAsyncKeepsMostRecentHundredForThatChannelOnly()
    {
        await using var database = await CreateDatabaseAsync();
        const int primaryChannelId = 1;
        const int secondaryChannelId = 2;
        var baseTimestamp = new DateTimeOffset(2026, 04, 27, 12, 0, 0, TimeSpan.Zero);
        int[] expectedPrimaryJobIds;

        await using (var scope = database.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
            dbContext.ChannelConfigurations.AddRange(
                new ChannelConfiguration
                {
                    Id = primaryChannelId,
                    TwitchLogin = "primary",
                    OutputDirectory = "C:\\archive\\primary",
                    CreatedUtc = baseTimestamp,
                    UpdatedUtc = baseTimestamp
                },
                new ChannelConfiguration
                {
                    Id = secondaryChannelId,
                    TwitchLogin = "secondary",
                    OutputDirectory = "C:\\archive\\secondary",
                    CreatedUtc = baseTimestamp,
                    UpdatedUtc = baseTimestamp
                });

            for (var i = 0; i < 105; i++)
            {
                dbContext.ArchiveJobs.Add(new ArchiveJob
                {
                    ChannelConfigurationId = primaryChannelId,
                    TriggerSource = "stream.offline",
                    Status = ArchiveJobStatus.Succeeded,
                    CreatedUtc = baseTimestamp.AddMinutes(i / 2)
                });
            }

            for (var i = 0; i < 3; i++)
            {
                dbContext.ArchiveJobs.Add(new ArchiveJob
                {
                    ChannelConfigurationId = secondaryChannelId,
                    TriggerSource = "stream.offline",
                    Status = ArchiveJobStatus.Succeeded,
                    CreatedUtc = baseTimestamp.AddHours(i)
                });
            }

            await dbContext.SaveChangesAsync();

            expectedPrimaryJobIds = (await dbContext.ArchiveJobs
                .Where(x => x.ChannelConfigurationId == primaryChannelId)
                .Select(x => new { x.Id, x.CreatedUtc })
                .ToListAsync())
                .OrderByDescending(x => x.CreatedUtc)
                .ThenByDescending(x => x.Id)
                .Take(100)
                .OrderBy(x => x.CreatedUtc)
                .ThenBy(x => x.Id)
                .Select(x => x.Id)
                .ToArray();
        }

        await using (var scope = database.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
            await dbContext.TrimArchiveJobsForChannelAsync(primaryChannelId, 100, CancellationToken.None);
        }

        await using (var scope = database.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
            var primaryJobs = (await dbContext.ArchiveJobs
                .Where(x => x.ChannelConfigurationId == primaryChannelId)
                .Select(x => new { x.Id, x.CreatedUtc })
                .ToListAsync())
                .OrderBy(x => x.CreatedUtc)
                .ThenBy(x => x.Id)
                .ToList();
            var secondaryJobs = await dbContext.ArchiveJobs
                .Where(x => x.ChannelConfigurationId == secondaryChannelId)
                .OrderBy(x => x.Id)
                .ToListAsync();

            Assert.Equal(100, primaryJobs.Count);
            Assert.Equal(3, secondaryJobs.Count);

            Assert.Equal(expectedPrimaryJobIds, primaryJobs.Select(x => x.Id).ToArray());
        }
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
