using TwitchArchivist.Services.Twitch;

namespace TwitchArchivist.UnitTests.Services.Twitch;

public class TwitchHelixUserIdsTests
{
    [Theory]
    [InlineData("29430843", true)]
    [InlineData("1", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("delete-me-resolved", false)]
    [InlineData("123a", false)]
    [InlineData(" 123 ", false)]
    public void IsHelixUserId_MatchesTwitchNumericSnowflakeRules(string? value, bool expected)
        => Assert.Equal(expected, TwitchHelixUserIds.IsHelixUserId(value));
}
