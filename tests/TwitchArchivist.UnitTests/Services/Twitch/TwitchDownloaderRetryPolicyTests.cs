using TwitchArchivist.Services.Twitch;

namespace TwitchArchivist.UnitTests.Services.Twitch;

public class TwitchDownloaderRetryPolicyTests
{
    [Fact]
    public async Task ExecuteAsync_RetriesForbiddenPlaylistFailuresWithStaggeredDelays()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();

        var result = await TwitchDownloaderRetryPolicy.ExecuteAsync(
            _ =>
            {
                attempts++;
                return Task.FromResult(attempts < 3
                    ? new TwitchDownloaderResult(false, 1, string.Empty, ForbiddenPlaylistFailure)
                    : new TwitchDownloaderResult(true, 0, "done", string.Empty));
            },
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            },
            (_, _, _) => { },
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(3, attempts);
        Assert.Equal([TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)], delays);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotRetryUnknownDownloaderFailures()
    {
        var attempts = 0;

        var result = await TwitchDownloaderRetryPolicy.ExecuteAsync(
            _ =>
            {
                attempts++;
                return Task.FromResult(new TwitchDownloaderResult(false, 1, string.Empty, "unexpected downloader failure"));
            },
            (_, _) => throw new InvalidOperationException("Unknown failures should not be delayed."),
            (_, _, _) => { },
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(1, attempts);
    }

    private const string ForbiddenPlaylistFailure = """
        Unhandled exception. System.AggregateException: One or more errors occurred. (Response status code does not indicate success: 403 (Forbidden).) ---> System.Net.Http.HttpRequestException: Response status code does not indicate success: 403 (Forbidden).
           at TwitchDownloaderCore.VideoDownloader.GetVideoPlaylist(String playlistUrl, CancellationToken cancellationToken)
        """;
}
