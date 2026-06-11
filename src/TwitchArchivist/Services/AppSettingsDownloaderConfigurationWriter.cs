using System.Text.Json;
using System.Text.Json.Nodes;

namespace TwitchArchivist.Services;

public sealed class AppSettingsDownloaderConfigurationWriter(string appSettingsPath) : IDownloaderConfigurationWriter, ITwitchApplicationConfigurationWriter
{
    public async Task UpdateDownloaderSettingsAsync(
        string executablePath,
        int maxConcurrentDownloads,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedPath = executablePath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            throw new ArgumentException("An executable path is required.", nameof(executablePath));
        }

        if (maxConcurrentDownloads < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxConcurrentDownloads),
                maxConcurrentDownloads,
                "Max concurrent downloads must be at least 1.");
        }

        JsonObject rootObject;
        if (File.Exists(appSettingsPath))
        {
            var existingJson = await File.ReadAllTextAsync(appSettingsPath, cancellationToken);
            rootObject = JsonNode.Parse(existingJson)?.AsObject() ?? [];
        }
        else
        {
            rootObject = [];
        }

        var downloaderObject = rootObject["Downloader"] as JsonObject ?? [];
        downloaderObject["ExecutablePath"] = normalizedPath;
        downloaderObject["MaxConcurrentDownloads"] = maxConcurrentDownloads;
        rootObject["Downloader"] = downloaderObject;

        var outputDirectory = Path.GetDirectoryName(appSettingsPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        var updatedJson = rootObject.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true
        });

        await File.WriteAllTextAsync(appSettingsPath, updatedJson, cancellationToken);
    }

    public async Task UpdateTwitchClientCredentialsAsync(string clientId, string clientSecret, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedClientId = clientId?.Trim() ?? string.Empty;
        var normalizedClientSecret = clientSecret?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedClientId))
        {
            throw new ArgumentException("A Twitch client id is required.", nameof(clientId));
        }

        if (string.IsNullOrWhiteSpace(normalizedClientSecret))
        {
            throw new ArgumentException("A Twitch client secret is required.", nameof(clientSecret));
        }

        JsonObject rootObject;
        if (File.Exists(appSettingsPath))
        {
            var existingJson = await File.ReadAllTextAsync(appSettingsPath, cancellationToken);
            rootObject = JsonNode.Parse(existingJson)?.AsObject() ?? [];
        }
        else
        {
            rootObject = [];
        }

        var twitchObject = rootObject["Twitch"] as JsonObject ?? [];
        twitchObject["ClientId"] = normalizedClientId;
        twitchObject["ClientSecret"] = normalizedClientSecret;
        rootObject["Twitch"] = twitchObject;

        var outputDirectory = Path.GetDirectoryName(appSettingsPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        var updatedJson = rootObject.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true
        });

        await File.WriteAllTextAsync(appSettingsPath, updatedJson, cancellationToken);
    }
}
