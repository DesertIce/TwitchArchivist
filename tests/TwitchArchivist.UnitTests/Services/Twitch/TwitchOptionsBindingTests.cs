using Microsoft.Extensions.Configuration;
using TwitchArchivist.Models;

namespace TwitchArchivist.UnitTests.Services.Twitch;

public class TwitchOptionsBindingTests
{
    [Fact]
    public void BindsConduitEventSubOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{TwitchOptions.SectionName}:EventSubTransportMode"] = "conduit-websocket",
                [$"{TwitchOptions.SectionName}:EventSubConduitShardCount"] = "8",
                [$"{TwitchOptions.SectionName}:EventSubConduitId"] = "conduit-123",
                [$"{TwitchOptions.SectionName}:EventSubConduitAssignmentTimeoutSeconds"] = "10",
                [$"{TwitchOptions.SectionName}:EventSubConduitReconcileIntervalSeconds"] = "45",
                [$"{TwitchOptions.SectionName}:LiveStateFallbackPollingIntervalSeconds"] = "1200"
            })
            .Build();

        var options = new TwitchOptions();
        configuration.GetSection(TwitchOptions.SectionName).Bind(options);

        Assert.Equal("conduit-websocket", options.EventSubTransportMode);
        Assert.Equal(8, options.EventSubConduitShardCount);
        Assert.Equal("conduit-123", options.EventSubConduitId);
        Assert.Equal(10, options.EventSubConduitAssignmentTimeoutSeconds);
        Assert.Equal(45, options.EventSubConduitReconcileIntervalSeconds);
        Assert.Equal(1200, options.LiveStateFallbackPollingIntervalSeconds);
    }
}
