namespace SpotifyWPF.Model
{
    /// <summary>
    /// Persisted Spotify paging position, so playlist fetching resumes where it
    /// left off across app restarts (playlist-pagination.json).
    /// </summary>
    public class PlaylistPaginationState
    {
        public int SpotifyFetchOffset { get; set; }

        public int? LastKnownTotal { get; set; }
    }
}
