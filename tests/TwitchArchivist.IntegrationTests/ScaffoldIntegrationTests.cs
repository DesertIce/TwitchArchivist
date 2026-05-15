using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System.Text.Json;
using TwitchArchivist.Persistence;

namespace TwitchArchivist.IntegrationTests;

public class ScaffoldIntegrationTests
{
    [Fact]
    public async Task DefaultFactoryDisablesHostedServicesAndMigratesDatabase()
    {
        await using var factory = new IntegrationTestWebApplicationFactory();

        Assert.DoesNotContain(
            factory.Services.GetServices<IHostedService>(),
            hostedService => hostedService.GetType().Namespace?.StartsWith("TwitchArchivist", StringComparison.Ordinal) == true);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        Assert.True(await dbContext.Database.CanConnectAsync());
        Assert.Empty(await dbContext.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task HealthEndpointReturnsHealthyStatus()
    {
        await using var factory = new IntegrationTestWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/healthz");

        Assert.True(response.IsSuccessStatusCode);

        await using var payload = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(payload);
        var root = document.RootElement;

        Assert.Equal("healthy", root.GetProperty("status").GetString());
        Assert.Equal("TwitchArchivist", root.GetProperty("service").GetString());
        Assert.Equal("TwitchArchivist", root.GetProperty("build").GetProperty("assemblyName").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("build").GetProperty("informationalVersion").GetString()));
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

    [Fact]
    public async Task RuntimeStatusEndpointReturnsBuildAndRuntimeContext()
    {
        await using var factory = new IntegrationTestWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/runtime-status");

        Assert.True(response.IsSuccessStatusCode);

        await using var payload = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(payload);
        var root = document.RootElement;

        var build = root.GetProperty("build");
        Assert.Equal("TwitchArchivist", build.GetProperty("assemblyName").GetString());
        Assert.False(string.IsNullOrWhiteSpace(build.GetProperty("informationalVersion").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(build.GetProperty("targetFramework").GetString()));

        var runtimeContext = root.GetProperty("runtimeContext");
        Assert.False(string.IsNullOrWhiteSpace(runtimeContext.GetProperty("environmentName").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(runtimeContext.GetProperty("contentRootPath").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(runtimeContext.GetProperty("frameworkDescription").GetString()));
        Assert.True(runtimeContext.GetProperty("processId").GetInt32() > 0);
    }
}
