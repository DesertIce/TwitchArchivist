using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using TwitchArchivist.Services;

namespace TwitchArchivist.Services.Twitch;

public sealed class ManagedTwitchDownloaderInstaller(
    HttpClient httpClient,
    IHostEnvironment hostEnvironment,
    IDownloaderConfigurationWriter downloaderConfigurationWriter) : IManagedTwitchDownloaderInstaller
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/lay295/TwitchDownloader/releases/latest";
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<ManagedTwitchDownloaderInstallResult> InstallOrUpdateAsync(CancellationToken cancellationToken)
    {
        var release = await GetLatestReleaseAsync(cancellationToken);
        var asset = release.Assets.FirstOrDefault(static asset =>
            asset.Name.EndsWith("-Windows-x64.zip", StringComparison.OrdinalIgnoreCase) &&
            asset.Name.StartsWith("TwitchDownloaderCLI-", StringComparison.OrdinalIgnoreCase));

        if (asset is null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
        {
            throw new InvalidOperationException("The latest TwitchDownloader release did not expose a Windows x64 CLI archive.");
        }

        var toolRoot = Path.Combine(hostEnvironment.ContentRootPath, "tools", "TwitchDownloaderCLI");
        var currentDirectory = Path.Combine(toolRoot, "current");
        var executablePath = Path.Combine(currentDirectory, "TwitchDownloaderCLI.exe");
        var manifestPath = Path.Combine(toolRoot, "managed-install.json");

        if (await IsCurrentInstallReusableAsync(manifestPath, executablePath, release.TagName, cancellationToken))
        {
            await downloaderConfigurationWriter.UpdateDownloaderExecutablePathAsync(executablePath, cancellationToken);
            return new ManagedTwitchDownloaderInstallResult(executablePath, release.TagName, true);
        }

        Directory.CreateDirectory(toolRoot);

        var archivePath = Path.Combine(toolRoot, asset.Name);
        var stagingDirectory = Path.Combine(toolRoot, $".staging-{Guid.NewGuid():N}");

        try
        {
            await DownloadArchiveAsync(asset.BrowserDownloadUrl, archivePath, cancellationToken);
            ZipFile.ExtractToDirectory(archivePath, stagingDirectory, overwriteFiles: true);

            var stagedExecutablePath = Path.Combine(stagingDirectory, "TwitchDownloaderCLI.exe");
            if (!File.Exists(stagedExecutablePath))
            {
                throw new InvalidOperationException("The downloaded TwitchDownloader archive did not contain TwitchDownloaderCLI.exe.");
            }

            if (Directory.Exists(currentDirectory))
            {
                Directory.Delete(currentDirectory, recursive: true);
            }

            Directory.Move(stagingDirectory, currentDirectory);

            var manifest = new ManagedTwitchDownloaderManifest
            {
                Version = release.TagName,
                ExecutablePath = Path.GetRelativePath(hostEnvironment.ContentRootPath, executablePath)
            };

            var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            await File.WriteAllTextAsync(manifestPath, manifestJson, cancellationToken);
            await downloaderConfigurationWriter.UpdateDownloaderExecutablePathAsync(executablePath, cancellationToken);

            return new ManagedTwitchDownloaderInstallResult(executablePath, release.TagName, false);
        }
        finally
        {
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }

            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
    }

    private async Task<TwitchDownloaderRelease> GetLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(LatestReleaseUrl, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var release = await JsonSerializer.DeserializeAsync<TwitchDownloaderRelease>(stream, SerializerOptions, cancellationToken);
        return release ?? throw new InvalidOperationException("GitHub returned an empty TwitchDownloader release payload.");
    }

    private async Task DownloadArchiveAsync(string downloadUrl, string archivePath, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(downloadUrl, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destinationStream = File.Create(archivePath);
        await sourceStream.CopyToAsync(destinationStream, cancellationToken);
    }

    private async Task<bool> IsCurrentInstallReusableAsync(
        string manifestPath,
        string executablePath,
        string requestedVersion,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(manifestPath) || !File.Exists(executablePath))
        {
            return false;
        }

        await using var stream = File.OpenRead(manifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<ManagedTwitchDownloaderManifest>(stream, SerializerOptions, cancellationToken);
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version))
        {
            return false;
        }

        if (!string.Equals(manifest.Version, requestedVersion, StringComparison.Ordinal))
        {
            return false;
        }

        var recordedPath = manifest.ExecutablePath;
        if (!string.IsNullOrWhiteSpace(recordedPath))
        {
            var normalizedRecordedPath = Path.IsPathRooted(recordedPath)
                ? recordedPath
                : Path.GetFullPath(recordedPath, hostEnvironment.ContentRootPath);

            if (!string.Equals(
                    normalizedRecordedPath,
                    executablePath,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private sealed class TwitchDownloaderRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; init; } = string.Empty;

        [JsonPropertyName("assets")]
        public List<TwitchDownloaderReleaseAsset> Assets { get; init; } = [];
    }

    private sealed class TwitchDownloaderReleaseAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; init; } = string.Empty;
    }

    private sealed class ManagedTwitchDownloaderManifest
    {
        public string Version { get; init; } = string.Empty;

        public string ExecutablePath { get; init; } = string.Empty;
    }
}
