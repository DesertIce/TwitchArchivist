namespace TwitchArchivist.Services.Twitch;

public interface ITwitchDownloaderBinaryVerifier
{
    Task<TwitchDownloaderBinaryVerificationResult> VerifyAsync(string? executablePath, CancellationToken cancellationToken);
}
