using System.Collections.ObjectModel;

using System.Linq;

using System.Threading.Tasks;

using System.Windows;

using GalaSoft.MvvmLight;

using GalaSoft.MvvmLight.Command;

using SpotifyWPF.Service.Theme;

using SpotifyWPF.View;

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

        private readonly IAppThemeStore _themeStore;



        private ViewModelBase _currentPage;



        public MainViewModel(

            LoginPageViewModel loginPageViewModel,

            PlaylistsPageViewModel playlistsPageViewModel,

            AlbumsPageViewModel albumsPageViewModel,

            ArtistsPageViewModel artistsPageViewModel,

            SearchPageViewModel searchPageViewModel,

            PredictionPageViewModel predictionPageViewModel,

            IAppThemeStore themeStore)

        {

            _loginPageViewModel = loginPageViewModel;

            _playlistsPageViewModel = playlistsPageViewModel;

            _albumsPageViewModel = albumsPageViewModel;

            _artistsPageViewModel = artistsPageViewModel;

            _searchPageViewModel = searchPageViewModel;

            _predictionPageViewModel = predictionPageViewModel;

            _themeStore = themeStore;



            CurrentPage = loginPageViewModel;



            MessengerInstance.Register<object>(this, MessageType.LoginSuccessful, LoginSuccessful);

            MessengerInstance.Register<string>(this, MessageType.OpenInLoopLab, OpenInLoopLab);



            MenuItems = new ObservableCollection<MenuItemViewModel>

            {

                new MenuItemViewModel("File")

                {

                    MenuItems = new ObservableCollection<MenuItemViewModel>

                    {

                        new MenuItemViewModel("Accounts")

                        {

                            MenuItems = new ObservableCollection<MenuItemViewModel>

                            {

                                new MenuItemViewModel("Log In", new RelayCommand<MenuItemViewModel>(HandleAccountMenuItem)),

                                new MenuItemViewModel("Change Account", new RelayCommand<MenuItemViewModel>(HandleAccountMenuItem)),

                                new MenuItemViewModel("Refresh token", new RelayCommand<MenuItemViewModel>(HandleAccountMenuItem)),

                            }

                        },

                        new MenuItemViewModel("Preferences", new RelayCommand(OpenPreferences)),

                        new MenuItemViewModel("Exit", new RelayCommand(Exit))

                    }

                },

                new MenuItemViewModel("View")

                {

                    MenuItems = new ObservableCollection<MenuItemViewModel>

                    {

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

                        new MenuItemViewModel("Infinite Jukebox", new RelayCommand<MenuItemViewModel>(SwitchViewFromMenuItem)) {IsEnabled = false},

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



        /// <summary>Navigates to the Infinite Jukebox page; the page itself handles the payload URI.</summary>

        private void OpenInLoopLab(string contextUri)

        {

            CurrentPage = _predictionPageViewModel;

            CheckMenuItem("Experimental", "Infinite Jukebox");

        }



        private void HandleAccountMenuItem(MenuItemViewModel menuItem)

        {

            switch (menuItem.Header)

            {

                case "Log In":

                case "Change Account":

                    _loginPageViewModel.ResetLoginState();

                    NavigateTo(_loginPageViewModel);

                    UncheckAllMenuItems(MenuItems);

                    break;

                case "Refresh token":

                    if (_loginPageViewModel.RefreshSpotifyTokenCommand.CanExecute(null))

                        _loginPageViewModel.RefreshSpotifyTokenCommand.Execute(null);

                    break;

            }

        }



        private void SwitchViewFromMenuItem(MenuItemViewModel menuItem)

        {

            switch (menuItem.Header)

            {

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

                case "Infinite Jukebox":

                    NavigateTo(_predictionPageViewModel);

                    CheckMenuItem("Experimental", menuItem.Header);

                    break;

                default:

                    return;

            }

        }



        private void CheckMenuItem(string topLevelHeader, string itemHeader)

        {

            UncheckAllMenuItems(MenuItems);



            var selectedItem = FindMenuItem(MenuItems, topLevelHeader, itemHeader);

            if (selectedItem != null)

                selectedItem.IsChecked = true;

        }



        private static void UncheckAllMenuItems(ObservableCollection<MenuItemViewModel> items)

        {

            if (items == null)

                return;



            foreach (var item in items)

            {

                item.IsChecked = false;



                if (item.MenuItems != null)

                    UncheckAllMenuItems(item.MenuItems);

            }

        }



        private static MenuItemViewModel FindMenuItem(

            ObservableCollection<MenuItemViewModel> items,

            string topLevelHeader,

            string itemHeader)

        {

            var topLevel = items?.FirstOrDefault(item => item.Header == topLevelHeader);



            return topLevel?.MenuItems?.FirstOrDefault(item => item.Header == itemHeader);

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

            var enabledOnAuth = new[] { "Playlists", "Albums", "Artists", "Search", "Infinite Jukebox" };



            foreach (var topLevel in MenuItems)

            {

                if (topLevel.MenuItems == null)

                    continue;



                foreach (var menuItem in topLevel.MenuItems)

                {

                    if (enabledOnAuth.Contains(menuItem.Header))

                        menuItem.IsEnabled = isEnabled;

                }

            }

        }



        private void OpenPreferences()
        {
            if (Application.Current?.MainWindow == null)
                return;

            var window = new PreferencesWindow(_themeStore)
            {
                Owner = Application.Current.MainWindow
            };

            window.ShowDialog();
        }



        private static void Exit()

        {

            if (Application.Current == null)
                return;

            Application.Current.Shutdown();

        }

    }

}

