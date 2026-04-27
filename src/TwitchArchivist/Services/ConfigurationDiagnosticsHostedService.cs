using Microsoft.Extensions.Options;
using TwitchArchivist.Models;

namespace TwitchArchivist.Services;

public class ConfigurationDiagnosticsHostedService(
    IOptions<DownloaderOptions> downloaderOptions,
    RuntimeStatusStore runtimeStatusStore,
    ILogger<ConfigurationDiagnosticsHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var configuredPath = downloaderOptions.Value.ExecutablePath;
            var hasPath = !string.IsNullOrWhiteSpace(configuredPath);
            var isValid = hasPath && File.Exists(configuredPath);

            var message = !hasPath
                ? "TwitchDownloaderCLI path is not configured yet."
                : isValid
                    ? "TwitchDownloaderCLI path is valid."
                    : $"Configured TwitchDownloaderCLI path was not found: {configuredPath}";

            runtimeStatusStore.UpdateDownloaderValidation(configuredPath, isValid, message);
            runtimeStatusStore.UpdateEventSubConnectionState("pending-implementation");
            logger.LogInformation("Configuration diagnostics refreshed");

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
