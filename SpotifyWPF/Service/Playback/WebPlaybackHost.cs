using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SpotifyWPF.Service.Prediction;

namespace SpotifyWPF.Service.Playback
{
    public class PlayerStateSnapshot
    {
        public string TrackId { get; set; }

        public string TrackUri { get; set; }

        public string TrackName { get; set; }

        public string ArtistNames { get; set; }

        public long DurationMs { get; set; }

        public long PositionMs { get; set; }

        public bool Paused { get; set; }
    }

    public class PositionSnapshot
    {
        public string TrackId { get; set; }

        public long PositionMs { get; set; }

        public bool Paused { get; set; }
    }

    public class ArmedActionFiredEventArgs : EventArgs
    {
        public string ActionId { get; set; }

        public long FiredAtMs { get; set; }

        public long SeekToMs { get; set; }
    }

    public class PlayerErrorEventArgs : EventArgs
    {
        public string Kind { get; set; }

        public string Message { get; set; }
    }

    public interface IWebPlaybackHost
    {
        bool IsReady { get; }

        string DeviceId { get; }

        /// <summary>Whether CoreWebView2 initialization failed (e.g. WebView2 Runtime missing).</summary>
        string InitializationError { get; }

        /// <summary>
        /// Returns the singleton WebView2 control, creating it on first use. The control survives page
        /// navigation (the Prediction view re-parents it on Load/Unload) so playback is uninterrupted.
        /// </summary>
        WebView2 GetOrCreateView();

        Task EnsureInitializedAsync();

        /// <summary>Arms the JS-side action: when position >= whenMs, seek to seekToMs. One action at a time.</summary>
        void ArmAction(string actionId, long whenMs, long seekToMs);

        void DisarmAction();

        void Pause();

        void Resume();

        void Seek(long positionMs);

        void SetVolume(double volume);

        event EventHandler PlayerReady;

        event EventHandler<PlayerStateSnapshot> StateChanged;

        event EventHandler<PositionSnapshot> PositionUpdated;

        event EventHandler<string> TrackEnded;

        event EventHandler<ArmedActionFiredEventArgs> ActionFired;

        event EventHandler<PlayerErrorEventArgs> PlayerError;

        event EventHandler InitializationFailed;
    }

    /// <summary>
    /// Hosts the Spotify Web Playback SDK in a WebView2 control (Assets/player.html) and bridges it to
    /// the app over JSON web messages. C# is the brain, JS is the metronome: JS tracks position locally
    /// every ~50 ms and enforces a single "armed action" (seek when a position is reached) with no
    /// interop latency; C# decides what to arm next.
    /// </summary>
    public class WebPlaybackHost : IWebPlaybackHost
    {
        private const string VirtualHostName = "spotifywpf.player";

        private readonly ISpotify _spotify;

        private WebView2 _webView;

        private Task _initializationTask;

        public bool IsReady { get; private set; }

        public string DeviceId { get; private set; }

        public string InitializationError { get; private set; }

        public event EventHandler PlayerReady;

        public event EventHandler<PlayerStateSnapshot> StateChanged;

        public event EventHandler<PositionSnapshot> PositionUpdated;

        public event EventHandler<string> TrackEnded;

        public event EventHandler<ArmedActionFiredEventArgs> ActionFired;

        public event EventHandler<PlayerErrorEventArgs> PlayerError;

        public event EventHandler InitializationFailed;

        public WebPlaybackHost(ISpotify spotify)
        {
            _spotify = spotify;
        }

        public WebView2 GetOrCreateView()
        {
            if (_webView == null)
                _webView = new WebView2();

            return _webView;
        }

        public Task EnsureInitializedAsync()
        {
            var view = GetOrCreateView();

            if (_initializationTask == null)
                _initializationTask = InitializeAsync();

            return _initializationTask;
        }

        private async Task InitializeAsync()
        {
            try
            {
                var environment = await CoreWebView2Environment.CreateAsync(
                    null,
                    PredictionPaths.WebView2UserDataDirectory,
                    // The SDK's <audio> element is started from the Web API, not a user gesture.
                    new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required"));

                await _webView.EnsureCoreWebView2Async(environment);

                var assetsDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets");

                _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    VirtualHostName, assetsDirectory, CoreWebView2HostResourceAccessKind.Allow);

                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

                _webView.CoreWebView2.Navigate($"https://{VirtualHostName}/player.html");
            }
            catch (Exception ex)
            {
                InitializationError =
                    "Failed to start the embedded player. Install the Microsoft Edge WebView2 Runtime, " +
                    "fully close and restart SpotifyWPF, then try again. If it still fails, delete " +
                    $"%LocalAppData%\\SpotifyWPF\\Prediction\\webview2 and retry. ({ex.Message})";
                Console.WriteLine($"WebPlaybackHost initialization failed: {ex}");
                InitializationFailed?.Invoke(this, EventArgs.Empty);
            }
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            JsonDocument document;

            try
            {
                document = JsonDocument.Parse(e.WebMessageAsJson);
            }
            catch (JsonException)
            {
                return;
            }

            using (document)
            {
                var root = document.RootElement;

                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeElement))
                    return;

                switch (typeElement.GetString())
                {
                    case "token-request":
                        HandleTokenRequest();
                        break;

                    case "ready":
                        DeviceId = GetString(root, "deviceId");
                        IsReady = true;
                        PlayerReady?.Invoke(this, EventArgs.Empty);
                        break;

                    case "not-ready":
                        IsReady = false;
                        break;

                    case "state":
                        StateChanged?.Invoke(this, new PlayerStateSnapshot
                        {
                            TrackId = GetString(root, "trackId"),
                            TrackUri = GetString(root, "trackUri"),
                            TrackName = GetString(root, "trackName"),
                            ArtistNames = GetString(root, "artistNames"),
                            DurationMs = GetInt64(root, "durationMs"),
                            PositionMs = GetInt64(root, "positionMs"),
                            Paused = GetBoolean(root, "paused")
                        });
                        break;

                    case "position":
                        PositionUpdated?.Invoke(this, new PositionSnapshot
                        {
                            TrackId = GetString(root, "trackId"),
                            PositionMs = GetInt64(root, "positionMs"),
                            Paused = GetBoolean(root, "paused")
                        });
                        break;

                    case "track-ended":
                        TrackEnded?.Invoke(this, GetString(root, "trackId"));
                        break;

                    case "action-fired":
                        ActionFired?.Invoke(this, new ArmedActionFiredEventArgs
                        {
                            ActionId = GetString(root, "actionId"),
                            FiredAtMs = GetInt64(root, "firedAtMs"),
                            SeekToMs = GetInt64(root, "seekToMs")
                        });
                        break;

                    case "error":
                        PlayerError?.Invoke(this, new PlayerErrorEventArgs
                        {
                            Kind = GetString(root, "kind"),
                            Message = GetString(root, "message")
                        });
                        break;
                }
            }
        }

        private async void HandleTokenRequest()
        {
            string accessToken = null;

            try
            {
                accessToken = await _spotify.GetAccessTokenAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Token request from player failed: {ex.Message}");
            }

            PostMessage(new { type = "token", accessToken = accessToken ?? string.Empty });
        }

        public void ArmAction(string actionId, long whenMs, long seekToMs)
        {
            PostMessage(new { type = "arm", actionId, whenMs, seekToMs });
        }

        public void DisarmAction()
        {
            PostMessage(new { type = "disarm" });
        }

        public void Pause()
        {
            PostMessage(new { type = "pause" });
        }

        public void Resume()
        {
            PostMessage(new { type = "resume" });
        }

        public void Seek(long positionMs)
        {
            PostMessage(new { type = "seek", positionMs });
        }

        public void SetVolume(double volume)
        {
            PostMessage(new { type = "volume", volume });
        }

        private void PostMessage(object message)
        {
            var core = _webView?.CoreWebView2;

            if (core == null)
                return;

            try
            {
                core.PostWebMessageAsJson(JsonSerializer.Serialize(message));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to post message to player: {ex.Message}");
            }
        }

        private static string GetString(JsonElement root, string name)
        {
            return root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : null;
        }

        private static long GetInt64(JsonElement root, string name)
        {
            if (root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number)
            {
                // Positions arrive as JS numbers which may carry a fractional part.
                return (long)element.GetDouble();
            }

            return 0;
        }

        private static bool GetBoolean(JsonElement root, string name)
        {
            return root.TryGetProperty(name, out var element) &&
                   (element.ValueKind == JsonValueKind.True || element.ValueKind == JsonValueKind.False) &&
                   element.GetBoolean();
        }
    }
}
