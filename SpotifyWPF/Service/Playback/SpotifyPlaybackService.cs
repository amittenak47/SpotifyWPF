using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SpotifyAPI.Web;

namespace SpotifyWPF.Service.Playback
{
    public interface ISpotifyPlaybackService
    {
        /// <summary>Starts playback of a single track on the given device (the in-app SDK player).</summary>
        Task PlayTrackAsync(string trackId, string deviceId, long positionMs = 0);

        /// <summary>Starts playback of a context (playlist/album URI) on the given device.</summary>
        Task PlayContextAsync(string contextUri, string deviceId);

        Task TransferPlaybackAsync(string deviceId, bool play);

        Task PauseAsync(string deviceId);

        Task SeekAsync(long positionMs, string deviceId);

        Task<CurrentlyPlayingContext> GetCurrentPlaybackAsync();

        Task<List<PlayHistoryItem>> GetRecentlyPlayedAsync(int limit);
    }

    /// <summary>
    /// Thin wrapper over the Web API player endpoints, reusing the request-spacing + 429-retry pattern
    /// from the Playlists page. Playback itself stays inside the WebView2 SDK player; these endpoints
    /// start tracks on it and act as a fallback control channel.
    /// </summary>
    public class SpotifyPlaybackService : ISpotifyPlaybackService
    {
        private const int RequestSpacingMilliseconds = 150;

        private const int MaxRetries = 3;

        private readonly ISpotify _spotify;

        private readonly SemaphoreSlim _requestSpacing = new SemaphoreSlim(1, 1);

        public SpotifyPlaybackService(ISpotify spotify)
        {
            _spotify = spotify;
        }

        public async Task PlayTrackAsync(string trackId, string deviceId, long positionMs = 0)
        {
            var request = new PlayerResumePlaybackRequest
            {
                DeviceId = deviceId,
                Uris = new List<string> { $"spotify:track:{trackId}" },
                PositionMs = (int)positionMs
            };

            await ExecuteAsync(api => api.Player.ResumePlayback(request));
        }

        public async Task PlayContextAsync(string contextUri, string deviceId)
        {
            var request = new PlayerResumePlaybackRequest
            {
                DeviceId = deviceId,
                ContextUri = contextUri
            };

            await ExecuteAsync(api => api.Player.ResumePlayback(request));
        }

        public async Task TransferPlaybackAsync(string deviceId, bool play)
        {
            var request = new PlayerTransferPlaybackRequest(new List<string> { deviceId })
            {
                Play = play
            };

            await ExecuteAsync(api => api.Player.TransferPlayback(request));
        }

        public async Task PauseAsync(string deviceId)
        {
            var request = new PlayerPausePlaybackRequest { DeviceId = deviceId };

            await ExecuteAsync(api => api.Player.PausePlayback(request));
        }

        public async Task SeekAsync(long positionMs, string deviceId)
        {
            var request = new PlayerSeekToRequest(positionMs) { DeviceId = deviceId };

            await ExecuteAsync(api => api.Player.SeekTo(request));
        }

        public async Task<CurrentlyPlayingContext> GetCurrentPlaybackAsync()
        {
            return await ExecuteAsync(api => api.Player.GetCurrentPlayback());
        }

        public async Task<List<PlayHistoryItem>> GetRecentlyPlayedAsync(int limit)
        {
            var response = await ExecuteAsync(api =>
                api.Player.GetRecentlyPlayed(new PlayerRecentlyPlayedRequest { Limit = limit }));

            return response?.Items ?? new List<PlayHistoryItem>();
        }

        private async Task<T> ExecuteAsync<T>(Func<ISpotifyClient, Task<T>> operation)
        {
            var api = _spotify.Api;

            if (api == null)
                throw new InvalidOperationException("Not logged in to Spotify.");

            for (var attempt = 0; ; attempt++)
            {
                await WaitForRequestSpacingAsync();

                try
                {
                    return await operation(api);
                }
                catch (APITooManyRequestsException ex) when (attempt < MaxRetries)
                {
                    var delay = ex.RetryAfter > TimeSpan.Zero ? ex.RetryAfter : TimeSpan.FromSeconds(1);
                    await Task.Delay(delay);
                }
            }
        }

        private async Task ExecuteAsync(Func<ISpotifyClient, Task<bool>> operation)
        {
            await ExecuteAsync<bool>(operation);
        }

        private async Task WaitForRequestSpacingAsync()
        {
            await _requestSpacing.WaitAsync();

            try
            {
                await Task.Delay(RequestSpacingMilliseconds);
            }
            finally
            {
                _requestSpacing.Release();
            }
        }
    }
}
