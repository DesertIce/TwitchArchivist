using System.Net;

namespace TwitchArchivist.Services.Twitch;

public sealed class TwitchHelixRateLimitException : HttpRequestException
{
    public TwitchHelixRateLimitException(string message, TimeSpan? retryAfter, Exception? innerException = null)
        : base(message, innerException, HttpStatusCode.TooManyRequests)
    {
        RetryAfter = retryAfter;
    }

    public TimeSpan? RetryAfter { get; }
}
