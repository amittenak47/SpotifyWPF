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
        private readonly PredictionPageViewModel _predictionPageViewModel;

        private ViewModelBase _currentPage;

        public MainViewModel(
            LoginPageViewModel loginPageViewModel,
            PlaylistsPageViewModel playlistsPageViewModel,
            AlbumsPageViewModel albumsPageViewModel,
            ArtistsPageViewModel artistsPageViewModel,
            SearchPageViewModel searchPageViewModel,
            PredictionPageViewModel predictionPageViewModel)
        {
            _loginPageViewModel = loginPageViewModel;
            _playlistsPageViewModel = playlistsPageViewModel;
            _albumsPageViewModel = albumsPageViewModel;
            _artistsPageViewModel = artistsPageViewModel;
            _searchPageViewModel = searchPageViewModel;
            _predictionPageViewModel = predictionPageViewModel;

            CurrentPage = loginPageViewModel;

            MessengerInstance.Register<object>(this, MessageType.LoginSuccessful, LoginSuccessful);
            MessengerInstance.Register<string>(this, MessageType.OpenInLoopLab, OpenInLoopLab);

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
                },
                new MenuItemViewModel("Experimental")
                {
                    MenuItems = new ObservableCollection<MenuItemViewModel>
                    {
                        new MenuItemViewModel("Prediction", new RelayCommand<MenuItemViewModel>(SwitchViewFromMenuItem)) {IsEnabled = false},
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
            NavigateTo(_playlistsPageViewModel);
            CheckViewMenuItem("Playlists");
        }

        /// <summary>Navigates to the Prediction page; the page itself handles the payload URI.</summary>
        private void OpenInLoopLab(string contextUri)
        {
            CurrentPage = _predictionPageViewModel;
            CheckMenuItem("Experimental", "Prediction");
        }

        private void SwitchViewFromMenuItem(MenuItemViewModel menuItem)
        {
            switch (menuItem.Header)
            {
                case "Accounts / Login":
                    _loginPageViewModel.ResetLoginState();
                    NavigateTo(_loginPageViewModel);
                    CheckMenuItem("View", menuItem.Header);
                    break;
                case "Playlists":
                    NavigateTo(_playlistsPageViewModel);
                    CheckMenuItem("View", menuItem.Header);
                    break;
                case "Albums":
                    NavigateTo(_albumsPageViewModel);
                    CheckMenuItem("View", menuItem.Header);
                    break;
                case "Artists":
                    NavigateTo(_artistsPageViewModel);
                    CheckMenuItem("View", menuItem.Header);
                    break;
                case "Search":
                    NavigateTo(_searchPageViewModel);
                    CheckMenuItem("View", menuItem.Header);
                    break;
                case "Prediction":
                    NavigateTo(_predictionPageViewModel);
                    CheckMenuItem("Experimental", menuItem.Header);
                    break;
                default:
                    return;
            }
        }

        private void CheckMenuItem(string topLevelHeader, string itemHeader)
        {
            foreach (var topLevel in MenuItems)
            {
                if (topLevel.MenuItems == null) continue;

                foreach (var item in topLevel.MenuItems)
                    item.IsChecked = false;
            }

            var menu = MenuItems.FirstOrDefault(item => item.Header == topLevelHeader);
            var selectedItem = menu?.MenuItems?.FirstOrDefault(item => item.Header == itemHeader);
            if (selectedItem != null)
                selectedItem.IsChecked = true;
        }

        // Page view models are singletons while views are recreated on every
        // navigation, so lifecycle hooks must be idempotent (safe on every
        // revisit). See docs/architecture.md.
        private async void NavigateTo(ViewModelBase page)
        {
            var previousPage = CurrentPage;
            CurrentPage = page;

            try
            {
                if (!ReferenceEquals(previousPage, page) && previousPage is IPageLifecycle leavingPage)
                    await leavingPage.OnNavigatedFromAsync();

                if (page is IPageLifecycle enteringPage)
                    await enteringPage.OnNavigatedToAsync();
            }
            catch (System.Exception ex)
            {
                System.Console.WriteLine($"Page lifecycle hook failed: {ex}");
            }
        }

        private void CheckViewMenuItem(string header)
        {
            CheckMenuItem("View", header);
        }

        private void SetAuthenticatedMenuItemsEnabled(bool isEnabled)
        {
            var enabledOnAuth = new[] { "Playlists", "Albums", "Artists", "Search", "Prediction" };

            foreach (var topLevel in MenuItems)
            {
                if (topLevel.MenuItems == null) continue;

                foreach (var menuItem in topLevel.MenuItems)
                {
                    if (enabledOnAuth.Contains(menuItem.Header))
                        menuItem.IsEnabled = isEnabled;
                }
            }
        }

        private static void Exit()
        {
            Application.Current.MainWindow?.Close();
        }
    }
}