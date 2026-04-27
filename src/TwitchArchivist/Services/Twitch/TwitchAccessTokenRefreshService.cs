using Microsoft.Extensions.Options;
using TwitchArchivist.Models;

namespace TwitchArchivist.Services.Twitch;

public sealed class TwitchAccessTokenRefreshService(
    ITwitchAccessTokenProvider accessTokenProvider,
    IOptions<TwitchOptions> twitchOptions,
    ILogger<TwitchAccessTokenRefreshService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await accessTokenProvider.GetAppAccessTokenAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
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
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to refresh the Twitch user access token");
            }

            var pollingIntervalSeconds = Math.Max(1, twitchOptions.Value.AppAccessTokenRefreshPollingIntervalSeconds);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(pollingIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
