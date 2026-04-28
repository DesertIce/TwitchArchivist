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

        var standardOutputLines = SplitOutputLines(result.StandardOutput);
        var versionBanner = standardOutputLines.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(versionBanner) ||
            !versionBanner.StartsWith("TwitchDownloaderCLI ", StringComparison.Ordinal))
        {
            if (result.ExitCode != 0)
            {
                var failureDetail = FirstNonEmpty(result.StandardError, result.StandardOutput, "<no output>");
                return new TwitchDownloaderBinaryVerificationResult(
                    false,
                    $"TwitchDownloaderCLI --version exited with code {result.ExitCode}{Environment.NewLine}{failureDetail}");
            }

            var detail = string.IsNullOrWhiteSpace(versionBanner) ? "<no output>" : versionBanner;
            return new TwitchDownloaderBinaryVerificationResult(
                false,
                $"Configured executable did not identify itself as TwitchDownloaderCLI: {detail}");
        }

        return new TwitchDownloaderBinaryVerificationResult(true, BuildVerifiedMessage(versionBanner, standardOutputLines));
    }

    private static string[] SplitOutputLines(string? value)
    {
        return (value ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    private static string BuildVerifiedMessage(string versionBanner, IReadOnlyList<string> standardOutputLines)
    {
        if (standardOutputLines.Count <= 1)
        {
            return $"TwitchDownloaderCLI binary verified{Environment.NewLine}{versionBanner}";
        }

        return string.Join(
            Environment.NewLine,
            ["TwitchDownloaderCLI binary verified", .. standardOutputLines]);
    }
}
