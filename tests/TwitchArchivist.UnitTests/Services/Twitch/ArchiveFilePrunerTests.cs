using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TwitchArchivist.Persistence;
using TwitchArchivist.Persistence.Entities;
using TwitchArchivist.Services.Twitch;

namespace TwitchArchivist.UnitTests.Services.Twitch;

public class ArchiveFilePrunerTests
{
    [Fact]
    public async Task PruneSucceededFilesForChannelAsyncDeletesOlderSucceededFilesBeyondLimit()
    {
        await using var database = await CreateDatabaseAsync();
        var outputDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDirectory);

        try
        {
            await using var scope = database.Services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();

            var channel = new ChannelConfiguration
            {
                TwitchLogin = "alpha",
                Alias = "alpha-archive",
                OutputDirectory = outputDirectory,
                IsEnabled = true,
                AutoPruneEnabled = true,
                AutoPruneVodCount = 2,
                CreatedUtc = DateTimeOffset.UtcNow.AddDays(-2),
                UpdatedUtc = DateTimeOffset.UtcNow.AddDays(-1)
            };
            dbContext.ChannelConfigurations.Add(channel);
            await dbContext.SaveChangesAsync();

            var oldestPath = Path.Combine(outputDirectory, "alpha-archive-2026-04-25-oldest-1.mp4");
            var middlePath = Path.Combine(outputDirectory, "alpha-archive-2026-04-26-middle-2.mp4");
            var newestPath = Path.Combine(outputDirectory, "alpha-archive-2026-04-27-newest-3.mp4");
            var failedPath = Path.Combine(outputDirectory, "alpha-2026-04-28-failed-4.mp4");

            File.WriteAllText(oldestPath, "oldest");
            File.WriteAllText(middlePath, "middle");
            File.WriteAllText(newestPath, "newest");
            File.WriteAllText(failedPath, "failed");

            dbContext.ArchiveJobs.AddRange(
                new ArchiveJob
                {
                    ChannelConfigurationId = channel.Id,
                    TriggerSource = "stream.offline",
                    Status = ArchiveJobStatus.Succeeded,
                    OutputPath = oldestPath,
                    CreatedUtc = DateTimeOffset.Parse("2026-04-25T12:00:00Z"),
                    CompletedUtc = DateTimeOffset.Parse("2026-04-25T13:00:00Z")
                },
                new ArchiveJob
                {
                    ChannelConfigurationId = channel.Id,
                    TriggerSource = "stream.offline",
                    Status = ArchiveJobStatus.Succeeded,
                    OutputPath = middlePath,
                    CreatedUtc = DateTimeOffset.Parse("2026-04-26T12:00:00Z"),
                    CompletedUtc = DateTimeOffset.Parse("2026-04-26T13:00:00Z")
                },
                new ArchiveJob
                {
                    ChannelConfigurationId = channel.Id,
                    TriggerSource = "stream.offline",
                    Status = ArchiveJobStatus.Succeeded,
                    OutputPath = newestPath,
                    CreatedUtc = DateTimeOffset.Parse("2026-04-27T12:00:00Z"),
                    CompletedUtc = DateTimeOffset.Parse("2026-04-27T13:00:00Z")
                },
                new ArchiveJob
                {
                    ChannelConfigurationId = channel.Id,
                    TriggerSource = "stream.offline",
                    Status = ArchiveJobStatus.Failed,
                    OutputPath = failedPath,
                    CreatedUtc = DateTimeOffset.Parse("2026-04-28T12:00:00Z"),
                    CompletedUtc = DateTimeOffset.Parse("2026-04-28T13:00:00Z")
                });
            await dbContext.SaveChangesAsync();

            await ArchiveFilePruner.PruneSucceededFilesForChannelAsync(
                dbContext,
                channel,
                NullLogger.Instance,
                CancellationToken.None);

            Assert.False(File.Exists(oldestPath));
            Assert.True(File.Exists(middlePath));
            Assert.True(File.Exists(newestPath));
            Assert.True(File.Exists(failedPath));
            Assert.Equal(4, await dbContext.ArchiveJobs.CountAsync());
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PruneSucceededFilesForChannelAsyncDoesNothingWhenDisabled()
    {
        await using var database = await CreateDatabaseAsync();
        var outputDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDirectory);

        try
        {
            await using var scope = database.Services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();

            var channel = new ChannelConfiguration
            {
                TwitchLogin = "alpha",
                OutputDirectory = outputDirectory,
                IsEnabled = true,
                AutoPruneEnabled = false,
                AutoPruneVodCount = 1,
                CompressEnabled = true,
                CompressVodCount = 1,
                CreatedUtc = DateTimeOffset.UtcNow.AddDays(-2),
                UpdatedUtc = DateTimeOffset.UtcNow.AddDays(-1)
            };
            dbContext.ChannelConfigurations.Add(channel);
            await dbContext.SaveChangesAsync();

            var oldestPath = Path.Combine(outputDirectory, "alpha-2026-04-25-oldest-1.mp4");
            var newestPath = Path.Combine(outputDirectory, "alpha-2026-04-27-newest-3.mp4");
            File.WriteAllText(oldestPath, "oldest");
            File.WriteAllText(newestPath, "newest");

            dbContext.ArchiveJobs.AddRange(
                new ArchiveJob
                {
                    ChannelConfigurationId = channel.Id,
                    TriggerSource = "stream.offline",
                    Status = ArchiveJobStatus.Succeeded,
                    OutputPath = oldestPath,
                    CreatedUtc = DateTimeOffset.Parse("2026-04-25T12:00:00Z"),
                    CompletedUtc = DateTimeOffset.Parse("2026-04-25T13:00:00Z")
                },
                new ArchiveJob
                {
                    ChannelConfigurationId = channel.Id,
                    TriggerSource = "stream.offline",
                    Status = ArchiveJobStatus.Succeeded,
                    OutputPath = newestPath,
                    CreatedUtc = DateTimeOffset.Parse("2026-04-27T12:00:00Z"),
                    CompletedUtc = DateTimeOffset.Parse("2026-04-27T13:00:00Z")
                });
            await dbContext.SaveChangesAsync();

            await ArchiveFilePruner.PruneSucceededFilesForChannelAsync(
                dbContext,
                channel,
                NullLogger.Instance,
                CancellationToken.None);

            Assert.True(File.Exists(oldestPath));
            Assert.True(File.Exists(newestPath));
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PruneSucceededFilesForChannelAsyncKeepsUncompressedAndGzipRetentionTiers()
    {
        await using var database = await CreateDatabaseAsync();
        var outputDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDirectory);

        try
        {
            await using var scope = database.Services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();

            var channel = new ChannelConfiguration
            {
                TwitchLogin = "alpha",
                OutputDirectory = outputDirectory,
                IsEnabled = true,
                AutoPruneEnabled = true,
                AutoPruneVodCount = 3,
                CompressEnabled = true,
                CompressVodCount = 10,
                CreatedUtc = DateTimeOffset.UtcNow.AddDays(-20),
                UpdatedUtc = DateTimeOffset.UtcNow.AddDays(-1)
            };
            dbContext.ChannelConfigurations.Add(channel);
            await dbContext.SaveChangesAsync();

            var paths = Enumerable.Range(1, 14)
                .Select(index => Path.Combine(outputDirectory, $"alpha-2026-04-{index:00}-vod-{index}.mp4"))
                .ToArray();
            var createdBase = DateTimeOffset.Parse("2026-04-01T12:00:00Z");

            for (var index = 0; index < paths.Length; index += 1)
            {
                await File.WriteAllTextAsync(paths[index], $"vod-content-{index + 1}");
                dbContext.ArchiveJobs.Add(new ArchiveJob
                {
                    ChannelConfigurationId = channel.Id,
                    TriggerSource = "stream.offline",
                    Status = ArchiveJobStatus.Succeeded,
                    OutputPath = paths[index],
                    CreatedUtc = createdBase.AddDays(index),
                    CompletedUtc = createdBase.AddDays(index).AddHours(1)
                });
            }

            await dbContext.SaveChangesAsync();

            await ArchiveFilePruner.PruneSucceededFilesForChannelAsync(
                dbContext,
                channel,
                NullLogger.Instance,
                CancellationToken.None);

            Assert.False(File.Exists(paths[0]));
            Assert.False(File.Exists($"{paths[0]}.gz"));

            foreach (var compressedSourcePath in paths.Skip(1).Take(10))
            {
                Assert.False(File.Exists(compressedSourcePath));
                Assert.True(File.Exists($"{compressedSourcePath}.gz"));
            }

            foreach (var retainedPath in paths.Skip(11))
            {
                Assert.True(File.Exists(retainedPath));
                Assert.False(File.Exists($"{retainedPath}.gz"));
            }

            var compressedJobs = await dbContext.ArchiveJobs
                .Where(x => x.OutputPath != null && x.OutputPath.EndsWith(".gz"))
                .ToListAsync();
            Assert.Equal(10, compressedJobs.Count);

            await using (var compressedStream = File.OpenRead($"{paths[1]}.gz"))
            await using (var gzip = new GZipStream(compressedStream, CompressionMode.Decompress))
            using (var reader = new StreamReader(gzip))
            {
                Assert.Equal("vod-content-2", await reader.ReadToEndAsync());
            }

            var promotedJob = await dbContext.ArchiveJobs
                .SingleAsync(x => x.OutputPath == $"{paths[10]}.gz");
            promotedJob.OutputPath = paths[10];
            channel.AutoPruneVodCount = 4;
            channel.CompressVodCount = 9;
            await dbContext.SaveChangesAsync();

            await ArchiveFilePruner.PruneSucceededFilesForChannelAsync(
                dbContext,
                channel,
                NullLogger.Instance,
                CancellationToken.None);

            Assert.Equal(13, Directory.EnumerateFiles(outputDirectory).Count());
            Assert.Equal($"{paths[10]}.gz", promotedJob.OutputPath);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PruneSucceededFilesForChannelAsyncOnlyPrunesFilesMatchingTheChannelFilenamePrefix()
    {
        await using var database = await CreateDatabaseAsync();
        var outputDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDirectory);

        try
        {
            await using var scope = database.Services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();

            var alphaChannel = new ChannelConfiguration
            {
                TwitchLogin = "alpha",
                OutputDirectory = outputDirectory,
                IsEnabled = true,
                AutoPruneEnabled = true,
                AutoPruneVodCount = 1,
                CreatedUtc = DateTimeOffset.UtcNow.AddDays(-2),
                UpdatedUtc = DateTimeOffset.UtcNow.AddDays(-1)
            };
            var betaChannel = new ChannelConfiguration
            {
                TwitchLogin = "beta",
                OutputDirectory = outputDirectory,
                IsEnabled = true,
                AutoPruneEnabled = true,
                AutoPruneVodCount = 1,
                CreatedUtc = DateTimeOffset.UtcNow.AddDays(-2),
                UpdatedUtc = DateTimeOffset.UtcNow.AddDays(-1)
            };

            dbContext.ChannelConfigurations.AddRange(alphaChannel, betaChannel);
            await dbContext.SaveChangesAsync();

            var alphaNewestPath = Path.Combine(outputDirectory, "alpha-2026-04-27-newest-3.mp4");
            var betaOwnedPath = Path.Combine(outputDirectory, "beta-2026-04-25-owned-by-beta-1.mp4");
            File.WriteAllText(alphaNewestPath, "alpha-newest");
            File.WriteAllText(betaOwnedPath, "beta-owned");

            dbContext.ArchiveJobs.AddRange(
                new ArchiveJob
                {
                    ChannelConfigurationId = alphaChannel.Id,
                    TriggerSource = "stream.offline",
                    Status = ArchiveJobStatus.Succeeded,
                    OutputPath = betaOwnedPath,
                    CreatedUtc = DateTimeOffset.Parse("2026-04-25T12:00:00Z"),
                    CompletedUtc = DateTimeOffset.Parse("2026-04-25T13:00:00Z")
                },
                new ArchiveJob
                {
                    ChannelConfigurationId = alphaChannel.Id,
                    TriggerSource = "stream.offline",
                    Status = ArchiveJobStatus.Succeeded,
                    OutputPath = alphaNewestPath,
                    CreatedUtc = DateTimeOffset.Parse("2026-04-27T12:00:00Z"),
                    CompletedUtc = DateTimeOffset.Parse("2026-04-27T13:00:00Z")
                },
                new ArchiveJob
                {
                    ChannelConfigurationId = betaChannel.Id,
                    TriggerSource = "stream.offline",
                    Status = ArchiveJobStatus.Succeeded,
                    OutputPath = betaOwnedPath,
                    CreatedUtc = DateTimeOffset.Parse("2026-04-25T12:00:00Z"),
                    CompletedUtc = DateTimeOffset.Parse("2026-04-25T13:00:00Z")
                });
            await dbContext.SaveChangesAsync();

            await ArchiveFilePruner.PruneSucceededFilesForChannelAsync(
                dbContext,
                alphaChannel,
                NullLogger.Instance,
                CancellationToken.None);

            Assert.True(File.Exists(alphaNewestPath));
            Assert.True(File.Exists(betaOwnedPath));
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
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
