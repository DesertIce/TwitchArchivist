namespace TwitchArchivist.Services.Twitch;

public static class TwitchDownloaderFailureClassifier
{
    public static string ToUserFacingMessage(string vodId, string rawFailureDetail)
    {
        var detail = rawFailureDetail.Trim();
        if (IsForbiddenPlaylistFailure(detail))
        {
            return $"TwitchDownloader could not fetch the media playlist for VOD {vodId}. Twitch/CDN returned 403 Forbidden after the VOD was discovered. The VOD may be temporarily blocked for the selected CDN or quality, or it may require viewer authorization despite being visible in Twitch.";
        }

        return detail;
    }

    public static bool IsForbiddenPlaylistFailure(string detail)
    {
        return detail.Contains("403 (Forbidden)", StringComparison.OrdinalIgnoreCase) &&
            detail.Contains("GetVideoPlaylist", StringComparison.Ordinal);
    }
}
