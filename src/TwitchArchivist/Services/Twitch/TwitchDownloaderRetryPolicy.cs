using Polly;

namespace TwitchArchivist.Services.Twitch;

public static class TwitchDownloaderRetryPolicy
{
    private static readonly TimeSpan[] ForbiddenPlaylistRetryDelays =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5)
    ];

    public static Task<TwitchDownloaderResult> ExecuteAsync(
        Func<CancellationToken, Task<TwitchDownloaderResult>> downloadAsync,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        Action<int, TimeSpan, string> onRetry,
        CancellationToken cancellationToken)
    {
        var policy = Policy<TwitchDownloaderResult>
            .HandleResult(result => IsRetryableForbiddenPlaylistFailure(result))
            .RetryAsync(
                ForbiddenPlaylistRetryDelays.Length,
                async (outcome, retryAttempt, _) =>
                {
                    var delay = ForbiddenPlaylistRetryDelays[retryAttempt - 1];
                    var failureDetail = BuildFailureDetail(outcome.Result);
                    onRetry(retryAttempt, delay, failureDetail);
                    await delayAsync(delay, cancellationToken);
                });

        return policy.ExecuteAsync(downloadAsync, cancellationToken);
    }

    public static bool IsRetryableForbiddenPlaylistFailure(TwitchDownloaderResult result)
    {
        return !result.Succeeded &&
            TwitchDownloaderFailureClassifier.IsForbiddenPlaylistFailure(BuildFailureDetail(result));
    }

    private static string BuildFailureDetail(TwitchDownloaderResult result)
    {
        return string.Join(Environment.NewLine, [result.StandardError, result.StandardOutput]).Trim();
    }
}
