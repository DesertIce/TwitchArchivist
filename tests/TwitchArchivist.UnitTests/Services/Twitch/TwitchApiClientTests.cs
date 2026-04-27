using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;
using TwitchArchivist.Models;
using TwitchArchivist.Services.Twitch;

namespace TwitchArchivist.UnitTests.Services.Twitch;

public class TwitchApiClientTests
{
    [Fact]
    public async Task AccessTokenProviderParsesSnakeCaseResponse()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "access_token": "test-token",
                  "expires_in": 3600
                }
                """,
                Encoding.UTF8,
                "application/json")
        });
        var factory = new StubHttpClientFactory(new HttpClient(handler));
        var options = Options.Create(new TwitchOptions
        {
            ClientId = "client-id",
            ClientSecret = "client-secret",
            AppAccessTokenRefreshBufferMinutes = 5
        });

        var provider = new TwitchAccessTokenProvider(factory, options);

        var token = await provider.GetAccessTokenAsync(CancellationToken.None);

        Assert.Equal("test-token", token);
    }

    [Fact]
    public async Task HelixClientParsesSubscriptionConditionBroadcasterUserId()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("Bearer test-token", request.Headers.Authorization?.ToString());
            Assert.Equal("client-id", request.Headers.GetValues("Client-Id").Single());

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "data": [
                        {
                          "id": "sub-1",
                          "type": "stream.online",
                          "status": "enabled",
                          "condition": {
                            "broadcaster_user_id": "12345"
                          }
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.twitch.tv/helix/")
        };
        var factory = new StubHttpClientFactory(client);
        var authProvider = new StubAccessTokenProvider();
        var options = Options.Create(new TwitchOptions
        {
            ClientId = "client-id"
        });

        var helixClient = new TwitchHelixClient(factory, authProvider, options);

        var subscriptions = await helixClient.GetEventSubscriptionsAsync(CancellationToken.None);

        var subscription = Assert.Single(subscriptions);
        Assert.Equal("12345", subscription.BroadcasterUserId);
        Assert.Equal("stream.online", subscription.Type);
    }

    private sealed class StubAccessTokenProvider : ITwitchAccessTokenProvider
    {
        public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
            => Task.FromResult<string?>("test-token");
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
