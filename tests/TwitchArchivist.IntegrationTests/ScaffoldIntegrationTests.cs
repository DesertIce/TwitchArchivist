using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace TwitchArchivist.IntegrationTests;

public class ScaffoldIntegrationTests
{
    [Fact]
    public async Task HealthEndpointReturnsHealthyStatus()
    {
        await using var factory = new IntegrationTestWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/healthz");
        var payload = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode);
        Assert.Contains("healthy", payload);
    }

    [Fact]
    public async Task RootRouteRendersDashboardPage()
    {
        await using var factory = new IntegrationTestWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/");
        var payload = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode);
        Assert.Contains("Service overview", payload);
        Assert.Contains("Recent jobs", payload);
        Assert.Contains("/css/site.css", payload);
        Assert.Contains("/auth/twitch/start", payload);

        var cssResponse = await client.GetAsync("/css/site.css");
        var css = await cssResponse.Content.ReadAsStringAsync();
        Assert.True(cssResponse.IsSuccessStatusCode);
        Assert.Contains("color-scheme: dark", css);
    }

    [Fact]
    public async Task TwitchAuthorizationStartRouteUsesTheSameWebServerCallback()
    {
        await using var factory = new IntegrationTestWebApplicationFactory(builder =>
        {
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            {
                configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Twitch:ClientId"] = "client-id",
                    ["Twitch:ClientSecret"] = "client-secret"
                });
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
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
        await using var factory = new IntegrationTestWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/runtime-status");
        var payload = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode);
        Assert.Contains("twitchUserAuthorizationValidity", payload);
        Assert.Contains("twitchUserAuthorizationIsValid", payload);
    }
}
