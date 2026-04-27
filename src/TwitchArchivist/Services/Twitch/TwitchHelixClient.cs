using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using TwitchArchivist.Models;

namespace TwitchArchivist.Services.Twitch;

public class TwitchHelixClient(
    IHttpClientFactory httpClientFactory,
    ITwitchAccessTokenProvider accessTokenProvider,
    IOptions<TwitchOptions> twitchOptions) : ITwitchHelixClient
{
    public async Task<string?> ResolveUserIdAsync(string twitchLogin, CancellationToken cancellationToken)
    {
        var response = await SendHelixAsync<HelixEnvelope<UserRecord>>(
            $"/users?login={Uri.EscapeDataString(twitchLogin)}",
            HttpMethod.Get,
            body: null,
            cancellationToken);

        return response?.Data.FirstOrDefault()?.Id;
    }

    public async Task<ArchiveVodRecord?> GetLatestArchiveVodAsync(
        string broadcasterUserId,
        DateTimeOffset? createdAfterUtc,
        CancellationToken cancellationToken)
    {
        var response = await SendHelixAsync<HelixEnvelope<VideoRecord>>(
            $"/videos?user_id={Uri.EscapeDataString(broadcasterUserId)}&type=archive&first=5",
            HttpMethod.Get,
            body: null,
            cancellationToken);

        var videos = response?.Data
            .Select(x => new ArchiveVodRecord(x.Id, x.CreatedAt))
            .ToList() ?? [];

        return ArchiveVodSelector.SelectLatestEligibleVod(videos, createdAfterUtc);
    }

    public async Task<IReadOnlyList<EventSubSubscriptionRecord>> GetEventSubscriptionsAsync(CancellationToken cancellationToken)
    {
        var response = await SendHelixAsync<HelixEnvelope<SubscriptionRecord>>(
            "/eventsub/subscriptions",
            HttpMethod.Get,
            body: null,
            cancellationToken);

        return response?.Data
            .Select(x => new EventSubSubscriptionRecord(x.Id, x.Type, x.Status, x.Condition.BroadcasterUserId))
            .ToList() ?? [];
    }

    public async Task<EventSubSubscriptionRecord> CreateStreamSubscriptionAsync(
        string subscriptionType,
        string broadcasterUserId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            type = subscriptionType,
            version = "1",
            condition = new Dictionary<string, string>
            {
                ["broadcaster_user_id"] = broadcasterUserId
            },
            transport = new
            {
                method = "websocket",
                session_id = sessionId
            }
        };

        var response = await SendHelixAsync<HelixEnvelope<SubscriptionRecord>>(
            "/eventsub/subscriptions",
            HttpMethod.Post,
            payload,
            cancellationToken);

        var record = response?.Data.FirstOrDefault()
            ?? throw new InvalidOperationException("Twitch did not return the created EventSub subscription.");

        return new EventSubSubscriptionRecord(record.Id, record.Type, record.Status, record.Condition.BroadcasterUserId);
    }

    private async Task<T?> SendHelixAsync<T>(string relativePath, HttpMethod method, object? body, CancellationToken cancellationToken)
    {
        var token = await accessTokenProvider.GetAccessTokenAsync(cancellationToken)
            ?? throw new InvalidOperationException("Twitch app credentials are not configured.");

        var options = twitchOptions.Value;
        var client = httpClientFactory.CreateClient(nameof(TwitchHelixClient));
        var normalizedPath = relativePath.TrimStart('/');
        using var request = new HttpRequestMessage(method, new Uri(client.BaseAddress!, normalizedPath));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Client-Id", options.ClientId);

        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);
    }

    private sealed class HelixEnvelope<TRecord>
    {
        public List<TRecord> Data { get; set; } = [];
    }

    private sealed class UserRecord
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;
    }

    private sealed class VideoRecord
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("created_at")]
        public DateTimeOffset CreatedAt { get; set; }
    }

    private sealed class SubscriptionRecord
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("condition")]
        public SubscriptionCondition Condition { get; set; } = new();
    }

    private sealed class SubscriptionCondition
    {
        [JsonPropertyName("broadcaster_user_id")]
        public string? BroadcasterUserId { get; set; }
    }
}
