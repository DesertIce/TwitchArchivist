using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

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
        Assert.Contains("color-scheme: dark", payload);
        Assert.Contains("/auth/twitch/start", payload);
    }

    [Fact]
    public async Task TwitchAuthorizationStartRouteUsesTheSameWebServerCallback()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configurationBuilder) =>
                {
                    configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Twitch:ClientId"] = "client-id",
                        ["Twitch:ClientSecret"] = "client-secret"
                    });
                });
            });

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("http://localhost:5000")
        });

        var response = await client.GetAsync("/auth/twitch/start");

        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Contains("https://id.twitch.tv/oauth2/authorize", response.Headers.Location!.ToString());
        Assert.Contains("redirect_uri=http%3A%2F%2Flocalhost%3A5000%2Fauth%2Ftwitch%2Fcallback", response.Headers.Location.ToString());
    }

    [Fact]
    public async Task RuntimeStatusEndpointReturnsTwitchTokenValidityFields()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/runtime-status");
        var payload = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode);
        Assert.Contains("twitchUserAuthorizationValidity", payload);
        Assert.Contains("twitchUserAuthorizationIsValid", payload);
    }
}
