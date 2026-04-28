using TwitchArchivist.Services.Twitch;

namespace TwitchArchivist.UnitTests.Services.Twitch;

public class ArchiveVodDiscoveryStrategyTests
{
    [Fact]
    public void ResolveCreatedAfterUtc_UsesMostRecentArchiveForRecentOfflineEvent()
    {
        var streamStartedAtUtc = new DateTimeOffset(2026, 4, 28, 8, 0, 0, TimeSpan.Zero);
        var offlineDetectedAtUtc = new DateTimeOffset(2026, 4, 28, 16, 0, 0, TimeSpan.Zero);
        var nowUtc = new DateTimeOffset(2026, 4, 28, 16, 30, 0, TimeSpan.Zero);

        var createdAfterUtc = ArchiveVodDiscoveryStrategy.ResolveCreatedAfterUtc(
            streamStartedAtUtc,
            offlineDetectedAtUtc,
            nowUtc);

        Assert.Null(createdAfterUtc);
    }

    [Fact]
    public void ResolveCreatedAfterUtc_UsesStreamStartCutoffForOlderOfflineEvent()
    {
        var streamStartedAtUtc = new DateTimeOffset(2026, 4, 28, 8, 0, 0, TimeSpan.Zero);
        var offlineDetectedAtUtc = new DateTimeOffset(2026, 4, 28, 8, 30, 0, TimeSpan.Zero);
        var nowUtc = new DateTimeOffset(2026, 4, 28, 16, 45, 0, TimeSpan.Zero);

        var createdAfterUtc = ArchiveVodDiscoveryStrategy.ResolveCreatedAfterUtc(
            streamStartedAtUtc,
            offlineDetectedAtUtc,
            nowUtc);

        Assert.Equal(streamStartedAtUtc, createdAfterUtc);
    }

    [Fact]
    public void DescribeMode_ReturnsMostRecentModeForRecentOfflineEvent()
    {
        var mode = ArchiveVodDiscoveryStrategy.DescribeMode(
            new DateTimeOffset(2026, 4, 28, 16, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 4, 28, 16, 30, 0, TimeSpan.Zero));

        Assert.Equal("most-recent-archive", mode);
    }
}
