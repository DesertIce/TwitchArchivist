using TwitchArchivist.Models;

namespace TwitchArchivist.UnitTests;

public class ScaffoldTests
{
    [Fact]
    public void StorageOptionsUseExpectedDefaultPath()
    {
        var options = new StorageOptions();

        Assert.Equal("data/twitcharchivist.db", options.DatabasePath);
    }
}
