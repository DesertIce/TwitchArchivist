namespace TwitchArchivist.Services.Twitch;

public sealed record ManagedTwitchDownloaderInstallResult(
    string ExecutablePath,
    string Version,
    bool AlreadyInstalled);
