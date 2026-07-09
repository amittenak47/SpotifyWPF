using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Command;
using SpotifyWPF.Service;
using SpotifyWPF.Service.MessageBoxes;
// ReSharper disable AsyncVoidLambda

namespace SpotifyWPF.ViewModel.Page
{
    public class LoginPageViewModel : ViewModelBase
    {
        private readonly IMessageBoxService _messageBoxService;
        private readonly ISpotify _spotify;

        public RelayCommand SpotifyLoginCommand { get; private set; }

        public RelayCommand RefreshSpotifyTokenCommand { get; private set; }

        private string _userClientId;

        public ObservableCollection<string> SavedClientIds { get; } = new ObservableCollection<string>();

        public string UserClientId
        {
            get => _userClientId;
            set
            {
                if (Set(ref _userClientId, value))
                {
                    SpotifyLoginCommand?.RaiseCanExecuteChanged();
                    RefreshSpotifyTokenCommand?.RaiseCanExecuteChanged();
                }
            }
        }

        private bool _isLoggingIn;

        public bool IsLoggingIn
        {
            get => _isLoggingIn;
            set
            {
                if (Set(ref _isLoggingIn, value))
                {
                    RaisePropertyChanged(nameof(IsLoginFormEnabled));
                    SpotifyLoginCommand?.RaiseCanExecuteChanged();
                    RefreshSpotifyTokenCommand?.RaiseCanExecuteChanged();
                }
            }
        }

        private bool _showLoginBridge;

        public bool ShowLoginBridge
        {
            get => _showLoginBridge;
            set
            {
                if (Set(ref _showLoginBridge, value))
                    RaisePropertyChanged(nameof(IsLoginFormEnabled));
            }
        }

        public bool IsLoginFormEnabled => !IsLoggingIn && !ShowLoginBridge;

        private string _loginStatusText = "Waiting for Spotify authorization…";

        public string LoginStatusText
        {
            get => _loginStatusText;
            set => Set(ref _loginStatusText, value);
        }

        public LoginPageViewModel(ISpotify spotify, IMessageBoxService messageBoxService)
        {
            _spotify = spotify;
            _messageBoxService = messageBoxService;

            LoadSavedClientIds();
            UserClientId = Properties.Settings.Default.SpotifyClientId;

            SpotifyLoginCommand = new RelayCommand(ExecuteLogin, CanExecuteLogin);
            RefreshSpotifyTokenCommand = new RelayCommand(ExecuteRefreshToken, CanExecuteLogin);

            MessengerInstance.Register<object>(this, "LoginFailed", _ =>
            {
                IsLoggingIn = false;
                ShowLoginBridge = false;
                LoginStatusText = "Authorization failed or was cancelled.";
            });
        }

        public void ResetLoginState()
        {
            _spotify.ResetAuthenticationState();
            IsLoggingIn = false;
            ShowLoginBridge = false;
            LoginStatusText = "Waiting for Spotify authorization…";
        }

        private async void ExecuteLogin()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(UserClientId))
                {
                    _messageBoxService.ShowMessageBox(
                        "Please enter your Spotify Client ID.",
                        "Client ID Required",
                        Service.MessageBoxes.MessageBoxButton.OK,
                        MessageBoxIcon.Warning
                    );
                    return;
                }

                IsLoggingIn = true;
                ShowLoginBridge = false;
                LoginStatusText = "Waiting for Spotify authorization…";
                SaveClientId(UserClientId.Trim());
                Properties.Settings.Default.Save();

                await _spotify.LoginAsync(OnSuccess);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"A hidden crash occurred:\n{ex.Message}", "Crash Detected");
                IsLoggingIn = false;
                ShowLoginBridge = false;
            }
        }

        private async void ExecuteRefreshToken()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(UserClientId))
                {
                    _messageBoxService.ShowMessageBox(
                        "Please enter your Spotify Client ID.",
                        "Client ID Required",
                        Service.MessageBoxes.MessageBoxButton.OK,
                        MessageBoxIcon.Warning
                    );
                    return;
                }

                IsLoggingIn = true;
                ShowLoginBridge = false;
                LoginStatusText = "Refreshing Spotify authorization…";
                SaveClientId(UserClientId.Trim());
                Properties.Settings.Default.Save();

                await _spotify.ReauthorizeAsync(OnSuccess);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"A hidden crash occurred:\n{ex.Message}", "Crash Detected");
                IsLoggingIn = false;
                ShowLoginBridge = false;
            }
        }

        private bool CanExecuteLogin()
        {
            return !IsLoggingIn && !ShowLoginBridge && !string.IsNullOrWhiteSpace(UserClientId);
        }

        private void LoadSavedClientIds()
        {
            try
            {
                var savedClientIds = JsonSerializer.Deserialize<string[]>(Properties.Settings.Default.SpotifyClientIdsJson) ?? Array.Empty<string>();

                foreach (var clientId in savedClientIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct())
                    SavedClientIds.Add(clientId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load saved Spotify Client IDs: {ex}");
            }

            if (!string.IsNullOrWhiteSpace(Properties.Settings.Default.SpotifyClientId) &&
                !SavedClientIds.Contains(Properties.Settings.Default.SpotifyClientId))
            {
                SavedClientIds.Add(Properties.Settings.Default.SpotifyClientId);
            }
        }

        private void SaveClientId(string clientId)
        {
            Properties.Settings.Default.SpotifyClientId = clientId;

            if (!SavedClientIds.Contains(clientId))
                SavedClientIds.Add(clientId);

            Properties.Settings.Default.SpotifyClientIdsJson = JsonSerializer.Serialize(SavedClientIds.ToArray());
        }

        private void OnSuccess()
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(async () =>
            {
                IsLoggingIn = false;
                LoginStatusText = "Signed in. Opening playlists…";
                ShowLoginBridge = true;

                await Task.Delay(900);

                ShowLoginBridge = false;
                // Pass new object() to avoid anonymous type matching bugs in MVVM Light
                MessengerInstance.Send<object>(new object(), MessageType.LoginSuccessful);
            }));
        }
    }
}
