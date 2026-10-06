using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// Tracks server outages and down states for StageMedia (522, 5xx, timeouts, connection failures).
    /// Enforces a cooldown period (default 6 hours) before allowing requests to resume, preventing
    /// library refreshes or background tasks from repeatedly hitting or hanging on an offline server.
    /// </summary>
    public static class StageMediaCircuitBreaker
    {
        private static readonly object Lock = new();
        private static readonly TimeSpan DefaultCooldown = TimeSpan.FromHours(6);
        private static DateTimeOffset? _disabledUntil;
        private static string? _lastFailureReason;

        /// <summary>
        /// Checks whether StageMedia is currently available or within its backoff cooldown period.
        /// </summary>
        /// <param name="logger">Logger for diagnostics.</param>
        /// <param name="reason">The recorded reason for the outage if currently cooling down.</param>
        /// <returns><c>true</c> if StageMedia is available to call; <c>false</c> if in cooldown.</returns>
        public static bool IsAvailable(ILogger logger, out string? reason)
        {
            lock (Lock)
            {
                if (_disabledUntil.HasValue)
                {
                    var now = DateTimeOffset.UtcNow;
                    if (now < _disabledUntil.Value)
                    {
                        var remaining = _disabledUntil.Value - now;
                        reason = _lastFailureReason;
                        logger.LogInformation(
                            "[Encora] [StageMedia] In cooldown until {DisabledUntil} (remaining: {Remaining:hh\\:mm\\:ss}) due to previous outage ({Reason}). Skipping request.",
                            _disabledUntil.Value.ToLocalTime(),
                            remaining,
                            reason ?? "Server down");
                        return false;
                    }

                    // Cooldown expired
                    _disabledUntil = null;
                    _lastFailureReason = null;
                }

                reason = null;
                return true;
            }
        }

        /// <summary>
        /// Convenience check returning true if available to call.
        /// </summary>
        /// <param name="logger">Logger for diagnostics.</param>
        /// <returns><c>true</c> if StageMedia is available to call; otherwise, <c>false</c>.</returns>
        public static bool IsAvailable(ILogger logger)
        {
            return IsAvailable(logger, out _);
        }

        /// <summary>
        /// Records an HTTP response. If the response status indicates an outage/server error
        /// (e.g. 522 Connection Timed Out, 502 Bad Gateway, 503 Service Unavailable, 504 Gateway Timeout,
        /// 500 Internal Server Error, 521 Web Server Is Down, 523, 524, 429 Too Many Requests),
        /// triggers the cooldown backoff.
        /// </summary>
        /// <param name="logger">Logger for diagnostics.</param>
        /// <param name="response">The HTTP response received from StageMedia.</param>
        public static void RecordResponse(ILogger logger, HttpResponseMessage response)
        {
            var code = (int)response.StatusCode;
            if (IsServerDownStatusCode(code, response.StatusCode))
            {
                var reason = $"HTTP {code} ({response.ReasonPhrase ?? response.StatusCode.ToString()})";
                TriggerCooldown(logger, reason);
            }
        }

        /// <summary>
        /// Records an exception from a request to StageMedia (e.g. timeout, connection refused, DNS error).
        /// If the exception indicates a network/server failure or Cloudflare outage, triggers the cooldown backoff.
        /// </summary>
        /// <param name="logger">Logger for diagnostics.</param>
        /// <param name="ex">The exception encountered.</param>
        public static void RecordException(ILogger logger, Exception ex)
        {
            var msg = ex.Message;
            if (ex is HttpRequestException httpEx && httpEx.StatusCode.HasValue)
            {
                var code = (int)httpEx.StatusCode.Value;
                if (IsServerDownStatusCode(code, httpEx.StatusCode.Value))
                {
                    TriggerCooldown(logger, $"HTTP {code}");
                    return;
                }
            }

            // Check for timeout, connection failure, or server error in message
            if (ex is TimeoutException or TaskCanceledException ||
                msg.Contains("522", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("521", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("523", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("524", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("502", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("503", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("504", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("Connection refused", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase))
            {
                TriggerCooldown(logger, msg);
            }
        }

        /// <summary>
        /// Explicitly triggers the cooldown period for StageMedia.
        /// </summary>
        /// <param name="logger">Logger for diagnostics.</param>
        /// <param name="reason">Detailed reason for cooldown.</param>
        /// <param name="cooldown">Optional custom cooldown duration (defaults to 6 hours).</param>
        public static void TriggerCooldown(ILogger logger, string reason, TimeSpan? cooldown = null)
        {
            var duration = cooldown ?? DefaultCooldown;
            lock (Lock)
            {
                _disabledUntil = DateTimeOffset.UtcNow.Add(duration);
                _lastFailureReason = reason;
                logger.LogWarning(
                    "[Encora] [StageMedia] Server outage detected ({Reason}). Pausing StageMedia requests for {Duration:c} until {DisabledUntil}.",
                    reason,
                    duration,
                    _disabledUntil.Value.ToLocalTime());
            }
        }

        /// <summary>
        /// Resets the circuit breaker (e.g. when a successful response is received or manually reset).
        /// </summary>
        public static void Reset()
        {
            lock (Lock)
            {
                _disabledUntil = null;
                _lastFailureReason = null;
            }
        }

        private static bool IsServerDownStatusCode(int statusCode, HttpStatusCode httpStatusCode)
        {
            // 520-527 are Cloudflare edge/origin error codes (522 = Connection Timed Out, 521 = Web Server Down, etc.)
            if (statusCode is >= 520 and <= 527)
            {
                return true;
            }

            // Standard 5xx server errors
            if (statusCode is 500 or 502 or 503 or 504)
            {
                return true;
            }

            // Cloudflare / proxy rate-limits or 429
            if (httpStatusCode == HttpStatusCode.TooManyRequests)
            {
                return true;
            }

            return false;
        }
    }
}
