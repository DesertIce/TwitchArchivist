using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TwitchArchivist.Models;
using TwitchArchivist.Persistence;
using TwitchArchivist.Persistence.Entities;
using TwitchArchivist.Services.Twitch;

namespace TwitchArchivist.UnitTests.Services.Twitch;

public class TwitchAccessTokenProviderTests
{
    [Fact]
    public async Task ExchangeAuthorizationCodeAsyncStoresValidatedUserTokenInSqlite()
    {
        await using var database = await CreateDatabaseAsync();
        var handler = new StubHttpMessageHandler(async request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.ToString() == "https://id.twitch.tv/oauth2/token")
            {
                var body = await request.Content!.ReadAsStringAsync();
                Assert.Contains("grant_type=authorization_code", body);
                Assert.Contains("redirect_uri=http%3A%2F%2Flocalhost%3A5000%2Fauth%2Ftwitch%2Fcallback", body);

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "access_token": "user-token",
                          "refresh_token": "refresh-token",
                          "expires_in": 3600,
                          "token_type": "bearer",
                          "scope": []
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                };
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.ToString() == "https://id.twitch.tv/oauth2/validate")
            {
                Assert.Equal("OAuth user-token", request.Headers.Authorization?.ToString());
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "client_id": "client-id",
                          "login": "immybisou",
                          "user_id": "1011883719"
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                };
            }

            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        var provider = CreateProvider(database.Services, handler);

        await provider.ExchangeAuthorizationCodeAsync(
            "auth-code",
            "http://localhost:5000/auth/twitch/callback",
            CancellationToken.None);

        await using var verificationScope = database.Services.CreateAsyncScope();
        var dbContext = verificationScope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        var storedToken = await dbContext.TwitchOAuthTokens.SingleAsync();

        Assert.Equal("user-token", storedToken.AccessToken);
        Assert.Equal("refresh-token", storedToken.RefreshToken);
        Assert.Equal("immybisou", storedToken.TwitchUserLogin);
        Assert.Equal("1011883719", storedToken.TwitchUserId);
        Assert.True(storedToken.ExpiresUtc > DateTimeOffset.UtcNow.AddMinutes(50));
    }

    [Fact]
    public async Task GetUserAccessTokenAsyncRefreshesExpiredStoredTokenAndPersistsNewValues()
    {
        await using var database = await CreateDatabaseAsync();
        await SeedExpiredUserTokenAsync(database.Services);

        var handler = new StubHttpMessageHandler(async request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.ToString() == "https://id.twitch.tv/oauth2/token")
            {
                var body = await request.Content!.ReadAsStringAsync();
                Assert.Contains("grant_type=refresh_token", body);
                Assert.Contains("refresh_token=old-refresh-token", body);

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "access_token": "new-user-token",
                          "refresh_token": "new-refresh-token",
                          "expires_in": 3600,
                          "token_type": "bearer",
                          "scope": []
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                };
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.ToString() == "https://id.twitch.tv/oauth2/validate")
            {
                Assert.Equal("OAuth new-user-token", request.Headers.Authorization?.ToString());
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "client_id": "client-id",
                          "login": "immybisou",
                          "user_id": "1011883719"
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                };
            }

            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        var provider = CreateProvider(database.Services, handler);

        var token = await provider.GetUserAccessTokenAsync(CancellationToken.None);

        Assert.Equal("new-user-token", token);

        await using var verificationScope = database.Services.CreateAsyncScope();
        var dbContext = verificationScope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        var storedToken = await dbContext.TwitchOAuthTokens.SingleAsync();

        Assert.Equal("new-user-token", storedToken.AccessToken);
        Assert.Equal("new-refresh-token", storedToken.RefreshToken);
        Assert.Equal("immybisou", storedToken.TwitchUserLogin);
        Assert.Equal("1011883719", storedToken.TwitchUserId);
        Assert.True(storedToken.ExpiresUtc > DateTimeOffset.UtcNow.AddMinutes(50));
    }

    [Fact]
    public async Task GetUserAccessTokenAsyncRefreshesTokenThatIsInsideUserRefreshBuffer()
    {
        await using var database = await CreateDatabaseAsync();
        await SeedSoonExpiringUserTokenAsync(database.Services, DateTimeOffset.UtcNow.AddMinutes(12));

        var tokenEndpointCalls = 0;
        var handler = new StubHttpMessageHandler(async request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.ToString() == "https://id.twitch.tv/oauth2/token")
            {
                tokenEndpointCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "access_token": "buffer-refreshed-user-token",
                          "refresh_token": "buffer-refreshed-refresh-token",
                          "expires_in": 3600,
                          "token_type": "bearer",
                          "scope": []
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                };
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.ToString() == "https://id.twitch.tv/oauth2/validate")
            {
                Assert.Equal("OAuth buffer-refreshed-user-token", request.Headers.Authorization?.ToString());
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "client_id": "client-id",
                          "login": "immybisou",
                          "user_id": "1011883719"
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                };
            }

            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        var provider = CreateProvider(database.Services, handler, userRefreshBufferMinutes: 15);

        var token = await provider.GetUserAccessTokenAsync(CancellationToken.None);

        Assert.Equal("buffer-refreshed-user-token", token);
        Assert.Equal(1, tokenEndpointCalls);
    }

    [Fact]
    public async Task ValidateUserAuthorizationAsyncReturnsValidStateForStoredToken()
    {
        await using var database = await CreateDatabaseAsync();
        await SeedValidUserTokenAsync(database.Services);

        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.ToString() == "https://id.twitch.tv/oauth2/validate")
            {
                Assert.Equal("OAuth current-user-token", request.Headers.Authorization?.ToString());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "client_id": "client-id",
                          "login": "immybisou",
                          "user_id": "1011883719"
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                });
            }

            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        var provider = CreateProvider(database.Services, handler);

        var state = await provider.ValidateUserAuthorizationAsync(CancellationToken.None);

        Assert.True(state.IsConfigured);
        Assert.True(state.IsValid);
        Assert.Equal("valid", state.Validity);
        Assert.Equal("immybisou", state.TwitchUserLogin);
        Assert.NotNull(state.LastValidatedUtc);
    }

    private static TwitchAccessTokenProvider CreateProvider(IServiceProvider services, HttpMessageHandler handler, int userRefreshBufferMinutes = 15)
    {
        var options = Options.Create(new TwitchOptions
        {
            ClientId = "client-id",
            ClientSecret = "client-secret",
            AppAccessTokenRefreshBufferMinutes = 5,
            UserAccessTokenRefreshBufferMinutes = userRefreshBufferMinutes
        });

        return new TwitchAccessTokenProvider(
            new StubHttpClientFactory(new HttpClient(handler)),
            services.GetRequiredService<IServiceScopeFactory>(),
            options,
            NullLogger<TwitchAccessTokenProvider>.Instance);
    }

    private static async Task SeedExpiredUserTokenAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        dbContext.TwitchOAuthTokens.Add(new TwitchOAuthToken
        {
            AccessToken = "expired-user-token",
            RefreshToken = "old-refresh-token",
            TokenType = "bearer",
            Scope = string.Empty,
            TwitchUserId = "1011883719",
            TwitchUserLogin = "immybisou",
            ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-30),
            CreatedUtc = DateTimeOffset.UtcNow.AddHours(-1),
            UpdatedUtc = DateTimeOffset.UtcNow.AddHours(-1)
        });
        await dbContext.SaveChangesAsync();
    }

    private static async Task SeedValidUserTokenAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        dbContext.TwitchOAuthTokens.Add(new TwitchOAuthToken
        {
            AccessToken = "current-user-token",
            RefreshToken = "current-refresh-token",
            TokenType = "bearer",
            Scope = string.Empty,
            TwitchUserId = "1011883719",
            TwitchUserLogin = "immybisou",
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1),
            CreatedUtc = DateTimeOffset.UtcNow.AddHours(-1),
            UpdatedUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
        });
        await dbContext.SaveChangesAsync();
    }

    private static async Task SeedSoonExpiringUserTokenAsync(IServiceProvider services, DateTimeOffset expiresUtc)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        dbContext.TwitchOAuthTokens.Add(new TwitchOAuthToken
        {
            AccessToken = "soon-expiring-user-token",
            RefreshToken = "soon-refresh-token",
            TokenType = "bearer",
            Scope = string.Empty,
            TwitchUserId = "1011883719",
            TwitchUserLogin = "immybisou",
            ExpiresUtc = expiresUtc,
            CreatedUtc = DateTimeOffset.UtcNow.AddHours(-1),
            UpdatedUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
        });
        await dbContext.SaveChangesAsync();
    }

    private static async Task<TestDatabase> CreateDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<TwitchArchivistDbContext>(options => options.UseSqlite(connection));

        var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        await dbContext.Database.EnsureCreatedAsync();

        return new TestDatabase(provider, connection);
    }

    private sealed class TestDatabase(IServiceProvider services, SqliteConnection connection) : IAsyncDisposable
    {
        public IServiceProvider Services { get; } = services;

        public async ValueTask DisposeAsync()
        {
            if (Services is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else if (Services is IDisposable disposable)
            {
                disposable.Dispose();
            }

            await connection.DisposeAsync();
        }
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request);
    }
}
