using GalaSoft.MvvmLight;

namespace SpotifyWPF.ViewModel.Page
{
    /// <summary>Host for Manage tabs (Playlists / Albums / Artists / Search).</summary>
    public class ManagePageViewModel : ViewModelBase
    {
        private int _selectedTabIndex;

        public int SelectedTabIndex
        {
            get => _selectedTabIndex;
            set => Set(ref _selectedTabIndex, value);
        }
    }
}
