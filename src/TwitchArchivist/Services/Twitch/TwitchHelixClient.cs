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
            useUserAccessToken: false,
            requestContext: $"resolving Twitch user id for login={twitchLogin}",
            cancellationToken);

        return response?.Data.FirstOrDefault()?.Id;
    }

    public async Task<IReadOnlyList<TwitchChannelSearchResult>> SearchChannelsAsync(string query, CancellationToken cancellationToken)
    {
        var response = await SendHelixAsync<HelixEnvelope<ChannelSearchRecord>>(
            $"/search/channels?query={Uri.EscapeDataString(query)}&first=10",
            HttpMethod.Get,
            body: null,
            useUserAccessToken: false,
            requestContext: $"searching Twitch channels for query={query}",
            cancellationToken);

        return response?.Data
            .Select(x => new TwitchChannelSearchResult(
                x.Id,
                x.BroadcasterLogin,
                x.DisplayName,
                x.ThumbnailUrl,
                x.IsLive))
            .ToList() ?? [];
    }

    public async Task<IReadOnlyList<TwitchLiveStreamState>> GetLiveStreamsByLoginsAsync(IReadOnlyList<string> twitchLogins, CancellationToken cancellationToken)
    {
        var normalizedLogins = twitchLogins
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalizedLogins.Length == 0)
        {
            return [];
        }

        var liveStreams = new List<TwitchLiveStreamState>();
        const int batchSize = 100;

        for (var index = 0; index < normalizedLogins.Length; index += batchSize)
        {
            var batch = normalizedLogins.Skip(index).Take(batchSize).ToArray();
            var query = string.Join("&", batch.Select(login => $"user_login={Uri.EscapeDataString(login)}"));
            var response = await SendHelixAsync<HelixEnvelope<StreamRecord>>(
                $"/streams?first={batch.Length}&{query}",
                HttpMethod.Get,
                body: null,
                useUserAccessToken: false,
                requestContext: $"listing Twitch live streams for logins=[{string.Join(", ", batch)}]",
                cancellationToken);

            if (response?.Data is null)
            {
                continue;
            }

            liveStreams.AddRange(response.Data.Select(x => new TwitchLiveStreamState(
                x.UserId,
                x.UserLogin,
                x.Id,
                x.StartedAt)));
        }

        return liveStreams;
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
            useUserAccessToken: false,
            requestContext: $"listing archive videos for broadcaster_user_id={broadcasterUserId}",
            cancellationToken);

        var videos = response?.Data
            .Select(x => new ArchiveVodRecord(x.Id, x.CreatedAt, x.Title))
            .ToList() ?? [];

        return ArchiveVodSelector.SelectLatestEligibleVod(videos, createdAfterUtc);
    }

    public async Task<IReadOnlyList<EventSubSubscriptionRecord>> GetEventSubscriptionsAsync(CancellationToken cancellationToken)
    {
        var response = await SendHelixAsync<HelixEnvelope<SubscriptionRecord>>(
            "/eventsub/subscriptions",
            HttpMethod.Get,
            body: null,
            useUserAccessToken: true,
            requestContext: "listing EventSub subscriptions",
            cancellationToken);

        return response?.Data
            .Select(x => new EventSubSubscriptionRecord(x.Id, x.Type, x.Status, x.Condition.BroadcasterUserId, x.Transport.SessionId))
            .ToList() ?? [];
    }

    public Task DeleteEventSubscriptionAsync(string subscriptionId, CancellationToken cancellationToken)
        => SendHelixAsync<object?>(
            $"/eventsub/subscriptions?id={Uri.EscapeDataString(subscriptionId)}",
            HttpMethod.Delete,
            body: null,
            useUserAccessToken: false,
            requestContext: $"deleting EventSub subscription id={subscriptionId}",
            cancellationToken);

    public async Task<IReadOnlyList<EventSubConduitRecord>> GetEventSubConduitsAsync(CancellationToken cancellationToken)
    {
        var response = await SendHelixAsync<HelixEnvelope<ConduitRecord>>(
            "/eventsub/conduits",
            HttpMethod.Get,
            body: null,
            useUserAccessToken: false,
            requestContext: "listing EventSub conduits",
            cancellationToken);

        if (response?.Data is null || response.Data.Count == 0)
        {
            return [];
        }

        var conduits = new List<EventSubConduitRecord>(response.Data.Count);
        foreach (var conduit in response.Data)
        {
            var shards = await GetEventSubConduitShardsAsync(conduit.Id, cancellationToken);
            conduits.Add(new EventSubConduitRecord(conduit.Id, conduit.ShardCount, shards));
        }

        return conduits;
    }

    public async Task<EventSubConduitRecord> CreateEventSubConduitAsync(int shardCount, CancellationToken cancellationToken)
    {
        var response = await SendHelixAsync<HelixEnvelope<ConduitRecord>>(
            "/eventsub/conduits",
            HttpMethod.Post,
            new
            {
                shard_count = shardCount
            },
            useUserAccessToken: false,
            requestContext: $"creating EventSub conduit with shard_count={shardCount}",
            cancellationToken);

        var record = response?.Data.FirstOrDefault()
            ?? throw new InvalidOperationException("Twitch did not return the created EventSub conduit.");

        return MapConduitRecord(record);
    }

    public async Task<EventSubConduitRecord> UpdateEventSubConduitAsync(string conduitId, int shardCount, CancellationToken cancellationToken)
    {
        var response = await SendHelixAsync<HelixEnvelope<ConduitRecord>>(
            "/eventsub/conduits",
            HttpMethod.Patch,
            new
            {
                id = conduitId,
                shard_count = shardCount
            },
            useUserAccessToken: false,
            requestContext: $"updating EventSub conduit {conduitId} shard_count={shardCount}",
            cancellationToken);

        var record = response?.Data.FirstOrDefault()
            ?? throw new InvalidOperationException($"Twitch did not return the updated EventSub conduit for conduit_id={conduitId}.");

        return MapConduitRecord(record);
    }

    public async Task<IReadOnlyList<EventSubConduitShardRecord>> UpdateEventSubConduitShardsAsync(
        string conduitId,
        IReadOnlyList<EventSubConduitShardRecord> shards,
        CancellationToken cancellationToken)
    {
        var response = await SendHelixAsync<HelixEnvelope<ConduitShardRecord>>(
            "/eventsub/conduits/shards",
            HttpMethod.Patch,
            new
            {
                conduit_id = conduitId,
                shards = shards.Select(x => new
                {
                    id = x.ShardId,
                    transport = new
                    {
                        method = "websocket",
                        session_id = x.TransportSessionId
                    }
                }).ToArray()
            },
            useUserAccessToken: false,
            requestContext: $"updating EventSub conduit shard assignments for conduit_id={conduitId} shard_ids=[{string.Join(", ", shards.Select(x => x.ShardId))}]",
            cancellationToken);

        return response?.Data.Select(MapConduitShardRecord).ToList() ?? [];
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
            useUserAccessToken: true,
            requestContext: $"creating EventSub websocket subscription type={subscriptionType} broadcaster_user_id={broadcasterUserId} session_id={sessionId}",
            cancellationToken);

        var record = response?.Data.FirstOrDefault()
            ?? throw new InvalidOperationException("Twitch did not return the created EventSub subscription.");

        return new EventSubSubscriptionRecord(record.Id, record.Type, record.Status, record.Condition.BroadcasterUserId, record.Transport.SessionId);
    }

    public async Task<EventSubSubscriptionRecord> CreateConduitSubscriptionAsync(
        string subscriptionType,
        string broadcasterUserId,
        string conduitId,
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
                method = "conduit",
                conduit_id = conduitId
            }
        };

        var response = await SendHelixAsync<HelixEnvelope<SubscriptionRecord>>(
            "/eventsub/subscriptions",
            HttpMethod.Post,
            payload,
            useUserAccessToken: false,
            requestContext: $"creating EventSub conduit subscription type={subscriptionType} broadcaster_user_id={broadcasterUserId} conduit_id={conduitId}",
            cancellationToken);

        var record = response?.Data.FirstOrDefault()
            ?? throw new InvalidOperationException($"Twitch did not return the created EventSub conduit subscription for conduit_id={conduitId}.");

        return new EventSubSubscriptionRecord(record.Id, record.Type, record.Status, record.Condition.BroadcasterUserId, record.Transport.SessionId);
    }

    private static EventSubConduitRecord MapConduitRecord(ConduitRecord record)
        => new(
            record.Id,
            record.ShardCount,
            []);

    private static EventSubConduitShardRecord MapConduitShardRecord(ConduitShardRecord record)
        => new(record.Id, record.Status, record.Transport.SessionId);

    private async Task<IReadOnlyList<EventSubConduitShardRecord>> GetEventSubConduitShardsAsync(
        string conduitId,
        CancellationToken cancellationToken)
    {
        var response = await SendHelixAsync<HelixEnvelope<ConduitShardRecord>>(
            $"/eventsub/conduits/shards?conduit_id={Uri.EscapeDataString(conduitId)}",
            HttpMethod.Get,
            body: null,
            useUserAccessToken: false,
            requestContext: $"listing EventSub conduit shards for conduit_id={conduitId}",
            cancellationToken);

        return response?.Data.Select(MapConduitShardRecord).ToList() ?? [];
    }

    private async Task<T?> SendHelixAsync<T>(
        string relativePath,
        HttpMethod method,
        object? body,
        bool useUserAccessToken,
        string requestContext,
        CancellationToken cancellationToken)
    {
        var token = useUserAccessToken
            ? await accessTokenProvider.GetUserAccessTokenAsync(cancellationToken)
            : await accessTokenProvider.GetAppAccessTokenAsync(cancellationToken);
        token ??= useUserAccessToken
            ? throw new InvalidOperationException("Twitch user authorization is not configured.")
            : throw new InvalidOperationException("Twitch app credentials are not configured.");

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
        if (!response.IsSuccessStatusCode)
        {
            var responseBody = response.Content is null ? string.Empty : await response.Content.ReadAsStringAsync(cancellationToken);
            if ((int)response.StatusCode == 429)
            {
                throw new TwitchHelixRateLimitException(
                    $"Twitch Helix request failed while {requestContext}. Status=429 ({response.ReasonPhrase}). Response={responseBody}",
                    ParseRetryAfter(response));
            }

            throw new HttpRequestException(
                $"Twitch Helix request failed while {requestContext}. Status={(int)response.StatusCode} ({response.ReasonPhrase}). Response={responseBody}",
                null,
                response.StatusCode);
        }

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

        [JsonPropertyName("title")]
        public string? Title { get; set; }
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

        [JsonPropertyName("transport")]
        public SubscriptionTransport Transport { get; set; } = new();
    }

    private sealed class ConduitRecord
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("shard_count")]
        public int ShardCount { get; set; }
    }

    private sealed class ConduitShardRecord
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("transport")]
        public SubscriptionTransport Transport { get; set; } = new();
    }

    private sealed class StreamRecord
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("user_id")]
        public string UserId { get; set; } = string.Empty;

        [JsonPropertyName("user_login")]
        public string UserLogin { get; set; } = string.Empty;

        [JsonPropertyName("started_at")]
        public DateTimeOffset StartedAt { get; set; }
    }

    private sealed class ChannelSearchRecord
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("broadcaster_login")]
        public string BroadcasterLogin { get; set; } = string.Empty;

        [JsonPropertyName("display_name")]
        public string DisplayName { get; set; } = string.Empty;

        [JsonPropertyName("thumbnail_url")]
        public string ThumbnailUrl { get; set; } = string.Empty;

        [JsonPropertyName("is_live")]
        public bool IsLive { get; set; }
    }

    private sealed class SubscriptionCondition
    {
        [JsonPropertyName("broadcaster_user_id")]
        public string? BroadcasterUserId { get; set; }
    }

    private sealed class SubscriptionTransport
    {
        [JsonPropertyName("session_id")]
        public string? SessionId { get; set; }

        [JsonPropertyName("conduit_id")]
        public string? ConduitId { get; set; }
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is not null)
        {
            return retryAfter.Delta.Value;
        }

        if (retryAfter?.Date is not null)
        {
            var delay = retryAfter.Date.Value - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        if (response.Headers.TryGetValues("Retry-After", out var values) &&
            int.TryParse(values.FirstOrDefault(), out var seconds))
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return null;
    }
}
