using GalaSoft.MvvmLight;

namespace SpotifyWPF.Model
{
    /// <summary>
    /// A single line item beneath a queued playlist action in the Actions tree
    /// (for example one playlist within a "Delete selection" action).
    /// </summary>
    public class QueuedActionDetailItem : ObservableObject
    {
        public QueuedActionDetailItem(string displayName, string playlistId = null, bool canRemove = true)
        {
            DisplayName = displayName;
            PlaylistId = playlistId;
            CanRemove = canRemove;
        }

        public string DisplayName { get; }

        public string PlaylistId { get; }

        public bool CanRemove { get; }
    }
}
