using GalaSoft.MvvmLight;

namespace SpotifyWPF.ViewModel.Page
{
    public enum ManageSection
    {
        None = 0,
        Playlists = 1,
        Albums = 2,
        Artists = 3,
        Search = 4
    }

    /// <summary>Host for Manage accordion sections (Playlists / Albums / Artists / Search).</summary>
    public class ManagePageViewModel : ViewModelBase
    {
        private ManageSection _activeSection = ManageSection.Playlists;

        /// <summary>Only one Manage section content area is open at a time.</summary>
        public ManageSection ActiveSection
        {
            get => _activeSection;
            set
            {
                if (!Set(ref _activeSection, value))
                    return;

                RaisePropertyChanged(nameof(IsPlaylistsOpen));
                RaisePropertyChanged(nameof(IsAlbumsOpen));
                RaisePropertyChanged(nameof(IsArtistsOpen));
                RaisePropertyChanged(nameof(IsSearchOpen));
            }
        }

        public bool IsPlaylistsOpen => ActiveSection == ManageSection.Playlists;
        public bool IsAlbumsOpen => ActiveSection == ManageSection.Albums;
        public bool IsArtistsOpen => ActiveSection == ManageSection.Artists;
        public bool IsSearchOpen => ActiveSection == ManageSection.Search;

        public void ToggleSection(ManageSection section)
        {
            ActiveSection = ActiveSection == section ? ManageSection.None : section;
        }
    }
}
