namespace TwitchArchivist.Services.Twitch;

public static class ArchiveVodDiscoveryStrategy
{
    public static readonly TimeSpan RecentOfflineWindow = TimeSpan.FromHours(6);

    public static DateTimeOffset? ResolveCreatedAfterUtc(
        DateTimeOffset? streamStartedAtUtc,
        DateTimeOffset offlineDetectedAtUtc,
        DateTimeOffset nowUtc)
    {
        if (nowUtc - offlineDetectedAtUtc <= RecentOfflineWindow)
        {
            return null;
        }

        return streamStartedAtUtc;
    }

    public static string DescribeMode(DateTimeOffset offlineDetectedAtUtc, DateTimeOffset nowUtc)
        => ResolveCreatedAfterUtc(streamStartedAtUtc: null, offlineDetectedAtUtc, nowUtc) is null
            ? "most-recent-archive"
            : "stream-start-cutoff";
}
