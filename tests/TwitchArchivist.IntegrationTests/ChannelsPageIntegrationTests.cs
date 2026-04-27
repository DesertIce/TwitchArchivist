using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    }

    [Fact]
    public async Task ChannelSearchEndpointReturnsHelixAutocompleteResults()
    {
        await using var factory = new IntegrationTestWebApplicationFactory(builder =>
        {
            builder.ConfigureServices(services =>
            {
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
    }
}
