using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SpotifyWPF.Model;

namespace SpotifyWPF.Service
{
    /// <summary>
    /// Deletes (unfollows) staged playlists in parallel batches with request
    /// spacing, transient-error retry, and rate-limit abort.
    /// </summary>
    public interface IPlaylistDeletionService
    {
        /// <summary>Raised for progress and error reporting: (message, isVerbose).</summary>
        event Action<string, bool> LogMessage;

        /// <summary>
        /// Deletes the given playlists. A rate-limit response cancels
        /// <paramref name="cancellationTokenSource"/> so all workers stop.
        /// Returns one result per attempted playlist.
        /// </summary>
        Task<IReadOnlyList<DeletePlaylistResult>> DeletePlaylistsAsync(
            IReadOnlyList<DeletionQueueItem> playlists,
            CancellationTokenSource cancellationTokenSource);
    }
}
