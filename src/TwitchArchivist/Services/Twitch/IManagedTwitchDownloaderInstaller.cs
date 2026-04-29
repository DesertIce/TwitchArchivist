namespace TwitchArchivist.Services.Twitch;

public interface IManagedTwitchDownloaderInstaller
{
    Task<ManagedTwitchDownloaderInstallResult> InstallOrUpdateAsync(CancellationToken cancellationToken);
}
