namespace TwitchArchivist.Services.Twitch;

public sealed record TwitchDownloaderResult(
    bool Succeeded,
    int ExitCode,
    string StandardOutput,
    string StandardError);
