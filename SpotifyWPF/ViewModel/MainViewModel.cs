using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Command;
using SpotifyWPF.ViewModel.Component;
using SpotifyWPF.ViewModel.Page;

namespace SpotifyWPF.ViewModel
{
    public class MainViewModel : ViewModelBase
    {
        private readonly LoginPageViewModel _loginPageViewModel;
        private readonly SearchPageViewModel _searchPageViewModel;
        private readonly PlaylistsPageViewModel _playlistsPageViewModel;
        private readonly AlbumsPageViewModel _albumsPageViewModel;
        private readonly ArtistsPageViewModel _artistsPageViewModel;

        private ViewModelBase _currentPage;

        public MainViewModel(
            LoginPageViewModel loginPageViewModel,
            PlaylistsPageViewModel playlistsPageViewModel,
            AlbumsPageViewModel albumsPageViewModel,
            ArtistsPageViewModel artistsPageViewModel,
            SearchPageViewModel searchPageViewModel)
        {
            _loginPageViewModel = loginPageViewModel;
            _playlistsPageViewModel = playlistsPageViewModel;
            _albumsPageViewModel = albumsPageViewModel;
            _artistsPageViewModel = artistsPageViewModel;
            _searchPageViewModel = searchPageViewModel;

            CurrentPage = loginPageViewModel;

            MessengerInstance.Register<object>(this, MessageType.LoginSuccessful, LoginSuccessful);

            MenuItems = new ObservableCollection<MenuItemViewModel>
            {
                new MenuItemViewModel("File")
                {
                    MenuItems = new ObservableCollection<MenuItemViewModel>
                    {
                        new MenuItemViewModel("Exit", new RelayCommand(Exit))
                    }
                },
                new MenuItemViewModel("View")
                {
                    MenuItems = new ObservableCollection<MenuItemViewModel>
                    {
                        new MenuItemViewModel("Accounts / Login", new RelayCommand<MenuItemViewModel>(SwitchViewFromMenuItem)) {IsChecked = true},
                        new MenuItemViewModel("Playlists", new RelayCommand<MenuItemViewModel>(SwitchViewFromMenuItem)) {IsEnabled = false},
                        new MenuItemViewModel("Albums", new RelayCommand<MenuItemViewModel>(SwitchViewFromMenuItem)) {IsEnabled = false},
                        new MenuItemViewModel("Artists", new RelayCommand<MenuItemViewModel>(SwitchViewFromMenuItem)) {IsEnabled = false},
                        new MenuItemViewModel("Search", new RelayCommand<MenuItemViewModel>(SwitchViewFromMenuItem)) {IsEnabled = false},
                    }
                }
            };
        }

        public ObservableCollection<MenuItemViewModel> MenuItems { get; set; }

        public ViewModelBase CurrentPage
        {
            get => _currentPage;

            set
            {
                _currentPage = value;
                RaisePropertyChanged();
            }
        }

        private void LoginSuccessful(object o)
        {
            SetAuthenticatedMenuItemsEnabled(true);
            CurrentPage = _playlistsPageViewModel;
            CheckViewMenuItem("Playlists");
        }

        private void SwitchViewFromMenuItem(MenuItemViewModel menuItem)
        {
            switch (menuItem.Header)
            {
                case "Accounts / Login":
                    CurrentPage = _loginPageViewModel;
                    break;
                case "Playlists":
                    CurrentPage = _playlistsPageViewModel;
                    break;
                case "Albums":
                    CurrentPage = _albumsPageViewModel;
                    break;
                case "Artists":
                    CurrentPage = _artistsPageViewModel;
                    break;
                case "Search":
                    CurrentPage = _searchPageViewModel;
                    break;
                default:
                    return;
            }

            CheckViewMenuItem(menuItem.Header);
        }

        private void CheckViewMenuItem(string header)
        {
            var viewMenuItems = MenuItems.First(item => item.Header == "View").MenuItems;

            viewMenuItems.ToList().ForEach(item => item.IsChecked = false);
            viewMenuItems.First(item => item.Header == header).IsChecked = true;
        }

        private void SetAuthenticatedMenuItemsEnabled(bool isEnabled)
        {
            var viewMenuItems = MenuItems.First(item => item.Header == "View").MenuItems;

            viewMenuItems.First(item => item.Header == "Playlists").IsEnabled = isEnabled;
            viewMenuItems.First(item => item.Header == "Albums").IsEnabled = isEnabled;
            viewMenuItems.First(item => item.Header == "Artists").IsEnabled = isEnabled;
            viewMenuItems.First(item => item.Header == "Search").IsEnabled = isEnabled;
        }

        private static void Exit()
        {
            Application.Current.MainWindow?.Close();
        }
    }
}