using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TwitchArchivist.Models;
using TwitchArchivist.Persistence;
using TwitchArchivist.Persistence.Entities;
using TwitchArchivist.Services.Twitch;

namespace TwitchArchivist.UnitTests.Services.Twitch;

public class ArchiveJobWorkerTests
{
    [Fact]
    public async Task ExecuteAsync_ProcessesTwoDownloadsConcurrentlyByDefault()
    {
        await using var database = await CreateDatabaseAsync();
        await SeedRunnableJobsAsync(database.Services, 3);

        var archiveJobQueue = new ArchiveJobQueue();
        var downloaderRunner = new BlockingDownloaderRunner(concurrencyThreshold: 2);
        var worker = new ArchiveJobWorker(
            database.Services.GetRequiredService<IServiceScopeFactory>(),
            archiveJobQueue,
            new ThrowingTwitchHelixClient(),
            downloaderRunner,
            Options.Create(new TwitchOptions()),
            Options.Create(new DownloaderOptions()),
            NullLogger<ArchiveJobWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);

        try
        {
            Assert.True(
                await downloaderRunner.WaitForConcurrencyThresholdAsync(TimeSpan.FromSeconds(1)),
                "Expected two downloads to be active at the same time.");
        }
        finally
        {
            downloaderRunner.ReleaseAll();
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExecuteAsync_HonorsConfiguredDownloadConcurrency()
    {
        await using var database = await CreateDatabaseAsync();
        await SeedRunnableJobsAsync(database.Services, 4);

        var archiveJobQueue = new ArchiveJobQueue();
        var downloaderRunner = new BlockingDownloaderRunner(concurrencyThreshold: 3);
        var worker = new ArchiveJobWorker(
            database.Services.GetRequiredService<IServiceScopeFactory>(),
            archiveJobQueue,
            new ThrowingTwitchHelixClient(),
            downloaderRunner,
            Options.Create(new TwitchOptions()),
            Options.Create(new DownloaderOptions { MaxConcurrentDownloads = 3 }),
            NullLogger<ArchiveJobWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);

        try
        {
            Assert.True(
                await downloaderRunner.WaitForConcurrencyThresholdAsync(TimeSpan.FromSeconds(1)),
                "Expected three downloads to be active at the same time.");
        }
        finally
        {
            downloaderRunner.ReleaseAll();
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExecuteAsync_TreatsConfiguredDownloadConcurrencyBelowOneAsOne()
    {
        await using var database = await CreateDatabaseAsync();
        await SeedRunnableJobsAsync(database.Services, 2);

        var archiveJobQueue = new ArchiveJobQueue();
        var downloaderRunner = new BlockingDownloaderRunner(concurrencyThreshold: 1);
        var worker = new ArchiveJobWorker(
            database.Services.GetRequiredService<IServiceScopeFactory>(),
            archiveJobQueue,
            new ThrowingTwitchHelixClient(),
            downloaderRunner,
            Options.Create(new TwitchOptions()),
            Options.Create(new DownloaderOptions { MaxConcurrentDownloads = 0 }),
            NullLogger<ArchiveJobWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);

        try
        {
            Assert.True(
                await downloaderRunner.WaitForConcurrencyThresholdAsync(TimeSpan.FromSeconds(1)),
                "Expected one download to start.");

            await Task.Delay(TimeSpan.FromMilliseconds(200));

            Assert.Equal(1, downloaderRunner.StartedDownloadCount);
        }
        finally
        {
            downloaderRunner.ReleaseAll();
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExecuteAsync_MarksActiveJobFailedWhenDownloaderThrows()
    {
        await using var database = await CreateDatabaseAsync();
        await SeedRunnableJobsAsync(database.Services, 1);

        var archiveJobQueue = new ArchiveJobQueue();
        var worker = new ArchiveJobWorker(
            database.Services.GetRequiredService<IServiceScopeFactory>(),
            archiveJobQueue,
            new ThrowingTwitchHelixClient(),
            new ThrowingDownloaderRunner(new InvalidOperationException("downloader crashed")),
            Options.Create(new TwitchOptions()),
            Options.Create(new DownloaderOptions()),
            NullLogger<ArchiveJobWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);

        try
        {
            var job = await WaitForSingleJobStatusAsync(
                database.Services,
                ArchiveJobStatus.Failed,
                TimeSpan.FromSeconds(3));

            Assert.Contains("downloader crashed", job.LastError, StringComparison.Ordinal);
            Assert.NotNull(job.CompletedUtc);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExecuteAsync_UsesChannelAliasInGeneratedOutputPath()
    {
        await using var database = await CreateDatabaseAsync();
        await SeedRunnableJobsAsync(database.Services, 1, "Alpha Archive");

        var worker = new ArchiveJobWorker(
            database.Services.GetRequiredService<IServiceScopeFactory>(),
            new ArchiveJobQueue(),
            new ThrowingTwitchHelixClient(),
            new SuccessfulDownloaderRunner(),
            Options.Create(new TwitchOptions()),
            Options.Create(new DownloaderOptions()),
            NullLogger<ArchiveJobWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);

        try
        {
            var job = await WaitForSingleJobStatusAsync(
                database.Services,
                ArchiveJobStatus.Succeeded,
                TimeSpan.FromSeconds(3));

            Assert.StartsWith("Alpha Archive-", Path.GetFileName(job.OutputPath), StringComparison.Ordinal);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static async Task SeedRunnableJobsAsync(IServiceProvider services, int count, string? alias = null)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();

        var channel = new ChannelConfiguration
        {
            TwitchLogin = "alpha",
            Alias = alias,
            TwitchUserId = "user-alpha",
            OutputDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            IsEnabled = true,
            CreatedUtc = DateTimeOffset.UtcNow.AddDays(-1),
            UpdatedUtc = DateTimeOffset.UtcNow
        };
        dbContext.ChannelConfigurations.Add(channel);
        await dbContext.SaveChangesAsync();

        for (var i = 1; i <= count; i += 1)
        {
            dbContext.ArchiveJobs.Add(new ArchiveJob
            {
                ChannelConfigurationId = channel.Id,
                TriggerSource = "test",
                VodId = $"vod-{i}",
                Status = ArchiveJobStatus.Pending,
                CreatedUtc = DateTimeOffset.UtcNow.AddMinutes(-i)
            });
        }

        await dbContext.SaveChangesAsync();
    }

    private static async Task<TestDatabase> CreateDatabaseAsync()
    {
        var connectionString = $"Data Source={Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<TwitchArchivistDbContext>(options => options.UseSqlite(connectionString));

        var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        await dbContext.Database.EnsureCreatedAsync();

        return new TestDatabase(provider, connection);
    }

    private static async Task<ArchiveJob> WaitForSingleJobStatusAsync(
        IServiceProvider services,
        ArchiveJobStatus status,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var scope = services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
            var job = await dbContext.ArchiveJobs.SingleAsync();
            if (job.Status == status)
            {
                return job;
            }

            await Task.Delay(50);
        }

        await using var verificationScope = services.CreateAsyncScope();
        var verificationDbContext = verificationScope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        var finalJob = await verificationDbContext.ArchiveJobs.SingleAsync();
        throw new TimeoutException($"Timed out waiting for job status {status}. Last status was {finalJob.Status}.");
    }

    private sealed class BlockingDownloaderRunner(int concurrencyThreshold) : ITwitchDownloaderRunner
    {
        private readonly TaskCompletionSource _releaseDownloads = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _thresholdReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _sync = new();
        private int _activeDownloads;
        private int _startedDownloadCount;

        public int StartedDownloadCount
        {
            get
            {
                lock (_sync)
                {
                    return _startedDownloadCount;
                }
            }
        }

        public async Task<TwitchDownloaderResult> DownloadVideoAsync(
            string vodId,
            string outputPath,
            CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                _activeDownloads += 1;
                _startedDownloadCount += 1;
                if (_activeDownloads >= concurrencyThreshold)
                {
                    _thresholdReached.TrySetResult();
                }
            }

            try
            {
                await _releaseDownloads.Task.WaitAsync(cancellationToken);
                return new TwitchDownloaderResult(true, 0, "downloaded", string.Empty);
            }
            finally
            {
                lock (_sync)
                {
                    _activeDownloads -= 1;
                }
            }
        }

        public void ReleaseAll()
        {
            _releaseDownloads.TrySetResult();
        }

        public async Task<bool> WaitForConcurrencyThresholdAsync(TimeSpan timeout)
        {
            try
            {
                await _thresholdReached.Task.WaitAsync(timeout);
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }
    }

    private sealed class ThrowingDownloaderRunner(Exception exception) : ITwitchDownloaderRunner
    {
        public Task<TwitchDownloaderResult> DownloadVideoAsync(
            string vodId,
            string outputPath,
            CancellationToken cancellationToken) => throw exception;
    }

    private sealed class SuccessfulDownloaderRunner : ITwitchDownloaderRunner
    {
        public Task<TwitchDownloaderResult> DownloadVideoAsync(
            string vodId,
            string outputPath,
            CancellationToken cancellationToken) =>
            Task.FromResult(new TwitchDownloaderResult(true, 0, "downloaded", string.Empty));
    }

    private sealed class ThrowingTwitchHelixClient : ITwitchHelixClient
    {
        public Task<string?> ResolveUserIdAsync(
            string twitchLogin,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<TwitchChannelSearchResult>> SearchChannelsAsync(
            string query,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<TwitchLiveStreamState>> GetLiveStreamsByLoginsAsync(
            IReadOnlyList<string> twitchLogins,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ArchiveVodRecord?> GetLatestArchiveVodAsync(
            string broadcasterUserId,
            DateTimeOffset? createdAfterUtc,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<EventSubSubscriptionRecord>> GetEventSubscriptionsAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<EventSubSubscriptionRecord> CreateStreamSubscriptionAsync(
            string subscriptionType,
            string broadcasterUserId,
            string sessionId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
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
