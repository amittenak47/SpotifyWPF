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

        private readonly ILoopLabSessionStore _sessionStore;

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
            IJukeboxSettingsStore jukeboxSettings,
            ILoopLabSessionStore sessionStore)
        {
            _loopRegionStore = loopRegionStore;
            _jukeboxSettings = jukeboxSettings;
            _sessionStore = sessionStore;
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
            SessionTracks = new ObservableCollection<LoopLabSessionTrack>();

            try
            {
                RefreshSessionTracks();
            }
            catch (System.Exception ex)
            {
                System.Console.WriteLine($"Failed to load Loop Lab session tracks: {ex.Message}");
            }

            _loopController.LoopEvent += (_, message) => Log(message);
            _loopController.JukeboxJump += OnJukeboxJump;

            _playbackHost.PlayerReady += OnPlayerReady;
            _playbackHost.StateChanged += OnStateChanged;
            _playbackHost.PositionUpdated += OnPositionUpdated;
            _playbackHost.TrackEnded += OnTrackEnded;
            _playbackHost.PlayerError += OnPlayerError;
            _playbackHost.InitializationFailed += OnInitializationFailed;

            TogglePlayPauseCommand = new RelayCommand(async () => await TogglePlayPauseAsync(), CanUsePlayer);
            ReprobeAnalysisCommand = new RelayCommand(async () => await ProbeAnalysisSourceAsync(true));
            AnalyzeTrackCommand = new RelayCommand(async () => await AnalyzeCurrentTrackAsync(),
                () => HasCurrentTrack() && !IsAnalyzing);
            AnalyzeInputCommand = new RelayCommand(async () => await AnalyzeFromInputAsync(), CanAnalyzeInput);
            AnalyzeSessionTrackCommand = new RelayCommand(async () => await AnalyzeSessionTrackAsync(),
                () => SelectedSessionTrack != null && !IsAnalyzing);
            PreviousSessionTrackCommand = new RelayCommand(() => NavigateSessionTrack(-1), () => CanNavigateSessionTrack(-1));
            NextSessionTrackCommand = new RelayCommand(() => NavigateSessionTrack(1), () => CanNavigateSessionTrack(1));
            PlaySessionTrackCommand = new RelayCommand(async () => await PlaySessionTrackAsync(),
                () => SelectedSessionTrack != null && CanUsePlayer());
            RemoveSessionTrackCommand = new RelayCommand(RemoveSelectedSessionTrack,
                () => SelectedSessionTrack != null);
            ClearSessionCommand = new RelayCommand(ClearSession, () => SessionTracks.Count > 0);
            RefreshPredictionsCommand = new RelayCommand(async () => await RefreshPredictionsAsync());
            PlayPredictionCommand = new RelayCommand<ScoredTrack>(
                async track => await PlayPredictionAsync(track));
            TogglePinCommand = new RelayCommand<ScoredTrack>(TogglePin);
            ExportLoopDataCommand = new RelayCommand(ExportLoopData);
            ImportLoopDataCommand = new RelayCommand(ImportLoopData);
            AutoDetectPythonCommand = new RelayCommand(AutoDetectPython);
            BrowsePythonCommand = new RelayCommand(BrowsePython);
            EnterMiniPlayerCommand = new RelayCommand(() => IsMiniPlayerMode = true, () => !IsMiniPlayerMode);
            ExitMiniPlayerCommand = new RelayCommand(() => IsMiniPlayerMode = false, () => IsMiniPlayerMode);
            ToggleRingLockCommand = new RelayCommand<int>(ToggleRingLock);
            ClearRingLocksCommand = new RelayCommand(ClearRingLocks);
            ResetRingPlaysCommand = new RelayCommand(() => RingResetPlaysToken++);

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
            set
            {
                if (Set(ref _trackInput, value))
                    AnalyzeInputCommand?.RaiseCanExecuteChanged();
            }
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
                {
                    RaisePropertyChanged(nameof(PositionText));
                    RaisePropertyChanged(nameof(HasScrubbableTrack));
                }
            }
        }

        private bool _isUserScrubbing;

        private long _scrubPositionMs;

        public double ScrubberPositionMs
        {
            get => _isUserScrubbing ? _scrubPositionMs : PositionMs;
            set
            {
                if (!_isUserScrubbing || DurationMs <= 0)
                    return;

                var ms = (long)Math.Max(0, Math.Min(DurationMs, value));

                if (_scrubPositionMs == ms)
                    return;

                _scrubPositionMs = ms;
                RaisePropertyChanged(nameof(ScrubberPositionMs));
                RaisePropertyChanged(nameof(PositionText));
            }
        }

        public bool HasScrubbableTrack => DurationMs > 0;

        public string PositionText =>
            $"{FormatMs(_isUserScrubbing ? _scrubPositionMs : PositionMs)} / {FormatMs(DurationMs)}";

        private bool _isPaused = true;

        public bool IsPaused
        {
            get => _isPaused;
            set
            {
                if (Set(ref _isPaused, value))
                    RaisePropertyChanged(nameof(ShowPauseIcon));
            }
        }

        /// <summary>True when the transport bar should show the pause icon (actively playing).</summary>
        public bool ShowPauseIcon => !IsPaused && HasCurrentTrack();

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

        public ObservableCollection<LoopLabSessionTrack> SessionTracks { get; }

        private LoopLabSessionTrack _selectedSessionTrack;

        public LoopLabSessionTrack SelectedSessionTrack
        {
            get => _selectedSessionTrack;
            set
            {
                if (Set(ref _selectedSessionTrack, value))
                {
                    if (value != null)
                        TrackInput = value.TrackId;

                    AnalyzeSessionTrackCommand.RaiseCanExecuteChanged();
                    PlaySessionTrackCommand.RaiseCanExecuteChanged();
                    RemoveSessionTrackCommand.RaiseCanExecuteChanged();
                    PreviousSessionTrackCommand.RaiseCanExecuteChanged();
                    NextSessionTrackCommand.RaiseCanExecuteChanged();
                }
            }
        }

        private bool _isBottomPanelExpanded;

        public bool IsBottomPanelExpanded
        {
            get => _isBottomPanelExpanded;
            set => Set(ref _isBottomPanelExpanded, value);
        }

        private bool _isMiniPlayerMode;

        /// <summary>True when the app should show only the jukebox ring (manual mini player mode).</summary>
        public bool IsMiniPlayerMode
        {
            get => _isMiniPlayerMode;
            set
            {
                if (!Set(ref _isMiniPlayerMode, value))
                    return;

                RaisePropertyChanged(nameof(IsFullLayout));
                EnterMiniPlayerCommand.RaiseCanExecuteChanged();
                ExitMiniPlayerCommand.RaiseCanExecuteChanged();
                MessengerInstance.Send(value, MessageType.MiniPlayerModeChanged);
            }
        }

        public bool IsFullLayout => !IsMiniPlayerMode;

        private double _bottomPanelHeight = 220;

        public double BottomPanelHeight
        {
            get => _bottomPanelHeight;
            set => Set(ref _bottomPanelHeight, value);
        }

        public string CurrentTrackId => _currentPlay?.TrackId;

        /// <summary>True while a track is loaded in the player (drives status-bar marquee).</summary>
        public bool IsNowPlaying =>
            !string.IsNullOrEmpty(_currentPlay?.TrackId) &&
            NowPlayingTitle != "Nothing playing";

        public string NowPlayingMarqueeText =>
            string.IsNullOrWhiteSpace(NowPlayingArtist)
                ? NowPlayingTitle
                : $"{NowPlayingTitle} — {NowPlayingArtist}";

        private bool _jukeboxSuppressedForCapture;

        private bool _isAnalyzing;

        public bool IsAnalyzing
        {
            get => _isAnalyzing;
            set
            {
                if (Set(ref _isAnalyzing, value))
                {
                    AnalyzeTrackCommand.RaiseCanExecuteChanged();
                    AnalyzeInputCommand.RaiseCanExecuteChanged();
                    AnalyzeSessionTrackCommand.RaiseCanExecuteChanged();
                }
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

        private string _ringSegmentCountText = "No beat map loaded";

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

        /// <summary>Beat graph rendered by the ring; built service-side, UI state only here.</summary>
        public BeatGraph RingGraph
        {
            get => _ringGraph;
            set => Set(ref _ringGraph, value);
        }

        private BeatGraph _ringGraph;

        /// <summary>Section start times used to tint the ring's beat bars and outer rim.</summary>
        public IReadOnlyList<double> RingSectionStartsSec
        {
            get => _ringSectionStartsSec;
            set => Set(ref _ringSectionStartsSec, value);
        }

        private IReadOnlyList<double> _ringSectionStartsSec = Array.Empty<double>();

        public int RingPlannedFromBeat
        {
            get => _ringPlannedFromBeat;
            set => Set(ref _ringPlannedFromBeat, value);
        }

        private int _ringPlannedFromBeat = -1;

        public int RingPlannedToBeat
        {
            get => _ringPlannedToBeat;
            set => Set(ref _ringPlannedToBeat, value);
        }

        private int _ringPlannedToBeat = -1;

        public JukeboxJumpFlash RingJumpFlash
        {
            get => _ringJumpFlash;
            set => Set(ref _ringJumpFlash, value);
        }

        private JukeboxJumpFlash _ringJumpFlash;

        public IReadOnlyList<BranchLock> RingLockedBranches
        {
            get => _ringLockedBranches;
            set => Set(ref _ringLockedBranches, value);
        }

        private IReadOnlyList<BranchLock> _ringLockedBranches = Array.Empty<BranchLock>();

        public string RingLockCountText
        {
            get => _ringLockCountText;
            set => Set(ref _ringLockCountText, value);
        }

        private string _ringLockCountText = "no locks";

        /// <summary>Bumped by "Reset plays" — the ring clears its coverage bars on change.</summary>
        public int RingResetPlaysToken
        {
            get => _ringResetPlaysToken;
            set => Set(ref _ringResetPlaysToken, value);
        }

        private int _ringResetPlaysToken;

        private bool _jukeboxLocksOnly;

        /// <summary>When true, jukebox jumps only travel the branches locked on the ring.</summary>
        public bool JukeboxLocksOnly
        {
            get => _jukeboxLocksOnly;
            set
            {
                if (Set(ref _jukeboxLocksOnly, value))
                    ApplyLoopSettings();
            }
        }

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

        public RelayCommand TogglePlayPauseCommand { get; }

        public RelayCommand ReprobeAnalysisCommand { get; }

        public RelayCommand AnalyzeTrackCommand { get; }

        public RelayCommand AnalyzeInputCommand { get; }

        public RelayCommand AnalyzeSessionTrackCommand { get; }

        public RelayCommand PreviousSessionTrackCommand { get; }

        public RelayCommand NextSessionTrackCommand { get; }

        public RelayCommand PlaySessionTrackCommand { get; }

        public RelayCommand RemoveSessionTrackCommand { get; }

        public RelayCommand ClearSessionCommand { get; }

        public RelayCommand RefreshPredictionsCommand { get; }

        public RelayCommand<ScoredTrack> PlayPredictionCommand { get; }

        public RelayCommand<ScoredTrack> TogglePinCommand { get; }

        public RelayCommand ExportLoopDataCommand { get; }

        public RelayCommand ImportLoopDataCommand { get; }

        public RelayCommand AutoDetectPythonCommand { get; }

        public RelayCommand BrowsePythonCommand { get; }

        public RelayCommand EnterMiniPlayerCommand { get; }

        public RelayCommand ExitMiniPlayerCommand { get; }

        /// <summary>Ring click: lock/unlock the clicked beat's best branch.</summary>
        public RelayCommand<int> ToggleRingLockCommand { get; }

        public RelayCommand ClearRingLocksCommand { get; }

        public RelayCommand ResetRingPlaysCommand { get; }

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

            TrackInput = trackId;
            await PlayTrackAsync(trackId, "prediction-page");
        }

        private async Task TogglePlayPauseAsync()
        {
            if (!CanUsePlayer())
                return;

            if (!HasCurrentTrack())
            {
                await PlayFromInputAsync();
                return;
            }

            if (IsPaused)
                _playbackHost.Resume();
            else
                _playbackHost.Pause();
        }

        public void BeginScrub()
        {
            if (DurationMs <= 0)
                return;

            _isUserScrubbing = true;
            _scrubPositionMs = PositionMs;
            RaisePropertyChanged(nameof(ScrubberPositionMs));
        }

        public void EndScrub()
        {
            if (!_isUserScrubbing)
                return;

            _isUserScrubbing = false;
            PositionMs = _scrubPositionMs;
            _playbackHost.Seek(_scrubPositionMs);
            RaisePropertyChanged(nameof(ScrubberPositionMs));
            RaisePropertyChanged(nameof(PositionText));
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
                TogglePlayPauseCommand.RaiseCanExecuteChanged();
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
                RaisePropertyChanged(nameof(ShowPauseIcon));
                RaisePropertyChanged(nameof(HasScrubbableTrack));
                if (!_isUserScrubbing)
                    RaisePropertyChanged(nameof(ScrubberPositionMs));
                NotifyNowPlayingDisplayChanged();
            });
        }

        private void NotifyNowPlayingDisplayChanged()
        {
            RaisePropertyChanged(nameof(IsNowPlaying));
            RaisePropertyChanged(nameof(NowPlayingMarqueeText));
        }

        private void OnPositionUpdated(object sender, PositionSnapshot position)
        {
            RunOnUiThread(() =>
            {
                if (!_isUserScrubbing)
                {
                    PositionMs = position.PositionMs;
                    RaisePropertyChanged(nameof(ScrubberPositionMs));
                }

                IsPaused = position.Paused;
                RaisePropertyChanged(nameof(ShowPauseIcon));

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
                NotifyNowPlayingDisplayChanged();
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

            AnalyzeTrackCommand.RaiseCanExecuteChanged();

            AnalysisStatusText = AnalysisCache.Exists(state.TrackId)
                ? "Analysis cached for this track."
                : "No analysis for this track yet.";

            UpsertSessionTrack(state.TrackId, state.TrackName, state.ArtistNames);
            RefreshRingVisualization(state.TrackId);
            ApplyLoopSettings();
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
                // Infinite Jukebox is always the default; profile is applied in ApplyLoopSettings.
                JukeboxLocksOnly = profile != null && profile.LocksOnly;
            }
            finally
            {
                _suppressLoopApply = false;
            }

            RefreshRingLocks(profile);
        }

        private void ApplyLoopSettings()
        {
            if (_suppressLoopApply)
                return;

            var trackId = _loopController.CurrentTrackId;

            if (trackId == null)
                return;

            var profile = _loopController.GetProfileForTrack(trackId);
            profile.Enabled = !_jukeboxSuppressedForCapture;
            profile.Mode = LoopModes.Jukebox;
            profile.LocksOnly = JukeboxLocksOnly;

            _loopController.ApplyProfile(profile);
        }

        private void OnJukeboxJump(object sender, JukeboxJumpEventArgs e)
        {
            RunOnUiThread(() =>
            {
                if (e.IsPlanned)
                {
                    RingPlannedFromBeat = e.FromBeatIndex;
                    RingPlannedToBeat = e.ToBeatIndex;
                }
                else
                {
                    RingJumpFlash = new JukeboxJumpFlash(e.FromBeatIndex, e.ToBeatIndex);
                }

                JukeboxBranchChanceText = e.IsPlanned
                    ? $"Branch chance: planning jump (sim-distance {e.BranchDistance:0})"
                    : $"Branch chance: jumped (sim-distance {e.BranchDistance:0})";
            });
        }

        private void ToggleRingLock(int beatIndex)
        {
            var graph = RingGraph;
            var trackId = _loopController.CurrentTrackId;

            if (graph == null || trackId == null || beatIndex < 0 || beatIndex >= graph.Beats.Count)
                return;

            var profile = _loopController.GetProfileForTrack(trackId);

            if (profile.LockedBranches == null)
                profile.LockedBranches = new List<BranchLock>();

            var existing = profile.LockedBranches
                .Where(l => l.FromBeatIndex == beatIndex)
                .ToList();

            if (existing.Count > 0)
            {
                foreach (var branchLock in existing)
                    profile.LockedBranches.Remove(branchLock);

                Log($"Ring: unlocked branch at beat {beatIndex}.");
            }
            else
            {
                var best = graph.Beats[beatIndex].Neighbors
                    .OrderBy(edge => edge.Distance)
                    .FirstOrDefault();

                if (best == null)
                    return;

                profile.LockedBranches.Add(new BranchLock
                {
                    FromBeatIndex = beatIndex,
                    ToBeatIndex = best.DestinationIndex
                });

                Log($"Ring: locked branch beat {beatIndex} → {best.DestinationIndex}.");
            }

            _loopController.ApplyProfile(profile);
            RefreshRingLocks(profile);
        }

        private void ClearRingLocks()
        {
            var trackId = _loopController.CurrentTrackId;

            if (trackId == null)
                return;

            var profile = _loopController.GetProfileForTrack(trackId);

            if (profile.LockedBranches == null || profile.LockedBranches.Count == 0)
                return;

            profile.LockedBranches.Clear();
            _loopController.ApplyProfile(profile);
            RefreshRingLocks(profile);
            Log("Ring: cleared all locked branches.");
        }

        private void RefreshRingLocks(LoopProfile profile)
        {
            RingLockedBranches = profile?.LockedBranches?.ToList()
                                 ?? (IReadOnlyList<BranchLock>)Array.Empty<BranchLock>();
            RingLockCountText = RingLockedBranches.Count == 0
                ? "no locks"
                : $"{RingLockedBranches.Count} locked";
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

            if (!_jukeboxSuppressedForCapture && _loopController.CurrentTrackId != null)
                ApplyLoopSettings();
        }

        private void RefreshRingVisualization(string trackId)
        {
            RingPlannedFromBeat = -1;
            RingPlannedToBeat = -1;

            if (string.IsNullOrEmpty(trackId))
            {
                RingGraph = null;
                RingSectionStartsSec = Array.Empty<double>();
                RingSegmentCountText = "No track playing";
                return;
            }

            var analysis = AnalysisCache.Load(trackId);

            if (analysis == null)
            {
                RingGraph = null;
                RingSectionStartsSec = Array.Empty<double>();
                RingSegmentCountText = "No beat map — analyze track first";
                return;
            }

            RingDurationMs = (long)(analysis.DurationSec * 1000);
            RingSectionStartsSec = analysis.Sections != null && analysis.Sections.Count > 0
                ? analysis.Sections.Select(s => s.Start).ToList()
                : (IReadOnlyList<double>)Array.Empty<double>();
            RingSegmentCountText = "Building beat graph…";

            // The graph is O(beats²) to build; keep the UI thread free.
            Task.Run(() => _loopController.GetGraphForTrack(trackId)).ContinueWith(task =>
                RunOnUiThread(() =>
                {
                    if (trackId != _loopController.CurrentTrackId)
                        return;

                    var graph = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
                    RingGraph = graph;
                    RingSegmentCountText = graph == null
                        ? "No beat map — analyze track first"
                        : $"{graph.Beats.Count} beats · {graph.TotalBranchCount} branches";
                }));
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

        #region Session tracks

        private void RefreshSessionTracks()
        {
            SessionTracks.Clear();

            foreach (var track in _sessionStore.Tracks)
                SessionTracks.Add(track);

            ClearSessionCommand.RaiseCanExecuteChanged();
            PreviousSessionTrackCommand.RaiseCanExecuteChanged();
            NextSessionTrackCommand.RaiseCanExecuteChanged();
        }

        private bool CanAnalyzeInput()
        {
            return !IsAnalyzing && ParseTrackId(TrackInput) != null;
        }

        private async Task AnalyzeFromInputAsync()
        {
            var trackId = ParseTrackId(TrackInput);

            if (trackId == null)
            {
                AnalysisStatusText = "Enter a valid track ID, URI, or open.spotify.com link.";
                return;
            }

            TrackInput = trackId;
            await AnalyzeTrackByIdAsync(trackId, trackId);
        }

        private int GetSessionTrackIndex()
        {
            if (SelectedSessionTrack != null)
                return SessionTracks.IndexOf(SelectedSessionTrack);

            var currentId = _loopController.CurrentTrackId ?? _currentPlay?.TrackId;

            if (currentId == null)
                return -1;

            for (var i = 0; i < SessionTracks.Count; i++)
            {
                if (SessionTracks[i].TrackId == currentId)
                    return i;
            }

            return -1;
        }

        private bool CanNavigateSessionTrack(int delta)
        {
            if (SessionTracks.Count == 0)
                return false;

            var index = GetSessionTrackIndex();
            var targetIndex = index < 0
                ? (delta > 0 ? 0 : SessionTracks.Count - 1)
                : index + delta;

            return targetIndex >= 0 && targetIndex < SessionTracks.Count;
        }

        private void NavigateSessionTrack(int delta)
        {
            if (SessionTracks.Count == 0)
                return;

            var index = GetSessionTrackIndex();
            var targetIndex = index < 0
                ? (delta > 0 ? 0 : SessionTracks.Count - 1)
                : index + delta;

            if (targetIndex < 0 || targetIndex >= SessionTracks.Count)
                return;

            SelectedSessionTrack = SessionTracks[targetIndex];
        }

        private void UpsertSessionTrack(string trackId, string title, string artist)
        {
            if (string.IsNullOrEmpty(trackId))
                return;

            _sessionStore.AddOrUpdate(new LoopLabSessionTrack
            {
                TrackId = trackId,
                Title = title,
                Artist = artist,
                AnalysisStatus = AnalysisCache.Exists(trackId)
                    ? SessionAnalysisStatus.Ready
                    : SessionAnalysisStatus.Pending
            });

            RefreshSessionTracks();
        }

        private void UpdateSessionTrackAnalysisStatus(string trackId, SessionAnalysisStatus status)
        {
            if (string.IsNullOrEmpty(trackId))
                return;

            var existing = _sessionStore.Tracks.FirstOrDefault(t => t.TrackId == trackId);

            if (existing == null)
            {
                _sessionStore.AddOrUpdate(new LoopLabSessionTrack
                {
                    TrackId = trackId,
                    AnalysisStatus = status
                });
            }
            else
            {
                existing.AnalysisStatus = status;
                _sessionStore.AddOrUpdate(existing);
            }

            RefreshSessionTracks();
        }

        private async Task AnalyzeSessionTrackAsync()
        {
            var track = SelectedSessionTrack;

            if (track == null)
                return;

            await AnalyzeTrackByIdAsync(track.TrackId, track.DisplayName);
        }

        private async Task PlaySessionTrackAsync()
        {
            var track = SelectedSessionTrack;

            if (track == null)
                return;

            TrackInput = track.TrackId;
            await PlayTrackAsync(track.TrackId, "session-track");
        }

        private void RemoveSelectedSessionTrack()
        {
            var track = SelectedSessionTrack;

            if (track == null)
                return;

            _sessionStore.Remove(track.TrackId);
            SelectedSessionTrack = null;
            RefreshSessionTracks();
            Log($"Removed {track.DisplayName} from session.");
        }

        private void ClearSession()
        {
            foreach (var trackId in _sessionStore.GetTrackIds().ToList())
                _sessionStore.Remove(trackId);

            SelectedSessionTrack = null;
            RefreshSessionTracks();
            Log("Cleared Loop Lab session tracks.");
        }

        private async Task AnalyzeTrackByIdAsync(string trackId, string displayName)
        {
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

            if (provider.Source == AnalysisSource.Local && !provider.IsCached(trackId) &&
                trackId != _loopController.CurrentTrackId)
            {
                AnalysisStatusText = "Local analysis requires playing the track — select it and press Play first.";
                return;
            }

            IsAnalyzing = true;
            UpdateSessionTrackAnalysisStatus(trackId, SessionAnalysisStatus.Analyzing);

            try
            {
                if (provider.Source == AnalysisSource.Local && !provider.IsCached(trackId) &&
                    trackId == _loopController.CurrentTrackId)
                {
                    _jukeboxSuppressedForCapture = true;
                    ApplyLoopSettings();
                    Log("Jukebox disabled during the capture pass.");
                }

                if (provider.Source == AnalysisSource.Local && !provider.IsCached(trackId))
                    Log("Local analysis records the system mix — keep other apps silent until the track ends.");

                var progress = new Progress<string>(message =>
                {
                    AnalysisStatusText = message;
                    Log(message, verbose: true);
                });

                var analysis = await provider.GetAnalysisAsync(trackId, progress, System.Threading.CancellationToken.None);

                AnalysisStatusText = $"Analysis ready: {analysis.Beats.Count} beats, " +
                                     $"{analysis.Segments.Count} segments ({analysis.SourceType}).";
                Log($"Analysis complete for {displayName ?? trackId} via {provider.Source}.");
                OnAnalysisCompleted(trackId, analysis);
            }
            catch (Exception ex)
            {
                AnalysisStatusText = $"Analysis failed: {ex.Message}";
                Log($"Analysis failed: {ex.Message}");
                UpdateSessionTrackAnalysisStatus(trackId, SessionAnalysisStatus.Failed);
            }
            finally
            {
                IsAnalyzing = false;

                if (_jukeboxSuppressedForCapture)
                {
                    _jukeboxSuppressedForCapture = false;
                    ApplyLoopSettings();
                }
            }
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

            await AnalyzeTrackByIdAsync(trackId, NowPlayingTitle);
        }

        /// <summary>Re-arms the jukebox once analysis lands for the current track.</summary>
        private void OnAnalysisCompleted(string trackId, Model.Prediction.TrackAnalysis analysis)
        {
            RefreshRingVisualization(trackId);

            if (trackId == _loopController.CurrentTrackId)
                ApplyLoopSettings();

            if (trackId == _loopController.CurrentTrackId && NowPlayingTitle != "Nothing playing")
                UpsertSessionTrack(trackId, NowPlayingTitle, NowPlayingArtist);
            else
                UpdateSessionTrackAnalysisStatus(trackId, SessionAnalysisStatus.Ready);
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
                var sessionIds = new HashSet<string>(_sessionStore.GetTrackIds());
                var export = new LoopDataExport
                {
                    LoopRegions = _loopRegionStore.GetAll().Values
                        .Where(p => p != null && sessionIds.Contains(p.TrackId))
                        .ToList()
                };

                if (Directory.Exists(PredictionPaths.AnalysisCacheDirectory))
                {
                    foreach (var file in Directory.GetFiles(PredictionPaths.AnalysisCacheDirectory, "*.json"))
                    {
                        try
                        {
                            var analysis = JsonSerializer.Deserialize<TrackAnalysis>(File.ReadAllText(file));

                            if (analysis != null && !string.IsNullOrEmpty(analysis.TrackId) &&
                                sessionIds.Contains(analysis.TrackId))
                                export.Analyses.Add(analysis);
                        }
                        catch (JsonException)
                        {
                            // Skip unreadable cache entries.
                        }
                    }
                }

                File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(export));
                Log($"Exported {export.LoopRegions.Count} loop profiles and {export.Analyses.Count} analyses for {sessionIds.Count} session tracks.");
                Status = "Session loop data exported.";
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
                        _sessionStore.AddOrUpdate(new LoopLabSessionTrack
                        {
                            TrackId = analysis.TrackId,
                            AnalysisStatus = SessionAnalysisStatus.Ready
                        });
                    }
                }

                foreach (var profile in import.LoopRegions ?? new List<LoopProfile>())
                {
                    if (profile != null && !string.IsNullOrEmpty(profile.TrackId))
                    {
                        _sessionStore.AddOrUpdate(new LoopLabSessionTrack
                        {
                            TrackId = profile.TrackId,
                            AnalysisStatus = AnalysisCache.Exists(profile.TrackId)
                                ? SessionAnalysisStatus.Ready
                                : SessionAnalysisStatus.Pending
                        });
                    }
                }

                RefreshSessionTracks();
                Log($"Imported {import.LoopRegions?.Count ?? 0} loop profiles and {analysisCount} analyses.");
                Status = "Session loop data imported.";

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
