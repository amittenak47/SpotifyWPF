using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SpotifyAPI.Web;
using SpotifyWPF.Model;

namespace SpotifyWPF.Service
{
    public class PlaylistDeletionService : IPlaylistDeletionService
    {
        public const int MaxConcurrentPlaylistDeletes = 4;
        private const int MaxTransientDeleteAttempts = 4;

        private readonly ISpotify _spotify;
        private readonly IRequestSpacingService _requestSpacing;

        public PlaylistDeletionService(ISpotify spotify, IRequestSpacingService requestSpacing)
        {
            _spotify = spotify;
            _requestSpacing = requestSpacing;
        }

        public event Action<string, bool> LogMessage;

        public async Task<IReadOnlyList<DeletePlaylistResult>> DeletePlaylistsAsync(
            IReadOnlyList<DeletionQueueItem> playlists,
            CancellationTokenSource cancellationTokenSource)
        {
            if (playlists == null || !playlists.Any())
                return new List<DeletePlaylistResult>();

            var deleteBatches = CreateDeleteBatches(playlists);
            var deleteTasks = deleteBatches.Select(batch => DeletePlaylistBatchAsync(batch, cancellationTokenSource)).ToList();

            return (await Task.WhenAll(deleteTasks)).SelectMany(result => result).ToList();
        }

        // Splits the work into up to four contiguous batches so deletes run with
        // bounded concurrency (MaxConcurrentPlaylistDeletes workers).
        private static List<List<DeletionQueueItem>> CreateDeleteBatches(IReadOnlyList<DeletionQueueItem> playlists)
        {
            var batches = new List<List<DeletionQueueItem>>();

            if (!playlists.Any())
                return batches;

            var starts = new[]
            {
                0,
                playlists.Count / 4,
                playlists.Count / 2,
                playlists.Count * 3 / 4,
                playlists.Count
            }.Distinct().OrderBy(index => index).ToList();

            for (var i = 0; i < starts.Count - 1; i++)
            {
                var start = starts[i];
                var end = starts[i + 1];
                var batch = new List<DeletionQueueItem>();

                for (var playlistIndex = start; playlistIndex < end; playlistIndex++)
                    batch.Add(playlists[playlistIndex]);

                if (batch.Any())
                    batches.Add(batch);
            }

            return batches;
        }

        private async Task<List<DeletePlaylistResult>> DeletePlaylistBatchAsync(List<DeletionQueueItem> playlists, CancellationTokenSource cancellationTokenSource)
        {
            var results = new List<DeletePlaylistResult>();

            if (!playlists.Any())
                return results;

            Log($"Delete worker starting at '{playlists[0].Playlist.Name}' with {playlists.Count} playlist(s).", true);

            foreach (var playlist in playlists)
            {
                if (cancellationTokenSource.IsCancellationRequested)
                    break;

                try
                {
                    var (status, retryAfter) = await DeletePlaylistWithRetryAsync(playlist.Playlist, cancellationTokenSource);
                    results.Add(new DeletePlaylistResult(playlist, status, retryAfter));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            return results;
        }

        private async Task<(DeletionStatus Status, TimeSpan? RetryAfter)> DeletePlaylistWithRetryAsync(PlaylistCacheItem playlist, CancellationTokenSource cancellationTokenSource)
        {
            var transientAttempt = 0;

            while (true)
            {
                if (cancellationTokenSource.IsCancellationRequested)
                    return (DeletionStatus.Failed, null);

                try
                {
                    Log($"Deleting playlist: {playlist.Name}", true);
                    await _requestSpacing.RunSpacedAsync(
                        () => _spotify.Api.Follow.UnfollowPlaylist(playlist.Id),
                        cancellationTokenSource.Token);

                    Log($"Successfully deleted playlist '{playlist.Name}'.");
                    return (DeletionStatus.Deleted, null);
                }
                catch (APITooManyRequestsException ex)
                {
                    var retryAfter = SpotifyApiErrorHelper.GetRetryDelay(ex);
                    Log($"Spotify rate limit while deleting '{playlist.Name}'. Cancelling remaining staged deletions. {SpotifyApiErrorHelper.FormatRetryAfter(ex)}.");
                    cancellationTokenSource.Cancel();
                    return (DeletionStatus.RateLimited, retryAfter);
                }
                catch (APIException ex) when (SpotifyApiErrorHelper.IsInsufficientScope(ex))
                {
                    Log($"Cannot delete playlist '{playlist.Name}': Spotify says the token has insufficient scope. Re-login may be required to grant playlist-modify-private and playlist-modify-public.");
                    Log($"Insufficient scope exception for '{playlist.Name}': {ex}", true);
                    return (DeletionStatus.Failed, null);
                }
                catch (APIException ex) when (SpotifyApiErrorHelper.IsTransientApiException(ex) && transientAttempt < MaxTransientDeleteAttempts)
                {
                    transientAttempt++;
                    var retryDelay = GetTransientRetryDelay(transientAttempt);

                    Log($"Transient Spotify/API connection error while deleting '{playlist.Name}'. Attempt {transientAttempt}/{MaxTransientDeleteAttempts}; retrying after {retryDelay}.");
                    Log($"Transient API exception for '{playlist.Name}': {ex}", true);
                    try
                    {
                        await Task.Delay(retryDelay, cancellationTokenSource.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return (DeletionStatus.Failed, null);
                    }
                }
                catch (Exception ex) when (SpotifyApiErrorHelper.IsTransientApiException(ex) && transientAttempt < MaxTransientDeleteAttempts)
                {
                    transientAttempt++;
                    var retryDelay = GetTransientRetryDelay(transientAttempt);

                    Log($"Transient connection error while deleting '{playlist.Name}'. Attempt {transientAttempt}/{MaxTransientDeleteAttempts}; retrying after {retryDelay}.");
                    Log($"Transient delete exception for '{playlist.Name}': {ex}", true);
                    try
                    {
                        await Task.Delay(retryDelay, cancellationTokenSource.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return (DeletionStatus.Failed, null);
                    }
                }
                catch (OperationCanceledException)
                {
                    return (DeletionStatus.Failed, null);
                }
                catch (Exception ex)
                {
                    Log($"Failed to delete playlist '{playlist.Name}': {ex.Message}");
                    Log($"Delete playlist exception for '{playlist.Name}': {ex}", true);
                    return (DeletionStatus.Failed, null);
                }
            }
        }

        private static TimeSpan GetTransientRetryDelay(int attempt)
        {
            return TimeSpan.FromMilliseconds(500 * attempt);
        }

        private void Log(string message, bool verbose = false)
        {
            LogMessage?.Invoke(message, verbose);
        }
    }
}
