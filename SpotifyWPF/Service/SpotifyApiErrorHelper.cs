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
            object retryAfter = ex.RetryAfter;

            switch (retryAfter)
            {
                case TimeSpan timeSpan:
                    return timeSpan;
                case int seconds:
                    return TimeSpan.FromSeconds(seconds);
                case long seconds:
                    return TimeSpan.FromSeconds(seconds);
                case double seconds:
                    return TimeSpan.FromSeconds(seconds);
                default:
                    return TimeSpan.FromSeconds(1);
            }
        }

        public static string FormatRetryDelay(TimeSpan retryDelay)
        {
            return retryDelay.TotalHours >= 1
                ? $"{retryDelay:c} ({retryDelay.TotalHours:N1} hour(s))"
                : retryDelay.ToString();
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
