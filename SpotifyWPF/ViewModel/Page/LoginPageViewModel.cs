using System;
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

        private string _userClientId;

        public string UserClientId
        {
            get => _userClientId;
            set => Set(ref _userClientId, value); 
        }

        private bool _isLoggingIn;
        public bool IsLoggingIn
        {
            get => _isLoggingIn;
            set
            {
                if (Set(ref _isLoggingIn, value))
                {
                    SpotifyLoginCommand?.RaiseCanExecuteChanged();
                }
            }
        }

        public LoginPageViewModel(ISpotify spotify, IMessageBoxService messageBoxService)
        {
            _spotify = spotify;
            _messageBoxService = messageBoxService;

            UserClientId = Properties.Settings.Default.SpotifyClientId;
            Console.WriteLine("STARTUP PROPERTY VALUE: " + Properties.Settings.Default.SpotifyClientId);

            SpotifyLoginCommand = new RelayCommand(ExecuteLogin);

            // Listen for login failures so we can unlock the button
            MessengerInstance.Register<object>(this, "LoginFailed", _ => 
            {
                IsLoggingIn = false;
            });
        }

        private async void ExecuteLogin()
        {
            try
            {
                // 1. Validate using your service instead of CanExecute
                if (string.IsNullOrWhiteSpace(UserClientId))
                {
                    _messageBoxService.ShowMessageBox(
                        "Please enter your Spotify Client ID.", 
                        "Client ID Required", 
                        Service.MessageBoxes.MessageBoxButton.OK, 
                        MessageBoxIcon.Warning
                    );
                    return; // Stop execution here
                }

                // 2. Proceed with login if valid
                IsLoggingIn = true;
                Properties.Settings.Default.SpotifyClientId = UserClientId.Trim();
                Properties.Settings.Default.Save();

                await _spotify.LoginAsync(OnSuccess);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"A hidden crash occurred:\n{ex.Message}", "Crash Detected");
                IsLoggingIn = false;
            }
        }

        private bool CanExecuteLogin()
        {
            return !IsLoggingIn;
        }

        private void OnSuccess()
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                // Pass new object() to avoid anonymous type matching bugs in MVVM Light
                MessengerInstance.Send<object>(new object(), MessageType.LoginSuccessful);
            }));
        }
    }
}
