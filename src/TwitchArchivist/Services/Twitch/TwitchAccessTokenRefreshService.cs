using Microsoft.Extensions.Options;
using TwitchArchivist.Models;

namespace TwitchArchivist.Services.Twitch;

public sealed class TwitchAccessTokenRefreshService(
    ITwitchAccessTokenProvider accessTokenProvider,
    ITwitchLiveStateSynchronizer liveStateSynchronizer,
    IOptions<TwitchOptions> twitchOptions,
    ILogger<TwitchAccessTokenRefreshService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextTokenRefreshUtc = DateTimeOffset.MinValue;
        var nextLiveStatePollUtc = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            var tokenRefreshInterval = ResolveInterval(twitchOptions.Value.AppAccessTokenRefreshPollingIntervalSeconds);
            var liveStateFallbackInterval = ResolveInterval(twitchOptions.Value.LiveStateFallbackPollingIntervalSeconds);

            if (now >= nextTokenRefreshUtc)
            {
                await RefreshTokensAsync(stoppingToken);
                nextTokenRefreshUtc = DateTimeOffset.UtcNow + tokenRefreshInterval;
            }

            if (now >= nextLiveStatePollUtc)
            {
                await PollLiveStateFallbackAsync(stoppingToken);
                nextLiveStatePollUtc = DateTimeOffset.UtcNow + liveStateFallbackInterval;
            }

            var delay = ComputeDelay(nextTokenRefreshUtc, nextLiveStatePollUtc);

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RefreshTokensAsync(CancellationToken stoppingToken)
    {
        try
        {
            await accessTokenProvider.GetAppAccessTokenAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to refresh the Twitch app access token");
        }

        try
        {
            await accessTokenProvider.GetUserAccessTokenAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to refresh the Twitch user access token");
        }
    }

    private async Task PollLiveStateFallbackAsync(CancellationToken stoppingToken)
    {
        try
        {
            await liveStateSynchronizer.SynchronizeAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to poll Helix for current live channel state");
        }
    }

    private static TimeSpan ResolveInterval(int intervalSeconds) =>
        TimeSpan.FromSeconds(Math.Max(1, intervalSeconds));

    private static TimeSpan ComputeDelay(DateTimeOffset nextTokenRefreshUtc, DateTimeOffset nextLiveStatePollUtc)
    {
        var nextWorkUtc = nextTokenRefreshUtc <= nextLiveStatePollUtc ? nextTokenRefreshUtc : nextLiveStatePollUtc;
        var delay = nextWorkUtc - DateTimeOffset.UtcNow;
        return delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1);
    }
}
