using GalaSoft.MvvmLight;

namespace SpotifyWPF.Model
{
    /// <summary>
    /// Unified row for the combined playlists grid. Backing stores remain separate
    /// (available-playlists vs deletion-queue); this only projects them for the UI.
    /// </summary>
    public class PlaylistGridItem : ObservableObject
    {
        public PlaylistGridItem(PlaylistCacheItem playlist)
        {
            Playlist = playlist;
        }

        public PlaylistGridItem(DeletionQueueItem deletionItem)
        {
            DeletionItem = deletionItem;
            Playlist = deletionItem?.Playlist;
        }

        public PlaylistCacheItem Playlist { get; }

        public DeletionQueueItem DeletionItem { get; }

        public bool IsLoaded => DeletionItem == null;

        public bool IsStaged => DeletionItem != null;

        public string Name => Playlist?.Name;

        public string OwnerDisplayName => Playlist?.OwnerDisplayName;

        public int? TracksTotal => Playlist?.TracksTotal;

        public string Id => Playlist?.Id;

        public string LoadedText => IsLoaded ? "Loaded" : "";

        public string QueueStatus => DeletionItem?.MarkStatus ?? "";

        public string DeletionStatusName => DeletionItem?.DeletionStatusName ?? "";
    }
}
