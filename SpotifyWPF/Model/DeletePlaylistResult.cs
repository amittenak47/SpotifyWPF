namespace SpotifyWPF.Model
{
    /// <summary>
    /// Outcome of a single playlist delete attempt performed by the deletion service.
    /// </summary>
    public class DeletePlaylistResult
    {
        public DeletePlaylistResult(DeletionQueueItem playlist, DeletionStatus status)
        {
            Playlist = playlist;
            Status = status;
        }

        public DeletionQueueItem Playlist { get; }

        public DeletionStatus Status { get; }
    }
}
