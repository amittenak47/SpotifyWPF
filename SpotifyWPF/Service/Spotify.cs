using SpotifyAPI.Web;
using SpotifyAPI.Web.Auth;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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

        private bool _isAuthServerRunning;

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
            await LoginAsync(onSuccess, false);
        }

        public async Task ReauthorizeAsync(Action onSuccess)
        {
            ClearCachedToken();
            await LoginAsync(onSuccess, true);
        }

        public void ResetAuthenticationState()
        {
            _loginSuccessAction = null;
            _verifier = null;
            StopAuthServerAsync().GetAwaiter().GetResult();
        }

        private async Task LoginAsync(Action onSuccess, bool forceReauthorization)
        {
            _loginSuccessAction = onSuccess;

            if (!forceReauthorization && await TryLoginWithCachedTokenAsync())
            {
                _loginSuccessAction?.Invoke();
                return;
            }

            await StartInteractiveLoginAsync();
        }

        private async Task StartInteractiveLoginAsync()
        {
            await StopAuthServerAsync();

            var (verifier, challenge) = PKCEUtil.GenerateCodes();

            _verifier = verifier;

            await _server.Start();
            _isAuthServerRunning = true;

            var request = new LoginRequest(_server.BaseUri, _settingsProvider.SpotifyClientId,
                LoginRequest.ResponseType.Code)
            {
                CodeChallengeMethod = "S256",
                CodeChallenge = challenge,
                Scope = new List<string>
                {
                    Scopes.UserReadPrivate, Scopes.PlaylistModifyPrivate, Scopes.PlaylistModifyPublic,
                    Scopes.PlaylistReadCollaborative, Scopes.PlaylistReadPrivate,
                    Scopes.UserLibraryRead, Scopes.UserLibraryModify,
                    Scopes.UserFollowRead, Scopes.UserFollowModify
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
            await StopAuthServerAsync();
            NotifyLoginFailed($"Spotify authorization failed: {error}");
        }

        private async Task OnAuthorizationCodeReceived(object sender, AuthorizationCodeResponse response)
        {
            try
            {
                await StopAuthServerAsync();

                var token = await new OAuthClient().RequestToken(
                    new PKCETokenRequest(_settingsProvider.SpotifyClientId, response.Code, _server.BaseUri, _verifier));

                await SaveTokenAsync(token);
                BuildClient(token);

                _loginSuccessAction?.Invoke();
                _loginSuccessAction = null;
            }
            catch (Exception ex)
            {
                _loginSuccessAction = null;
                NotifyLoginFailed($"Failed to retrieve token from Spotify:\n{ex.Message}");
            }
        }

        private async Task StopAuthServerAsync()
        {
            if (!_isAuthServerRunning)
                return;

            try
            {
                await _server.Stop();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to stop Spotify auth server: {ex}");
            }
            finally
            {
                _isAuthServerRunning = false;
            }
        }

        private static void NotifyLoginFailed(string message)
        {
            _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!string.IsNullOrWhiteSpace(message))
                {
                    System.Windows.MessageBox.Show(
                        message,
                        "Authentication Error",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Error);
                }

                GalaSoft.MvvmLight.Messaging.Messenger.Default.Send<object>(null, "LoginFailed");
            }));
        }

        private async Task<bool> TryLoginWithCachedTokenAsync()
        {
            var token = await LoadTokenAsync();

            if (token == null)
                return false;

            try
            {
                BuildClient(token);
                _privateProfile = null;

                // Force a lightweight authenticated request so expired tokens refresh before the UI switches pages.
                _privateProfile = await Api.UserProfile.Current();
                await SaveTokenAsync(token);

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Cached Spotify token could not be used: {ex}");
                ClearCachedToken();
                Api = null;
                _privateProfile = null;

                return false;
            }
        }

        private void BuildClient(PKCETokenResponse token)
        {
            var authenticator = new PKCEAuthenticator(_settingsProvider.SpotifyClientId, token);
            authenticator.TokenRefreshed += (_, refreshedToken) => SaveTokenAsync(refreshedToken).GetAwaiter().GetResult();

            var config = SpotifyClientConfig.CreateDefault().WithAuthenticator(authenticator);

            Api = new SpotifyClient(config);
        }

        private async Task<PKCETokenResponse> LoadTokenAsync()
        {
            var path = GetTokenCachePath();

            if (!File.Exists(path))
                return null;

            using (var stream = File.OpenRead(path))
            {
                return await JsonSerializer.DeserializeAsync<PKCETokenResponse>(stream);
            }
        }

        private async Task SaveTokenAsync(PKCETokenResponse token)
        {
            var path = GetTokenCachePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            using (var stream = File.Create(path))
            {
                await JsonSerializer.SerializeAsync(stream, token, new JsonSerializerOptions { WriteIndented = true });
            }
        }

        private void ClearCachedToken()
        {
            var path = GetTokenCachePath();

            if (File.Exists(path))
                File.Delete(path);

            Api = null;
            _privateProfile = null;
        }

        private string GetTokenCachePath()
        {
            var safeClientId = new string((_settingsProvider.SpotifyClientId ?? string.Empty).Where(char.IsLetterOrDigit).ToArray());
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            return Path.Combine(localAppData, "SpotifyWPF", "Auth", $"pkce-token-{safeClientId}.json");
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
