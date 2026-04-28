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

        logger.LogInformation(
            "Processing archive job {ArchiveJobId} for channel {ChannelLogin} with status {Status}",
            job.Id,
            job.ChannelConfiguration.TwitchLogin,
            job.Status);

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

        ArchiveVodRecord? vod = null;
        var vodId = job.VodId;
        if (string.IsNullOrWhiteSpace(vodId))
        {
            var streamSession = await dbContext.StreamSessionStates
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.ChannelConfigurationId == job.ChannelConfigurationId, cancellationToken);

            var now = DateTimeOffset.UtcNow;
            var createdAfterUtc = ArchiveVodDiscoveryStrategy.ResolveCreatedAfterUtc(
                streamSession?.LastOnlineUtc,
                job.CreatedUtc,
                now);
            logger.LogInformation(
                "Archive job {ArchiveJobId} for channel {ChannelLogin} is waiting for a VOD using {DiscoveryMode} lookup; stream started at {StreamStartedAtUtc}, offline detected at {OfflineDetectedAtUtc}, created-after cutoff {CreatedAfterUtc}",
                job.Id,
                job.ChannelConfiguration.TwitchLogin,
                createdAfterUtc is null ? "most-recent-archive" : "stream-start-cutoff",
                streamSession?.LastOnlineUtc,
                job.CreatedUtc,
                createdAfterUtc);

            vod = await WaitForVodAsync(
                job.ChannelConfiguration.TwitchUserId,
                createdAfterUtc,
                job.Id,
                job.ChannelConfiguration.TwitchLogin,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(vod?.Id))
            {
                job.Status = ArchiveJobStatus.Failed;
                job.LastError = "No VOD was discoverable after the configured retry window.";
                job.CompletedUtc = DateTimeOffset.UtcNow;
                await dbContext.SaveChangesAsync(cancellationToken);
                logger.LogWarning(
                    "Archive job {ArchiveJobId} for channel {ChannelLogin} failed because no VOD was discoverable after the configured retry window",
                    job.Id,
                    job.ChannelConfiguration.TwitchLogin);
                return;
            }

            vodId = vod.Id;
            job.VodId = vodId;
            logger.LogInformation(
                "Archive job {ArchiveJobId} for channel {ChannelLogin} matched VOD {VodId}",
                job.Id,
                job.ChannelConfiguration.TwitchLogin,
                vodId);
        }

        job.Status = ArchiveJobStatus.Running;
        var outputPath = string.IsNullOrWhiteSpace(job.OutputPath)
            ? BuildOutputPath(
                job.ChannelConfiguration.OutputDirectory,
                job.ChannelConfiguration.TwitchLogin,
                vod ?? new ArchiveVodRecord(vodId, job.CreatedUtc))
            : job.OutputPath;
        job.OutputPath = outputPath;
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Archive job {ArchiveJobId} for channel {ChannelLogin} is downloading VOD {VodId} to {OutputPath}",
            job.Id,
            job.ChannelConfiguration.TwitchLogin,
            vodId,
            outputPath);
        var result = await twitchDownloaderRunner.DownloadVideoAsync(vodId, outputPath, cancellationToken);
        job.Status = result.Succeeded ? ArchiveJobStatus.Succeeded : ArchiveJobStatus.Failed;
        job.LastError = result.Succeeded ? null : string.Join(Environment.NewLine, [result.StandardError, result.StandardOutput]).Trim();
        job.CompletedUtc = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        if (result.Succeeded)
        {
            logger.LogInformation(
                "Archive job {ArchiveJobId} for channel {ChannelLogin} completed successfully for VOD {VodId}",
                job.Id,
                job.ChannelConfiguration.TwitchLogin,
                vodId);

            try
            {
                await ArchiveFilePruner.PruneSucceededFilesForChannelAsync(
                    dbContext,
                    job.ChannelConfiguration,
                    logger,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Archive job {ArchiveJobId} for channel {ChannelLogin} completed but auto prune failed",
                    job.Id,
                    job.ChannelConfiguration.TwitchLogin);
            }
        }
        else
        {
            logger.LogError(
                "Archive job {ArchiveJobId} for channel {ChannelLogin} failed downloading VOD {VodId}: {FailureDetail}",
                job.Id,
                job.ChannelConfiguration.TwitchLogin,
                vodId,
                job.LastError);
        }
    }

    private async Task<ArchiveVodRecord?> WaitForVodAsync(
        string? broadcasterUserId,
        DateTimeOffset? createdAfterUtc,
        int archiveJobId,
        string channelLogin,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(broadcasterUserId))
        {
            logger.LogWarning(
                "Archive job {ArchiveJobId} for channel {ChannelLogin} cannot discover a VOD because the broadcaster user id is missing",
                archiveJobId,
                channelLogin);
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
            logger.LogInformation(
                "Archive job {ArchiveJobId} for channel {ChannelLogin} is checking Twitch for a VOD (attempt {Attempt}/{MaxAttempts}, created-after cutoff {CreatedAfterUtc})",
                archiveJobId,
                channelLogin,
                attempt + 1,
                Math.Max(1, options.VodDiscoveryRetryCount),
                createdAfterUtc);
            var vod = await twitchHelixClient.GetLatestArchiveVodAsync(
                broadcasterUserId,
                createdAfterUtc,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(vod?.Id))
            {
                logger.LogInformation(
                    "Archive job {ArchiveJobId} for channel {ChannelLogin} found VOD {VodId} created at {VodCreatedAtUtc}",
                    archiveJobId,
                    channelLogin,
                    vod.Id,
                    vod.CreatedAtUtc);
                return vod;
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.VodDiscoveryRetryDelaySeconds)), cancellationToken);
        }

        return null;
    }

    private static string BuildOutputPath(string outputDirectory, string channelName, ArchiveVodRecord vod)
    {
        return ArchiveOutputPathBuilder.Build(outputDirectory, channelName, vod);
    }
}
