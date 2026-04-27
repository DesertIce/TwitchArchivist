using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TwitchArchivist.Models;
using TwitchArchivist.Persistence;
using TwitchArchivist.Persistence.Entities;

namespace TwitchArchivist.Services.Twitch;

public class ArchiveJobWorker(
    IServiceScopeFactory scopeFactory,
    IArchiveJobQueue archiveJobQueue,
    ITwitchHelixClient twitchHelixClient,
    ITwitchDownloaderRunner twitchDownloaderRunner,
    IOptions<TwitchOptions> twitchOptions,
    ILogger<ArchiveJobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await QueueRecoverableJobsAsync(stoppingToken);

        await foreach (var archiveJobId in archiveJobQueue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessJobAsync(archiveJobId, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process archive job {ArchiveJobId}", archiveJobId);
            }
        }
    }

    private async Task QueueRecoverableJobsAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        var recoverableJobs = await dbContext.ArchiveJobs
            .Where(x => x.Status == ArchiveJobStatus.Pending || x.Status == ArchiveJobStatus.WaitingForVod || x.Status == ArchiveJobStatus.Running)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        foreach (var jobId in recoverableJobs)
        {
            await archiveJobQueue.EnqueueAsync(jobId, cancellationToken);
        }
    }

    private async Task ProcessJobAsync(int archiveJobId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        var job = await dbContext.ArchiveJobs
            .Include(x => x.ChannelConfiguration)
            .SingleOrDefaultAsync(x => x.Id == archiveJobId, cancellationToken);

        if (job is null)
        {
            return;
        }

        if (!job.ChannelConfiguration.IsEnabled)
        {
            job.Status = ArchiveJobStatus.Skipped;
            job.LastError = "Channel is disabled.";
            job.CompletedUtc = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        job.Status = ArchiveJobStatus.WaitingForVod;
        job.StartedUtc = DateTimeOffset.UtcNow;
        job.AttemptCount += 1;
        await dbContext.SaveChangesAsync(cancellationToken);

        var vodId = job.VodId;
        if (string.IsNullOrWhiteSpace(vodId))
        {
            var streamSession = await dbContext.StreamSessionStates
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.ChannelConfigurationId == job.ChannelConfigurationId, cancellationToken);

            vodId = await WaitForVodIdAsync(
                job.ChannelConfiguration.TwitchUserId,
                streamSession?.LastOnlineUtc,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(vodId))
            {
                job.Status = ArchiveJobStatus.Failed;
                job.LastError = "No VOD was discoverable after the configured retry window.";
                job.CompletedUtc = DateTimeOffset.UtcNow;
                await dbContext.SaveChangesAsync(cancellationToken);
                return;
            }

            job.VodId = vodId;
        }

        job.Status = ArchiveJobStatus.Running;
        var outputPath = BuildOutputPath(job.ChannelConfiguration.OutputDirectory, vodId);
        job.OutputPath = outputPath;
        await dbContext.SaveChangesAsync(cancellationToken);

        var result = await twitchDownloaderRunner.DownloadVideoAsync(vodId, outputPath, cancellationToken);
        job.Status = result.Succeeded ? ArchiveJobStatus.Succeeded : ArchiveJobStatus.Failed;
        job.LastError = result.Succeeded ? null : string.Join(Environment.NewLine, [result.StandardError, result.StandardOutput]).Trim();
        job.CompletedUtc = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<string?> WaitForVodIdAsync(
        string? broadcasterUserId,
        DateTimeOffset? streamStartedAtUtc,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(broadcasterUserId))
        {
            return null;
        }

        var options = twitchOptions.Value;
        var initialDelay = TimeSpan.FromSeconds(Math.Max(0, options.VodDiscoveryInitialDelaySeconds));
        if (initialDelay > TimeSpan.Zero)
        {
            await Task.Delay(initialDelay, cancellationToken);
        }

        for (var attempt = 0; attempt < Math.Max(1, options.VodDiscoveryRetryCount); attempt += 1)
        {
            var vod = await twitchHelixClient.GetLatestArchiveVodAsync(
                broadcasterUserId,
                streamStartedAtUtc,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(vod?.Id))
            {
                return vod.Id;
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.VodDiscoveryRetryDelaySeconds)), cancellationToken);
        }

        return null;
    }

    private static string BuildOutputPath(string outputDirectory, string vodId)
    {
        var basePath = Path.Combine(outputDirectory, $"{vodId}.mp4");
        if (!File.Exists(basePath))
        {
            return basePath;
        }

        var counter = 1;
        while (true)
        {
            var candidate = Path.Combine(outputDirectory, $"{vodId}_{counter}.mp4");
            if (!File.Exists(candidate))
            {
                return candidate;
            }

            counter += 1;
        }
    }
}
