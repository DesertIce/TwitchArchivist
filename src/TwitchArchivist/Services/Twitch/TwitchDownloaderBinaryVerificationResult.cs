namespace TwitchArchivist.Services.Twitch;

public sealed record TwitchDownloaderBinaryVerificationResult(
    bool IsValid,
    string Message);
