using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using SpotifyAPI.Web;

namespace SpotifyWPF.Service
{
    /// <summary>
    /// Shared classification and formatting helpers for Spotify API failures
    /// (rate limits, transient connection errors, scope problems).
    /// </summary>
    public static class SpotifyApiErrorHelper
    {
        public static TimeSpan GetRetryDelay(APITooManyRequestsException ex)
        {
            if (ex == null)
                return TimeSpan.FromSeconds(1);

            if (TryGetRetryAfterHeaderSeconds(ex, out var headerSeconds) && headerSeconds > 0)
                return TimeSpan.FromSeconds(headerSeconds);

            if (ex.RetryAfter > TimeSpan.Zero)
                return ex.RetryAfter;

            return TimeSpan.FromSeconds(1);
        }

        /// <summary>
        /// Raw Retry-After header value from the 429 response when present;
        /// otherwise the resolved delay in whole seconds.
        /// </summary>
        public static string GetRetryAfterHeaderValue(APITooManyRequestsException ex)
        {
            if (TryGetRetryAfterHeaderRaw(ex, out var raw) && !string.IsNullOrWhiteSpace(raw))
                return raw.Trim();

            var seconds = Math.Max(1, (int)Math.Ceiling(GetRetryDelay(ex).TotalSeconds));
            return seconds.ToString();
        }

        /// <summary>
        /// Log/status fragment, e.g. "Retry-After: 7 (00:00:07)".
        /// </summary>
        public static string FormatRetryAfter(APITooManyRequestsException ex)
        {
            var header = GetRetryAfterHeaderValue(ex);
            var delay = GetRetryDelay(ex);
            return $"Retry-After: {header} ({FormatRetryDelay(delay)})";
        }

        public static string FormatRetryDelay(TimeSpan retryDelay)
        {
            return retryDelay.TotalHours >= 1
                ? $"{retryDelay:c} ({retryDelay.TotalHours:N1} hour(s))"
                : retryDelay.ToString();
        }

        private static bool TryGetRetryAfterHeaderRaw(APITooManyRequestsException ex, out string value)
        {
            value = null;
            var headers = ex?.Response?.Headers;
            if (headers == null)
                return false;

            foreach (var pair in headers)
            {
                if (!pair.Key.Equals("Retry-After", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (string.IsNullOrWhiteSpace(pair.Value))
                    return false;

                value = pair.Value;
                return true;
            }

            return false;
        }

        private static bool TryGetRetryAfterHeaderSeconds(APITooManyRequestsException ex, out double seconds)
        {
            seconds = 0;
            if (!TryGetRetryAfterHeaderRaw(ex, out var raw))
                return false;

            if (double.TryParse(raw.Trim(), out seconds) && seconds >= 0)
                return true;

            // HTTP Retry-After may also be an HTTP-date; fall back to library TimeSpan.
            return false;
        }

        public static bool ContainsExceptionMessage(Exception ex, string value)
        {
            for (var currentException = ex; currentException != null; currentException = currentException.InnerException)
            {
                if (currentException.Message?.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        public static bool IsInsufficientScope(Exception ex)
        {
            return ContainsExceptionMessage(ex, "insufficient client scope");
        }

        public static bool IsTransientApiException(Exception ex)
        {
            return ex is HttpRequestException ||
                   ex is WebException ||
                   ex is IOException ||
                   ex is TaskCanceledException ||
                   ContainsExceptionMessage(ex, "underlying connection was closed") ||
                   ContainsExceptionMessage(ex, "connection was closed") ||
                   ContainsExceptionMessage(ex, "request was aborted") ||
                   ContainsExceptionMessage(ex, "temporarily unavailable");
        }

        public static bool IsPlaylistTracksForbidden(APIException ex)
        {
            return ex != null && ex.Message?.IndexOf("Forbidden", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
