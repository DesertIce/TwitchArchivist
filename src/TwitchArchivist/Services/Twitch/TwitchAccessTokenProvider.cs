using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TwitchArchivist.Models;
using TwitchArchivist.Persistence;
using TwitchArchivist.Persistence.Entities;

namespace TwitchArchivist.Services.Twitch;

public class TwitchAccessTokenProvider(
    IHttpClientFactory httpClientFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<TwitchOptions> twitchOptions,
    ILogger<TwitchAccessTokenProvider> logger) : ITwitchAccessTokenProvider
{
    private readonly SemaphoreSlim _appTokenSync = new(1, 1);
    private readonly SemaphoreSlim _userTokenSync = new(1, 1);
    private string? _cachedAppToken;
    private DateTimeOffset _appTokenExpiresUtc = DateTimeOffset.MinValue;
    private string? _cachedUserToken;
    private DateTimeOffset _userTokenExpiresUtc = DateTimeOffset.MinValue;

    public string? BuildUserAuthorizationUrl(string state, string redirectUri)
    {
        var options = twitchOptions.Value;
        if (string.IsNullOrWhiteSpace(options.ClientId) || string.IsNullOrWhiteSpace(redirectUri))
        {
            return null;
        }

        return $"https://id.twitch.tv/oauth2/authorize?response_type=code&client_id={Uri.EscapeDataString(options.ClientId)}&redirect_uri={Uri.EscapeDataString(redirectUri)}&scope=&state={Uri.EscapeDataString(state)}";
    }

    public async Task ExchangeAuthorizationCodeAsync(string code, string redirectUri, CancellationToken cancellationToken)
    {
        var options = twitchOptions.Value;
        if (string.IsNullOrWhiteSpace(options.ClientId) || string.IsNullOrWhiteSpace(options.ClientSecret))
        {
            throw new InvalidOperationException("Twitch client credentials are not configured.");
        }

        var client = httpClientFactory.CreateClient(nameof(TwitchAccessTokenProvider));
        using var response = await client.PostAsync(
            "https://id.twitch.tv/oauth2/token",
            new FormUrlEncodedContent(new Dictionary<string, string?>
            {
                ["client_id"] = options.ClientId,
                ["client_secret"] = options.ClientSecret,
                ["code"] = code,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = redirectUri
            }!),
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var tokenPayload = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Twitch did not return a token payload.");

        var validation = await ValidateUserTokenAsync(client, tokenPayload.AccessToken, cancellationToken);
        await PersistUserTokenAsync(tokenPayload, validation, cancellationToken);
    }

    public async Task<string?> GetAppAccessTokenAsync(CancellationToken cancellationToken)
    {
        var options = twitchOptions.Value;
        if (string.IsNullOrWhiteSpace(options.ClientId) || string.IsNullOrWhiteSpace(options.ClientSecret))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(_cachedAppToken) &&
            _appTokenExpiresUtc > DateTimeOffset.UtcNow.AddMinutes(Math.Max(1, options.AppAccessTokenRefreshBufferMinutes)))
        {
            return _cachedAppToken;
        }

        await _appTokenSync.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(_cachedAppToken) &&
                _appTokenExpiresUtc > DateTimeOffset.UtcNow.AddMinutes(Math.Max(1, options.AppAccessTokenRefreshBufferMinutes)))
            {
                return _cachedAppToken;
            }

            var client = httpClientFactory.CreateClient(nameof(TwitchAccessTokenProvider));
            using var response = await client.PostAsync(
                "https://id.twitch.tv/oauth2/token",
                new FormUrlEncodedContent(new Dictionary<string, string?>
                {
                    ["client_id"] = options.ClientId,
                    ["client_secret"] = options.ClientSecret,
                    ["grant_type"] = "client_credentials"
                }!),
                cancellationToken);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: cancellationToken);
            _cachedAppToken = payload?.AccessToken;
            _appTokenExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(payload?.ExpiresIn ?? 0);
            return _cachedAppToken;
        }
        finally
        {
            _appTokenSync.Release();
        }
    }

    public async Task<string?> GetUserAccessTokenAsync(CancellationToken cancellationToken)
    {
        var options = twitchOptions.Value;
        var refreshThreshold = DateTimeOffset.UtcNow.AddMinutes(Math.Max(1, options.AppAccessTokenRefreshBufferMinutes));

        if (!string.IsNullOrWhiteSpace(_cachedUserToken) && _userTokenExpiresUtc > refreshThreshold)
        {
            return _cachedUserToken;
        }

        await _userTokenSync.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(_cachedUserToken) && _userTokenExpiresUtc > refreshThreshold)
            {
                return _cachedUserToken;
            }

            var storedToken = await LoadStoredUserTokenAsync(cancellationToken);
            if (storedToken is null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(storedToken.AccessToken) && storedToken.ExpiresUtc > refreshThreshold)
            {
                CacheUserToken(storedToken.AccessToken, storedToken.ExpiresUtc);
                return storedToken.AccessToken;
            }

            if (string.IsNullOrWhiteSpace(storedToken.RefreshToken))
            {
                logger.LogWarning("Twitch user token refresh was requested but no refresh token is stored.");
                return null;
            }

            var refreshedToken = await RefreshUserAccessTokenAsync(storedToken.RefreshToken, cancellationToken);
            CacheUserToken(refreshedToken.AccessToken, refreshedToken.ExpiresUtc);
            return refreshedToken.AccessToken;
        }
        finally
        {
            _userTokenSync.Release();
        }
    }

    public async Task<TwitchUserAuthorizationState> GetUserAuthorizationStateAsync(CancellationToken cancellationToken)
    {
        var storedToken = await LoadStoredUserTokenAsync(cancellationToken);
        if (storedToken is null)
        {
            return new TwitchUserAuthorizationState(false, false, "missing", "No Twitch user token is stored.", false, null, null, null, null);
        }

        var validity = GetValidityLabel(storedToken.ExpiresUtc);
        return new TwitchUserAuthorizationState(
            true,
            validity is "valid" or "expiring-soon",
            validity,
            BuildValidityDetail(validity, storedToken.ExpiresUtc),
            !string.IsNullOrWhiteSpace(storedToken.RefreshToken),
            string.IsNullOrWhiteSpace(storedToken.TwitchUserId) ? null : storedToken.TwitchUserId,
            string.IsNullOrWhiteSpace(storedToken.TwitchUserLogin) ? null : storedToken.TwitchUserLogin,
            storedToken.ExpiresUtc == DateTimeOffset.MinValue ? null : storedToken.ExpiresUtc,
            storedToken.UpdatedUtc == DateTimeOffset.MinValue ? null : storedToken.UpdatedUtc);
    }

    public async Task<TwitchUserAuthorizationState> ValidateUserAuthorizationAsync(CancellationToken cancellationToken)
    {
        var storedToken = await LoadStoredUserTokenAsync(cancellationToken);
        if (storedToken is null)
        {
            return new TwitchUserAuthorizationState(false, false, "missing", "No Twitch user token is stored.", false, null, null, null, null);
        }

        try
        {
            var accessToken = await GetUserAccessTokenAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return new TwitchUserAuthorizationState(true, false, "invalid", "The stored Twitch user token could not be refreshed.", !string.IsNullOrWhiteSpace(storedToken.RefreshToken), storedToken.TwitchUserId, storedToken.TwitchUserLogin, storedToken.ExpiresUtc, DateTimeOffset.UtcNow);
            }

            var client = httpClientFactory.CreateClient(nameof(TwitchAccessTokenProvider));
            var validation = await ValidateUserTokenAsync(client, accessToken, cancellationToken);
            var refreshed = await LoadStoredUserTokenAsync(cancellationToken) ?? storedToken;
            var validity = GetValidityLabel(refreshed.ExpiresUtc);

            return new TwitchUserAuthorizationState(
                true,
                true,
                validity,
                BuildValidityDetail(validity, refreshed.ExpiresUtc),
                !string.IsNullOrWhiteSpace(refreshed.RefreshToken),
                string.IsNullOrWhiteSpace(validation.UserId) ? refreshed.TwitchUserId : validation.UserId,
                string.IsNullOrWhiteSpace(validation.Login) ? refreshed.TwitchUserLogin : validation.Login,
                refreshed.ExpiresUtc,
                DateTimeOffset.UtcNow);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Failed to validate the current Twitch user token");
            return new TwitchUserAuthorizationState(
                true,
                false,
                "invalid",
                "Twitch rejected the current user token.",
                !string.IsNullOrWhiteSpace(storedToken.RefreshToken),
                storedToken.TwitchUserId,
                storedToken.TwitchUserLogin,
                storedToken.ExpiresUtc,
                DateTimeOffset.UtcNow);
        }
    }

    private void CacheUserToken(string accessToken, DateTimeOffset expiresUtc)
    {
        _cachedUserToken = accessToken;
        _userTokenExpiresUtc = expiresUtc;
    }

    private async Task<TwitchOAuthToken?> LoadStoredUserTokenAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        return await dbContext.TwitchOAuthTokens.OrderBy(x => x.Id).FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<PersistedUserToken> RefreshUserAccessTokenAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var options = twitchOptions.Value;
        if (string.IsNullOrWhiteSpace(options.ClientId) || string.IsNullOrWhiteSpace(options.ClientSecret))
        {
            throw new InvalidOperationException("Twitch client credentials are not configured.");
        }

        var client = httpClientFactory.CreateClient(nameof(TwitchAccessTokenProvider));
        using var response = await client.PostAsync(
            "https://id.twitch.tv/oauth2/token",
            new FormUrlEncodedContent(new Dictionary<string, string?>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = options.ClientId,
                ["client_secret"] = options.ClientSecret
            }!),
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var tokenPayload = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Twitch did not return a refreshed user token payload.");

        var validation = await ValidateUserTokenAsync(client, tokenPayload.AccessToken, cancellationToken);
        return await PersistUserTokenAsync(tokenPayload, validation, cancellationToken);
    }

    private async Task<PersistedUserToken> PersistUserTokenAsync(
        TokenResponse tokenPayload,
        TokenValidationResponse validation,
        CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        var entity = await dbContext.TwitchOAuthTokens.OrderBy(x => x.Id).FirstOrDefaultAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (entity is null)
        {
            entity = new TwitchOAuthToken
            {
                CreatedUtc = now
            };
            dbContext.TwitchOAuthTokens.Add(entity);
        }

        entity.AccessToken = tokenPayload.AccessToken;
        entity.RefreshToken = tokenPayload.RefreshToken;
        entity.TokenType = string.IsNullOrWhiteSpace(tokenPayload.TokenType) ? "bearer" : tokenPayload.TokenType;
        entity.Scope = string.Join(' ', tokenPayload.Scope ?? []);
        entity.TwitchUserId = validation.UserId ?? string.Empty;
        entity.TwitchUserLogin = validation.Login ?? string.Empty;
        entity.ExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(tokenPayload.ExpiresIn);
        entity.UpdatedUtc = now;

        await dbContext.SaveChangesAsync(cancellationToken);

        return new PersistedUserToken(entity.AccessToken, entity.ExpiresUtc);
    }

    private static async Task<TokenValidationResponse> ValidateUserTokenAsync(HttpClient client, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://id.twitch.tv/oauth2/validate");
        request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", accessToken);

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TokenValidationResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Twitch did not return validation details for the user token.");
    }

    private static string GetValidityLabel(DateTimeOffset expiresUtc)
    {
        var remaining = expiresUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return "expired";
        }

        if (remaining <= TimeSpan.FromMinutes(10))
        {
            return "expiring-soon";
        }

        return "valid";
    }

    private static string BuildValidityDetail(string validity, DateTimeOffset expiresUtc)
    {
        return validity switch
        {
            "expired" => "The stored Twitch user token has expired and requires refresh or re-authorization.",
            "expiring-soon" => $"The stored Twitch user token expires soon at {expiresUtc:u}.",
            "valid" => $"The stored Twitch user token is valid until {expiresUtc:u}.",
            _ => "The Twitch user token state is unknown."
        };
    }

    private sealed record PersistedUserToken(string AccessToken, DateTimeOffset ExpiresUtc);

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("refresh_token")]
        public string RefreshToken { get; set; } = string.Empty;

        [JsonPropertyName("scope")]
        public List<string>? Scope { get; set; }

        [JsonPropertyName("token_type")]
        public string TokenType { get; set; } = string.Empty;
    }

    private sealed class TokenValidationResponse
    {
        [JsonPropertyName("client_id")]
        public string? ClientId { get; set; }

        [JsonPropertyName("login")]
        public string? Login { get; set; }

        [JsonPropertyName("user_id")]
        public string? UserId { get; set; }
    }
}
