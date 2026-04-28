namespace TwitchArchivist.Services.Twitch;

public sealed class TwitchDownloaderBinaryVerifier(ICommandLineRunner commandLineRunner) : ITwitchDownloaderBinaryVerifier
{
    public async Task<TwitchDownloaderBinaryVerificationResult> VerifyAsync(string? executablePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return new TwitchDownloaderBinaryVerificationResult(false, "TwitchDownloaderCLI path is not configured yet.");
        }

        if (!File.Exists(executablePath))
        {
            return new TwitchDownloaderBinaryVerificationResult(false, $"Configured TwitchDownloaderCLI path was not found: {executablePath}");
        }

        CommandLineResult result;
        try
        {
            result = await commandLineRunner.RunAsync(executablePath, ["--version"], cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new TwitchDownloaderBinaryVerificationResult(false, $"TwitchDownloaderCLI version check failed: {ex.Message}");
        }

        var versionBanner = result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
            return new TwitchDownloaderBinaryVerificationResult(
                false,
                $"TwitchDownloaderCLI --version exited with code {result.ExitCode}: {detail.Trim()}");
        }

        if (string.IsNullOrWhiteSpace(versionBanner) ||
            !versionBanner.StartsWith("TwitchDownloaderCLI ", StringComparison.Ordinal))
        {
            var detail = string.IsNullOrWhiteSpace(versionBanner) ? "<no output>" : versionBanner;
            return new TwitchDownloaderBinaryVerificationResult(
                false,
                $"Configured executable did not identify itself as TwitchDownloaderCLI: {detail}");
        }

        return new TwitchDownloaderBinaryVerificationResult(true, $"TwitchDownloaderCLI binary verified: {versionBanner}");
    }
}
