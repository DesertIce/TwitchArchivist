using TwitchArchivist.Services.Logging;

namespace TwitchArchivist.UnitTests.Services.Logging;

public class RollingFileLogStoreTests
{
    [Fact]
    public void AppendWritesToCurrentUtcDateFile()
    {
        var root = CreateTempDirectory();
        try
        {
            var clock = new MutableTimeProvider(new DateTimeOffset(2026, 4, 28, 3, 0, 0, TimeSpan.Zero));
            var store = new RollingFileLogStore(root, "twitcharchivist", retainedDayCount: 3, clock);

            store.Append("2026-04-28 03:00:00Z [Information] Test message");

            var logFile = Path.Combine(root, "twitcharchivist-2026-04-28.log");
            Assert.True(File.Exists(logFile));
            Assert.Contains("Test message", File.ReadAllText(logFile));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AppendPrunesFilesOlderThanMostRecentThreeUtcDays()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "twitcharchivist-2026-04-24.log"), "old");
            File.WriteAllText(Path.Combine(root, "twitcharchivist-2026-04-25.log"), "keep");
            File.WriteAllText(Path.Combine(root, "twitcharchivist-2026-04-26.log"), "keep");
            File.WriteAllText(Path.Combine(root, "twitcharchivist-2026-04-27.log"), "keep");

            var clock = new MutableTimeProvider(new DateTimeOffset(2026, 4, 27, 20, 0, 0, TimeSpan.Zero));
            var store = new RollingFileLogStore(root, "twitcharchivist", retainedDayCount: 3, clock);

            store.Append("current");

            Assert.False(File.Exists(Path.Combine(root, "twitcharchivist-2026-04-24.log")));
            Assert.True(File.Exists(Path.Combine(root, "twitcharchivist-2026-04-25.log")));
            Assert.True(File.Exists(Path.Combine(root, "twitcharchivist-2026-04-26.log")));
            Assert.True(File.Exists(Path.Combine(root, "twitcharchivist-2026-04-27.log")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"twitcharchivist-logs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
