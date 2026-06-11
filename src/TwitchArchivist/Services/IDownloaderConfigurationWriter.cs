namespace TwitchArchivist.Services;

public interface IDownloaderConfigurationWriter
{
    Task UpdateDownloaderSettingsAsync(
        string executablePath,
        int maxConcurrentDownloads,
        CancellationToken cancellationToken);
}
