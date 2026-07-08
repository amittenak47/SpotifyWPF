using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Command;
using Microsoft.Win32;
using SpotifyWPF.Model.Prediction;
using SpotifyWPF.Service;
using SpotifyWPF.Service.Playback;
using SpotifyWPF.Service.Prediction;
using SpotifyWPF.ViewModel.Component;

namespace SpotifyWPF.ViewModel.Page
{
    /// <summary>
    /// Experimental → Prediction page: hosts the Web Playback SDK player (Loop Lab) and the
    /// next-track prediction UI. Playback runs inside a WebView2 the view re-parents on load, so
    /// audio survives page navigation.
    /// </summary>
    public class PredictionPageViewModel : ViewModelBase
    {
        /// <summary>A play counts as ended naturally when it stops within this window of the end.</summary>
        private const long NaturalEndToleranceMs = 5000;

        private readonly ISpotify _spotify;

        private readonly IWebPlaybackHost _playbackHost;

        private readonly ISpotifyPlaybackService _playbackService;

        private readonly IAnalysisGate _analysisGate;

        private readonly IListeningLogService _listeningLog;

        private readonly ILoopController _loopController;

        private readonly IAnalysisProviderSelector _analysisProviderSelector;

        private readonly INextTrackPredictor _predictor;

        private readonly ILoopRegionStore _loopRegionStore;

        private readonly IJukeboxSettingsStore _jukeboxSettings;

        private readonly JukeboxSettings _jukeboxSettingsModel;

        private readonly PredictorWeights _weights;

        /// <summary>Last track that finished naturally — the anchor for "what next" scoring.</summary>
        private string _lastEndedTrackId;

        /// <summary>Context URI handed over from the Playlists grid, played once the SDK device is up.</summary>
        private string _pendingContextUri;

        /// <summary>Guards against re-applying the loop profile while the UI is being refreshed from it.</summary>
        private bool _suppressLoopApply;

        // Current play tracking for the listening log.
        private PlayEvent _currentPlay;

        private bool _currentPlayEndedNaturally;

        public PredictionPageViewModel(
            ISpotify spotify,
            IWebPlaybackHost playbackHost,
            ISpotifyPlaybackService playbackService,
            IAnalysisGate analysisGate,
            IListeningLogService listeningLog,
            ILoopController loopController,
            IAnalysisProviderSelector analysisProviderSelector,
            INextTrackPredictor predictor,
            ILoopRegionStore loopRegionStore,
            IJukeboxSettingsStore jukeboxSettings)
        {
            _loopRegionStore = loopRegionStore;
            _jukeboxSettings = jukeboxSettings;
            _jukeboxSettingsModel = _jukeboxSettings.Get();
            _spotify = spotify;
            _playbackHost = playbackHost;
            _playbackService = playbackService;
            _analysisGate = analysisGate;
            _listeningLog = listeningLog;
            _loopController = loopController;
            _analysisProviderSelector = analysisProviderSelector;
            _predictor = predictor;
            _weights = predictor.GetWeights();

            ActivityLog = new ActivityLogViewModel { NewestFirst = true };

            _loopController.LoopEvent += (_, message) => Log(message);
            _loopController.JukeboxJump += OnJukeboxJump;

            _playbackHost.PlayerReady += OnPlayerReady;
            _playbackHost.StateChanged += OnStateChanged;
            _playbackHost.PositionUpdated += OnPositionUpdated;
            _playbackHost.TrackEnded += OnTrackEnded;
            _playbackHost.PlayerError += OnPlayerError;
            _playbackHost.InitializationFailed += OnInitializationFailed;

            PlayCommand = new RelayCommand(async () => await PlayFromInputAsync(), CanUsePlayer);
            PauseResumeCommand = new RelayCommand(PauseResume, CanUsePlayer);
            ReprobeAnalysisCommand = new RelayCommand(async () => await ProbeAnalysisSourceAsync(true));
            SetLoopStartCommand = new RelayCommand(() => LoopStartMs = PositionMs, HasCurrentTrack);
            SetLoopEndCommand = new RelayCommand(() => LoopEndMs = PositionMs, HasCurrentTrack);
            AnalyzeTrackCommand = new RelayCommand(async () => await AnalyzeCurrentTrackAsync(),
                () => HasCurrentTrack() && !IsAnalyzing);
            RefreshPredictionsCommand = new RelayCommand(async () => await RefreshPredictionsAsync());
            PlayPredictionCommand = new RelayCommand<ScoredTrack>(
                async track => await PlayPredictionAsync(track));
            TogglePinCommand = new RelayCommand<ScoredTrack>(TogglePin);
            ExportLoopDataCommand = new RelayCommand(ExportLoopData);
            ImportLoopDataCommand = new RelayCommand(ImportLoopData);
            AutoDetectPythonCommand = new RelayCommand(AutoDetectPython);
            BrowsePythonCommand = new RelayCommand(BrowsePython);

            MessengerInstance.Register<string>(this, MessageType.OpenInLoopLab,
                async contextUri => await HandleOpenInLoopLabAsync(contextUri));

            UpdateAnalysisSourceText(_analysisGate.CachedSource);
            _pythonExecutablePath = PythonLauncher.GetConfiguredPath();
        }

        #region Bindable state

        private string _status = "Player not started.";

        public string Status
        {
            get => _status;
            set => Set(ref _status, value);
        }

        private string _trackInput;

        public string TrackInput
        {
            get => _trackInput;
            set => Set(ref _trackInput, value);
        }

        private string _nowPlayingTitle = "Nothing playing";

        public string NowPlayingTitle
        {
            get => _nowPlayingTitle;
            set => Set(ref _nowPlayingTitle, value);
        }

        private string _nowPlayingArtist = string.Empty;

        public string NowPlayingArtist
        {
            get => _nowPlayingArtist;
            set => Set(ref _nowPlayingArtist, value);
        }

        private long _positionMs;

        public long PositionMs
        {
            get => _positionMs;
            set
            {
                if (Set(ref _positionMs, value))
                    RaisePropertyChanged(nameof(PositionText));
            }
        }

        private long _durationMs;

        public long DurationMs
        {
            get => _durationMs;
            set
            {
                if (Set(ref _durationMs, value))
                    RaisePropertyChanged(nameof(PositionText));
            }
        }

        public string PositionText => $"{FormatMs(PositionMs)} / {FormatMs(DurationMs)}";

        private bool _isPaused = true;

        public bool IsPaused
        {
            get => _isPaused;
            set
            {
                if (Set(ref _isPaused, value))
                    RaisePropertyChanged(nameof(PauseResumeLabel));
            }
        }

        public string PauseResumeLabel => IsPaused ? "Resume" : "Pause";

        private string _deviceText = "Player starting…";

        public string DeviceText
        {
            get => _deviceText;
            set => Set(ref _deviceText, value);
        }

        private string _analysisSourceText;

        public string AnalysisSourceText
        {
            get => _analysisSourceText;
            set => Set(ref _analysisSourceText, value);
        }

        private string _pythonExecutablePath = string.Empty;

        /// <summary>Full path to python.exe for Path B local analysis; stored in per-user app settings.</summary>
        public string PythonExecutablePath
        {
            get => _pythonExecutablePath;
            set
            {
                if (Set(ref _pythonExecutablePath, value ?? string.Empty))
                    PythonLauncher.SaveConfiguredPath(_pythonExecutablePath);
            }
        }

        private string _playerInitializationError;

        public string PlayerInitializationError
        {
            get => _playerInitializationError;
            set
            {
                if (Set(ref _playerInitializationError, value))
                    RaisePropertyChanged(nameof(HasPlayerInitializationError));
            }
        }

        public bool HasPlayerInitializationError => !string.IsNullOrEmpty(PlayerInitializationError);

        public ActivityLogViewModel ActivityLog { get; }

        public string CurrentTrackId => _currentPlay?.TrackId;

        private long _loopStartMs;

        public long LoopStartMs
        {
            get => _loopStartMs;
            set
            {
                if (Set(ref _loopStartMs, value))
                    ApplyLoopSettings();
            }
        }

        private long _loopEndMs;

        public long LoopEndMs
        {
            get => _loopEndMs;
            set
            {
                if (Set(ref _loopEndMs, value))
                    ApplyLoopSettings();
            }
        }

        private bool _loopEnabled;

        public bool LoopEnabled
        {
            get => _loopEnabled;
            set
            {
                if (Set(ref _loopEnabled, value))
                    ApplyLoopSettings();
            }
        }

        private bool _jukeboxModeEnabled;

        /// <summary>False = simple loop (start/end region); true = Infinite Jukebox beat jumps.</summary>
        public bool JukeboxModeEnabled
        {
            get => _jukeboxModeEnabled;
            set
            {
                if (Set(ref _jukeboxModeEnabled, value))
                {
                    RaisePropertyChanged(nameof(SimpleModeEnabled));
                    ApplyLoopSettings();
                }
            }
        }

        public bool SimpleModeEnabled
        {
            get => !_jukeboxModeEnabled;
            set => JukeboxModeEnabled = !value;
        }

        private string _loopStatusText = "No loop set for this track.";

        public string LoopStatusText
        {
            get => _loopStatusText;
            set => Set(ref _loopStatusText, value);
        }

        private bool _isAnalyzing;

        public bool IsAnalyzing
        {
            get => _isAnalyzing;
            set
            {
                if (Set(ref _isAnalyzing, value))
                    AnalyzeTrackCommand.RaiseCanExecuteChanged();
            }
        }

        private string _analysisStatusText = "No analysis for this track yet.";

        public string AnalysisStatusText
        {
            get => _analysisStatusText;
            set => Set(ref _analysisStatusText, value);
        }

        public double JukeboxSimilarityThresholdMax
        {
            get => _jukeboxSettingsModel.BranchSimilarityThresholdMax;
            set
            {
                if (Math.Abs(_jukeboxSettingsModel.BranchSimilarityThresholdMax - value) < 0.01)
                    return;

                _jukeboxSettingsModel.BranchSimilarityThresholdMax = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(JukeboxSimilarityThresholdMaxText));
                PersistJukeboxSettings();
            }
        }

        public double JukeboxBranchProbabilityMinPercent
        {
            get => _jukeboxSettingsModel.BranchProbabilityMin * 100;
            set => SetJukeboxProbabilityMin(value / 100);
        }

        public double JukeboxBranchProbabilityMaxPercent
        {
            get => _jukeboxSettingsModel.BranchProbabilityMax * 100;
            set => SetJukeboxProbabilityMax(value / 100);
        }

        public double JukeboxBranchRampPerBeatPercent
        {
            get => _jukeboxSettingsModel.BranchProbabilityRampPerBeat * 100;
            set
            {
                if (Math.Abs(_jukeboxSettingsModel.BranchProbabilityRampPerBeat - value / 100) < 0.0001)
                    return;

                _jukeboxSettingsModel.BranchProbabilityRampPerBeat = value / 100;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(JukeboxBranchRampText));
                PersistJukeboxSettings();
            }
        }

        public double JukeboxSeekLeadMs
        {
            get => _jukeboxSettingsModel.SeekLeadMs;
            set
            {
                var rounded = (int)Math.Round(value);

                if (_jukeboxSettingsModel.SeekLeadMs == rounded)
                    return;

                _jukeboxSettingsModel.SeekLeadMs = rounded;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(JukeboxSeekLeadMsText));
                PersistJukeboxSettings();
            }
        }

        public string JukeboxSimilarityThresholdMaxText =>
            $"{_jukeboxSettingsModel.BranchSimilarityThresholdMax:0}";

        public string JukeboxBranchProbabilityMinText =>
            $"{_jukeboxSettingsModel.BranchProbabilityMin * 100:0.#}%";

        public string JukeboxBranchProbabilityMaxText =>
            $"{_jukeboxSettingsModel.BranchProbabilityMax * 100:0.#}%";

        public string JukeboxBranchRampText =>
            $"{_jukeboxSettingsModel.BranchProbabilityRampPerBeat * 100:0.##}%";

        public string JukeboxSeekLeadMsText =>
            $"{_jukeboxSettingsModel.SeekLeadMs:0} ms";

        public bool JukeboxAllowOnlyReverseBranches
        {
            get => _jukeboxSettingsModel.AllowOnlyReverseBranches;
            set
            {
                if (_jukeboxSettingsModel.AllowOnlyReverseBranches == value)
                    return;

                _jukeboxSettingsModel.AllowOnlyReverseBranches = value;
                RaisePropertyChanged();
                PersistJukeboxSettings();
            }
        }

        public bool JukeboxAllowOnlyLongBranches
        {
            get => _jukeboxSettingsModel.AllowOnlyLongBranches;
            set
            {
                if (_jukeboxSettingsModel.AllowOnlyLongBranches == value)
                    return;

                _jukeboxSettingsModel.AllowOnlyLongBranches = value;
                RaisePropertyChanged();
                PersistJukeboxSettings();
            }
        }

        public string RingSegmentCountText
        {
            get => _ringSegmentCountText;
            set => Set(ref _ringSegmentCountText, value);
        }

        private string _ringSegmentCountText = "No segments loaded";

        public string JukeboxBranchChanceText
        {
            get => _jukeboxBranchChanceText;
            set => Set(ref _jukeboxBranchChanceText, value);
        }

        private string _jukeboxBranchChanceText = "Branch chance: —";

        public long RingDurationMs
        {
            get => _ringDurationMs;
            set => Set(ref _ringDurationMs, value);
        }

        private long _ringDurationMs;

        public IReadOnlyList<double> RingSegmentStartsSec
        {
            get => _ringSegmentStartsSec;
            set => Set(ref _ringSegmentStartsSec, value);
        }

        private IReadOnlyList<double> _ringSegmentStartsSec = Array.Empty<double>();

        public long? RingGlowFromMs
        {
            get => _ringGlowFromMs;
            set => Set(ref _ringGlowFromMs, value);
        }

        private long? _ringGlowFromMs;

        public long? RingGlowToMs
        {
            get => _ringGlowToMs;
            set => Set(ref _ringGlowToMs, value);
        }

        private long? _ringGlowToMs;

        public ObservableCollection<ScoredTrack> Predictions { get; } =
            new ObservableCollection<ScoredTrack>();

        private string _predictionStatusText = "Predictions appear after a track finishes (or refresh manually).";

        public string PredictionStatusText
        {
            get => _predictionStatusText;
            set => Set(ref _predictionStatusText, value);
        }

        private bool _autoPlayNext;

        /// <summary>Opt-in: automatically play the top prediction when a track ends naturally.</summary>
        public bool AutoPlayNext
        {
            get => _autoPlayNext;
            set => Set(ref _autoPlayNext, value);
        }

        public double WeightTransition
        {
            get => _weights.Transition;
            set
            {
                _weights.Transition = value;
                RaisePropertyChanged();
                _predictor.SaveWeights(_weights);
            }
        }

        public double WeightRepeatAffinity
        {
            get => _weights.RepeatAffinity;
            set
            {
                _weights.RepeatAffinity = value;
                RaisePropertyChanged();
                _predictor.SaveWeights(_weights);
            }
        }

        public double WeightSameArtist
        {
            get => _weights.SameArtist;
            set
            {
                _weights.SameArtist = value;
                RaisePropertyChanged();
                _predictor.SaveWeights(_weights);
            }
        }

        public double WeightTempoSimilarity
        {
            get => _weights.TempoSimilarity;
            set
            {
                _weights.TempoSimilarity = value;
                RaisePropertyChanged();
                _predictor.SaveWeights(_weights);
            }
        }

        public double WeightRecencyPenalty
        {
            get => _weights.RecencyPenalty;
            set
            {
                _weights.RecencyPenalty = value;
                RaisePropertyChanged();
                _predictor.SaveWeights(_weights);
            }
        }

        public double WeightPinned
        {
            get => _weights.Pinned;
            set
            {
                _weights.Pinned = value;
                RaisePropertyChanged();
                _predictor.SaveWeights(_weights);
            }
        }

        #endregion

        #region Commands

        public RelayCommand PlayCommand { get; }

        public RelayCommand PauseResumeCommand { get; }

        public RelayCommand ReprobeAnalysisCommand { get; }

        public RelayCommand SetLoopStartCommand { get; }

        public RelayCommand SetLoopEndCommand { get; }

        public RelayCommand AnalyzeTrackCommand { get; }

        public RelayCommand RefreshPredictionsCommand { get; }

        public RelayCommand<ScoredTrack> PlayPredictionCommand { get; }

        public RelayCommand<ScoredTrack> TogglePinCommand { get; }

        public RelayCommand ExportLoopDataCommand { get; }

        public RelayCommand ImportLoopDataCommand { get; }

        public RelayCommand AutoDetectPythonCommand { get; }

        public RelayCommand BrowsePythonCommand { get; }

        private bool CanUsePlayer()
        {
            return _playbackHost.IsReady && !HasPlayerInitializationError;
        }

        private bool HasCurrentTrack()
        {
            return _loopController.CurrentTrackId != null;
        }

        #endregion

        /// <summary>Called by the view whenever the page loads; safe to call repeatedly.</summary>
        public async Task OnPageLoadedAsync()
        {
            if (!string.IsNullOrEmpty(_playbackHost.InitializationError))
            {
                PlayerInitializationError = _playbackHost.InitializationError;
                Status = "Embedded player failed to start.";
                return;
            }

            if (_playbackHost.IsReady)
            {
                Status = "Player ready.";
                return;
            }

            Status = "Starting embedded player…";
            await _playbackHost.EnsureInitializedAsync();

            if (!string.IsNullOrEmpty(_playbackHost.InitializationError))
            {
                PlayerInitializationError = _playbackHost.InitializationError;
                Status = "Embedded player failed to start.";
            }
        }

        private async Task PlayFromInputAsync()
        {
            var trackId = ParseTrackId(TrackInput);

            if (trackId == null)
            {
                Status = "Enter a track ID, spotify:track: URI, or open.spotify.com track link.";
                return;
            }

            await PlayTrackAsync(trackId, "prediction-page");
        }

        /// <summary>Plays a track on the in-app SDK device. Source tags the listening-log entry.</summary>
        public async Task PlayTrackAsync(string trackId, string source)
        {
            if (!_playbackHost.IsReady)
            {
                Status = "Player is not ready yet.";
                return;
            }

            try
            {
                Status = $"Starting playback of {trackId}…";
                await _playbackService.PlayTrackAsync(trackId, _playbackHost.DeviceId);
                _pendingPlaySource = source;
            }
            catch (Exception ex)
            {
                Status = $"Playback failed: {ex.Message}";
                Log($"Playback failed: {ex.Message}");
            }
        }

        private void PauseResume()
        {
            if (IsPaused)
                _playbackHost.Resume();
            else
                _playbackHost.Pause();
        }

        /// <summary>Entry point for "Open in Loop Lab" from the Playlists grid.</summary>
        private async Task HandleOpenInLoopLabAsync(string contextUri)
        {
            if (string.IsNullOrEmpty(contextUri))
                return;

            if (_playbackHost.IsReady)
            {
                await PlayContextAsync(contextUri);
                return;
            }

            _pendingContextUri = contextUri;
            Status = "Player starting — playback begins once the device is ready.";
            await _playbackHost.EnsureInitializedAsync();
        }

        private async Task PlayContextAsync(string contextUri)
        {
            try
            {
                Status = "Starting playlist in Loop Lab…";
                await _playbackService.PlayContextAsync(contextUri, _playbackHost.DeviceId);
                _pendingPlaySource = "loop-lab-playlist";
            }
            catch (Exception ex)
            {
                Status = $"Playback failed: {ex.Message}";
                Log($"Playback failed for {contextUri}: {ex.Message}");
            }
        }

        #region Player event handlers

        private string _pendingPlaySource;

        private async void OnPlayerReady(object sender, EventArgs e)
        {
            RunOnUiThread(() =>
            {
                DeviceText = $"Device: SpotifyWPF Loop Lab ({_playbackHost.DeviceId})";
                Status = "Player ready.";
                Log("Web Playback SDK device is ready.");
                PlayCommand.RaiseCanExecuteChanged();
                PauseResumeCommand.RaiseCanExecuteChanged();
            });

            await ProbeAnalysisSourceAsync(false);

            var pendingContext = _pendingContextUri;
            _pendingContextUri = null;

            if (pendingContext != null)
                await PlayContextAsync(pendingContext);
        }

        private void OnStateChanged(object sender, PlayerStateSnapshot state)
        {
            RunOnUiThread(() =>
            {
                if (!string.IsNullOrEmpty(state.TrackId) && state.TrackId != _currentPlay?.TrackId)
                {
                    FinalizeCurrentPlay(userSkipped: true);
                    StartPlayTracking(state);
                    OnTrackChanged(state);
                }

                NowPlayingTitle = string.IsNullOrEmpty(state.TrackName) ? "Nothing playing" : state.TrackName;
                NowPlayingArtist = state.ArtistNames ?? string.Empty;
                DurationMs = state.DurationMs;
                RingDurationMs = state.DurationMs;
                PositionMs = state.PositionMs;
                IsPaused = state.Paused;
            });
        }

        private void OnPositionUpdated(object sender, PositionSnapshot position)
        {
            RunOnUiThread(() =>
            {
                PositionMs = position.PositionMs;
                IsPaused = position.Paused;

                if (_currentPlay != null && position.TrackId == _currentPlay.TrackId &&
                    position.PositionMs > _currentPlay.MaxPositionMs)
                {
                    _currentPlay.MaxPositionMs = position.PositionMs;
                }
            });
        }

        private void OnTrackEnded(object sender, string trackId)
        {
            RunOnUiThread(() =>
            {
                if (_currentPlay != null && _currentPlay.TrackId == trackId)
                {
                    _currentPlayEndedNaturally = true;
                    FinalizeCurrentPlay(userSkipped: false);
                }

                Status = "Track ended.";
                OnTrackEndedNaturally(trackId);
            });
        }

        private void OnPlayerError(object sender, PlayerErrorEventArgs error)
        {
            RunOnUiThread(() =>
            {
                if (error.Kind == "account_error")
                {
                    Status = "Spotify Premium is required for in-app playback.";
                    PlayerInitializationError = "This feature needs a Spotify Premium account: " + error.Message;
                }
                else
                {
                    Status = $"Player error ({error.Kind}): {error.Message}";
                }

                Log($"Player error ({error.Kind}): {error.Message}");
            });
        }

        private void OnInitializationFailed(object sender, EventArgs e)
        {
            RunOnUiThread(() =>
            {
                PlayerInitializationError = _playbackHost.InitializationError;
                Status = "Embedded player failed to start.";
            });
        }

        #endregion

        #region Listening log

        private void StartPlayTracking(PlayerStateSnapshot state)
        {
            _currentPlay = new PlayEvent
            {
                TrackId = state.TrackId,
                TrackName = state.TrackName,
                ArtistName = state.ArtistNames,
                StartedAt = DateTime.UtcNow,
                DurationMs = state.DurationMs,
                MaxPositionMs = state.PositionMs,
                Source = _pendingPlaySource ?? "external"
            };

            _pendingPlaySource = null;
            _currentPlayEndedNaturally = false;
        }

        private void FinalizeCurrentPlay(bool userSkipped)
        {
            if (_currentPlay == null)
                return;

            var play = _currentPlay;
            _currentPlay = null;

            play.EndedAt = DateTime.UtcNow;
            play.EndedNaturally = _currentPlayEndedNaturally ||
                                  (play.DurationMs > 0 &&
                                   play.MaxPositionMs >= play.DurationMs - NaturalEndToleranceMs);
            play.UserSkipped = userSkipped && !play.EndedNaturally;
            play.LoopActive = IsLoopActiveForLog();

            try
            {
                _listeningLog.Append(play);
                Log($"Logged play: {play.TrackName} " +
                    $"({(play.EndedNaturally ? "finished" : play.UserSkipped ? "skipped" : "stopped")})", verbose: true);
            }
            catch (Exception ex)
            {
                Log($"Failed to write listening log: {ex.Message}");
            }
        }

        #endregion

        #region Loop Lab

        /// <summary>Invoked when playback moves to a different track.</summary>
        private void OnTrackChanged(PlayerStateSnapshot state)
        {
            RefreshLoopSettingsFromStore(state.TrackId);

            SetLoopStartCommand.RaiseCanExecuteChanged();
            SetLoopEndCommand.RaiseCanExecuteChanged();
            AnalyzeTrackCommand.RaiseCanExecuteChanged();

            AnalysisStatusText = AnalysisCache.Exists(state.TrackId)
                ? "Analysis cached for this track."
                : "No analysis for this track yet.";

            RefreshRingVisualization(state.TrackId);
        }

        /// <summary>Whether a loop mode was active (stamped on log entries).</summary>
        private bool IsLoopActiveForLog()
        {
            return _loopController.IsLoopActive;
        }

        private void RefreshLoopSettingsFromStore(string trackId)
        {
            var profile = _loopController.GetProfileForTrack(trackId);

            _suppressLoopApply = true;

            try
            {
                LoopStartMs = profile?.LoopStartMs ?? 0;
                LoopEndMs = profile?.LoopEndMs ?? 0;
                LoopEnabled = profile != null && profile.Enabled;
                JukeboxModeEnabled = profile != null && profile.Mode == LoopModes.Jukebox;
            }
            finally
            {
                _suppressLoopApply = false;
            }

            UpdateLoopStatusText();
        }

        private void ApplyLoopSettings()
        {
            if (_suppressLoopApply)
                return;

            var trackId = _loopController.CurrentTrackId;

            if (trackId == null)
            {
                LoopStatusText = "Play a track first, then set its loop.";
                return;
            }

            var profile = _loopController.GetProfileForTrack(trackId);
            profile.LoopStartMs = LoopStartMs;
            profile.LoopEndMs = LoopEndMs;
            profile.Enabled = LoopEnabled;
            profile.Mode = JukeboxModeEnabled ? LoopModes.Jukebox : LoopModes.Simple;

            if (profile.Enabled && !JukeboxModeEnabled && !profile.IsValidRegion)
            {
                LoopStatusText = "Loop end must be after loop start.";
                return;
            }

            _loopController.ApplyProfile(profile);
            UpdateLoopStatusText();
        }

        private void UpdateLoopStatusText()
        {
            if (_loopController.IsLoopActive)
                LoopStatusText = JukeboxModeEnabled
                    ? "Infinite Jukebox active."
                    : $"Looping {FormatMs(LoopStartMs)} → {FormatMs(LoopEndMs)}.";
            else if (LoopEndMs > 0 || JukeboxModeEnabled)
                LoopStatusText = "Loop saved (disabled).";
            else
                LoopStatusText = "No loop set for this track.";
        }

        private void OnJukeboxJump(object sender, JukeboxJumpEventArgs e)
        {
            RunOnUiThread(() =>
            {
                RingGlowFromMs = e.FromMs;
                RingGlowToMs = e.ToMs;
                JukeboxBranchChanceText = e.IsPlanned
                    ? $"Branch chance: planning jump (sim-distance {e.BranchDistance:0})"
                    : $"Branch chance: jumped (sim-distance {e.BranchDistance:0})";
            });
        }

        private void SetJukeboxProbabilityMin(double value)
        {
            if (Math.Abs(_jukeboxSettingsModel.BranchProbabilityMin - value) < 0.0001)
                return;

            _jukeboxSettingsModel.BranchProbabilityMin = value;
            RaisePropertyChanged(nameof(JukeboxBranchProbabilityMinPercent));
            RaisePropertyChanged(nameof(JukeboxBranchProbabilityMinText));
            PersistJukeboxSettings();
        }

        private void SetJukeboxProbabilityMax(double value)
        {
            if (Math.Abs(_jukeboxSettingsModel.BranchProbabilityMax - value) < 0.0001)
                return;

            _jukeboxSettingsModel.BranchProbabilityMax = value;
            RaisePropertyChanged(nameof(JukeboxBranchProbabilityMaxPercent));
            RaisePropertyChanged(nameof(JukeboxBranchProbabilityMaxText));
            PersistJukeboxSettings();
        }

        private void PersistJukeboxSettings()
        {
            _jukeboxSettings.Save(_jukeboxSettingsModel);
            _loopController.InvalidateGraphCache();

            if (LoopEnabled && JukeboxModeEnabled)
                ApplyLoopSettings();
        }

        private void RefreshRingVisualization(string trackId)
        {
            RingGlowFromMs = null;
            RingGlowToMs = null;

            if (string.IsNullOrEmpty(trackId))
            {
                RingSegmentStartsSec = Array.Empty<double>();
                UpdateRingSegmentCountText(Array.Empty<double>());
                return;
            }

            var analysis = AnalysisCache.Load(trackId);

            if (analysis == null)
            {
                RingSegmentStartsSec = Array.Empty<double>();
                UpdateRingSegmentCountText(Array.Empty<double>());
                return;
            }

            RingDurationMs = (long)(analysis.DurationSec * 1000);
            var segments = BuildRingSegments(analysis);
            RingSegmentStartsSec = segments;
            UpdateRingSegmentCountText(segments);
        }

        private static IReadOnlyList<double> BuildRingSegments(TrackAnalysis analysis)
        {
            var sources = new List<double>();

            // Prefer analysis segments (overlapping windows) for a full ring; fall back to sections/beats.
            if (analysis.Segments != null && analysis.Segments.Count >= 8)
            {
                sources.AddRange(analysis.Segments.Select(s => s.Start));
            }
            else if (analysis.Sections != null && analysis.Sections.Count >= 4)
            {
                sources.AddRange(analysis.Sections.Select(s => s.Start));
            }
            else if (analysis.Beats != null)
            {
                sources.AddRange(analysis.Beats.Select(b => b.Start));
            }

            if (sources.Count == 0)
                return Array.Empty<double>();

            sources.Sort();

            const int maxSegments = 64;

            if (sources.Count <= maxSegments)
                return sources;

            var step = sources.Count / (double)maxSegments;
            var reduced = new List<double>();

            for (var i = 0; i < maxSegments; i++)
                reduced.Add(sources[(int)Math.Floor(i * step)]);

            return reduced;
        }

        private void UpdateRingSegmentCountText(IReadOnlyList<double> segments)
        {
            RingSegmentCountText = segments == null || segments.Count == 0
                ? "No segments — analyze track first"
                : $"{segments.Count} ring segments (song map)";
        }

        /// <summary>Invoked when a track finishes without user intervention (non-loop mode).</summary>
        private async void OnTrackEndedNaturally(string trackId)
        {
            _lastEndedTrackId = trackId;

            await RefreshPredictionsAsync();

            if (AutoPlayNext && !_loopController.IsLoopActive && Predictions.Count > 0)
            {
                var top = Predictions[0];
                Log($"Auto-playing top prediction: {top.DisplayName}");
                await PlayTrackAsync(top.TrackId, "prediction-auto");
            }
        }

        #endregion

        #region Next-track prediction

        private async Task RefreshPredictionsAsync()
        {
            var anchor = _lastEndedTrackId ?? _loopController.CurrentTrackId;

            PredictionStatusText = anchor == null
                ? "Ranking your library (no anchor track yet)…"
                : "Ranking candidates…";

            try
            {
                var predictions = await Task.Run(() => _predictor.PredictAsync(anchor, 5));

                RunOnUiThread(() =>
                {
                    Predictions.Clear();

                    foreach (var track in predictions)
                        Predictions.Add(track);

                    PredictionStatusText = predictions.Count == 0
                        ? "No candidates yet — play some music so the listening log has data."
                        : anchor == null
                            ? "Top picks from your history:"
                            : "Predicted next tracks:";
                });
            }
            catch (Exception ex)
            {
                PredictionStatusText = $"Prediction failed: {ex.Message}";
                Log($"Prediction failed: {ex.Message}");
            }
        }

        private async Task PlayPredictionAsync(ScoredTrack track)
        {
            if (track == null)
                return;

            await PlayTrackAsync(track.TrackId, "prediction-pick");
        }

        private void TogglePin(ScoredTrack track)
        {
            if (track == null)
                return;

            track.IsPinned = _predictor.TogglePin(track.TrackId);
            Log(track.IsPinned ? $"Pinned {track.DisplayName}." : $"Unpinned {track.DisplayName}.");

            // Re-rank so the pin bonus is reflected immediately.
            _ = RefreshPredictionsAsync();
        }

        #endregion

        #region Analysis

        private async Task AnalyzeCurrentTrackAsync()
        {
            var trackId = _loopController.CurrentTrackId;

            if (trackId == null)
            {
                AnalysisStatusText = "Play a track first, then analyze it.";
                return;
            }

            ITrackAnalysisProvider provider;

            try
            {
                provider = await _analysisProviderSelector.GetProviderAsync();
            }
            catch (InvalidOperationException ex)
            {
                AnalysisStatusText = ex.Message;
                return;
            }

            IsAnalyzing = true;

            try
            {
                if (provider.Source == AnalysisSource.Local && !provider.IsCached(trackId))
                {
                    // The capture pass records the system mix, and an active loop would corrupt it.
                    if (LoopEnabled)
                    {
                        LoopEnabled = false;
                        Log("Loop disabled during the capture pass.");
                    }

                    Log("Local analysis records the system mix — keep other apps silent until the track ends.");
                }

                var progress = new Progress<string>(message =>
                {
                    AnalysisStatusText = message;
                    Log(message, verbose: true);
                });

                var analysis = await provider.GetAnalysisAsync(trackId, progress, System.Threading.CancellationToken.None);

                AnalysisStatusText = $"Analysis ready: {analysis.Beats.Count} beats, " +
                                     $"{analysis.Segments.Count} segments ({analysis.SourceType}).";
                Log($"Analysis complete for {trackId} via {provider.Source}.");
                OnAnalysisCompleted(trackId, analysis);
            }
            catch (Exception ex)
            {
                AnalysisStatusText = $"Analysis failed: {ex.Message}";
                Log($"Analysis failed: {ex.Message}");
            }
            finally
            {
                IsAnalyzing = false;
            }
        }

        /// <summary>Re-arms the jukebox once analysis lands for the current track.</summary>
        private void OnAnalysisCompleted(string trackId, Model.Prediction.TrackAnalysis analysis)
        {
            RefreshRingVisualization(trackId);

            if (LoopEnabled && JukeboxModeEnabled && trackId == _loopController.CurrentTrackId)
                ApplyLoopSettings();
        }

        #endregion

        private async Task ProbeAnalysisSourceAsync(bool force)
        {
            try
            {
                var source = await _analysisGate.GetAnalysisSourceAsync(force);

                RunOnUiThread(() =>
                {
                    UpdateAnalysisSourceText(source);

                    if (source != AnalysisSource.Unknown)
                        Log($"Analysis gate: using {source} analysis pipeline.");
                });
            }
            catch (Exception ex)
            {
                Log($"Analysis probe failed: {ex.Message}");
            }
        }

        private void UpdateAnalysisSourceText(AnalysisSource source)
        {
            switch (source)
            {
                case AnalysisSource.Spotify:
                    AnalysisSourceText = "Analysis: Spotify audio-analysis API (Path A)";
                    break;
                case AnalysisSource.Local:
                    AnalysisSourceText = "Analysis: local capture + librosa (Path B)";
                    break;
                default:
                    AnalysisSourceText = "Analysis: not probed yet";
                    break;
            }
        }

        #region Export / import

        /// <summary>Bundle of loop profiles + cached analyses for moving between machines.</summary>
        private class LoopDataExport
        {
            public List<LoopProfile> LoopRegions { get; set; } = new List<LoopProfile>();

            public List<TrackAnalysis> Analyses { get; set; } = new List<TrackAnalysis>();
        }

        private void AutoDetectPython()
        {
            if (PythonLauncher.TryAutoDetect(out var path))
            {
                PythonExecutablePath = path;
                Log($"Python auto-detect: {path}");
                Status = "Python interpreter detected and saved.";
                return;
            }

            Status = "Could not auto-detect Python 3. Browse to python.exe or install Python 3.12 + librosa.";
            Log("Python auto-detect failed. Install Python 3 and run: py -3.12 -m pip install librosa soundfile");
        }

        private void BrowsePython()
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Python (python.exe)|python.exe|All files (*.*)|*.*",
                Title = "Select Python interpreter"
            };

            if (dialog.ShowDialog() != true)
                return;

            PythonExecutablePath = dialog.FileName;
            Log($"Python path set: {dialog.FileName}");
            Status = "Python interpreter saved.";
        }

        private void ExportLoopData()
        {
            var dialog = new SaveFileDialog
            {
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                FileName = $"spotifywpf-loop-data-{DateTime.Now:yyyyMMdd-HHmmss}.json"
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                var export = new LoopDataExport
                {
                    LoopRegions = _loopRegionStore.GetAll().Values.ToList()
                };

                if (Directory.Exists(PredictionPaths.AnalysisCacheDirectory))
                {
                    foreach (var file in Directory.GetFiles(PredictionPaths.AnalysisCacheDirectory, "*.json"))
                    {
                        try
                        {
                            var analysis = JsonSerializer.Deserialize<TrackAnalysis>(File.ReadAllText(file));

                            if (analysis != null && !string.IsNullOrEmpty(analysis.TrackId))
                                export.Analyses.Add(analysis);
                        }
                        catch (JsonException)
                        {
                            // Skip unreadable cache entries.
                        }
                    }
                }

                File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(export));
                Log($"Exported {export.LoopRegions.Count} loop profiles and {export.Analyses.Count} analyses.");
                Status = "Loop data exported.";
            }
            catch (Exception ex)
            {
                Status = $"Export failed: {ex.Message}";
                Log($"Export failed: {ex.Message}");
            }
        }

        private void ImportLoopData()
        {
            var dialog = new OpenFileDialog
            {
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*"
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                var import = JsonSerializer.Deserialize<LoopDataExport>(File.ReadAllText(dialog.FileName));

                if (import == null)
                {
                    Status = "Import failed: file was empty.";
                    return;
                }

                _loopRegionStore.ImportAll(import.LoopRegions);

                var analysisCount = 0;

                foreach (var analysis in import.Analyses ?? new List<TrackAnalysis>())
                {
                    if (analysis != null && !string.IsNullOrEmpty(analysis.TrackId))
                    {
                        AnalysisCache.Save(analysis);
                        analysisCount++;
                    }
                }

                Log($"Imported {import.LoopRegions?.Count ?? 0} loop profiles and {analysisCount} analyses.");
                Status = "Loop data imported.";

                RefreshLoopSettingsFromStore(_loopController.CurrentTrackId);
            }
            catch (Exception ex)
            {
                Status = $"Import failed: {ex.Message}";
                Log($"Import failed: {ex.Message}");
            }
        }

        #endregion

        #region Helpers

        private static readonly Regex TrackIdRegex = new Regex("^[a-zA-Z0-9]{22}$", RegexOptions.Compiled);

        /// <summary>Accepts a bare ID, spotify:track: URI, or an open.spotify.com track URL.</summary>
        public static string ParseTrackId(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return null;

            var value = input.Trim();

            if (value.StartsWith("spotify:track:", StringComparison.OrdinalIgnoreCase))
                value = value.Substring("spotify:track:".Length);
            else
            {
                var match = Regex.Match(value, @"open\.spotify\.com/(?:intl-[a-z\-]+/)?track/([a-zA-Z0-9]{22})");
                if (match.Success)
                    value = match.Groups[1].Value;
            }

            return TrackIdRegex.IsMatch(value) ? value : null;
        }

        private static string FormatMs(long ms)
        {
            if (ms < 0)
                ms = 0;

            var time = TimeSpan.FromMilliseconds(ms);
            return time.TotalHours >= 1
                ? time.ToString(@"h\:mm\:ss")
                : time.ToString(@"m\:ss");
        }

        private void Log(string message, bool verbose = false)
        {
            ActivityLog.Log(message, verbose);
        }

        private static void RunOnUiThread(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;

            if (dispatcher == null || dispatcher.CheckAccess())
                action();
            else
                dispatcher.BeginInvoke(action);
        }

        #endregion
    }
}
