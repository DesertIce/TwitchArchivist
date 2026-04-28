using System.Globalization;
using TwitchArchivist.Services.Twitch;

namespace TwitchArchivist.UnitTests.Services.Twitch;

public class ArchiveOutputPathBuilderTests
{
    [Fact]
    public void BuildUsesChannelDateTitleAndVodIdWhenTitleIsAvailable()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

        var path = ArchiveOutputPathBuilder.Build(
            outputDirectory,
            "testchannel",
            new ArchiveVodRecord(
                "123456789",
                new DateTimeOffset(2026, 4, 28, 19, 30, 0, TimeSpan.Zero),
                "Ranked grind session"));

        Assert.Equal(
            Path.Combine(outputDirectory, "testchannel-2026-04-28-Ranked grind session-123456789.mp4"),
            path);
    }

    [Fact]
    public void BuildOmitsTitleSegmentWhenTitleIsMissing()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

        var path = ArchiveOutputPathBuilder.Build(
            outputDirectory,
            "testchannel",
            new ArchiveVodRecord(
                "123456789",
                new DateTimeOffset(2026, 4, 28, 19, 30, 0, TimeSpan.Zero),
                null));

        Assert.Equal(
            Path.Combine(outputDirectory, "testchannel-2026-04-28-123456789.mp4"),
            path);
    }

    [Fact]
    public void BuildSanitizesUnsafeCharactersAndAddsCollisionSuffix()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(outputDirectory);
        var existingPath = Path.Combine(outputDirectory, "testchannel-2026-04-28-Boss fight finale-123456789.mp4");
        File.WriteAllText(existingPath, "existing");

        try
        {
            var path = ArchiveOutputPathBuilder.Build(
                outputDirectory,
                "testchannel",
                new ArchiveVodRecord(
                    "123456789",
                    new DateTimeOffset(2026, 4, 28, 19, 30, 0, TimeSpan.Zero),
                    "Boss:/fight*finale?"));

            Assert.Equal(
                Path.Combine(outputDirectory, "testchannel-2026-04-28-Boss fight finale-123456789_1.mp4"),
                path);
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }
}
