using System.Text.Json;
using System.Text.Json.Nodes;

namespace TwitchArchivist.Services;

public sealed class AppSettingsDownloaderConfigurationWriter(string appSettingsPath) : IDownloaderConfigurationWriter
{
    public async Task UpdateDownloaderExecutablePathAsync(string executablePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedPath = executablePath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            throw new ArgumentException("An executable path is required.", nameof(executablePath));
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
}
