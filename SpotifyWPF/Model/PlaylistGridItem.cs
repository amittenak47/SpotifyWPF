using System.ComponentModel;
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

            if (DeletionItem != null)
                DeletionItem.PropertyChanged += OnDeletionItemPropertyChanged;
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

        public string QueueStatus => IsStaged ? "Queued" : "";

        public string DeletionStatusName => DeletionItem?.DeletionStatusName ?? "";

        private void OnDeletionItemPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DeletionQueueItem.DeletionStatus) ||
                e.PropertyName == nameof(DeletionQueueItem.DeletionStatusName) ||
                e.PropertyName == nameof(DeletionQueueItem.IsMarkedForDeletion) ||
                e.PropertyName == nameof(DeletionQueueItem.MarkStatus) ||
                string.IsNullOrEmpty(e.PropertyName))
            {
                RaisePropertyChanged(nameof(DeletionStatusName));
                RaisePropertyChanged(nameof(QueueStatus));
            }
        }
    }
}
