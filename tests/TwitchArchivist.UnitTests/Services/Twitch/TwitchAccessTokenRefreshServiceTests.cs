using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TwitchArchivist.Models;
using TwitchArchivist.Services.Twitch;

namespace TwitchArchivist.UnitTests.Services.Twitch;

public class TwitchAccessTokenRefreshServiceTests
{
    [Fact]
    public async Task RefreshServicePollsTokensOnTokenIntervalButLiveStateOnFallbackInterval()
    {
        var provider = new CountingAccessTokenProvider();
        var synchronizer = new CountingLiveStateSynchronizer();
        var options = Options.Create(new TwitchOptions
        {
            ClientId = "client-id",
            ClientSecret = "client-secret",
            AppAccessTokenRefreshPollingIntervalSeconds = 1,
            LiveStateFallbackPollingIntervalSeconds = 60
        });
        using var service = new TwitchAccessTokenRefreshService(
            provider,
            synchronizer,
            options,
            NullLogger<TwitchAccessTokenRefreshService>.Instance);

        using var cancellationTokenSource = new CancellationTokenSource();
        await service.StartAsync(cancellationTokenSource.Token);

        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        await service.StopAsync(CancellationToken.None);

        Assert.True(provider.AppCallCount >= 2, $"Expected at least 2 app-token refresh attempts but saw {provider.AppCallCount}.");
        Assert.True(provider.UserCallCount >= 2, $"Expected at least 2 user-token refresh attempts but saw {provider.UserCallCount}.");
        Assert.Equal(1, synchronizer.CallCount);
    }

    [Fact]
    public async Task RefreshServiceHonorsConfiguredLiveStateFallbackPollingInterval()
    {
        var provider = new CountingAccessTokenProvider();
        var synchronizer = new CountingLiveStateSynchronizer();
        var options = Options.Create(new TwitchOptions
        {
            ClientId = "client-id",
            ClientSecret = "client-secret",
            AppAccessTokenRefreshPollingIntervalSeconds = 1,
            LiveStateFallbackPollingIntervalSeconds = 1
        });
        using var service = new TwitchAccessTokenRefreshService(
            provider,
            synchronizer,
            options,
            NullLogger<TwitchAccessTokenRefreshService>.Instance);

        using var cancellationTokenSource = new CancellationTokenSource();
        await service.StartAsync(cancellationTokenSource.Token);

        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        await service.StopAsync(CancellationToken.None);

        Assert.True(synchronizer.CallCount >= 2, $"Expected at least 2 live-state sync attempts but saw {synchronizer.CallCount}.");
    }

    private sealed class CountingAccessTokenProvider : ITwitchAccessTokenProvider
    {
        public int AppCallCount { get; private set; }

        public int UserCallCount { get; private set; }

        public string? BuildUserAuthorizationUrl(string state, string redirectUri) => null;

        public Task ExchangeAuthorizationCodeAsync(string code, string redirectUri, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<string?> GetAppAccessTokenAsync(CancellationToken cancellationToken)
        {
            AppCallCount++;
            return Task.FromResult<string?>("app-token");
        }

        public Task<string?> GetUserAccessTokenAsync(CancellationToken cancellationToken)
        {
            UserCallCount++;
            return Task.FromResult<string?>("user-token");
        }

        public Task<TwitchUserAuthorizationState> GetUserAuthorizationStateAsync(CancellationToken cancellationToken)
            => Task.FromResult(new TwitchUserAuthorizationState(false, false, "missing", null, false, null, null, null, null));

        public Task<TwitchUserAuthorizationState> ValidateUserAuthorizationAsync(CancellationToken cancellationToken)
            => Task.FromResult(new TwitchUserAuthorizationState(false, false, "missing", null, false, null, null, null, null));
    }

    private sealed class CountingLiveStateSynchronizer : ITwitchLiveStateSynchronizer
    {
        public int CallCount { get; private set; }

        public Task SynchronizeAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.CompletedTask;
        }
    }
}
