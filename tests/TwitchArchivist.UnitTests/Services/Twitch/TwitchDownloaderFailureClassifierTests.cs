using TwitchArchivist.Services.Twitch;

namespace TwitchArchivist.UnitTests.Services.Twitch;

public class TwitchDownloaderFailureClassifierTests
{
    [Fact]
    public void ToUserFacingMessage_ExplainsForbiddenPlaylistFailure()
    {
        const string rawFailure = """
            Unhandled exception. System.AggregateException: One or more errors occurred. (Response status code does not indicate success: 403 (Forbidden).) ---> System.Net.Http.HttpRequestException: Response status code does not indicate success: 403 (Forbidden).
               at System.Net.Http.HttpResponseMessage.EnsureSuccessStatusCode()
               at TwitchDownloaderCore.VideoDownloader.GetVideoPlaylist(String playlistUrl, CancellationToken cancellationToken)
            TwitchDownloaderCLI 1.56.4 Copyright (c) lay295 and contributors
            [STATUS] - Fetching Video Info [1/4]
            """;

        var message = TwitchDownloaderFailureClassifier.ToUserFacingMessage("123456789", rawFailure);

        Assert.Equal(
            "TwitchDownloader could not fetch the media playlist for VOD 123456789. Twitch/CDN returned 403 Forbidden after the VOD was discovered. The VOD may be temporarily blocked for the selected CDN or quality, or it may require viewer authorization despite being visible in Twitch.",
            message);
    }

    [Fact]
    public void ToUserFacingMessage_PreservesUnknownFailureDetail()
    {
        const string rawFailure = "unexpected downloader failure";

        var message = TwitchDownloaderFailureClassifier.ToUserFacingMessage("123456789", rawFailure);

        Assert.Equal(rawFailure, message);
    }
}
