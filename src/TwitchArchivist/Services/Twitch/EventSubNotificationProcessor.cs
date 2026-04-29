using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Polly;
using TwitchArchivist.Models;
using TwitchArchivist.Persistence;
using TwitchArchivist.Persistence.Entities;

namespace TwitchArchivist.Services.Twitch;

public class EventSubNotificationProcessor(
    IServiceScopeFactory scopeFactory,
    IArchiveJobQueue archiveJobQueue,
    ILogger<EventSubNotificationProcessor> logger,
    Microsoft.Extensions.Options.IOptions<TwitchOptions> twitchOptions,
    TimeProvider timeProvider)
{
    private const int ArchiveJobRetentionLimitPerChannel = 100;
    private static readonly TimeSpan RecentOfflineJobWindow = TimeSpan.FromHours(6);
    private static readonly ArchiveJobStatus[] ActiveArchiveJobStatuses =
    [
        ArchiveJobStatus.Pending,
        ArchiveJobStatus.WaitingForVod,
        ArchiveJobStatus.Running
    ];

    private readonly SemaphoreSlim _archiveJobCreationSync = new(1, 1);

    public async Task HandleStreamOnlineAsync(object? sender, EventSubStreamOnlineEventArgs args)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        var login = args.BroadcasterUserLogin.ToLowerInvariant();
        var channel = await dbContext.ChannelConfigurations.SingleOrDefaultAsync(x => x.TwitchLogin == login);
        if (channel is null)
        {
            return;
        }

        channel.TwitchUserId = args.BroadcasterUserId;
        channel.UpdatedUtc = timeProvider.GetUtcNow();

        var state = await dbContext.StreamSessionStates.SingleOrDefaultAsync(x => x.ChannelConfigurationId == channel.Id);
        if (state is null)
        {
            state = new StreamSessionState
            {
                ChannelConfigurationId = channel.Id,
                CreatedUtc = timeProvider.GetUtcNow()
            };
            dbContext.StreamSessionStates.Add(state);
        }

        state.LastKnownStreamId = args.StreamId;
        state.LastOnlineUtc = args.StartedAtUtc;
        state.UpdatedUtc = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync();
    }

    public async Task HandleStreamOfflineAsync(object? sender, EventSubStreamOfflineEventArgs args)
    {
        var login = args.BroadcasterUserLogin.ToLowerInvariant();
        logger.LogInformation("Received EventSub stream.offline notification for channel {ChannelLogin}", login);

        await _archiveJobCreationSync.WaitAsync();
        try
        {
            await CreateArchiveJobCreationRetryPolicy(login).ExecuteAsync(async () =>
            {
                await CreateArchiveJobFromOfflineAsync(login, args.BroadcasterUserId);
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                ex,
                "Failed to create archive job from stream.offline for channel {ChannelLogin} and broadcaster user id {BroadcasterUserId}",
                login,
                args.BroadcasterUserId);
        }
        finally
        {
            _archiveJobCreationSync.Release();
        }
    }

    private async Task CreateArchiveJobFromOfflineAsync(string login, string broadcasterUserId)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        var channel = await dbContext.ChannelConfigurations.SingleOrDefaultAsync(x => x.TwitchLogin == login);
        if (channel is null)
        {
            logger.LogWarning("Ignoring stream.offline notification for unknown channel {ChannelLogin}", login);
            return;
        }

        channel.TwitchUserId = broadcasterUserId;
        channel.UpdatedUtc = timeProvider.GetUtcNow();

        var state = await dbContext.StreamSessionStates.SingleOrDefaultAsync(x => x.ChannelConfigurationId == channel.Id);
        if (state is null)
        {
            state = new StreamSessionState
            {
                ChannelConfigurationId = channel.Id,
                CreatedUtc = timeProvider.GetUtcNow()
            };
            dbContext.StreamSessionStates.Add(state);
        }

        var now = timeProvider.GetUtcNow();
        logger.LogInformation(
            "Evaluating archive job creation for channel {ChannelLogin}; broadcaster user id {BroadcasterUserId}, last online at {LastOnlineUtc}, last offline at {LastOfflineUtc}, current stream id {LastKnownStreamId}",
            channel.TwitchLogin,
            broadcasterUserId,
            state.LastOnlineUtc,
            state.LastOfflineUtc,
            state.LastKnownStreamId);

        if (state.LastOfflineUtc.HasValue && now - state.LastOfflineUtc.Value < TimeSpan.FromMinutes(10))
        {
            logger.LogInformation(
                "Ignoring duplicate stream.offline notification for channel {ChannelLogin}; last offline at {LastOfflineUtc}",
                channel.TwitchLogin,
                state.LastOfflineUtc.Value);
            return;
        }

        var recentJobCutoffUtc = now - RecentOfflineJobWindow;
        var recentJobs = await dbContext.ArchiveJobs
            .Where(x => x.ChannelConfigurationId == channel.Id)
            .Select(x => new
            {
                x.Id,
                x.Status,
                x.CreatedUtc
            })
            .ToListAsync();

        var recentPendingJob = recentJobs
            .Where(x =>
                x.CreatedUtc >= recentJobCutoffUtc &&
                ActiveArchiveJobStatuses.Contains(x.Status))
            .OrderByDescending(x => x.CreatedUtc)
            .FirstOrDefault();

        if (recentPendingJob is not null)
        {
            logger.LogInformation(
                "Skipping archive job creation for channel {ChannelLogin} because active job {ArchiveJobId} with status {ArchiveJobStatus} already exists from {ArchiveJobCreatedUtc} within the cutoff {RecentJobCutoffUtc}",
                channel.TwitchLogin,
                recentPendingJob.Id,
                recentPendingJob.Status,
                recentPendingJob.CreatedUtc,
                recentJobCutoffUtc);
            return;
        }

        state.LastOfflineUtc = now;
        state.LastProcessedOfflineMessageId = $"{channel.TwitchLogin}:{now:O}";
        state.UpdatedUtc = now;

        var archiveJob = new ArchiveJob
        {
            ChannelConfigurationId = channel.Id,
            TriggerSource = "stream.offline",
            Status = ArchiveJobStatus.Pending,
            CreatedUtc = now
        };
        dbContext.ArchiveJobs.Add(archiveJob);
        await dbContext.SaveChangesAsync();

        try
        {
            await dbContext.TrimArchiveJobsForChannelAsync(channel.Id, ArchiveJobRetentionLimitPerChannel, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Archive job {ArchiveJobId} for channel {ChannelLogin} was created but retention trimming failed",
                archiveJob.Id,
                channel.TwitchLogin);
        }

        logger.LogInformation(
            "Created archive job {ArchiveJobId} for channel {ChannelLogin} from stream.offline at {OfflineDetectedAtUtc}",
            archiveJob.Id,
            channel.TwitchLogin,
            now);

        try
        {
            await CreateArchiveJobEnqueueRetryPolicy(channel.TwitchLogin, archiveJob.Id).ExecuteAsync(async () =>
            {
                await archiveJobQueue.EnqueueAsync(archiveJob.Id, CancellationToken.None);
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                ex,
                "Archive job {ArchiveJobId} for channel {ChannelLogin} was created but could not be enqueued; it will remain pending for recovery",
                archiveJob.Id,
                channel.TwitchLogin);
        }
    }

    private AsyncPolicy CreateArchiveJobCreationRetryPolicy(string channelLogin)
    {
        var maxDelay = TimeSpan.FromSeconds(Math.Max(1, twitchOptions.Value.EventSubRetryMaxDelaySeconds));
        var baseDelay = Math.Max(1, twitchOptions.Value.EventSubRetryBaseDelaySeconds);

        return Policy
            .Handle<DbUpdateException>()
            .Or<SqliteException>()
            .Or<TimeoutException>()
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: retryAttempt =>
                {
                    var delay = TimeSpan.FromSeconds(baseDelay * Math.Pow(2, retryAttempt - 1));
                    return delay <= maxDelay ? delay : maxDelay;
                },
                onRetry: (exception, delay, retryAttempt, _) =>
                {
                    logger.LogWarning(
                        exception,
                        "Retrying archive job creation for channel {ChannelLogin} after transient failure. Attempt {RetryAttempt}/3 in {RetryDelay}",
                        channelLogin,
                        retryAttempt,
                        delay);
                });
    }

    private AsyncPolicy CreateArchiveJobEnqueueRetryPolicy(string channelLogin, int archiveJobId)
    {
        return Policy
            .Handle<InvalidOperationException>()
            .Or<TimeoutException>()
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: retryAttempt => TimeSpan.FromMilliseconds(200 * retryAttempt),
                onRetry: (exception, delay, retryAttempt, _) =>
                {
                    logger.LogWarning(
                        exception,
                        "Retrying archive job enqueue for channel {ChannelLogin} and archive job {ArchiveJobId}. Attempt {RetryAttempt}/3 in {RetryDelay}",
                        channelLogin,
                        archiveJobId,
                        retryAttempt,
                        delay);
                });
    }
}
