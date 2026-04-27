using Microsoft.Extensions.Logging;
using TwitchArchivist.Services.Logging;

namespace TwitchArchivist.UnitTests.Services;

public class RecentLogStoreTests
{
    [Fact]
    public void AppendEvictsOldestEntriesWhenCapacityIsExceeded()
    {
        var store = new RecentLogStore(capacity: 2);

        store.Append(LogLevel.Information, "first", category: "Test");
        store.Append(LogLevel.Warning, "second", category: "Test");
        store.Append(LogLevel.Error, "third", category: "Test");

        var entries = store.GetEntries(LogLevel.Information);

        Assert.Collection(
            entries,
            entry =>
            {
                Assert.Equal(LogLevel.Warning, entry.Level);
                Assert.Equal("second", entry.Message);
            },
            entry =>
            {
                Assert.Equal(LogLevel.Error, entry.Level);
                Assert.Equal("third", entry.Message);
            });
    }

    [Fact]
    public void ClearRemovesAllEntries()
    {
        var store = new RecentLogStore(capacity: 10);
        store.Append(LogLevel.Information, "message", category: "Test");

        store.Clear();

        Assert.Empty(store.GetEntries(LogLevel.Trace));
    }

    [Fact]
    public void GetEntriesFiltersByMinimumLevel()
    {
        var store = new RecentLogStore(capacity: 10);
        store.Append(LogLevel.Debug, "debug", category: "Test");
        store.Append(LogLevel.Information, "info", category: "Test");
        store.Append(LogLevel.Error, "error", category: "Test");

        var entries = store.GetEntries(LogLevel.Information);

        Assert.Collection(
            entries,
            entry => Assert.Equal("info", entry.Message),
            entry => Assert.Equal("error", entry.Message));
    }
}
