using TwitchArchivist.Services.Twitch;

namespace TwitchArchivist.UnitTests;

public class ArchiveVodSelectorTests
{
    [Fact]
    public void SelectLatestEligibleVodReturnsNewestVodWhenNoCutoffExists()
    {
        var result = ArchiveVodSelector.SelectLatestEligibleVod(
            [
                new ArchiveVodRecord("old", new DateTimeOffset(2026, 4, 27, 10, 0, 0, TimeSpan.Zero)),
                new ArchiveVodRecord("new", new DateTimeOffset(2026, 4, 27, 12, 0, 0, TimeSpan.Zero))
            ],
            createdAfterUtc: null);

        Assert.Equal("new", result?.Id);
    }

    [Fact]
    public void SelectLatestEligibleVodSkipsOlderArchivesBeforeCurrentStreamStart()
    {
        var streamStartedAtUtc = new DateTimeOffset(2026, 4, 27, 12, 0, 0, TimeSpan.Zero);

        var result = ArchiveVodSelector.SelectLatestEligibleVod(
            [
                new ArchiveVodRecord("stale", new DateTimeOffset(2026, 4, 27, 11, 30, 0, TimeSpan.Zero)),
                new ArchiveVodRecord("current", new DateTimeOffset(2026, 4, 27, 12, 5, 0, TimeSpan.Zero))
            ],
            streamStartedAtUtc);

        Assert.Equal("current", result?.Id);
    }

    [Fact]
    public void SelectLatestEligibleVodReturnsNullWhenNoArchiveMeetsCutoff()
    {
        var streamStartedAtUtc = new DateTimeOffset(2026, 4, 27, 12, 0, 0, TimeSpan.Zero);

        var result = ArchiveVodSelector.SelectLatestEligibleVod(
            [
                new ArchiveVodRecord("stale", new DateTimeOffset(2026, 4, 27, 11, 59, 59, TimeSpan.Zero))
            ],
            streamStartedAtUtc);

        Assert.Null(result);
    }
}
