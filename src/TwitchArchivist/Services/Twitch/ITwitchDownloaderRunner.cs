namespace TwitchArchivist.Services.Twitch;

public interface ITwitchDownloaderRunner
{
    Task<TwitchDownloaderResult> DownloadVideoAsync(string vodId, string outputPath, CancellationToken cancellationToken);
}
