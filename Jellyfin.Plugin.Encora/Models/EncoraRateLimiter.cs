using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// A shared rate limiter for calls to the Encora API, driven by the X-RateLimit-* / Retry-After
    /// response headers Encora actually returns, since all metadata providers (Movie, Series, Season,
    /// Episode, Artist, Album, Track) hit the same endpoint and share the same limit.
    /// </summary>
    public static class EncoraRateLimiter
    {
        private static readonly object Lock = new();
        private static int? _remaining;
        private static DateTimeOffset? _resetAt;

        /// <summary>
        /// Waits, if a previous response indicated the limit is currently exhausted, until Encora's
        /// reported reset time before allowing another request through.
        /// </summary>
        /// <param name="logger">Logger for diagnostics.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task that completes once a request is safe to make.</returns>
        public static async Task WaitAsync(ILogger logger, CancellationToken cancellationToken)
        {
            TimeSpan delay;
            lock (Lock)
            {
                if (_remaining is null or > 0 || _resetAt is null)
                {
                    return;
                }

                delay = _resetAt.Value - DateTimeOffset.UtcNow;
            }

            if (delay > TimeSpan.Zero)
            {
                logger.LogInformation("[Encora] Rate limit reached, waiting {Delay} before the next request", delay);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Records the rate-limit state reported by an Encora API response, so subsequent calls to
        /// <see cref="WaitAsync"/> know whether to back off.
        /// </summary>
        /// <param name="response">The HTTP response from Encora.</param>
        public static void UpdateFromResponse(HttpResponseMessage response)
        {
            lock (Lock)
            {
                if (TryGetHeaderInt(response, "X-RateLimit-Remaining", out var remaining))
                {
                    _remaining = remaining;
                }

                if (TryGetHeaderLong(response, "X-RateLimit-Reset", out var resetEpochSeconds))
                {
                    _resetAt = DateTimeOffset.FromUnixTimeSeconds(resetEpochSeconds);
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests &&
                    TryGetHeaderInt(response, "Retry-After", out var retryAfterSeconds))
                {
                    _remaining = 0;
                    _resetAt = DateTimeOffset.UtcNow.AddSeconds(retryAfterSeconds);
                }
            }
        }

        private static bool TryGetHeaderInt(HttpResponseMessage response, string headerName, out int value)
        {
            value = 0;
            return response.Headers.TryGetValues(headerName, out var values) &&
                   int.TryParse(values.FirstOrDefault(), out value);
        }

        private static bool TryGetHeaderLong(HttpResponseMessage response, string headerName, out long value)
        {
            value = 0;
            return response.Headers.TryGetValues(headerName, out var values) &&
                   long.TryParse(values.FirstOrDefault(), out value);
        }
    }
}
