using SpotifyAPI.Web;
using SpotifyAPI.Web.Auth;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SpotifyWPF.Service
{
    public class Spotify : ISpotify
    {
        private readonly ISettingsProvider _settingsProvider;

        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

        private readonly EmbedIOAuthServer _server;

        private PrivateUser _privateProfile;

        private Action _loginSuccessAction;

        private string _verifier;

        public ISpotifyClient Api { get; private set; }

        public Spotify(ISettingsProvider settingsProvider)
        {
            _settingsProvider = settingsProvider;

            _server = new EmbedIOAuthServer(
                new Uri($"http://127.0.0.1:{_settingsProvider.SpotifyRedirectPort}/callback"),
                int.Parse(_settingsProvider.SpotifyRedirectPort));

            _server.AuthorizationCodeReceived += OnAuthorizationCodeReceived;
            _server.ErrorReceived += OnErrorReceived;
        }

        public async Task LoginAsync(Action onSuccess)
        {
            var (verifier, challenge) = PKCEUtil.GenerateCodes();

            _verifier = verifier;

            await _server.Start();

            _loginSuccessAction = onSuccess;

            var request = new LoginRequest(_server.BaseUri, _settingsProvider.SpotifyClientId,
                LoginRequest.ResponseType.Code)
            {
                CodeChallengeMethod = "S256",
                CodeChallenge = challenge,
                Scope = new List<string>
                {
                    Scopes.UserReadPrivate, Scopes.PlaylistModifyPrivate, Scopes.PlaylistModifyPublic,
                    Scopes.PlaylistReadCollaborative, Scopes.PlaylistReadPrivate
                }
            };

            // BrowserUtil.Open(request.ToUri());
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = request.ToUri().ToString(),
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
        }

        private async Task OnErrorReceived(object sender, string error, string state)
        {
            await _server.Stop();
        }

        private async Task OnAuthorizationCodeReceived(object sender, AuthorizationCodeResponse response)
        {
            try
            {
                await _server.Stop();

                var token = await new OAuthClient().RequestToken(
                    new PKCETokenRequest(_settingsProvider.SpotifyClientId, response.Code, _server.BaseUri, _verifier));

                var authenticator = new PKCEAuthenticator(_settingsProvider.SpotifyClientId, token);

                var config = SpotifyClientConfig.CreateDefault().WithAuthenticator(authenticator);

                Api = new SpotifyClient(config);

                _loginSuccessAction?.Invoke();
            }
            catch (Exception ex)
            {
                // 1. Reveal the hidden error on the UI thread
                _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    System.Windows.MessageBox.Show(
                        $"Failed to retrieve token from Spotify:\n{ex.Message}",
                        "Authentication Error",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Error);

                    // 2. Send a failure message to the ViewModel to unlock the login button

                    GalaSoft.MvvmLight.Messaging.Messenger.Default.Send<object>(null, "LoginFailed");
                }));
            }
        }

        public async Task<PrivateUser> GetPrivateProfileAsync()
        {
            if (_privateProfile != null)
            {
                return _privateProfile;
            }

            await _semaphore.WaitAsync();

            try
            {
                if (Api == null)
                {
                    return null;
                }

                if (_privateProfile != null)
                {
                    return _privateProfile;
                }

                _privateProfile = await Api.UserProfile.Current();

                return _privateProfile;
            }

            finally
            {
                _semaphore.Release();
            }
        }
    }
}
