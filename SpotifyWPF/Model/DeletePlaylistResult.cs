using System;

namespace SpotifyWPF.Model
{
    /// <summary>
    /// Outcome of a single playlist delete attempt performed by the deletion service.
    /// </summary>
    public class DeletePlaylistResult
    {
        public DeletePlaylistResult(DeletionQueueItem playlist, DeletionStatus status, TimeSpan? retryAfter = null)
        {
            Playlist = playlist;
            Status = status;
            RetryAfter = retryAfter;
        }

        public DeletionQueueItem Playlist { get; }

        public DeletionStatus Status { get; }

        /// <summary>
        /// Present when <see cref="Status"/> is <see cref="DeletionStatus.RateLimited"/>.
        /// </summary>
        public TimeSpan? RetryAfter { get; }
    }
}
