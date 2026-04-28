using Microsoft.Extensions.Logging;
using TwitchArchivist.Services.Logging;

namespace TwitchArchivist.UnitTests.Services.Logging;

public class RecentLogLoggerProviderTests
{
    [Fact]
    public void LoggerSuppressesEfCoreCommandInformationEntries()
    {
        var store = new RecentLogStore(capacity: 10);
        var provider = new RecentLogLoggerProvider(store);
        var logger = provider.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command");

        logger.LogInformation("Executed DbCommand");
        logger.LogWarning("Slow DbCommand");

        var entries = store.GetEntries(LogLevel.Trace);
        Assert.Single(entries);
        Assert.Equal("Slow DbCommand", entries[0].Message);
    }
}
