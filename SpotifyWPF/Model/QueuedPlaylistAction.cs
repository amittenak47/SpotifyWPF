using System.Collections.Generic;
using System.Collections.ObjectModel;
using GalaSoft.MvvmLight;

namespace SpotifyWPF.Model
{
    /// <summary>
    /// A deferred playlist action (load a page, load all pages, or delete a
    /// selection) waiting in the action queue.
    /// </summary>
    public class QueuedPlaylistAction : ObservableObject
    {
        private bool _isExpanded;

        public PlaylistActionType ActionType { get; set; }

        public List<string> PlaylistIds { get; } = new List<string>();

        public ObservableCollection<QueuedActionDetailItem> DetailItems { get; } = new ObservableCollection<QueuedActionDetailItem>();

        public string DisplayName { get; private set; }

        public bool IsExpanded
        {
            get => _isExpanded;
            set => Set(ref _isExpanded, value);
        }

        public void RefreshDisplayName()
        {
            switch (ActionType)
            {
                case PlaylistActionType.LoadLimit:
                    DisplayName = "Load limit";
                    break;
                case PlaylistActionType.LoadAll:
                    DisplayName = "Load all";
                    break;
                case PlaylistActionType.DeleteSelection:
                    DisplayName = $"Delete selection ({DetailItems.Count})";
                    break;
            }

            RaisePropertyChanged(nameof(DisplayName));
        }
    }
}
