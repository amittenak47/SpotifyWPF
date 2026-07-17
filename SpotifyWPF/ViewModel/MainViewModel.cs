using System.Collections.ObjectModel;
using System.Linq;
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
        private readonly ManagePageViewModel _managePageViewModel;
        private readonly ExperimentalPageViewModel _experimentalPageViewModel;
        private readonly IAppThemeStore _themeStore;

        private ViewModelBase _currentPage;

        public MainViewModel(
            LoginPageViewModel loginPageViewModel,
            PlaylistsPageViewModel playlistsPageViewModel,
            AlbumsPageViewModel albumsPageViewModel,
            ArtistsPageViewModel artistsPageViewModel,
            SearchPageViewModel searchPageViewModel,
            PredictionPageViewModel predictionPageViewModel,
            ManagePageViewModel managePageViewModel,
            ExperimentalPageViewModel experimentalPageViewModel,
            IAppThemeStore themeStore)
        {
            _loginPageViewModel = loginPageViewModel;
            _playlistsPageViewModel = playlistsPageViewModel;
            _albumsPageViewModel = albumsPageViewModel;
            _artistsPageViewModel = artistsPageViewModel;
            _searchPageViewModel = searchPageViewModel;
            _predictionPageViewModel = predictionPageViewModel;
            _managePageViewModel = managePageViewModel;
            _experimentalPageViewModel = experimentalPageViewModel;
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
                        new MenuItemViewModel("Preferences", new RelayCommand(OpenPreferences)),
                        new MenuItemViewModel("Exit", new RelayCommand(Exit))
                    }
                },
                new MenuItemViewModel("Accounts")
                {
                    MenuItems = new ObservableCollection<MenuItemViewModel>
                    {
                        new MenuItemViewModel("Log In", new RelayCommand<MenuItemViewModel>(HandleAccountMenuItem)),
                        new MenuItemViewModel("Change Account", new RelayCommand<MenuItemViewModel>(HandleAccountMenuItem)),
                        new MenuItemViewModel("Refresh token", new RelayCommand<MenuItemViewModel>(HandleAccountMenuItem)),
                    }
                },
                new MenuItemViewModel("Manage", new RelayCommand<MenuItemViewModel>(SwitchViewFromMenuItem)) { IsEnabled = false },
                new MenuItemViewModel("Infinite Jukebox", new RelayCommand<MenuItemViewModel>(SwitchViewFromMenuItem)) { IsEnabled = false },
                new MenuItemViewModel("Experimental", new RelayCommand<MenuItemViewModel>(SwitchViewFromMenuItem)) { IsEnabled = false },
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
            NavigateTo(_predictionPageViewModel);
            CheckTopLevelMenuItem("Infinite Jukebox");
        }

        /// <summary>Navigates to the Infinite Jukebox page; the page itself handles the payload URI.</summary>
        private void OpenInLoopLab(string contextUri)
        {
            CurrentPage = _predictionPageViewModel;
            CheckTopLevelMenuItem("Infinite Jukebox");
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
                case "Manage":
                    NavigateTo(_managePageViewModel);
                    CheckTopLevelMenuItem("Manage");
                    break;

                case "Infinite Jukebox":
                    NavigateTo(_predictionPageViewModel);
                    CheckTopLevelMenuItem("Infinite Jukebox");
                    break;

                case "Experimental":
                    NavigateTo(_experimentalPageViewModel);
                    CheckTopLevelMenuItem("Experimental");
                    break;

                default:
                    return;
            }
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

        private void CheckTopLevelMenuItem(string header)
        {
            UncheckAllMenuItems(MenuItems);
            var item = MenuItems?.FirstOrDefault(m => m.Header == header);
            if (item != null)
                item.IsChecked = true;
        }

        private void SetAuthenticatedMenuItemsEnabled(bool isEnabled)
        {
            var enabledOnAuth = new[] { "Manage", "Infinite Jukebox", "Experimental" };

            foreach (var topLevel in MenuItems)
            {
                if (enabledOnAuth.Contains(topLevel.Header))
                    topLevel.IsEnabled = isEnabled;

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
