using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TwitchArchivist.Services.Logging;

namespace TwitchArchivist.IntegrationTests;

public class LogsPageIntegrationTests
{
    [Fact]
    public async Task LogsPageShowsInformationEntriesByDefault()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var store = factory.Services.GetRequiredService<RecentLogStore>();
        store.Clear();
        store.Append(LogLevel.Information, "information entry", category: "Test");
        store.Append(LogLevel.Debug, "debug entry", category: "Test");

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/logs");
        var payload = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode);
        Assert.Contains("Recent logs", payload);
        Assert.Contains("information entry", payload);
        Assert.DoesNotContain("debug entry", payload);
    }

    [Fact]
    public async Task LogsPageHonorsRequestedMinimumLevel()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var store = factory.Services.GetRequiredService<RecentLogStore>();
        store.Clear();
        store.Append(LogLevel.Information, "information entry", category: "Test");
        store.Append(LogLevel.Warning, "warning entry", category: "Test");

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/logs?minimumLevel=Warning");
        var payload = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode);
        Assert.DoesNotContain("information entry", payload);
        Assert.Contains("warning entry", payload);
    }

    [Fact]
    public async Task ClearLogsPostRemovesEntriesAndRedirectsToSelectedLevel()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var store = factory.Services.GetRequiredService<RecentLogStore>();
        store.Clear();
        store.Append(LogLevel.Error, "error entry", category: "Test");

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.PostAsync("/logs?minimumLevel=Error", content: null);

        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/logs?minimumLevel=Error", response.Headers.Location?.ToString());
        Assert.Empty(store.GetEntries(LogLevel.Trace));
    }
}
