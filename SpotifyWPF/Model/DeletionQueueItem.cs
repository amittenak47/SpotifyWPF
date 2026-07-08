using GalaSoft.MvvmLight;

namespace SpotifyWPF.Model
{
    /// <summary>
    /// A playlist staged for deletion, persisted as JSON under
    /// %LocalAppData%\SpotifyWPF\Playlists\&lt;clientId&gt;\deletion-queue.json.
    /// </summary>
    public class DeletionQueueItem : ObservableObject
    {
        private DeletionStatus _deletionStatus = DeletionStatus.Pending;
        private bool _isMarkedForDeletion;

        public DeletionQueueItem() { }

        public DeletionQueueItem(PlaylistCacheItem playlist)
        {
            Playlist = playlist;
        }

        public PlaylistCacheItem Playlist { get; set; }

        public bool IsMarkedForDeletion
        {
            get => _isMarkedForDeletion;
            set
            {
                if (Set(ref _isMarkedForDeletion, value))
                    RaisePropertyChanged(nameof(MarkStatus));
            }
        }

        public DeletionStatus DeletionStatus
        {
            get => _deletionStatus;
            set
            {
                if (Set(ref _deletionStatus, value))
                    RaisePropertyChanged(nameof(DeletionStatusName));
            }
        }

        public string DeletionStatusName => DeletionStatus.ToString();

        public string MarkStatus => IsMarkedForDeletion ? "Marked" : "Queued";

        public bool ResultsAcknowledged { get; set; }
    }
}
