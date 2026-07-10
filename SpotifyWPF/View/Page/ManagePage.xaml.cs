using CommonServiceLocator;
using SpotifyWPF.ViewModel.Page;

namespace SpotifyWPF.View.Page
{
    public partial class ManagePage
    {
        public ManagePage()
        {
            InitializeComponent();
            Loaded += async (_, __) =>
            {
                var playlists = ServiceLocator.Current.GetInstance<PlaylistsPageViewModel>();
                if (playlists != null)
                    await playlists.OnNavigatedToAsync();
            };
        }
    }
}
