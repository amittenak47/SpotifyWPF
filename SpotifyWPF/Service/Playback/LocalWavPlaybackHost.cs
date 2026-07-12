using System;
using System.IO;
using System.Windows.Threading;
using NAudio.Wave;
using SpotifyWPF.Service.Audio;
using SpotifyWPF.Service.Prediction;

namespace SpotifyWPF.Service.Playback
{
    /// <summary>
    /// Plays a cached analysis WAV with NAudio and mirrors the Web Playback armed-action contract:
    /// a 50 ms tick enforces seek-when-reached so jukebox jumps are sample-accurate in-process seeks
    /// (no Spotify SDK poll jitter).
    /// </summary>
    public sealed class LocalWavPlaybackHost : IJukeboxTransport, IDisposable
    {
        private readonly object _gate = new object();

        private readonly DispatcherTimer _tickTimer;

        private WaveOutEvent _output;

        private AudioFileReader _reader;

        private string _trackId;

        private string _trackName;

        private string _artistNames;

        private long _durationMs;

        private bool _paused = true;

        private bool _hasArmed;

        private string _armedActionId;

        private long _armedWhenMs;

        private long _armedSeekToMs;

        private int _positionTickCounter;

        public event EventHandler<PlayerStateSnapshot> StateChanged;

        public event EventHandler<PositionSnapshot> PositionUpdated;

        public event EventHandler<ArmedActionFiredEventArgs> ActionFired;

        public event EventHandler<string> TrackEnded;

        public bool IsPlayingTrack => _reader != null && !string.IsNullOrEmpty(_trackId);

        public string CurrentTrackId
        {
            get
            {
                lock (_gate)
                    return _trackId;
            }
        }

        public LocalWavPlaybackHost()
        {
            _tickTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _tickTimer.Tick += OnTick;
        }

        /// <summary>True when a complete capture WAV exists for the track.</summary>
        public static bool CanPlayTrack(string trackId, long expectedDurationMs = 0)
        {
            return !string.IsNullOrEmpty(trackId) &&
                   WavCaptureValidator.HasCompleteCapture(trackId, expectedDurationMs);
        }

        public bool PlayTrack(string trackId, string trackName = null, string artistNames = null,
            long startMs = 0)
        {
            if (!CanPlayTrack(trackId))
                return false;

            var wavPath = PredictionPaths.ResolveAudioCachePath(trackId);

            if (string.IsNullOrEmpty(wavPath) || !File.Exists(wavPath))
                return false;

            lock (_gate)
            {
                StopInternal(raiseEnded: false);

                try
                {
                    _reader = new AudioFileReader(wavPath);
                    _output = new WaveOutEvent();
                    _output.Init(_reader);
                    _output.PlaybackStopped += OnPlaybackStopped;

                    _trackId = trackId;
                    _trackName = trackName ?? trackId;
                    _artistNames = artistNames ?? string.Empty;
                    _durationMs = (long)_reader.TotalTime.TotalMilliseconds;
                    _paused = false;
                    _hasArmed = false;
                    _positionTickCounter = 0;

                    if (startMs > 0)
                        SeekInternal(startMs);

                    _output.Play();
                    _tickTimer.Start();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Local WAV play failed for {trackId}: {ex.Message}");
                    StopInternal(raiseEnded: false);
                    return false;
                }
            }

            RaiseStateChanged();
            RaisePositionUpdated();
            return true;
        }

        public void Stop()
        {
            lock (_gate)
                StopInternal(raiseEnded: false);

            RaiseStateChanged();
        }

        public void ArmAction(string actionId, long whenMs, long seekToMs)
        {
            lock (_gate)
            {
                _armedActionId = actionId;
                _armedWhenMs = Math.Max(0, whenMs);
                _armedSeekToMs = Math.Max(0, seekToMs);
                _hasArmed = !string.IsNullOrEmpty(actionId);
            }
        }

        public void DisarmAction()
        {
            lock (_gate)
                _hasArmed = false;
        }

        public void Pause()
        {
            lock (_gate)
            {
                if (_output == null || _paused)
                    return;

                _output.Pause();
                _paused = true;
            }

            RaiseStateChanged();
            RaisePositionUpdated();
        }

        public void Resume()
        {
            lock (_gate)
            {
                if (_output == null || !_paused)
                    return;

                _output.Play();
                _paused = false;
            }

            RaiseStateChanged();
            RaisePositionUpdated();
        }

        public void Seek(long positionMs)
        {
            lock (_gate)
                SeekInternal(positionMs);

            RaiseStateChanged();
            RaisePositionUpdated();
        }

        public void SetVolume(double volume)
        {
            lock (_gate)
            {
                if (_output == null)
                    return;

                _output.Volume = (float)Math.Max(0, Math.Min(1, volume));
            }
        }

        public void Dispose()
        {
            _tickTimer.Stop();
            _tickTimer.Tick -= OnTick;

            lock (_gate)
                StopInternal(raiseEnded: false);
        }

        private void OnTick(object sender, EventArgs e)
        {
            string firedActionId = null;
            long firedAt = 0;
            long seekTo = 0;
            var shouldRaisePosition = false;
            var shouldRaiseEnded = false;

            lock (_gate)
            {
                if (_reader == null || _output == null)
                    return;

                var positionMs = CurrentPositionMsUnlocked();
                var firedSeekThisTick = false;

                if (_hasArmed && !_paused && positionMs >= _armedWhenMs)
                {
                    firedActionId = _armedActionId;
                    firedAt = positionMs;
                    seekTo = _armedSeekToMs;
                    _hasArmed = false;
                    SeekInternal(seekTo);
                    firedSeekThisTick = true;
                    positionMs = CurrentPositionMsUnlocked();
                }

                _positionTickCounter++;

                // ~250 ms position posts (every 5th 50 ms tick), matching the SDK host cadence.
                if (_positionTickCounter >= 5)
                {
                    _positionTickCounter = 0;
                    shouldRaisePosition = true;
                }

                // Never raise TrackEnded on the same tick as a jukebox seek (old position was near EOF
                // and would stop playback right after escaping). Also suppress while an armed jump
                // is still waiting — end-loop must win over natural finish.
                if (!firedSeekThisTick && !_hasArmed && !_paused && _durationMs > 0 &&
                    positionMs >= _durationMs - 25)
                    shouldRaiseEnded = true;
            }

            if (firedActionId != null)
            {
                ActionFired?.Invoke(this, new ArmedActionFiredEventArgs
                {
                    ActionId = firedActionId,
                    FiredAtMs = firedAt,
                    SeekToMs = seekTo
                });
            }

            if (shouldRaisePosition)
                RaisePositionUpdated();

            if (shouldRaiseEnded)
            {
                string endedId;

                lock (_gate)
                {
                    endedId = _trackId;
                    StopInternal(raiseEnded: false);
                }

                if (!string.IsNullOrEmpty(endedId))
                    TrackEnded?.Invoke(this, endedId);

                RaiseStateChanged();
            }
        }

        private void OnPlaybackStopped(object sender, StoppedEventArgs e)
        {
            // Natural end is handled on the tick; ignore stop from Seek/Dispose/Pause transitions.
        }

        private void SeekInternal(long positionMs)
        {
            if (_reader == null)
                return;

            var clamped = Math.Max(0, Math.Min(positionMs, Math.Max(0, _durationMs - 1)));
            _reader.CurrentTime = TimeSpan.FromMilliseconds(clamped);
        }

        private long CurrentPositionMsUnlocked()
        {
            if (_reader == null)
                return 0;

            return (long)_reader.CurrentTime.TotalMilliseconds;
        }

        private void StopInternal(bool raiseEnded)
        {
            _tickTimer.Stop();
            _hasArmed = false;

            if (_output != null)
            {
                _output.PlaybackStopped -= OnPlaybackStopped;

                try
                {
                    _output.Stop();
                }
                catch
                {
                    // ignore teardown races
                }

                _output.Dispose();
                _output = null;
            }

            if (_reader != null)
            {
                _reader.Dispose();
                _reader = null;
            }

            var endedId = _trackId;
            _trackId = null;
            _paused = true;
            _durationMs = 0;

            if (raiseEnded && !string.IsNullOrEmpty(endedId))
                TrackEnded?.Invoke(this, endedId);
        }

        private void RaiseStateChanged()
        {
            PlayerStateSnapshot snapshot;

            lock (_gate)
            {
                snapshot = new PlayerStateSnapshot
                {
                    TrackId = _trackId,
                    TrackUri = string.IsNullOrEmpty(_trackId) ? null : $"spotify:track:{_trackId}",
                    TrackName = _trackName,
                    ArtistNames = _artistNames,
                    DurationMs = _durationMs,
                    PositionMs = CurrentPositionMsUnlocked(),
                    Paused = _paused || _reader == null
                };
            }

            StateChanged?.Invoke(this, snapshot);
        }

        private void RaisePositionUpdated()
        {
            PositionSnapshot snapshot;

            lock (_gate)
            {
                if (string.IsNullOrEmpty(_trackId))
                    return;

                snapshot = new PositionSnapshot
                {
                    TrackId = _trackId,
                    PositionMs = CurrentPositionMsUnlocked(),
                    Paused = _paused
                };
            }

            PositionUpdated?.Invoke(this, snapshot);
        }
    }
}
