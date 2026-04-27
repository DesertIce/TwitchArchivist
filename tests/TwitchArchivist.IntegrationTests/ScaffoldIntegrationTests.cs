using Microsoft.AspNetCore.Mvc.Testing;

namespace TwitchArchivist.IntegrationTests;

public class ScaffoldIntegrationTests
{
    [Fact]
    public async Task HealthEndpointReturnsHealthyStatus()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/healthz");
        var payload = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode);
        Assert.Contains("healthy", payload);
    }

    [Fact]
    public async Task RootRouteRendersDashboardPage()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/");
        var payload = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode);
        Assert.Contains("Service overview", payload);
        Assert.Contains("Recent jobs", payload);
    }
}
