using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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

        var services = new ServiceCollection().BuildServiceProvider();
        var provider = new TwitchAccessTokenProvider(
            factory,
            services.GetRequiredService<IServiceScopeFactory>(),
            options,
            NullLogger<TwitchAccessTokenProvider>.Instance);

        var token = await provider.GetAppAccessTokenAsync(CancellationToken.None);

        Assert.Equal("test-token", token);
    }

    [Fact]
    public async Task HelixClientParsesSubscriptionConditionBroadcasterUserId()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.twitch.tv/helix/eventsub/subscriptions?first=100", request.RequestUri?.ToString());
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
                          "transport": {
                            "session_id": "session-123"
                          },
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

        var helixClient = new TwitchHelixClient(factory, authProvider, options, NullLogger<TwitchHelixClient>.Instance);

        var subscriptions = await helixClient.GetEventSubscriptionsAsync(CancellationToken.None);

        var subscription = Assert.Single(subscriptions);
        Assert.Equal("12345", subscription.BroadcasterUserId);
        Assert.Equal("stream.online", subscription.Type);
        Assert.Equal("session-123", subscription.TransportSessionId);
        Assert.Null(subscription.TransportConduitId);
    }

    [Fact]
    public async Task HelixClientGetsAllEventSubscriptionsAcrossPages()
    {
        var requests = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>([
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("https://api.twitch.tv/helix/eventsub/subscriptions?first=100", request.RequestUri?.ToString());

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
                              "transport": {
                                "conduit_id": "conduit-1"
                              },
                              "condition": {
                                "broadcaster_user_id": "12345"
                              }
                            }
                          ],
                          "pagination": {
                            "cursor": "cursor-2"
                          }
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                };
            },
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("https://api.twitch.tv/helix/eventsub/subscriptions?first=100&after=cursor-2", request.RequestUri?.ToString());

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "data": [
                            {
                              "id": "sub-2",
                              "type": "stream.offline",
                              "status": "enabled",
                              "transport": {
                                "conduit_id": "conduit-1"
                              },
                              "condition": {
                                "broadcaster_user_id": "12345"
                              }
                            }
                          ],
                          "pagination": {}
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                };
            }
        ]);
        var handler = new StubHttpMessageHandler(request => requests.Dequeue().Invoke(request));
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

        var helixClient = new TwitchHelixClient(factory, authProvider, options, NullLogger<TwitchHelixClient>.Instance);

        var subscriptions = await helixClient.GetEventSubscriptionsAsync(CancellationToken.None);

        Assert.Collection(
            subscriptions,
            subscription =>
            {
                Assert.Equal("sub-1", subscription.Id);
                Assert.Equal("conduit-1", subscription.TransportConduitId);
            },
            subscription =>
            {
                Assert.Equal("sub-2", subscription.Id);
                Assert.Equal("conduit-1", subscription.TransportConduitId);
            });
    }

    [Fact]
    public async Task HelixClientUsesAppTokenToListConduitSubscriptions()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            Assert.Equal("Bearer app-token", request.Headers.Authorization?.ToString());

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "data": [],
                      "pagination": {}
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
        var authProvider = new StubAccessTokenProvider("user-token", "app-token");
        var options = Options.Create(new TwitchOptions
        {
            ClientId = "client-id",
            EventSubTransportMode = "conduit-websocket"
        });

        var helixClient = new TwitchHelixClient(factory, authProvider, options, NullLogger<TwitchHelixClient>.Instance);

        var subscriptions = await helixClient.GetEventSubscriptionsAsync(CancellationToken.None);

        Assert.Empty(subscriptions);
    }

    [Fact]
    public async Task HelixClientUsesUserTokenToListWebsocketSubscriptions()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            Assert.Equal("Bearer user-token", request.Headers.Authorization?.ToString());

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "data": [],
                      "pagination": {}
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
        var authProvider = new StubAccessTokenProvider("user-token", "app-token");
        var options = Options.Create(new TwitchOptions
        {
            ClientId = "client-id",
            EventSubTransportMode = "websocket"
        });

        var helixClient = new TwitchHelixClient(factory, authProvider, options, NullLogger<TwitchHelixClient>.Instance);

        var subscriptions = await helixClient.GetEventSubscriptionsAsync(CancellationToken.None);

        Assert.Empty(subscriptions);
    }

    [Fact]
    public async Task HelixClientSkipsArchiveVideosCreatedBeforeTheCurrentStream()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "data": [
                    {
                      "id": "older-vod",
                      "created_at": "2026-04-27T11:30:00Z",
                      "title": "Older archive"
                    },
                    {
                      "id": "current-vod",
                      "created_at": "2026-04-27T12:05:00Z",
                      "title": "Current archive"
                    }
                  ]
                }
                """,
                Encoding.UTF8,
                "application/json")
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

        var helixClient = new TwitchHelixClient(factory, authProvider, options, NullLogger<TwitchHelixClient>.Instance);

        var vod = await helixClient.GetLatestArchiveVodAsync(
            "12345",
            new DateTimeOffset(2026, 4, 27, 12, 0, 0, TimeSpan.Zero),
            CancellationToken.None);

        Assert.Equal("current-vod", vod?.Id);
        Assert.Equal("Current archive", vod?.Title);
    }

    [Fact]
    public async Task HelixClientResolvesUsersAgainstHelixPath()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            Assert.Equal("https://api.twitch.tv/helix/users?login=testchannel", request.RequestUri?.ToString());

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "data": [
                        {
                          "id": "12345"
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

        var helixClient = new TwitchHelixClient(factory, authProvider, options, NullLogger<TwitchHelixClient>.Instance);

        var userId = await helixClient.ResolveUserIdAsync("testchannel", CancellationToken.None);

        Assert.Equal("12345", userId);
    }

    [Fact]
    public async Task HelixClientSearchesChannelsAgainstHelixSearchPath()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            Assert.Equal("https://api.twitch.tv/helix/search/channels?query=test&first=10", request.RequestUri?.ToString());

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "data": [
                        {
                          "id": "12345",
                          "broadcaster_login": "testchannel",
                          "display_name": "TestChannel",
                          "thumbnail_url": "https://example.com/avatar.png",
                          "is_live": true
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

        var helixClient = new TwitchHelixClient(factory, authProvider, options, NullLogger<TwitchHelixClient>.Instance);

        var channels = await helixClient.SearchChannelsAsync("test", CancellationToken.None);

        var channel = Assert.Single(channels);
        Assert.Equal("12345", channel.UserId);
        Assert.Equal("testchannel", channel.Login);
        Assert.Equal("TestChannel", channel.DisplayName);
        Assert.Equal("https://example.com/avatar.png", channel.ThumbnailUrl);
        Assert.True(channel.IsLive);
    }

    [Fact]
    public async Task HelixClientCreatesConduitWithAppToken()
    {
        var handler = new StubHttpMessageHandler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.twitch.tv/helix/eventsub/conduits", request.RequestUri?.ToString());
            Assert.Equal("Bearer test-token", request.Headers.Authorization?.ToString());

            var payload = await request.Content!.ReadAsStringAsync();
            Assert.Contains("\"shard_count\":4", payload);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "data": [
                        {
                          "id": "conduit-123",
                          "shard_count": 4
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var helixClient = CreateHelixClient(handler);

        var conduit = await helixClient.CreateEventSubConduitAsync(4, CancellationToken.None);

        Assert.Equal("conduit-123", conduit.Id);
        Assert.Equal(4, conduit.ShardCount);
        Assert.Empty(conduit.Shards);
    }

    [Fact]
    public async Task HelixClientGetsExistingConduitsWithShards()
    {
        var requests = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>([
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("https://api.twitch.tv/helix/eventsub/conduits", request.RequestUri?.ToString());

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "data": [
                            {
                              "id": "conduit-123",
                              "shard_count": 2
                            }
                          ]
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                };
            },
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("https://api.twitch.tv/helix/eventsub/conduits/shards?conduit_id=conduit-123", request.RequestUri?.ToString());

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "data": [
                            {
                              "id": "0",
                              "status": "enabled",
                              "transport": {
                                "session_id": "session-a"
                              }
                            },
                            {
                              "id": "1",
                              "status": "websocket_disconnected",
                              "transport": {
                                "session_id": "session-b"
                              }
                            }
                          ]
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                };
            }
        ]);
        var handler = new StubHttpMessageHandler(request => requests.Dequeue().Invoke(request));
        var helixClient = CreateHelixClient(handler);

        var conduits = await helixClient.GetEventSubConduitsAsync(CancellationToken.None);

        var conduit = Assert.Single(conduits);
        Assert.Equal("conduit-123", conduit.Id);
        Assert.Equal(2, conduit.ShardCount);
        Assert.Collection(
            conduit.Shards.OrderBy(x => x.ShardId),
            shard =>
            {
                Assert.Equal("0", shard.ShardId);
                Assert.Equal("enabled", shard.Status);
                Assert.Equal("session-a", shard.TransportSessionId);
            },
            shard =>
            {
                Assert.Equal("1", shard.ShardId);
                Assert.Equal("websocket_disconnected", shard.Status);
                Assert.Equal("session-b", shard.TransportSessionId);
            });
    }

    [Fact]
    public async Task HelixClientUpdatesConduitShardAssignments()
    {
        var handler = new StubHttpMessageHandler(async request =>
        {
            Assert.Equal(HttpMethod.Patch, request.Method);
            Assert.Equal("https://api.twitch.tv/helix/eventsub/conduits/shards", request.RequestUri?.ToString());

            var payload = await request.Content!.ReadAsStringAsync();
            Assert.Contains("\"conduit_id\":\"conduit-123\"", payload);
            Assert.Contains("\"id\":\"0\"", payload);
            Assert.Contains("\"session_id\":\"session-a\"", payload);
            Assert.Contains("\"id\":\"1\"", payload);
            Assert.Contains("\"session_id\":\"session-b\"", payload);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "data": [
                        {
                          "id": "0",
                          "status": "enabled",
                          "transport": {
                            "session_id": "session-a"
                          }
                        },
                        {
                          "id": "1",
                          "status": "enabled",
                          "transport": {
                            "session_id": "session-b"
                          }
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var helixClient = CreateHelixClient(handler);

        var shards = await helixClient.UpdateEventSubConduitShardsAsync(
            "conduit-123",
            [
                new EventSubConduitShardRecord("0", "enabled", "session-a"),
                new EventSubConduitShardRecord("1", "enabled", "session-b")
            ],
            CancellationToken.None);

        Assert.Collection(
            shards.OrderBy(x => x.ShardId),
            shard => Assert.Equal("session-a", shard.TransportSessionId),
            shard => Assert.Equal("session-b", shard.TransportSessionId));
    }

    [Fact]
    public async Task HelixClientCreatesConduitBackedSubscription()
    {
        var handler = new StubHttpMessageHandler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.twitch.tv/helix/eventsub/subscriptions", request.RequestUri?.ToString());

            var payload = await request.Content!.ReadAsStringAsync();
            Assert.Contains("\"type\":\"stream.offline\"", payload);
            Assert.Contains("\"broadcaster_user_id\":\"12345\"", payload);
            Assert.Contains("\"method\":\"conduit\"", payload);
            Assert.Contains("\"conduit_id\":\"conduit-123\"", payload);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "data": [
                        {
                          "id": "sub-1",
                          "type": "stream.offline",
                          "status": "enabled",
                          "condition": {
                            "broadcaster_user_id": "12345"
                          },
                          "transport": {
                            "conduit_id": "conduit-123"
                          }
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var helixClient = CreateHelixClient(handler);

        var subscription = await helixClient.CreateConduitSubscriptionAsync(
            "stream.offline",
            "12345",
            "conduit-123",
            CancellationToken.None);

        Assert.Equal("sub-1", subscription.Id);
        Assert.Equal("stream.offline", subscription.Type);
        Assert.Equal("12345", subscription.BroadcasterUserId);
        Assert.Null(subscription.TransportSessionId);
    }

    [Fact]
    public async Task HelixClientIncludesConduitContextWhenShardAssignmentFails()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            ReasonPhrase = "Too Many Requests",
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        });
        var helixClient = CreateHelixClient(handler);

        var exception = await Assert.ThrowsAsync<TwitchHelixRateLimitException>(() => helixClient.UpdateEventSubConduitShardsAsync(
            "conduit-123",
            [new EventSubConduitShardRecord("7", "enabled", "session-z")],
            CancellationToken.None));

        Assert.Contains("conduit-123", exception.Message);
        Assert.Contains("7", exception.Message);
    }

    [Fact]
    public async Task HelixClientParsesRetryAfterOnRateLimit()
    {
        var handler = new StubHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                ReasonPhrase = "Too Many Requests",
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(12));
            return response;
        });
        var helixClient = CreateHelixClient(handler);

        var exception = await Assert.ThrowsAsync<TwitchHelixRateLimitException>(() => helixClient.UpdateEventSubConduitShardsAsync(
            "conduit-123",
            [new EventSubConduitShardRecord("7", "enabled", "session-z")],
            CancellationToken.None));

        Assert.Equal(TimeSpan.FromSeconds(12), exception.RetryAfter);
    }

    [Fact]
    public async Task HelixClientUpdatesConduitShardCount()
    {
        var handler = new StubHttpMessageHandler(async request =>
        {
            Assert.Equal(HttpMethod.Patch, request.Method);
            Assert.Equal("https://api.twitch.tv/helix/eventsub/conduits", request.RequestUri?.ToString());

            var payload = await request.Content!.ReadAsStringAsync();
            Assert.Contains("\"id\":\"conduit-123\"", payload);
            Assert.Contains("\"shard_count\":6", payload);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "data": [
                        {
                          "id": "conduit-123",
                          "shard_count": 6
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var helixClient = CreateHelixClient(handler);

        var conduit = await helixClient.UpdateEventSubConduitAsync("conduit-123", 6, CancellationToken.None);

        Assert.Equal("conduit-123", conduit.Id);
        Assert.Equal(6, conduit.ShardCount);
    }

    [Fact]
    public async Task HelixClientRetriesGetAfterTransientTransportFailures()
    {
        var attemptCount = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            attemptCount++;
            if (attemptCount < 3)
            {
                throw new HttpRequestException("Temporary DNS failure");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "data": [
                        {
                          "id": "12345"
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var helixClient = CreateHelixClient(handler);

        var userId = await helixClient.ResolveUserIdAsync("testchannel", CancellationToken.None);

        Assert.Equal("12345", userId);
        Assert.Equal(3, attemptCount);
    }

    [Fact]
    public async Task HelixClientDoesNotRetryPostAfterTransientTransportFailure()
    {
        var attemptCount = 0;
        var handler = new StubHttpMessageHandler(
            new Func<HttpRequestMessage, HttpResponseMessage>(_ =>
            {
                attemptCount++;
                throw new HttpRequestException("Temporary DNS failure");
            }));
        var helixClient = CreateHelixClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => helixClient.CreateEventSubConduitAsync(4, CancellationToken.None));

        Assert.Equal(1, attemptCount);
    }

    [Fact]
    public async Task HelixClientCancelsGetDuringRetryDelay()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        var attemptCount = 0;
        var handler = new StubHttpMessageHandler(
            new Func<HttpRequestMessage, HttpResponseMessage>(_ =>
            {
                attemptCount++;
                throw new HttpRequestException("Temporary DNS failure");
            }));
        var helixClient = CreateHelixClient(handler);
        cancellationTokenSource.CancelAfter(TimeSpan.FromMilliseconds(100));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => helixClient.ResolveUserIdAsync("testchannel", cancellationTokenSource.Token));

        stopwatch.Stop();
        Assert.Equal(1, attemptCount);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(750),
            $"Cancellation took {stopwatch.Elapsed}.");
    }

    private static TwitchHelixClient CreateHelixClient(HttpMessageHandler handler)
    {
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

        return new TwitchHelixClient(factory, authProvider, options, NullLogger<TwitchHelixClient>.Instance);
    }

    private sealed class StubAccessTokenProvider(string userToken = "test-token", string appToken = "test-token") : ITwitchAccessTokenProvider
    {
        public string? BuildUserAuthorizationUrl(string state, string redirectUri) => null;

        public Task ExchangeAuthorizationCodeAsync(string code, string redirectUri, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<string?> GetAppAccessTokenAsync(CancellationToken cancellationToken)
            => Task.FromResult<string?>(appToken);

        public Task<string?> GetUserAccessTokenAsync(CancellationToken cancellationToken)
            => Task.FromResult<string?>(userToken);

        public Task<TwitchUserAuthorizationState> GetUserAuthorizationStateAsync(CancellationToken cancellationToken)
            => Task.FromResult(new TwitchUserAuthorizationState(false, false, "missing", null, false, null, null, null, null));

        public Task<TwitchUserAuthorizationState> ValidateUserAuthorizationAsync(CancellationToken cancellationToken)
            => Task.FromResult(new TwitchUserAuthorizationState(false, false, "missing", null, false, null, null, null, null));
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = request => Task.FromResult(handler(request));
        }

        public StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _handler(request);
    }
}
