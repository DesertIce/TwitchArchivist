namespace TwitchArchivist.Services.Twitch;

public static class ArchiveVodSelector
{
    public static ArchiveVodRecord? SelectLatestEligibleVod(
        IEnumerable<ArchiveVodRecord> vods,
        DateTimeOffset? createdAfterUtc)
    {
        return vods
            .Where(vod => string.IsNullOrWhiteSpace(vod.Id) is false)
            .OrderByDescending(vod => vod.CreatedAtUtc)
            .FirstOrDefault(vod => createdAfterUtc is null || vod.CreatedAtUtc >= createdAfterUtc.Value);
    }
}
