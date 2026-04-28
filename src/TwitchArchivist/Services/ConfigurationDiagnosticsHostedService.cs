using Microsoft.Extensions.Options;
using TwitchArchivist.Models;
using TwitchArchivist.Services.Twitch;

namespace TwitchArchivist.Services;

public class ConfigurationDiagnosticsHostedService(
    IOptionsMonitor<DownloaderOptions> downloaderOptions,
    ITwitchDownloaderBinaryVerifier twitchDownloaderBinaryVerifier,
    ITwitchAccessTokenProvider accessTokenProvider,
    RuntimeStatusStore runtimeStatusStore,
    ILogger<ConfigurationDiagnosticsHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var configuredPath = DownloaderExecutablePathResolver.Resolve(downloaderOptions.CurrentValue.ExecutablePath);
            var verification = await twitchDownloaderBinaryVerifier.VerifyAsync(configuredPath, stoppingToken);
            runtimeStatusStore.UpdateDownloaderValidation(configuredPath, verification.IsValid, verification.Message);

            var authorizationState = await accessTokenProvider.ValidateUserAuthorizationAsync(stoppingToken);
            runtimeStatusStore.UpdateTwitchUserAuthorization(
                authorizationState.IsConfigured,
                authorizationState.IsValid,
                authorizationState.Validity,
                authorizationState.Detail,
                authorizationState.TwitchUserLogin,
                authorizationState.ExpiresUtc,
                authorizationState.LastValidatedUtc);
            logger.LogInformation("Configuration diagnostics refreshed");

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
