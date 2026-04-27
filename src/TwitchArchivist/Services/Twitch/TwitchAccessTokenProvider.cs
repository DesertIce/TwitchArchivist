using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using TwitchArchivist.Models;

namespace TwitchArchivist.Services.Twitch;

public class TwitchAccessTokenProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<TwitchOptions> twitchOptions) : ITwitchAccessTokenProvider
{
    private readonly SemaphoreSlim _sync = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset _expiresUtc = DateTimeOffset.MinValue;

    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var options = twitchOptions.Value;
        if (string.IsNullOrWhiteSpace(options.ClientId) || string.IsNullOrWhiteSpace(options.ClientSecret))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(_cachedToken) &&
            _expiresUtc > DateTimeOffset.UtcNow.AddMinutes(Math.Max(1, options.AppAccessTokenRefreshBufferMinutes)))
        {
            return _cachedToken;
        }

        await _sync.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(_cachedToken) &&
                _expiresUtc > DateTimeOffset.UtcNow.AddMinutes(Math.Max(1, options.AppAccessTokenRefreshBufferMinutes)))
            {
                return _cachedToken;
            }

            var client = httpClientFactory.CreateClient(nameof(TwitchAccessTokenProvider));
            var uri = $"https://id.twitch.tv/oauth2/token?client_id={Uri.EscapeDataString(options.ClientId)}&client_secret={Uri.EscapeDataString(options.ClientSecret)}&grant_type=client_credentials";
            using var response = await client.PostAsync(uri, content: null, cancellationToken);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: cancellationToken);
            _cachedToken = payload?.AccessToken;
            _expiresUtc = DateTimeOffset.UtcNow.AddSeconds(payload?.ExpiresIn ?? 0);
            return _cachedToken;
        }
        finally
        {
            _sync.Release();
        }
    }

    private sealed class TokenResponse
    {
        public string AccessToken { get; set; } = string.Empty;

        public int ExpiresIn { get; set; }
    }
}
