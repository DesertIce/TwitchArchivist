namespace TwitchArchivist.Services;

public interface IDownloaderConfigurationWriter
{
    Task UpdateDownloaderExecutablePathAsync(string executablePath, CancellationToken cancellationToken);
}
