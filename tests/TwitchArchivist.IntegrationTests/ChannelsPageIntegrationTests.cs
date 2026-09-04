using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using TwitchArchivist.Persistence;
using TwitchArchivist.Persistence.Entities;
using TwitchArchivist.Services;
using TwitchArchivist.Services.Twitch;

namespace TwitchArchivist.IntegrationTests;

public class ChannelsPageIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public async Task CreatePageRendersDirectoryPickerAndAutocompleteHooks()
    {
        await using var factory = new IntegrationTestWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/channels/create");
        var payload = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode);
        Assert.Contains("data-directory-picker", payload);
        Assert.Contains("data-directory-picker-portal", payload);
        Assert.Contains("data-twitch-login-autocomplete", payload);
        Assert.Contains("data-twitch-login-suggestions", payload);
        Assert.Contains("name=\"Input.Alias\"", payload);
        Assert.Contains("name=\"Input.CompressEnabled\"", payload);
        Assert.Contains("name=\"Input.CompressVodCount\"", payload);
    }

    [Fact]
    public async Task CreatePageRendersScrollableDirectoryPickerLayout()
    {
        await using var factory = new IntegrationTestWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/channels/create");
        var payload = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode);
        Assert.Contains("/css/site.css", payload);
        Assert.Contains("data-directory-picker-portal=\"true\"", payload);

        var cssResponse = await client.GetAsync("/css/site.css");
        var css = await cssResponse.Content.ReadAsStringAsync();
        Assert.True(cssResponse.IsSuccessStatusCode);
        Assert.Contains(".portal-body {", css);
        Assert.Contains("min-height: 0;", css);
        Assert.Contains(".portal-list {", css);
        Assert.Contains("overflow: auto;", css);
    }

    [Fact]
    public async Task ChannelsPageEnablesAutoRefreshWithDefaultInterval()
    {
        await using var factory = new IntegrationTestWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/channels");
        var payload = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode);
        Assert.Contains("data-auto-refresh-enabled=\"true\"", payload);
        Assert.Contains("data-auto-refresh-default-interval-seconds=\"120\"", payload);
        Assert.Contains("data-auto-refresh-toggle=\"true\"", payload);
        Assert.Contains("data-auto-refresh-interval=\"true\"", payload);
        Assert.Contains("/js/site.js", payload);

        var jsResponse = await client.GetAsync("/js/site.js");
        var js = await jsResponse.Content.ReadAsStringAsync();
        Assert.True(jsResponse.IsSuccessStatusCode);
        Assert.Contains("initAutoRefresh", js);
        Assert.Contains("twitchArchivist.autoRefresh.enabled", js);
        Assert.Contains("twitchArchivist.autoRefresh.intervalSeconds", js);
        Assert.Contains("window.location.reload();", js);
    }

    [Fact]
    public async Task CreatePageDisablesAutoRefresh()
    {
        await using var factory = new IntegrationTestWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/channels/create");
        var payload = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode);
        Assert.Contains("data-auto-refresh-enabled=\"false\"", payload);
        Assert.Contains("data-auto-refresh-default-interval-seconds=\"120\"", payload);
    }

    [Fact]
    public async Task EditPageRendersDeleteAction()
    {
        await using var factory = new IntegrationTestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();

        var channel = new ChannelConfiguration
        {
            TwitchLogin = $"delete-me-{Guid.NewGuid():N}",
            OutputDirectory = @"D:\archive\delete-me",
            IsEnabled = true,
            CreatedUtc = DateTimeOffset.UtcNow.AddDays(-1),
            UpdatedUtc = DateTimeOffset.UtcNow
        };

        dbContext.ChannelConfigurations.Add(channel);
        await dbContext.SaveChangesAsync();

        using var client = factory.CreateClient();
        var response = await client.GetAsync($"/channels/edit/{channel.Id}");
        var payload = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode);
        Assert.Contains("?handler=Delete", payload);
        Assert.Contains("Delete mapping", payload);
    }

    [Fact]
    public async Task ChannelsPageRendersLiveAndLastLiveColumns()
    {
        await using var factory = new IntegrationTestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();

        var now = DateTimeOffset.Parse("2026-04-28T18:00:00Z");

        var liveLogin = $"alpha-{Guid.NewGuid():N}";
        var offlineLogin = $"beta-{Guid.NewGuid():N}";

        var liveChannel = new ChannelConfiguration
        {
            TwitchLogin = liveLogin,
            OutputDirectory = @"D:\archive\alpha",
            IsEnabled = true,
            CreatedUtc = now.AddDays(-2),
            UpdatedUtc = now
        };

        var offlineChannel = new ChannelConfiguration
        {
            TwitchLogin = offlineLogin,
            OutputDirectory = @"D:\archive\beta",
            IsEnabled = true,
            CreatedUtc = now.AddDays(-2),
            UpdatedUtc = now
        };

        dbContext.ChannelConfigurations.AddRange(liveChannel, offlineChannel);
        await dbContext.SaveChangesAsync();

        dbContext.StreamSessionStates.AddRange(
            new StreamSessionState
            {
                ChannelConfigurationId = liveChannel.Id,
                LastOnlineUtc = now.AddMinutes(-15),
                CreatedUtc = now.AddDays(-1),
                UpdatedUtc = now
            },
            new StreamSessionState
            {
                ChannelConfigurationId = offlineChannel.Id,
                LastOnlineUtc = now.AddHours(-4),
                LastOfflineUtc = now.AddHours(-2),
                CreatedUtc = now.AddDays(-1),
                UpdatedUtc = now
            });

        await dbContext.SaveChangesAsync();

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/channels");
        var payload = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode);
        Assert.Contains("<th scope=\"col\">Live</th>", payload);
        Assert.Contains("<th scope=\"col\">Last live</th>", payload);
        Assert.Contains(">Live</span>", payload);
        Assert.Contains(">Offline</span>", payload);
        Assert.Contains("2026-04-28 17:45:00Z", payload);
        Assert.Contains("2026-04-28 14:00:00Z", payload);
    }

    [Fact]
    public async Task ChannelSearchEndpointReturnsHelixAutocompleteResults()
    {
        await using var factory = new IntegrationTestWebApplicationFactory(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<ITwitchHelixClient>();
                services.AddSingleton<ITwitchHelixClient>(new StubTwitchHelixClient(
                [
                    new TwitchChannelSearchResult("12345", "testchannel", "TestChannel", "https://example.com/avatar.png", true)
                ]));
            });
        });

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/twitch/channels/search?query=test");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var payload = await response.Content.ReadAsStreamAsync();
        var results = await JsonSerializer.DeserializeAsync<List<TwitchChannelSearchResponse>>(payload, JsonOptions);

        var result = Assert.Single(results ?? []);
        Assert.Equal("12345", result.UserId);
        Assert.Equal("testchannel", result.Login);
        Assert.Equal("TestChannel", result.DisplayName);
        Assert.Equal("https://example.com/avatar.png", result.ThumbnailUrl);
        Assert.True(result.IsLive);
    }

    [Fact]
    public async Task ChannelSearchEndpointRejectsShortQueries()
    {
        await using var factory = new IntegrationTestWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/twitch/channels/search?query=tes");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DirectoryRootsEndpointReturnsFilesystemRoots()
    {
        await using var factory = new IntegrationTestWebApplicationFactory(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IFileSystemBrowserService>();
                services.AddSingleton<IFileSystemBrowserService>(new StubFileSystemBrowserService(
                    roots: [@"D:\", @"E:\"],
                    directoriesByPath: new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)));
            });
        });

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/filesystem/roots");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var payload = await response.Content.ReadAsStreamAsync();
        var result = await JsonSerializer.DeserializeAsync<List<FileSystemPathResponse>>(payload, JsonOptions);

        Assert.Equal([@"D:\", @"E:\"], result?.Select(x => x.Path));
    }

    [Fact]
    public async Task DirectoryChildrenEndpointReturnsDirectoriesForSelectedPath()
    {
        await using var factory = new IntegrationTestWebApplicationFactory(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IFileSystemBrowserService>();
                services.AddSingleton<IFileSystemBrowserService>(new StubFileSystemBrowserService(
                    roots: [@"D:\"],
                    directoriesByPath: new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        [@"D:\"] = [@"D:\archive", @"D:\captures"]
                    }));
            });
        });

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/filesystem/directories?path=D%3A%5C");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var payload = await response.Content.ReadAsStreamAsync();
        var result = await JsonSerializer.DeserializeAsync<List<FileSystemPathResponse>>(payload, JsonOptions);

        Assert.Equal([@"D:\archive", @"D:\captures"], result?.Select(x => x.Path));
    }

    private sealed record TwitchChannelSearchResponse(
        string UserId,
        string Login,
        string DisplayName,
        string ThumbnailUrl,
        bool IsLive);

    private sealed record FileSystemPathResponse(string Path);

    private sealed class StubTwitchHelixClient(IReadOnlyList<TwitchChannelSearchResult> results) : ITwitchHelixClient
    {
        public Task<EventSubSubscriptionRecord> CreateStreamSubscriptionAsync(string subscriptionType, string broadcasterUserId, string sessionId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<EventSubSubscriptionRecord>> GetEventSubscriptionsAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ArchiveVodRecord?> GetLatestArchiveVodAsync(string broadcasterUserId, DateTimeOffset? createdAfterUtc, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<TwitchLiveStreamState>> GetLiveStreamsByLoginsAsync(IReadOnlyList<string> twitchLogins, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<string?> ResolveUserIdAsync(string twitchLogin, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<TwitchChannelSearchResult>> SearchChannelsAsync(string query, CancellationToken cancellationToken)
            => Task.FromResult(results);
    }

    private sealed class StubFileSystemBrowserService(
        IReadOnlyList<string> roots,
        IReadOnlyDictionary<string, IReadOnlyList<string>> directoriesByPath) : IFileSystemBrowserService
    {
        public Task<IReadOnlyList<string>> GetRootDirectoriesAsync(CancellationToken cancellationToken)
            => Task.FromResult(roots);

        public Task<IReadOnlyList<string>> GetSubdirectoriesAsync(string path, CancellationToken cancellationToken)
            => Task.FromResult(directoriesByPath.TryGetValue(path, out var directories)
                ? directories
                : (IReadOnlyList<string>)[]);

        public Task<IReadOnlyList<FileSystemBrowserEntry>> GetEntriesAsync(
            string path,
            bool includeFiles,
            string? searchPattern,
            CancellationToken cancellationToken)
            => Task.FromResult((IReadOnlyList<FileSystemBrowserEntry>)(directoriesByPath.TryGetValue(path, out var directories)
                ? directories.Select(directory => new FileSystemBrowserEntry(directory, true)).ToList()
                : []));
    }
}
