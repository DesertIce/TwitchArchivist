using Microsoft.Extensions.Options;
using TwitchArchivist.Models;
using TwitchArchivist.Services.Twitch;

namespace TwitchArchivist.Services;

public class ConfigurationDiagnosticsHostedService(
    IOptions<DownloaderOptions> downloaderOptions,
    ITwitchAccessTokenProvider accessTokenProvider,
    RuntimeStatusStore runtimeStatusStore,
    ILogger<ConfigurationDiagnosticsHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var configuredPath = DownloaderExecutablePathResolver.Resolve(downloaderOptions.Value.ExecutablePath);
            var hasPath = !string.IsNullOrWhiteSpace(configuredPath);
            var isValid = hasPath && File.Exists(configuredPath);

            var message = !hasPath
                ? "TwitchDownloaderCLI path is not configured yet."
                : isValid
                    ? "TwitchDownloaderCLI path is valid."
                    : $"Configured TwitchDownloaderCLI path was not found: {configuredPath}";

            runtimeStatusStore.UpdateDownloaderValidation(configuredPath, isValid, message);

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
