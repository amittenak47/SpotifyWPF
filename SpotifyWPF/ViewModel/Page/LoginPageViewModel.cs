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
    /// <summary>Login transition state machine, animated by LoginTransitionController in the view.</summary>
    public enum LoginPhase
    {
        /// <summary>Login form visible and interactive.</summary>
        Form,

        /// <summary>Form faded out, loading overlay faded in, waiting for authorization.</summary>
        Loading,

        /// <summary>Authorized: loading animation keeps running while the session bridge preps.</summary>
        SuccessFade,

        /// <summary>Bridge done: loading overlay fades out slowly, then the app page fades in.</summary>
        Done
    }

    public class LoginPageViewModel : ViewModelBase
    {
        /// <summary>Minimum time the "Signed in" bridge stays up so the transition never feels abrupt.</summary>
        private const int MinBridgeDurationMs = 750;

        /// <summary>Fallback if the view never reports its fade-out finished (e.g. no view attached).</summary>
        private const int MaxLoadingFadeWaitMs = 1500;

        private readonly IMessageBoxService _messageBoxService;
        private readonly ISpotify _spotify;

        private TaskCompletionSource<bool> _loadingFadeTcs;

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

        private LoginPhase _phase = LoginPhase.Form;

        /// <summary>Drives the fade choreography; the view animates on every change.</summary>
        public LoginPhase Phase
        {
            get => _phase;
            private set
            {
                if (Set(ref _phase, value))
                {
                    RaisePropertyChanged(nameof(IsLoginFormEnabled));
                    SpotifyLoginCommand?.RaiseCanExecuteChanged();
                    RefreshSpotifyTokenCommand?.RaiseCanExecuteChanged();
                }
            }
        }

        public bool IsLoginFormEnabled => Phase == LoginPhase.Form;

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
                // Reverse fade: the view hides the loading overlay and restores the form.
                LoginStatusText = "Authorization failed or was cancelled.";
                Phase = LoginPhase.Form;
            });
        }

        public void ResetLoginState()
        {
            _spotify.ResetAuthenticationState();
            Phase = LoginPhase.Form;
            LoginStatusText = "Waiting for Spotify authorization…";
        }

        /// <summary>Called by the view when the loading overlay's final fade-out has completed.</summary>
        public void NotifyLoadingFadeCompleted()
        {
            _loadingFadeTcs?.TrySetResult(true);
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

                LoginStatusText = "Waiting for Spotify authorization…";
                Phase = LoginPhase.Loading;
                SaveClientId(UserClientId.Trim());
                Properties.Settings.Default.Save();

                await _spotify.LoginAsync(OnSuccess);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"A hidden crash occurred:\n{ex.Message}", "Crash Detected");
                Phase = LoginPhase.Form;
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

                LoginStatusText = "Refreshing Spotify authorization…";
                Phase = LoginPhase.Loading;
                SaveClientId(UserClientId.Trim());
                Properties.Settings.Default.Save();

                await _spotify.ReauthorizeAsync(OnSuccess);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"A hidden crash occurred:\n{ex.Message}", "Crash Detected");
                Phase = LoginPhase.Form;
            }
        }

        private bool CanExecuteLogin()
        {
            return Phase == LoginPhase.Form && !string.IsNullOrWhiteSpace(UserClientId);
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
                // The loading animation keeps running; only the status text cross-fades.
                LoginStatusText = "Signed in — loading your library…";
                Phase = LoginPhase.SuccessFade;

                // Minimum bridge duration instead of a blind fixed delay: long enough to read,
                // short enough not to stall the reveal.
                await Task.Delay(MinBridgeDurationMs);

                // Ask the view to fade the loading overlay out, and wait for it to finish
                // (bounded by a timeout so a missing view can never wedge the login).
                _loadingFadeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Phase = LoginPhase.Done;
                await Task.WhenAny(_loadingFadeTcs.Task, Task.Delay(MaxLoadingFadeWaitMs));
                _loadingFadeTcs = null;

                // Pass new object() to avoid anonymous type matching bugs in MVVM Light
                MessengerInstance.Send<object>(new object(), MessageType.LoginSuccessful);

                // Reset quietly for the next visit (navigation has already left this page).
                Phase = LoginPhase.Form;
                LoginStatusText = "Waiting for Spotify authorization…";
            }));
        }
    }
}
