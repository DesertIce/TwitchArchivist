namespace TwitchArchivist.Models;

public class DownloaderOptions
{
    public const string SectionName = "Downloader";

    public string? ExecutablePath { get; set; }

    public int MaxConcurrentDownloads { get; set; } = 2;

    public int DownloadInactivityTimeoutSeconds { get; set; } = 600;

    public int DownloadWatchIntervalSeconds { get; set; } = 15;
}
