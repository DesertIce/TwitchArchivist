using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TwitchArchivist.Models;
using TwitchArchivist.Services.Twitch;

namespace TwitchArchivist.UnitTests.Services.Twitch;

public class TwitchAccessTokenRefreshServiceTests
{
    [Fact]
    public async Task RefreshServicePollsAccessTokenProviderOnInterval()
    {
        var provider = new CountingAccessTokenProvider();
        var options = Options.Create(new TwitchOptions
        {
            ClientId = "client-id",
            ClientSecret = "client-secret",
            AppAccessTokenRefreshPollingIntervalSeconds = 1
        });
        using var service = new TwitchAccessTokenRefreshService(
            provider,
            options,
            NullLogger<TwitchAccessTokenRefreshService>.Instance);

        using var cancellationTokenSource = new CancellationTokenSource();
        await service.StartAsync(cancellationTokenSource.Token);

        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        await service.StopAsync(CancellationToken.None);

        Assert.True(provider.CallCount >= 2, $"Expected at least 2 refresh attempts but saw {provider.CallCount}.");
    }

    private sealed class CountingAccessTokenProvider : ITwitchAccessTokenProvider
    {
        public int CallCount { get; private set; }

        public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult<string?>("token");
        }
    }
}
