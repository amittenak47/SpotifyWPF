using System;

namespace SpotifyWPF.Service.Playback
{
    /// <summary>
    /// Routes jukebox arm/seek/position events to either the Spotify Web Playback host or the
    /// local WAV host. WebView2 lifecycle stays on <see cref="IWebPlaybackHost"/>; only the
    /// active source's events are forwarded so <c>LoopController</c> sees one transport.
    /// </summary>
    public sealed class JukeboxTransportRouter : IJukeboxTransport
    {
        private readonly IWebPlaybackHost _spotify;

        private readonly LocalWavPlaybackHost _local;

        private JukeboxPlaybackSource _source = JukeboxPlaybackSource.Spotify;

        private bool _spotifyHooked;

        private bool _localHooked;

        public event EventHandler<PlayerStateSnapshot> StateChanged;

        public event EventHandler<PositionSnapshot> PositionUpdated;

        public event EventHandler<ArmedActionFiredEventArgs> ActionFired;

        public event EventHandler<string> TrackEnded;

        public JukeboxPlaybackSource Source
        {
            get => _source;
            set
            {
                if (_source == value)
                    return;

                var previous = _source;
                _source = value;
                Active.DisarmAction();

                if (previous == JukeboxPlaybackSource.Local && value == JukeboxPlaybackSource.Spotify)
                    _local.Stop();
                else if (previous == JukeboxPlaybackSource.Spotify && value == JukeboxPlaybackSource.Local)
                    _spotify.Pause();
            }
        }

        public LocalWavPlaybackHost Local => _local;

        public IWebPlaybackHost Spotify => _spotify;

        private IJukeboxTransport Active =>
            _source == JukeboxPlaybackSource.Local ? (IJukeboxTransport)_local : _spotify;

        public JukeboxTransportRouter(IWebPlaybackHost spotify, LocalWavPlaybackHost local)
        {
            _spotify = spotify ?? throw new ArgumentNullException(nameof(spotify));
            _local = local ?? throw new ArgumentNullException(nameof(local));
            HookSources();
        }

        public void ArmAction(string actionId, long whenMs, long seekToMs) =>
            Active.ArmAction(actionId, whenMs, seekToMs);

        public void DisarmAction() => Active.DisarmAction();

        public void Pause() => Active.Pause();

        public void Resume() => Active.Resume();

        public void Seek(long positionMs) => Active.Seek(positionMs);

        public void SetVolume(double volume) => Active.SetVolume(volume);

        public void SetPlaybackRate(double rate)
        {
            // Hold-to-scan is a Local WAV feature; Spotify Web Playback has no rate API here.
            if (_source == JukeboxPlaybackSource.Local)
                _local.SetPlaybackRate(rate);
            else
                _local.SetPlaybackRate(1.0);
        }

        private void HookSources()
        {
            if (!_spotifyHooked)
            {
                _spotify.StateChanged += OnSpotifyStateChanged;
                _spotify.PositionUpdated += OnSpotifyPositionUpdated;
                _spotify.ActionFired += OnSpotifyActionFired;
                _spotify.TrackEnded += OnSpotifyTrackEnded;
                _spotifyHooked = true;
            }

            if (!_localHooked)
            {
                _local.StateChanged += OnLocalStateChanged;
                _local.PositionUpdated += OnLocalPositionUpdated;
                _local.ActionFired += OnLocalActionFired;
                _local.TrackEnded += OnLocalTrackEnded;
                _localHooked = true;
            }
        }

        private void OnSpotifyStateChanged(object sender, PlayerStateSnapshot e)
        {
            if (_source == JukeboxPlaybackSource.Spotify)
                StateChanged?.Invoke(this, e);
        }

        private void OnSpotifyPositionUpdated(object sender, PositionSnapshot e)
        {
            if (_source == JukeboxPlaybackSource.Spotify)
                PositionUpdated?.Invoke(this, e);
        }

        private void OnSpotifyActionFired(object sender, ArmedActionFiredEventArgs e)
        {
            if (_source == JukeboxPlaybackSource.Spotify)
                ActionFired?.Invoke(this, e);
        }

        private void OnSpotifyTrackEnded(object sender, string e)
        {
            if (_source == JukeboxPlaybackSource.Spotify)
                TrackEnded?.Invoke(this, e);
        }

        private void OnLocalStateChanged(object sender, PlayerStateSnapshot e)
        {
            if (_source == JukeboxPlaybackSource.Local)
                StateChanged?.Invoke(this, e);
        }

        private void OnLocalPositionUpdated(object sender, PositionSnapshot e)
        {
            if (_source == JukeboxPlaybackSource.Local)
                PositionUpdated?.Invoke(this, e);
        }

        private void OnLocalActionFired(object sender, ArmedActionFiredEventArgs e)
        {
            if (_source == JukeboxPlaybackSource.Local)
                ActionFired?.Invoke(this, e);
        }

        private void OnLocalTrackEnded(object sender, string e)
        {
            if (_source == JukeboxPlaybackSource.Local)
                TrackEnded?.Invoke(this, e);
        }
    }
}
