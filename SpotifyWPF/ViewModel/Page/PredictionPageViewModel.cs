using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Command;
using Microsoft.Win32;
using SpotifyWPF.Model;
using SpotifyWPF.Model.Lyrics;
using SpotifyWPF.Model.Prediction;
using SpotifyWPF.Service;
using SpotifyWPF.Service.Audio;
using SpotifyWPF.Service.Lyrics;
using SpotifyWPF.Service.Playback;
using SpotifyWPF.Service.Prediction;
using SpotifyWPF.Service.Visual;
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

        private readonly IJukeboxTransport _transport;

        private readonly JukeboxTransportRouter _transportRouter;

        private readonly ISpotifyPlaybackService _playbackService;

        private readonly IAnalysisGate _analysisGate;

        private readonly IListeningLogService _listeningLog;

        private readonly ILoopController _loopController;

        private readonly IAnalysisProviderSelector _analysisProviderSelector;

        private readonly INextTrackPredictor _predictor;

        private readonly ILoopRegionStore _loopRegionStore;

        private readonly IJukeboxSettingsStore _jukeboxSettings;

        private readonly ILoopLabSessionStore _sessionStore;

        private readonly IVisualEffectsStore _visualEffectsStore;

        private readonly IPlaylistLocalStore _playlistLocalStore;

        private readonly ILyricsService _lyricsService;

        private readonly VisualEffectsSettings _visualEffectsSettings;

        /// <summary>Shared beat/energy state feeding the plasma equalizer and fractal background.</summary>
        private readonly VisualEnergyState _visualEnergy = new VisualEnergyState();

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
            IJukeboxTransport transport,
            ISpotifyPlaybackService playbackService,
            IAnalysisGate analysisGate,
            IListeningLogService listeningLog,
            ILoopController loopController,
            IAnalysisProviderSelector analysisProviderSelector,
            INextTrackPredictor predictor,
            ILoopRegionStore loopRegionStore,
            IJukeboxSettingsStore jukeboxSettings,
            ILoopLabSessionStore sessionStore,
            IVisualEffectsStore visualEffectsStore,
            IPlaylistLocalStore playlistLocalStore,
            ILyricsService lyricsService)
        {
            _loopRegionStore = loopRegionStore;
            _jukeboxSettings = jukeboxSettings;
            _sessionStore = sessionStore;
            _visualEffectsStore = visualEffectsStore;
            _playlistLocalStore = playlistLocalStore;
            _lyricsService = lyricsService ?? throw new ArgumentNullException(nameof(lyricsService));
            _visualEffectsSettings = visualEffectsStore.Get();
            _visualEffectsStore.SettingsChanged += (_, __) =>
            {
                RaisePropertyChanged(nameof(IsFractalBackgroundEnabled));
                RaisePropertyChanged(nameof(ShowStatusOverlay));
                RaisePropertyChanged(nameof(SelectedEqualizerPreset));
            };
            _jukeboxSettingsModel = _jukeboxSettings.Get();
            _spotify = spotify;
            _playbackHost = playbackHost;
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _transportRouter = transport as JukeboxTransportRouter
                ?? throw new ArgumentException("Expected JukeboxTransportRouter.", nameof(transport));
            _playbackService = playbackService;
            _analysisGate = analysisGate;
            _listeningLog = listeningLog;
            _loopController = loopController;
            _analysisProviderSelector = analysisProviderSelector;
            _predictor = predictor;
            _weights = predictor.GetWeights();

            ActivityLog = new ActivityLogViewModel { NewestFirst = true };
            SessionTracks = new ObservableCollection<LoopLabSessionTrack>();
            LibraryPlaylists = new ObservableCollection<PlaylistCacheItem>();
            TrackSearchResults = new ObservableCollection<TrackSearchHit>();

            try
            {
                var purged = WavCaptureValidator.PurgeIncompleteArtifacts();
                if (purged > 0)
                    System.Console.WriteLine($"Purged {purged} incomplete capture/analysis artifact(s) on startup.");
                ReloadSessionTracks(silent: true);
                RefreshLibraryPlaylists();
            }
            catch (System.Exception ex)
            {
                System.Console.WriteLine($"Failed to load Loop Lab session tracks: {ex.Message}");
            }

            _loopController.LoopEvent += (_, message) =>
            {
                Log(message);

                if (message != null &&
                    (message.IndexOf("preference", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     message.IndexOf("jumped beat", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    RaisePropertyChanged(nameof(BranchPreferenceStatusText));
                    ClearBranchPreferencesCommand?.RaiseCanExecuteChanged();
                }
            };
            _loopController.JukeboxJump += OnJukeboxJump;

            _playbackHost.PlayerReady += OnPlayerReady;
            _playbackHost.PlayerError += OnPlayerError;
            _playbackHost.InitializationFailed += OnInitializationFailed;
            // Position/state/end come from the active transport (Spotify or local WAV).
            _transport.StateChanged += OnStateChanged;
            _transport.PositionUpdated += OnPositionUpdated;
            _transport.TrackEnded += OnTrackEnded;

            ApplyPlaybackSourceFromSettings();

            TogglePlayPauseCommand = new RelayCommand(async () => await TogglePlayPauseAsync(), CanPlayTransport);
            ReprobeAnalysisCommand = new RelayCommand(async () => await ProbeAnalysisSourceAsync(true));
            AnalyzeTrackCommand = new RelayCommand(async () => await AnalyzeCurrentTrackAsync(),
                () => HasCurrentTrack() && !IsAnalyzing);
            AnalyzeInputCommand = new RelayCommand(async () => await AnalyzeFromInputAsync(), CanAnalyzeInput);
            PlayFromInputCommand = new RelayCommand(async () => await PlayFromInputAsync(), CanPlayFromInput);
            AnalyzeSessionTrackCommand = new RelayCommand<LoopLabSessionTrack>(
                async track => await AnalyzeSessionTrackAsync(track),
                track => (track ?? SelectedSessionTrack) != null && !IsAnalyzing);
            PreviousSessionTrackCommand = new RelayCommand(async () => await NavigateAndPlaySessionTrackAsync(-1), () => CanNavigateSessionTrack(-1));
            NextSessionTrackCommand = new RelayCommand(async () => await NavigateAndPlaySessionTrackAsync(1), () => CanNavigateSessionTrack(1));
            ToggleSessionRepeatCommand = new RelayCommand(CycleSessionRepeatMode);
            ToggleLibrarySidebarCommand = new RelayCommand(() => IsLibrarySidebarOpen = !IsLibrarySidebarOpen);
            RefreshLibraryPlaylistsCommand = new RelayCommand(RefreshLibraryPlaylists);
            AddLibraryPlaylistToSessionCommand = new RelayCommand<PlaylistCacheItem>(
                async playlist => await AddLibraryPlaylistToSessionAsync(playlist),
                playlist => playlist != null && _spotify.Api != null);
            SelectTrackSearchHitCommand = new RelayCommand<TrackSearchHit>(
                async hit => await SelectTrackSearchHitAsync(hit),
                hit => hit != null && !string.IsNullOrWhiteSpace(hit.Id));
            PlaySessionTrackCommand = new RelayCommand<LoopLabSessionTrack>(
                async track => await PlaySessionTrackAsync(track),
                track => (track ?? SelectedSessionTrack) != null && CanPlayTransport());
            AbortSessionWorkCommand = new RelayCommand(async () => await AbortSessionWorkAsync(), CanAbortSessionWork);
            RemoveSessionTrackCommand = new RelayCommand(RemoveSelectedSessionTrack,
                () => SelectedSessionTrack != null);
            RefreshSessionCommand = new RelayCommand(() => ReloadSessionTracks(), () => !IsAnalyzing);
            DeleteSessionCacheCommand = new RelayCommand(DeleteSelectedSessionCache, CanDeleteSessionCache);
            DeleteSessionCapturesCommand = new RelayCommand(DeleteSelectedSessionCaptures, CanDeleteSessionCaptures);
            DeleteSessionAnalysisCommand = new RelayCommand(DeleteSelectedSessionAnalysis, CanDeleteSessionAnalysis);
            DeleteSessionTuneCommand = new RelayCommand(DeleteSelectedSessionTune, () => SelectedSessionTrack != null);
            ClearSessionCommand = new RelayCommand(ClearSession, () => SessionTracks.Count > 0);
            ExportSessionAudioCommand = new RelayCommand(ExportSessionAudio, CanExportSessionAudio);
            ExportSessionAnalysisCommand = new RelayCommand(ExportLoopData);
            ExportSessionBothCommand = new RelayCommand(ExportSessionBoth, () => SessionTracks.Count > 0);
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
            ToggleRingLockCommand = new RelayCommand<RingBranchClick>(ToggleRingLock);
            StretchRingModifierCommand = new RelayCommand<RingModifierStretch>(StretchRingModifier);
            CycleRingModifierCommand = new RelayCommand<RingBranchClick>(CycleRingModifier);
            RingScrubToCommand = new RelayCommand<long>(ScrubToPositionMs, ms => DurationMs > 0 && CanPlayTransport());
            EndRingScrubCommand = new RelayCommand(EndScrub);
            HoldRingSpeedCommand = new RelayCommand<bool>(SetRingHoldSpeed);
            SetRingPlaybackRateCommand = new RelayCommand<double>(SetRingPlaybackRate);
            ClearRingLocksCommand = new RelayCommand(ClearRingLocks);
            ClearBranchPreferencesCommand = new RelayCommand(ClearBranchPreferences,
                () => _loopController.PreferenceEdgeCount > 0);
            ResetRingPlaysCommand = new RelayCommand(() => RingResetPlaysToken++);
            StopPlaybackCommand = new RelayCommand(async () => await StopPlaybackAsync(), CanStopPlayback);
            LoadBranchPresetCommand = new RelayCommand(LoadSelectedBranchPreset, CanLoadBranchPreset);
            SaveBranchPresetCommand = new RelayCommand(SaveBranchPreset, CanSaveBranchPreset);

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
                {
                    AnalyzeInputCommand?.RaiseCanExecuteChanged();
                    PlayFromInputCommand?.RaiseCanExecuteChanged();
                    _ = RunTrackSearchAsync(value);
                }
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

        private SyncedLyrics _currentLyrics;

        public SyncedLyrics CurrentLyrics
        {
            get => _currentLyrics;
            private set
            {
                if (Set(ref _currentLyrics, value))
                {
                    RaisePropertyChanged(nameof(HasLyrics));
                    RaisePropertyChanged(nameof(LyricsStatusText));
                    UpdateActiveLyricLine(PositionMs);
                }
            }
        }

        public bool HasLyrics => CurrentLyrics != null &&
                                 (CurrentLyrics.HasSyncedLines || !string.IsNullOrWhiteSpace(CurrentLyrics.PlainLyrics));

        public bool ShowLyricsPanel
        {
            get => _jukeboxSettingsModel.ShowLyrics;
            set
            {
                if (_jukeboxSettingsModel.ShowLyrics == value)
                    return;
                _jukeboxSettingsModel.ShowLyrics = value;
                // Navigator/UI only — do not rebuild the beat graph.
                PersistJukeboxSettings();
                RaisePropertyChanged(nameof(ShowLyricsPanel));

                if (value && !string.IsNullOrEmpty(CurrentTrackId))
                {
                    _ = LoadLyricsForTrackAsync(CurrentTrackId, NowPlayingTitle, NowPlayingArtist, DurationMs);
                }
                else if (!value)
                {
                    CurrentLyrics = null;
                    LyricsStatusText = "Lyrics hidden.";
                    _loopController.SetLyricFlowContext(null);
                }
            }
        }

        public string LyricPhraseWeightText => $"{LyricPhraseWeight:0.00}";

        public double LyricPhraseWeight
        {
            get => _jukeboxSettingsModel.LyricPhraseWeight;
            set
            {
                var clamped = Math.Max(0, Math.Min(2, value));
                if (Math.Abs(_jukeboxSettingsModel.LyricPhraseWeight - clamped) < 0.001)
                    return;
                _jukeboxSettingsModel.LyricPhraseWeight = clamped;
                PersistJukeboxSettings();
                RaisePropertyChanged(nameof(LyricPhraseWeight));
                RaisePropertyChanged(nameof(LyricPhraseWeightText));
                PushLyricFlowToNavigator();
            }
        }

        public bool LyricFlowPhraseCuts
        {
            get => _jukeboxSettingsModel.LyricFlowPhraseCuts;
            set
            {
                if (_jukeboxSettingsModel.LyricFlowPhraseCuts == value)
                    return;
                _jukeboxSettingsModel.LyricFlowPhraseCuts = value;
                PersistJukeboxSettings();
                RaisePropertyChanged(nameof(LyricFlowPhraseCuts));
                PushLyricFlowToNavigator();
            }
        }

        public bool LyricFlowSameSection
        {
            get => _jukeboxSettingsModel.LyricFlowSameSection;
            set
            {
                if (_jukeboxSettingsModel.LyricFlowSameSection == value)
                    return;
                _jukeboxSettingsModel.LyricFlowSameSection = value;
                PersistJukeboxSettings();
                RaisePropertyChanged(nameof(LyricFlowSameSection));
                PushLyricFlowToNavigator();
            }
        }

        public bool LyricFlowBlockClean
        {
            get => _jukeboxSettingsModel.LyricFlowBlockClean;
            set
            {
                if (_jukeboxSettingsModel.LyricFlowBlockClean == value)
                    return;
                _jukeboxSettingsModel.LyricFlowBlockClean = value;
                PersistJukeboxSettings();
                RaisePropertyChanged(nameof(LyricFlowBlockClean));
                PushLyricFlowToNavigator();
            }
        }

        private string _lyricsStatusText = "No lyrics loaded.";

        public string LyricsStatusText
        {
            get => _lyricsStatusText;
            private set => Set(ref _lyricsStatusText, value);
        }

        public ObservableCollection<LyricDisplayLine> LyricDisplayLines { get; } =
            new ObservableCollection<LyricDisplayLine>();

        private string _activeLyricText = string.Empty;

        public string ActiveLyricText
        {
            get => _activeLyricText;
            private set => Set(ref _activeLyricText, value);
        }

        private string _previousLyricText = string.Empty;

        public string PreviousLyricText
        {
            get => _previousLyricText;
            private set => Set(ref _previousLyricText, value);
        }

        private string _nextLyricText = string.Empty;

        public string NextLyricText
        {
            get => _nextLyricText;
            private set => Set(ref _nextLyricText, value);
        }

        private int _activeLyricIndex = -1;

        private CancellationTokenSource _lyricsLoadCts;

        private long _positionMs;

        public long PositionMs
        {
            get => _positionMs;
            set
            {
                if (Set(ref _positionMs, value))
                {
                    RaisePropertyChanged(nameof(PositionText));
                    _visualEnergy.SetTransport(value, IsPaused);
                    UpdateActiveLyricLine(value);
                }
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
                {
                    RaisePropertyChanged(nameof(ShowPauseIcon));
                    RaisePropertyChanged(nameof(SessionPlayButtonText));
                    RaisePropertyChanged(nameof(SessionActiveTrackId));
                    _visualEnergy.SetTransport(PositionMs, value);
                }
            }
        }

        /// <summary>Shared energy source consumed by the equalizer and fractal background.</summary>
        public IVisualEnergyProvider VisualEnergy => _visualEnergy;

        /// <summary>Fractal background toggle; persisted, defaults to off (pure black).</summary>
        public bool IsFractalBackgroundEnabled
        {
            get => _visualEffectsSettings.FractalBackgroundEnabled;
            set
            {
                if (_visualEffectsSettings.FractalBackgroundEnabled == value)
                    return;

                _visualEffectsSettings.FractalBackgroundEnabled = value;
                _visualEffectsStore.Save(_visualEffectsSettings);
                RaisePropertyChanged();
            }
        }

        /// <summary>When enabled, status/HUD info is available as a hover box under the title.</summary>
        public bool ShowStatusOverlay
        {
            get => _visualEffectsSettings.ShowStatusOverlay;
            set
            {
                if (_visualEffectsSettings.ShowStatusOverlay == value)
                    return;

                _visualEffectsSettings.ShowStatusOverlay = value;
                _visualEffectsStore.Save(_visualEffectsSettings);
                RaisePropertyChanged();
            }
        }

        public ObservableCollection<string> EqualizerPresetOptions { get; } =
            new ObservableCollection<string> { "Cascading bars", "Wave ring" };

        /// <summary>Outer-ring equalizer look (Window / Preferences).</summary>
        public string SelectedEqualizerPreset
        {
            get => EqualizerPresetToLabel(_visualEffectsSettings.EqualizerPreset);
            set
            {
                var mode = LabelToEqualizerPreset(value);

                if (string.Equals(_visualEffectsSettings.EqualizerPreset, mode, StringComparison.OrdinalIgnoreCase))
                    return;

                _visualEffectsSettings.EqualizerPreset = mode;
                _visualEffectsStore.Save(_visualEffectsSettings);
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(EqualizerPresetKey));
            }
        }

        /// <summary>Raw preset key bound into the ring canvas (<c>bars</c> / <c>wave-ring</c>).</summary>
        public string EqualizerPresetKey =>
            string.IsNullOrWhiteSpace(_visualEffectsSettings.EqualizerPreset)
                ? "bars"
                : _visualEffectsSettings.EqualizerPreset;

        private static string EqualizerPresetToLabel(string mode)
        {
            if (string.Equals(mode, "wave-ring", StringComparison.OrdinalIgnoreCase))
                return "Wave ring";

            return "Cascading bars";
        }

        private static string LabelToEqualizerPreset(string label)
        {
            if (string.Equals(label, "Wave ring", StringComparison.OrdinalIgnoreCase))
                return "wave-ring";

            return "bars";
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

        public ObservableCollection<PlaylistCacheItem> LibraryPlaylists { get; }

        public ObservableCollection<TrackSearchHit> TrackSearchResults { get; }

        private PlaylistCacheItem _selectedLibraryPlaylist;

        public PlaylistCacheItem SelectedLibraryPlaylist
        {
            get => _selectedLibraryPlaylist;
            set => Set(ref _selectedLibraryPlaylist, value);
        }

        private bool _isLibrarySidebarOpen;

        public bool IsLibrarySidebarOpen
        {
            get => _isLibrarySidebarOpen;
            set
            {
                if (Set(ref _isLibrarySidebarOpen, value) && value)
                    RefreshLibraryPlaylists();
            }
        }

        private SessionRepeatMode _sessionRepeatMode = SessionRepeatMode.Off;

        /// <summary>Off → One (current track) → All (session wrap). Click cycles.</summary>
        public SessionRepeatMode SessionRepeatMode
        {
            get => _sessionRepeatMode;
            set
            {
                if (Set(ref _sessionRepeatMode, value))
                {
                    RaisePropertyChanged(nameof(SessionRepeatEnabled));
                    RaisePropertyChanged(nameof(SessionRepeatGlyph));
                    RaisePropertyChanged(nameof(SessionRepeatToolTip));
                    RaisePropertyChanged(nameof(IsSessionRepeatAccent));
                    PreviousSessionTrackCommand?.RaiseCanExecuteChanged();
                    NextSessionTrackCommand?.RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>True when repeat is One or All.</summary>
        public bool SessionRepeatEnabled
        {
            get => SessionRepeatMode != SessionRepeatMode.Off;
            set => SessionRepeatMode = value ? SessionRepeatMode.All : SessionRepeatMode.Off;
        }

        public string SessionRepeatGlyph
        {
            get
            {
                switch (SessionRepeatMode)
                {
                    case SessionRepeatMode.One:
                        return "\uE8ED";
                    case SessionRepeatMode.All:
                        return "\uE8EE";
                    default:
                        return "\uE8EE";
                }
            }
        }

        public string SessionRepeatToolTip
        {
            get
            {
                switch (SessionRepeatMode)
                {
                    case SessionRepeatMode.One:
                        return "Repeat one — replay the current session track";
                    case SessionRepeatMode.All:
                        return "Repeat all — wrap through the session list";
                    default:
                        return "Repeat off — stop at list ends (click to cycle)";
                }
            }
        }

        public bool IsSessionRepeatAccent => SessionRepeatMode != SessionRepeatMode.Off;

        private bool _isTrackSearchOpen;

        public bool IsTrackSearchOpen
        {
            get => _isTrackSearchOpen;
            set => Set(ref _isTrackSearchOpen, value);
        }

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

                    RefreshBranchPresetsForSelection();
                    AnalyzeSessionTrackCommand.RaiseCanExecuteChanged();
                    PlaySessionTrackCommand.RaiseCanExecuteChanged();
                    AbortSessionWorkCommand?.RaiseCanExecuteChanged();
                    RemoveSessionTrackCommand.RaiseCanExecuteChanged();
                    PreviousSessionTrackCommand.RaiseCanExecuteChanged();
                    NextSessionTrackCommand.RaiseCanExecuteChanged();
                    LoadBranchPresetCommand.RaiseCanExecuteChanged();
                    SaveBranchPresetCommand.RaiseCanExecuteChanged();
                    DeleteSessionCacheCommand?.RaiseCanExecuteChanged();
                    DeleteSessionCapturesCommand?.RaiseCanExecuteChanged();
                    DeleteSessionAnalysisCommand?.RaiseCanExecuteChanged();
                    DeleteSessionTuneCommand?.RaiseCanExecuteChanged();
                    ExportSessionAudioCommand?.RaiseCanExecuteChanged();
                    ExportSessionBothCommand?.RaiseCanExecuteChanged();
                    RefreshSessionCommand?.RaiseCanExecuteChanged();
                    RaisePropertyChanged(nameof(SessionPlayButtonText));
                    RaisePropertyChanged(nameof(SessionActiveTrackId));
                    RefreshLocalPlaybackAvailability();
                    RefreshTrackStatusHud();
                }
            }
        }

        public ObservableCollection<BranchLockPreset> BranchPresets { get; } =
            new ObservableCollection<BranchLockPreset>();

        private BranchLockPreset _selectedBranchPreset;

        public BranchLockPreset SelectedBranchPreset
        {
            get => _selectedBranchPreset;
            set
            {
                if (Set(ref _selectedBranchPreset, value))
                    LoadBranchPresetCommand.RaiseCanExecuteChanged();
            }
        }

        private bool _isBottomPanelExpanded = true;

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

        private double _bottomPanelHeight = 400;

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

        private readonly SemaphoreSlim _playbackSessionGate = new SemaphoreSlim(1, 1);

        private CancellationTokenSource _analysisCancellation;

        private bool _captureInProgress;

        private string _analyzingTrackId;

        private string _analyzingDisplayName;

        private bool _analysisProgressKnown;

        private double _analysisProgressPercent;

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
                    PlayFromInputCommand.RaiseCanExecuteChanged();
                    AnalyzeSessionTrackCommand.RaiseCanExecuteChanged();
                    AbortSessionWorkCommand?.RaiseCanExecuteChanged();
                    NotifyTransportStateChanged();
                    RaisePropertyChanged(nameof(IsIndeterminateAnalysisProgress));
                    RaisePropertyChanged(nameof(ShowAnalysisProgress));
                    RefreshTrackStatusHud();
                }
            }
        }

        public bool CaptureInProgress => _captureInProgress;

        public bool ShowAnalysisProgress => IsAnalyzing;

        public bool IsIndeterminateAnalysisProgress => IsAnalyzing && !_analysisProgressKnown;

        /// <summary>Bound to ProgressBar — never negative (WPF throws on Value &lt; Minimum).</summary>
        public double AnalysisProgressPercent
        {
            get => _analysisProgressKnown ? _analysisProgressPercent : 0;
            private set
            {
                var known = value >= 0;
                var clamped = known ? Math.Max(0, Math.Min(100, value)) : 0;

                if (_analysisProgressKnown == known &&
                    (!known || Math.Abs(_analysisProgressPercent - clamped) < 0.01))
                    return;

                _analysisProgressKnown = known;
                _analysisProgressPercent = clamped;
                RaisePropertyChanged(nameof(AnalysisProgressPercent));
                RaisePropertyChanged(nameof(IsIndeterminateAnalysisProgress));
            }
        }

        public string AnalyzingDisplayName => _analyzingDisplayName ?? _analyzingTrackId ?? "";

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
                PersistJukeboxSettings(invalidateGraph: true);
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
            $"{_jukeboxSettingsModel.BranchSimilarityThresholdMax:0}th %ile";

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
                Log(value
                    ? "Jukebox: only-backward jumps ON — forward hops blocked (hurts novelty)."
                    : "Jukebox: only-backward jumps OFF.");
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
                Log(value
                    ? "Jukebox: only long jumps ON (16+ beats; end-loop still exempt)."
                    : "Jukebox: only long jumps OFF.");
            }
        }

        /// <summary>Linear beats forced after a hop before another random branch (stability dwell).</summary>
        public double JukeboxMinBeatsBetweenJumps
        {
            get => _jukeboxSettingsModel.MinBeatsBetweenJumps;
            set
            {
                var rounded = (int)Math.Round(value);

                if (_jukeboxSettingsModel.MinBeatsBetweenJumps == rounded)
                    return;

                _jukeboxSettingsModel.MinBeatsBetweenJumps = Math.Max(0, rounded);
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(JukeboxMinBeatsBetweenJumpsText));
                PersistJukeboxSettings();
                Log($"Jukebox: dwell after hop = {_jukeboxSettingsModel.MinBeatsBetweenJumps} beats.");
            }
        }

        public string JukeboxMinBeatsBetweenJumpsText =>
            $"{_jukeboxSettingsModel.MinBeatsBetweenJumps:0} beats";

        public double JukeboxPhraseAlignBeats
        {
            get => _jukeboxSettingsModel.PhraseAlignBeats;
            set
            {
                var rounded = (int)Math.Round(value);

                if (_jukeboxSettingsModel.PhraseAlignBeats == rounded)
                    return;

                _jukeboxSettingsModel.PhraseAlignBeats = Math.Max(0, rounded);
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(JukeboxPhraseAlignBeatsText));
                PersistJukeboxSettings();
                Log(_jukeboxSettingsModel.PhraseAlignBeats <= 1
                    ? "Jukebox: phrase align off (bar phase penalty unchanged — separate control)."
                    : $"Jukebox: phrase align = {_jukeboxSettingsModel.PhraseAlignBeats} beats " +
                      "(HARD filter: hop only when beatIndex % N matches — can wipe options on floating grooves).");
            }
        }

        public string JukeboxPhraseAlignBeatsText =>
            _jukeboxSettingsModel.PhraseAlignBeats <= 1
                ? "off"
                : $"{_jukeboxSettingsModel.PhraseAlignBeats:0} beats";

        public ObservableCollection<string> PhasePenaltyModeOptions { get; } =
            new ObservableCollection<string> { "Off", "Soft", "Hard" };

        public string SelectedPhasePenaltyMode
        {
            get => PhasePenaltyModeToLabel(_jukeboxSettingsModel.PhasePenaltyMode);
            set
            {
                var mode = LabelToPhasePenaltyMode(value);

                if (string.Equals(_jukeboxSettingsModel.PhasePenaltyMode, mode, StringComparison.OrdinalIgnoreCase))
                    return;

                _jukeboxSettingsModel.PhasePenaltyMode = mode;
                RaisePropertyChanged();
                PersistJukeboxSettings(invalidateGraph: true);
                Log($"Jukebox: bar phase penalty = {mode}.");
            }
        }

        private static string PhasePenaltyModeToLabel(string mode)
        {
            switch ((mode ?? "").Trim().ToLowerInvariant())
            {
                case "hard":
                    return "Hard";
                case "off":
                    return "Off";
                default:
                    return "Soft";
            }
        }

        private static string LabelToPhasePenaltyMode(string label)
        {
            switch ((label ?? "").Trim().ToLowerInvariant())
            {
                case "hard":
                    return "hard";
                case "off":
                    return "off";
                default:
                    return "soft";
            }
        }

        public double JukeboxSoftmaxTemperature
        {
            get => _jukeboxSettingsModel.SoftmaxTemperature;
            set
            {
                if (Math.Abs(_jukeboxSettingsModel.SoftmaxTemperature - value) < 0.001)
                    return;

                _jukeboxSettingsModel.SoftmaxTemperature = Math.Max(0.05, value);
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(JukeboxSoftmaxTemperatureText));
                PersistJukeboxSettings();
            }
        }

        public string JukeboxSoftmaxTemperatureText =>
            $"τ {_jukeboxSettingsModel.SoftmaxTemperature:0.##}";

        public double JukeboxVisitNoveltyLambda
        {
            get => _jukeboxSettingsModel.VisitNoveltyLambda;
            set
            {
                if (Math.Abs(_jukeboxSettingsModel.VisitNoveltyLambda - value) < 0.001)
                    return;

                _jukeboxSettingsModel.VisitNoveltyLambda = Math.Max(0, value);
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(JukeboxVisitNoveltyLambdaText));
                PersistJukeboxSettings();
            }
        }

        public string JukeboxVisitNoveltyLambdaText =>
            $"λ {_jukeboxSettingsModel.VisitNoveltyLambda:0.##}";

        public bool JukeboxUseMutualKnn
        {
            get => _jukeboxSettingsModel.UseMutualKnn;
            set
            {
                if (_jukeboxSettingsModel.UseMutualKnn == value)
                    return;

                _jukeboxSettingsModel.UseMutualKnn = value;
                RaisePropertyChanged();
                PersistJukeboxSettings(invalidateGraph: true);
                Log(value
                    ? "Jukebox: mutual-kNN ON (rebuilds graph)."
                    : "Jukebox: mutual-kNN OFF (directed kNN).");
            }
        }

        public bool JukeboxEnableSccBridges
        {
            get => _jukeboxSettingsModel.EnableSccBridges;
            set
            {
                if (_jukeboxSettingsModel.EnableSccBridges == value)
                    return;

                _jukeboxSettingsModel.EnableSccBridges = value;
                RaisePropertyChanged();
                PersistJukeboxSettings(invalidateGraph: true);
                Log(value
                    ? "Jukebox: component bridges ON."
                    : "Jukebox: component bridges OFF.");
            }
        }

        public double JukeboxPreferenceWeightPercent
        {
            get => _jukeboxSettingsModel.PreferenceWeight * 100;
            set
            {
                var next = Math.Max(0, Math.Min(100, value)) / 100.0;

                if (Math.Abs(_jukeboxSettingsModel.PreferenceWeight - next) < 0.0001)
                    return;

                _jukeboxSettingsModel.PreferenceWeight = next;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(JukeboxPreferenceWeightText));
                PersistJukeboxSettings();
            }
        }

        public string JukeboxPreferenceWeightText =>
            $"w_pref {_jukeboxSettingsModel.PreferenceWeight * 100:0.#}%";

        public double JukeboxPreferenceSkipWindowSeconds
        {
            get => Math.Max(0.5, _jukeboxSettingsModel.PreferenceSkipWindowMs / 1000.0);
            set
            {
                var next = (int)Math.Round(Math.Max(0.5, Math.Min(30, value)) * 1000);

                if (_jukeboxSettingsModel.PreferenceSkipWindowMs == next)
                    return;

                _jukeboxSettingsModel.PreferenceSkipWindowMs = next;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(JukeboxPreferenceSkipWindowText));
                PersistJukeboxSettings();
            }
        }

        public string JukeboxPreferenceSkipWindowText =>
            $"skip window {_jukeboxSettingsModel.PreferenceSkipWindowMs / 1000.0:0.#}s";

        public string BranchPreferenceStatusText =>
            _loopController.PreferenceEdgeCount <= 0
                ? "No preference labels yet"
                : $"{_loopController.PreferenceEdgeCount} labeled branch edge(s)";

        public bool JukeboxEssentiaRegionGate
        {
            get => _jukeboxSettingsModel.EssentiaRegionGate;
            set
            {
                if (_jukeboxSettingsModel.EssentiaRegionGate == value)
                    return;

                _jukeboxSettingsModel.EssentiaRegionGate = value;
                RaisePropertyChanged();
                PersistJukeboxSettings(invalidateGraph: true);
                Log(value
                    ? "Jukebox: Essentia region gate ON (needs regionEmbeddings in analysis)."
                    : "Jukebox: Essentia region gate OFF.");
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

        private string _jukeboxBranchChanceText = "—";

        private string _currentTrackCacheStatusText = "—";

        /// <summary>Session-style capture/analysis badge for the active track (HUD).</summary>
        public string CurrentTrackCacheStatusText
        {
            get => _currentTrackCacheStatusText;
            private set => Set(ref _currentTrackCacheStatusText, value);
        }

        private string _currentTrackAnalysisDetailText = "—";

        /// <summary>Per-track analysis path / tracker / metric for the HUD.</summary>
        public string CurrentTrackAnalysisDetailText
        {
            get => _currentTrackAnalysisDetailText;
            private set => Set(ref _currentTrackAnalysisDetailText, value);
        }

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

        /// <summary>Classic stacked features for the Slice 3 SSM heatmap (null if analysis lacks them).</summary>
        public System.Collections.IList RingStackedFeatures
        {
            get => _ringStackedFeatures;
            set => Set(ref _ringStackedFeatures, value);
        }

        private System.Collections.IList _ringStackedFeatures;

        /// <summary>Beat under inspect on the ring / SSM (shared selection).</summary>
        public int RingInspectBeat
        {
            get => _ringInspectBeat;
            set => Set(ref _ringInspectBeat, value);
        }

        private int _ringInspectBeat = -1;

        /// <summary>Classic beatFeatures (chroma+mfcc+rms) for hop tooltips.</summary>
        public System.Collections.IList RingBeatFeatures
        {
            get => _ringBeatFeatures;
            set => Set(ref _ringBeatFeatures, value);
        }

        private System.Collections.IList _ringBeatFeatures;

        /// <summary>True while the ring is in manual hop-pick mode (shows window confirm/cancel).</summary>
        public bool IsManualBranchSelect
        {
            get => _isManualBranchSelect;
            set => Set(ref _isManualBranchSelect, value);
        }

        private bool _isManualBranchSelect;

        /// <summary>Show the self-similarity heatmap (Slice 3 Observe).</summary>
        public bool ShowSsmHeatmap
        {
            get => _showSsmHeatmap;
            set => Set(ref _showSsmHeatmap, value);
        }

        private bool _showSsmHeatmap;

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

        public IReadOnlyList<BranchModifier> RingBranchModifiers
        {
            get => _ringBranchModifiers;
            set => Set(ref _ringBranchModifiers, value);
        }

        private IReadOnlyList<BranchModifier> _ringBranchModifiers = Array.Empty<BranchModifier>();

        public string RingLockCountText
        {
            get => _ringLockCountText;
            set => Set(ref _ringLockCountText, value);
        }

        private string _ringLockCountText = "no locks";

        public int RingLockCount => RingLockedBranches?.Count ?? 0;

        /// <summary>Bumped by "Reset plays" — the ring clears its coverage bars on change.</summary>
        public int RingResetPlaysToken
        {
            get => _ringResetPlaysToken;
            set => Set(ref _ringResetPlaysToken, value);
        }

        private int _ringResetPlaysToken;

        private int _ringPreviewHopDepth = 2;

        private int _sliderDragDepth;

        private bool _jukeboxSettingsDirty;

        private bool _weightsDirty;

        /// <summary>Branch hops to preview when hovering the ring (1–3).</summary>
        public int RingPreviewHopDepth
        {
            get => _ringPreviewHopDepth;
            set => Set(ref _ringPreviewHopDepth, Math.Max(1, Math.Min(3, value)));
        }

        private bool _jukeboxRandomBranches = true;

        /// <summary>
        /// When true (default), random walk + locks at their probability.
        /// When false, only locked branches fire (+ end-loop guard if enabled).
        /// </summary>
        public bool JukeboxRandomBranches
        {
            get => _jukeboxRandomBranches;
            set
            {
                if (Set(ref _jukeboxRandomBranches, value))
                {
                    ApplyLoopSettings();
                    Log(value
                        ? "Jukebox: random jumps ON (plus any locked branches)."
                        : "Jukebox: random jumps OFF — locked branches only" +
                          (JukeboxEnableEndLoop ? " (+ end loop)." : "."));
                }
            }
        }

        public bool JukeboxEnableEndLoop
        {
            get => _jukeboxSettingsModel.EnableEndLoop;
            set
            {
                if (_jukeboxSettingsModel.EnableEndLoop == value)
                    return;

                _jukeboxSettingsModel.EnableEndLoop = value;
                RaisePropertyChanged();
                PersistJukeboxSettings(invalidateGraph: true);
                Log(value
                    ? "Jukebox: end loop ON — track should not finish; graph rebuilt."
                    : "Jukebox: end loop OFF — song can play out linearly; graph rebuilt.");
            }
        }

        /// <summary>True when playback uses the cached analysis WAV instead of the Spotify SDK.</summary>
        public bool UseLocalPlayback
        {
            get => _transportRouter.Source == JukeboxPlaybackSource.Local;
            set
            {
                var desired = value ? JukeboxPlaybackSource.Local : JukeboxPlaybackSource.Spotify;

                if (desired == JukeboxPlaybackSource.Local && !IsLocalPlaybackAvailable)
                {
                    Log("Local playback needs a complete WAV cache — Analyze the track first.");
                    RaisePropertyChanged();
                    return;
                }

                if (_transportRouter.Source == desired)
                    return;

                _ = SwitchPlaybackSourceAsync(desired);
            }
        }

        public string PlaybackSourceLabel => UseLocalPlayback ? "Local WAV" : "Spotify";

        private string _branchPresetName = string.Empty;

        /// <summary>Editable name for the tune+branches ComboBox (type to name a save).</summary>
        public string BranchPresetName
        {
            get => _branchPresetName;
            set
            {
                if (Set(ref _branchPresetName, value ?? string.Empty))
                    LoadBranchPresetCommand.RaiseCanExecuteChanged();
            }
        }

        /// <summary>Local source is only available when a complete capture WAV exists for the active track.</summary>
        public bool IsLocalPlaybackAvailable
        {
            get
            {
                var trackId = SelectedSessionTrack?.TrackId
                              ?? _loopController.CurrentTrackId
                              ?? _currentPlay?.TrackId;
                return LocalWavPlaybackHost.CanPlayTrack(trackId, DurationMs);
            }
        }

        public ObservableCollection<string> BeatTrackerModeOptions { get; } =
            new ObservableCollection<string> { "Auto", "BeatThis", "DP (librosa)" };

        public ObservableCollection<string> GraphMetricModeOptions { get; } =
            new ObservableCollection<string> { "Auto", "Enhanced", "Legacy" };

        public string SelectedBeatTrackerMode
        {
            get => BeatTrackerModeToLabel(_jukeboxSettingsModel.BeatTrackerMode);
            set
            {
                var mode = LabelToBeatTrackerMode(value);

                if (string.Equals(_jukeboxSettingsModel.BeatTrackerMode, mode, StringComparison.OrdinalIgnoreCase))
                    return;

                _jukeboxSettingsModel.BeatTrackerMode = mode;
                RaisePropertyChanged();
                PersistJukeboxSettings();
                Log($"Beat tracker preference: {value}. Takes effect on next Analyze (delete cache to re-run).");
            }
        }

        public string SelectedGraphMetricMode
        {
            get => GraphMetricModeToLabel(_jukeboxSettingsModel.GraphMetricMode);
            set
            {
                var mode = LabelToGraphMetricMode(value);

                if (string.Equals(_jukeboxSettingsModel.GraphMetricMode, mode, StringComparison.OrdinalIgnoreCase))
                    return;

                _jukeboxSettingsModel.GraphMetricMode = mode;
                RaisePropertyChanged();
                PersistJukeboxSettings(invalidateGraph: true);
                Log($"Graph metric: {value}.");
            }
        }

        private static string BeatTrackerModeToLabel(string mode)
        {
            switch ((mode ?? "auto").Trim().ToLowerInvariant())
            {
                case "beatthis":
                case "beat_this":
                case "beat-this":
                    return "BeatThis";
                case "dp":
                case "librosa-dp":
                case "librosa":
                    return "DP (librosa)";
                default:
                    return "Auto";
            }
        }

        private static string LabelToBeatTrackerMode(string label)
        {
            if (string.Equals(label, "BeatThis", StringComparison.OrdinalIgnoreCase))
                return "beatthis";
            if (string.Equals(label, "DP (librosa)", StringComparison.OrdinalIgnoreCase))
                return "dp";
            return "auto";
        }

        private static string GraphMetricModeToLabel(string mode)
        {
            switch ((mode ?? "auto").Trim().ToLowerInvariant())
            {
                case "classic":
                    return "Enhanced";
                case "legacy":
                    return "Legacy";
                default:
                    return "Auto";
            }
        }

        private static string LabelToGraphMetricMode(string label)
        {
            if (string.Equals(label, "Enhanced", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(label, "Classic", StringComparison.OrdinalIgnoreCase))
                return "classic";
            if (string.Equals(label, "Legacy", StringComparison.OrdinalIgnoreCase))
                return "legacy";
            return "auto";
        }

        private static string FormatMetricModeLabel(string metricMode)
        {
            if (string.Equals(metricMode, "classic", StringComparison.OrdinalIgnoreCase))
                return "enhanced";
            return string.IsNullOrWhiteSpace(metricMode) ? "—" : metricMode;
        }

        private async Task SwitchPlaybackSourceAsync(JukeboxPlaybackSource desired)
        {
            var trackId = SelectedSessionTrack?.TrackId
                          ?? _currentPlay?.TrackId
                          ?? _loopController.CurrentTrackId
                          ?? ParseTrackId(TrackInput);

            // Tear down both sides so the ring/pause wheel never keep a stale transport.
            _transport.DisarmAction();
            _transportRouter.Local.Stop();

            if (_playbackHost.IsReady)
            {
                _playbackHost.DisarmAction();
                _playbackHost.Pause();
            }

            _transportRouter.Source = desired;
            _jukeboxSettingsModel.PlaybackSource = desired == JukeboxPlaybackSource.Local ? "Local" : "Spotify";
            PersistJukeboxSettings();

            RaisePropertyChanged(nameof(UseLocalPlayback));
            RaisePropertyChanged(nameof(PlaybackSourceLabel));
            DeviceText = desired == JukeboxPlaybackSource.Local
                ? "Local WAV"
                : (_playbackHost.IsReady
                    ? $"Spotify ({_playbackHost.DeviceId})"
                    : "Spotify");

            // Clear stale "still playing" state so Play starts fresh on the new source
            // (Resume after a source switch is not valid).
            FinalizeCurrentPlay(userSkipped: true);
            IsPaused = true;
            PositionMs = 0;
            RaisePropertyChanged(nameof(ShowPauseIcon));
            RaisePropertyChanged(nameof(SessionPlayButtonText));
            RaisePropertyChanged(nameof(SessionActiveTrackId));
            RaisePropertyChanged(nameof(ScrubberPositionMs));
            RaisePropertyChanged(nameof(PositionText));
            NotifyNowPlayingDisplayChanged();
            TogglePlayPauseCommand.RaiseCanExecuteChanged();
            PlaySessionTrackCommand.RaiseCanExecuteChanged();
            RingScrubToCommand.RaiseCanExecuteChanged();
            StopPlaybackCommand.RaiseCanExecuteChanged();

            if (string.IsNullOrEmpty(trackId))
            {
                Status = desired == JukeboxPlaybackSource.Local
                    ? "Local WAV selected — press Play."
                    : "Spotify selected — press Play.";
                RefreshTrackStatusHud();
                return;
            }

            // Arm the selected track for the new source, but do not autoplay — user presses Play.
            TrackInput = trackId;
            Status = desired == JukeboxPlaybackSource.Local
                ? "Local WAV ready — press Play."
                : "Spotify ready — press Play.";
            Log(desired == JukeboxPlaybackSource.Local
                ? $"Local WAV armed for {trackId} (stopped — press Play)."
                : $"Spotify armed for {trackId} (stopped — press Play).");
            RefreshTrackStatusHud();
            await Task.CompletedTask;
        }

        private void ApplyPlaybackSourceFromSettings()
        {
            var wantLocal = string.Equals(_jukeboxSettingsModel.PlaybackSource, "Local",
                StringComparison.OrdinalIgnoreCase);

            if (wantLocal && IsLocalPlaybackAvailable)
                _transportRouter.Source = JukeboxPlaybackSource.Local;
            else
            {
                _transportRouter.Source = JukeboxPlaybackSource.Spotify;
                _transportRouter.Local.ClearBranchModifier();

                if (wantLocal)
                    _jukeboxSettingsModel.PlaybackSource = "Spotify";
            }
        }

        private void RefreshLocalPlaybackAvailability()
        {
            RaisePropertyChanged(nameof(IsLocalPlaybackAvailable));

            if (UseLocalPlayback && !IsLocalPlaybackAvailable)
            {
                UseLocalPlayback = false;
                Log("Local playback unavailable for this track — switched back to Spotify.");
            }

            RefreshTrackStatusHud();
        }

        /// <summary>Keep the top-left HUD in sync with Session capture/analysis and live branch chance.</summary>
        private void RefreshTrackStatusHud()
        {
            var trackId = _loopController.CurrentTrackId
                          ?? _currentPlay?.TrackId
                          ?? SelectedSessionTrack?.TrackId;

            if (string.IsNullOrEmpty(trackId))
            {
                CurrentTrackCacheStatusText = "—";
                CurrentTrackAnalysisDetailText = "—";
                UpdateLiveBranchChanceText();
                return;
            }

            var hasCapture = WavCaptureValidator.HasCompleteCapture(trackId);
            var analysis = AnalysisCache.Load(trackId);
            var hasAnalysis = analysis?.Beats != null && analysis.Beats.Count > 0;
            var analyzingThis = IsAnalyzing &&
                                string.Equals(_analyzingTrackId, trackId, StringComparison.Ordinal);

            if (analyzingThis)
                CurrentTrackCacheStatusText = "Capturing / analyzing…";
            else
            {
                var capture = hasCapture ? "Ready" : "Missing";
                var analysisLabel = hasAnalysis ? "Ready" : "Missing";
                CurrentTrackCacheStatusText = $"capture {capture} · analysis {analysisLabel}";
            }

            if (hasAnalysis)
            {
                var tracker = string.IsNullOrWhiteSpace(analysis.BeatTracker)
                    ? "beats"
                    : analysis.BeatTracker;
                var metric = FormatMetricModeLabel(RingGraph?.MetricMode
                             ?? (analysis.HasClassicFeatures ? "classic" : "legacy"));
                var source = string.IsNullOrWhiteSpace(analysis.SourceType)
                    ? "cached"
                    : analysis.SourceType;
                CurrentTrackAnalysisDetailText =
                    $"{source} · {tracker} · {metric} · {analysis.Beats.Count} beats";
            }
            else
            {
                CurrentTrackAnalysisDetailText = "none for this track";
            }

            UpdateLiveBranchChanceText();
        }

        private void UpdateLiveBranchChanceText()
        {
            var chance = _loopController.NavigatorBranchChance;
            var beat = _loopController.NavigatorBeatIndex;
            var phase = "";

            if (RingGraph != null && beat.HasValue && beat.Value >= 0 && beat.Value < RingGraph.Beats.Count)
            {
                var node = RingGraph.Beats[beat.Value];
                phase = $" · beat {beat.Value} · bar+{node.IndexInBar}";
            }

            if (chance.HasValue)
                JukeboxBranchChanceText = $"{chance.Value:P0}{phase}";
            else
                JukeboxBranchChanceText = string.IsNullOrEmpty(phase) ? "—" : $"—{phase}";
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
                SaveWeightsDeferred();
            }
        }

        public double WeightRepeatAffinity
        {
            get => _weights.RepeatAffinity;
            set
            {
                _weights.RepeatAffinity = value;
                RaisePropertyChanged();
                SaveWeightsDeferred();
            }
        }

        public double WeightSameArtist
        {
            get => _weights.SameArtist;
            set
            {
                _weights.SameArtist = value;
                RaisePropertyChanged();
                SaveWeightsDeferred();
            }
        }

        public double WeightTempoSimilarity
        {
            get => _weights.TempoSimilarity;
            set
            {
                _weights.TempoSimilarity = value;
                RaisePropertyChanged();
                SaveWeightsDeferred();
            }
        }

        public double WeightRecencyPenalty
        {
            get => _weights.RecencyPenalty;
            set
            {
                _weights.RecencyPenalty = value;
                RaisePropertyChanged();
                SaveWeightsDeferred();
            }
        }

        public double WeightPinned
        {
            get => _weights.Pinned;
            set
            {
                _weights.Pinned = value;
                RaisePropertyChanged();
                SaveWeightsDeferred();
            }
        }

        #endregion

        #region Commands

        public RelayCommand TogglePlayPauseCommand { get; }

        public RelayCommand ReprobeAnalysisCommand { get; }

        public RelayCommand AnalyzeTrackCommand { get; }

        public RelayCommand AnalyzeInputCommand { get; }

        public RelayCommand PlayFromInputCommand { get; }

        public RelayCommand<LoopLabSessionTrack> AnalyzeSessionTrackCommand { get; }

        public RelayCommand PreviousSessionTrackCommand { get; }

        public RelayCommand NextSessionTrackCommand { get; }

        public RelayCommand ToggleSessionRepeatCommand { get; }

        public RelayCommand ToggleLibrarySidebarCommand { get; }

        public RelayCommand RefreshLibraryPlaylistsCommand { get; }

        public RelayCommand<PlaylistCacheItem> AddLibraryPlaylistToSessionCommand { get; }

        public RelayCommand<TrackSearchHit> SelectTrackSearchHitCommand { get; }

        public RelayCommand<LoopLabSessionTrack> PlaySessionTrackCommand { get; }

        public RelayCommand AbortSessionWorkCommand { get; }

        public RelayCommand RefreshSessionCommand { get; }

        public RelayCommand DeleteSessionCacheCommand { get; }

        public RelayCommand DeleteSessionCapturesCommand { get; }

        public RelayCommand DeleteSessionAnalysisCommand { get; }

        public RelayCommand DeleteSessionTuneCommand { get; }

        public RelayCommand RemoveSessionTrackCommand { get; }

        public RelayCommand ClearSessionCommand { get; }

        public RelayCommand ExportSessionAudioCommand { get; }

        public RelayCommand ExportSessionAnalysisCommand { get; }

        public RelayCommand ExportSessionBothCommand { get; }

        public string SessionPlayButtonText =>
            SelectedSessionTrack != null &&
            string.Equals(SelectedSessionTrack.TrackId, ActivePlaybackTrackId, StringComparison.Ordinal) &&
            !IsPaused
                ? "Pause"
                : "Play";

        private string ActivePlaybackTrackId =>
            _loopController.CurrentTrackId ?? _currentPlay?.TrackId;

        /// <summary>Public for session-row play/pause glyph bindings.</summary>
        public string SessionActiveTrackId => ActivePlaybackTrackId;

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
        public RelayCommand<RingBranchClick> ToggleRingLockCommand { get; }

        /// <summary>Local WAV: stretch a locked branch outward to attach/update a modifier.</summary>
        public RelayCommand<RingModifierStretch> StretchRingModifierCommand { get; }

        /// <summary>Local WAV: cycle supercharge → turbocharge → remove on a locked branch.</summary>
        public RelayCommand<RingBranchClick> CycleRingModifierCommand { get; }

        public RelayCommand<long> RingScrubToCommand { get; }

        public RelayCommand EndRingScrubCommand { get; }

        public RelayCommand<bool> HoldRingSpeedCommand { get; }

        public RelayCommand<double> SetRingPlaybackRateCommand { get; }

        public RelayCommand ClearRingLocksCommand { get; }

        public RelayCommand ClearBranchPreferencesCommand { get; }

        public RelayCommand ResetRingPlaysCommand { get; }

        public RelayCommand StopPlaybackCommand { get; }

        public RelayCommand LoadBranchPresetCommand { get; }

        public RelayCommand SaveBranchPresetCommand { get; }

        private bool CanPlayTransport()
        {
            if (IsAnalyzing)
                return false;

            if (UseLocalPlayback)
                return IsLocalPlaybackAvailable;

            return _playbackHost.IsReady && !HasPlayerInitializationError;
        }

        private bool CanStopPlayback()
        {
            if (UseLocalPlayback)
                return _transportRouter.Local.IsPlayingTrack || HasCurrentTrack();

            return _playbackHost.IsReady && !HasPlayerInitializationError;
        }

        private bool CanPlaySessionTrack()
        {
            return SelectedSessionTrack != null && CanPlayTransport();
        }

        private async Task AnalyzeSessionTrackAsync(LoopLabSessionTrack track = null)
        {
            track = track ?? SelectedSessionTrack;

            if (track == null)
                return;

            if (!ReferenceEquals(SelectedSessionTrack, track))
                SelectedSessionTrack = track;

            await AnalyzeTrackByIdAsync(track.TrackId, track.DisplayName);
        }

        private async Task PlaySessionTrackAsync(LoopLabSessionTrack track = null)
        {
            track = track ?? SelectedSessionTrack;

            if (track == null || !CanPlayTransport())
                return;

            if (!ReferenceEquals(SelectedSessionTrack, track))
                SelectedSessionTrack = track;

            // Pause only when this track is actively playing on the current transport.
            // After a Local WAV / Spotify switch we clear play state and must PlayTrackAsync,
            // not Resume (stale transport).
            var isCurrent = string.Equals(track.TrackId, ActivePlaybackTrackId, StringComparison.Ordinal);
            if (isCurrent && !IsPaused && _currentPlay != null)
            {
                _transport.Pause();
                return;
            }

            TrackInput = track.TrackId;
            await PlayTrackAsync(track.TrackId, "session-track");
        }

        private async Task AbortSessionWorkAsync()
        {
            if (!CanAbortSessionWork())
                return;

            var captureTrackId = _captureInProgress ? _analyzingTrackId : null;
            var wasAnalyzing = IsAnalyzing;

            if (wasAnalyzing)
                _analysisCancellation?.Cancel();

            if (CanStopPlayback() || wasAnalyzing || _captureInProgress)
                await StopPlaybackInternalAsync().ConfigureAwait(true);

            // Discard incomplete capture/analysis left by cancel or stop-during-capture.
            var purged = WavCaptureValidator.PurgeIncompleteArtifacts();

            if (!string.IsNullOrEmpty(captureTrackId) &&
                !WavCaptureValidator.HasCompleteCapture(captureTrackId))
            {
                WavCaptureValidator.TryDeleteCapture(captureTrackId);
            }

            if (!string.IsNullOrEmpty(captureTrackId) || !string.IsNullOrEmpty(_analyzingTrackId))
            {
                var id = captureTrackId ?? _analyzingTrackId;
                var analysis = AnalysisCache.Load(id);

                if (analysis?.Beats == null || analysis.Beats.Count == 0)
                    AnalysisCache.Delete(id);

                UpdateSessionTrackAnalysisStatus(id, SessionAnalysisStatus.Pending);
            }

            if (wasAnalyzing || !string.IsNullOrEmpty(captureTrackId))
            {
                Log(purged > 0
                    ? $"Aborted — discarded incomplete capture/analysis ({purged} artifact(s))."
                    : "Aborted — incomplete capture/analysis discarded.");
                Status = "Aborted.";
            }
            else
            {
                Status = "Stopped.";
            }

            AbortSessionWorkCommand?.RaiseCanExecuteChanged();
            NotifyTransportStateChanged();
        }

        private bool CanDeleteSessionCache()
        {
            return CanDeleteSessionCaptures() || CanDeleteSessionAnalysis();
        }

        private bool CanDeleteSessionCaptures()
        {
            if (SelectedSessionTrack == null || IsAnalyzing)
                return false;

            var trackId = SelectedSessionTrack.TrackId;
            return WavCaptureValidator.HasCompleteCapture(trackId) ||
                   File.Exists(PredictionPaths.ResolveAudioCachePath(trackId));
        }

        private bool CanDeleteSessionAnalysis()
        {
            if (SelectedSessionTrack == null || IsAnalyzing)
                return false;

            return AnalysisCache.Exists(SelectedSessionTrack.TrackId);
        }

        private bool CanExportSessionAudio()
        {
            if (SelectedSessionTrack == null)
                return false;

            return WavCaptureValidator.HasCompleteCapture(SelectedSessionTrack.TrackId) ||
                   File.Exists(PredictionPaths.ResolveAudioCachePath(SelectedSessionTrack.TrackId));
        }

        private bool CanAbortSessionWork()
        {
            return IsAnalyzing || _captureInProgress || CanStopPlayback();
        }

        private bool CanUsePlayer() => CanPlayTransport();

        private void NotifyTransportStateChanged()
        {
            TogglePlayPauseCommand.RaiseCanExecuteChanged();
            StopPlaybackCommand.RaiseCanExecuteChanged();
            PlaySessionTrackCommand.RaiseCanExecuteChanged();
            AbortSessionWorkCommand?.RaiseCanExecuteChanged();
            RefreshSessionCommand?.RaiseCanExecuteChanged();
            DeleteSessionCacheCommand?.RaiseCanExecuteChanged();
            DeleteSessionCapturesCommand?.RaiseCanExecuteChanged();
            DeleteSessionAnalysisCommand?.RaiseCanExecuteChanged();
            DeleteSessionTuneCommand?.RaiseCanExecuteChanged();
            ExportSessionAudioCommand?.RaiseCanExecuteChanged();
            ExportSessionBothCommand?.RaiseCanExecuteChanged();
            ClearSessionCommand?.RaiseCanExecuteChanged();
            RaisePropertyChanged(nameof(SessionPlayButtonText));
            RaisePropertyChanged(nameof(SessionActiveTrackId));
            RingScrubToCommand.RaiseCanExecuteChanged();
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
            UpsertSessionTrack(trackId, trackId, "");

            // Play immediately when analysis is already cached; otherwise analyze first, then play.
            if (AnalysisCache.Exists(trackId))
            {
                await PlayTrackAsync(trackId, "prediction-page");
                return;
            }

            await AnalyzeTrackByIdAsync(trackId, trackId);

            if (AnalysisCache.Exists(trackId))
                await PlayTrackAsync(trackId, "prediction-page");
        }

        private bool CanPlayFromInput()
        {
            return !IsAnalyzing && ParseTrackId(TrackInput) != null;
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
                _transport.Resume();
            else
                _transport.Pause();
        }

        public void BeginScrub()
        {
            if (DurationMs <= 0)
                return;

            _isUserScrubbing = true;
            _scrubPositionMs = PositionMs;
            RaisePropertyChanged(nameof(ScrubberPositionMs));
        }

        private void ScrubToPositionMs(long ms)
        {
            if (DurationMs <= 0)
                return;

            if (!_isUserScrubbing)
                BeginScrub();

            var clamped = Math.Max(0L, Math.Min(ms, DurationMs));

            if (_scrubPositionMs == clamped)
                return;

            _scrubPositionMs = clamped;
            PositionMs = clamped;
            RaisePropertyChanged(nameof(ScrubberPositionMs));
            RaisePropertyChanged(nameof(PositionText));
        }

        public void EndScrub()
        {
            if (!_isUserScrubbing)
                return;

            _isUserScrubbing = false;
            PositionMs = _scrubPositionMs;
            _transport.Seek(_scrubPositionMs);
            _loopController.NotifyPlaybackSeek(_scrubPositionMs);
            RaisePropertyChanged(nameof(ScrubberPositionMs));
            RaisePropertyChanged(nameof(PositionText));
            RaisePropertyChanged(nameof(BranchPreferenceStatusText));
            ClearBranchPreferencesCommand?.RaiseCanExecuteChanged();
            Log($"Scrubbed to {FormatMs(_scrubPositionMs)} — replanned jukebox from here.", verbose: true);
        }

        private double _ringPlaybackRate = 1.0;

        /// <summary>Speed dial readout under the search bar (empty / faded at 1×).</summary>
        public string RingPlaybackRateText =>
            Math.Abs(_ringPlaybackRate - 1.0) < 0.001
                ? string.Empty
                : string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.00}×", _ringPlaybackRate);

        /// <summary>True when the dial is off 1× — drives the fade-in/out under search.</summary>
        public bool IsRingPlaybackRateModified => Math.Abs(_ringPlaybackRate - 1.0) >= 0.001;

        private void SetRingHoldSpeed(bool holding)
        {
            // Legacy hold binding kept for safety; dial path uses SetRingPlaybackRate.
            SetRingPlaybackRate(holding ? 2.0 : 1.0);
        }

        private void SetRingPlaybackRate(double rate)
        {
            rate = Math.Max(0.5, Math.Min(2.5, rate));

            if (Math.Abs(rate - 1.0) < 0.06)
                rate = 1.0;

            if (Math.Abs(_ringPlaybackRate - rate) < 0.0005)
            {
                ApplyLocalPlaybackRate(rate);
                return;
            }

            _ringPlaybackRate = rate;
            RaisePropertyChanged(nameof(RingPlaybackRateText));
            RaisePropertyChanged(nameof(IsRingPlaybackRateModified));

            if (!UseLocalPlayback)
            {
                if (IsLocalPlaybackAvailable)
                {
                    Status = $"Speed dial {rate:0.00}× — switch Playback source to Local WAV to hear it.";
                    Log(Status, verbose: true);
                }
                else
                {
                    Status = "Speed dial needs Local WAV (Analyze/capture this track first).";
                    Log(Status, verbose: true);
                }

                return;
            }

            ApplyLocalPlaybackRate(rate);

            if (Math.Abs(rate - 1.0) > 0.001)
                Log($"Playback rate {rate:0.00}×.", verbose: true);
        }

        private void ApplyLocalPlaybackRate(double rate)
        {
            if (UseLocalPlayback)
                _transport.SetPlaybackRate(rate);
        }

        /// <summary>Defer disk writes while a tuning slider is being dragged.</summary>
        public void BeginSliderDrag() => _sliderDragDepth++;

        /// <summary>Flush any settings/weights deferred during slider drag.</summary>
        public void EndSliderDrag()
        {
            if (_sliderDragDepth <= 0)
                return;

            _sliderDragDepth--;

            if (_sliderDragDepth > 0)
                return;

            if (_jukeboxSettingsDirty)
            {
                _jukeboxSettingsDirty = false;
                // Threshold slider is deferred with other tune sliders; always rebuild to be safe.
                PersistJukeboxSettingsNow(invalidateGraph: true);
            }

            if (_weightsDirty)
            {
                _weightsDirty = false;
                _predictor.SaveWeights(_weights);
            }
        }

        public async Task StopPlaybackAsync()
        {
            if (IsAnalyzing)
            {
                _analysisCancellation?.Cancel();
                await StopPlaybackInternalAsync().ConfigureAwait(true);
                return;
            }

            if (!CanStopPlayback())
                return;

            await StopPlaybackInternalAsync();
        }

        private async Task StopPlaybackInternalAsync()
        {
            try
            {
                _transport.DisarmAction();
                _transport.Pause();

                if (UseLocalPlayback)
                {
                    _transportRouter.Local.Stop();
                }
                else if (_playbackHost.IsReady)
                {
                    _playbackHost.Seek(0);
                    await _playbackService.PauseAsync(_playbackHost.DeviceId);
                }

                Log("Playback stopped.");
            }
            catch (Exception ex)
            {
                Log($"Stop failed: {ex.Message}");
            }
        }

        /// <summary>Plays a track via Spotify SDK or local WAV. Source tags the listening-log entry.</summary>
        public async Task PlayTrackAsync(string trackId, string source)
        {
            if (IsAnalyzing)
            {
                Status = $"Cannot play while analyzing {AnalyzingDisplayName}.";
                return;
            }

            if (UseLocalPlayback)
            {
                if (!LocalWavPlaybackHost.CanPlayTrack(trackId, DurationMs))
                {
                    Status = "Local playback needs a complete WAV — Analyze first, or switch to Spotify.";
                    Log(Status);
                    return;
                }

                try
                {
                    var session = SessionTracks.FirstOrDefault(t => t.TrackId == trackId);
                    Status = $"Starting local playback of {trackId}…";
                    _playbackHost.Pause();
                    _playbackHost.DisarmAction();

                    if (!_transportRouter.Local.PlayTrack(trackId, session?.Title, session?.Artist))
                    {
                        Status = "Local WAV playback failed.";
                        Log(Status);
                        return;
                    }

                    _transportRouter.Local.SetPlaybackRate(_ringPlaybackRate);
                    _pendingPlaySource = source;
                    DeviceText = "Local WAV";
                    Status = "Playing locally (cached WAV).";
                    Log($"Local WAV playback started for {trackId}.");
                }
                catch (Exception ex)
                {
                    Status = $"Local playback failed: {ex.Message}";
                    Log(Status);
                }

                return;
            }

            if (!_playbackHost.IsReady)
            {
                Status = "Player is not ready yet.";
                return;
            }

            try
            {
                _transportRouter.Local.Stop();
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
                DeviceText = $"Spotify ({_playbackHost.DeviceId})";
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
                RaisePropertyChanged(nameof(SessionPlayButtonText));
                RaisePropertyChanged(nameof(SessionActiveTrackId));
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
                UpdateLiveBranchChanceText();

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
            SaveBranchPresetCommand.RaiseCanExecuteChanged();
            LoadBranchPresetCommand.RaiseCanExecuteChanged();

            Log(AnalysisCache.Exists(state.TrackId)
                ? "Analysis cached for this track."
                : "No analysis for this track yet.", verbose: true);

            UpsertSessionTrack(state.TrackId, state.TrackName, state.ArtistNames);
            RefreshBranchPresetsForSelection();
            RefreshLocalPlaybackAvailability();
            RefreshRingVisualization(state.TrackId);
            ApplyLoopSettings();
            RefreshTrackStatusHud();
            _ = LoadLyricsForTrackAsync(state.TrackId, state.TrackName, state.ArtistNames, state.DurationMs);
        }

        private async Task LoadLyricsForTrackAsync(string trackId, string title, string artist, long durationMs)
        {
            _lyricsLoadCts?.Cancel();
            _lyricsLoadCts = new CancellationTokenSource();
            var ct = _lyricsLoadCts.Token;

            if (!_jukeboxSettingsModel.ShowLyrics || string.IsNullOrWhiteSpace(trackId))
            {
                CurrentLyrics = null;
                LyricsStatusText = "Lyrics hidden.";
                _loopController.SetLyricFlowContext(null);
                return;
            }

            LyricsStatusText = "Loading lyrics…";
            ActiveLyricText = string.Empty;
            PreviousLyricText = string.Empty;
            NextLyricText = string.Empty;

            try
            {
                var durationSec = durationMs > 0 ? (int)Math.Round(durationMs / 1000.0) : 0;
                var lyrics = await _lyricsService.GetSyncedLyricsAsync(
                    trackId, title, artist, album: null, durationSec, ct).ConfigureAwait(true);

                if (ct.IsCancellationRequested)
                    return;

                if (lyrics == null)
                {
                    CurrentLyrics = null;
                    LyricsStatusText = "No lyrics found.";
                    _loopController.SetLyricFlowContext(null);
                    return;
                }

                CurrentLyrics = lyrics;
                LyricsStatusText = lyrics.Instrumental
                    ? "Instrumental"
                    : lyrics.HasSyncedLines
                        ? $"{lyrics.Lines.Count} lines · {lyrics.Source}"
                        : "Untimed lyrics";
                UpdateActiveLyricLine(PositionMs);
                PushLyricFlowToNavigator(trackId);
            }
            catch (OperationCanceledException)
            {
                // superseded by a newer track
            }
            catch (Exception ex)
            {
                LyricsStatusText = "Lyrics unavailable.";
                Log($"Lyrics load failed: {ex.Message}", verbose: true);
            }
        }

        /// <summary>
        /// Map lyrics + analysis sections onto Softmax lyric-flow context and rearm navigator only.
        /// </summary>
        private void PushLyricFlowToNavigator(string trackId = null)
        {
            trackId = trackId
                      ?? CurrentLyrics?.TrackId
                      ?? _loopController.CurrentTrackId
                      ?? CurrentTrackId;

            if (CurrentLyrics == null || string.IsNullOrEmpty(trackId))
            {
                _loopController.SetLyricFlowContext(null);
                return;
            }

            var graph = _loopController.GetGraphForTrack(trackId);
            var analysis = AnalysisCache.Load(trackId);
            IReadOnlyList<long> starts = null;
            if (graph?.Beats != null && graph.Beats.Count > 0)
                starts = graph.Beats.Select(b => b.StartMs).ToList();
            else if (analysis?.Beats != null && analysis.Beats.Count > 0)
                starts = analysis.Beats.Select(b => (long)(b.Start * 1000)).ToList();

            if (starts == null || starts.Count == 0)
            {
                _loopController.SetLyricFlowContext(null);
                return;
            }

            var ctx = LyricBeatMapper.BuildContext(CurrentLyrics, starts, analysis?.Sections);
            _loopController.SetLyricFlowContext(ctx);
        }

        private void UpdateActiveLyricLine(long positionMs)
        {
            var lyrics = CurrentLyrics;
            if (lyrics?.Lines == null || lyrics.Lines.Count == 0)
            {
                if (_activeLyricIndex != -1 || LyricDisplayLines.Count > 0)
                {
                    _activeLyricIndex = -1;
                    ActiveLyricText = string.Empty;
                    PreviousLyricText = string.Empty;
                    NextLyricText = string.Empty;
                    LyricDisplayLines.Clear();
                }

                return;
            }

            var index = LyricBeatMapper.FindActiveLineIndex(lyrics, positionMs);
            if (index == _activeLyricIndex && LyricDisplayLines.Count > 0)
                return;

            _activeLyricIndex = index;
            ActiveLyricText = index >= 0 ? lyrics.Lines[index].Text : string.Empty;
            PreviousLyricText = index > 0 ? lyrics.Lines[index - 1].Text : string.Empty;
            NextLyricText = index >= 0 && index + 1 < lyrics.Lines.Count
                ? lyrics.Lines[index + 1].Text
                : string.Empty;
            RaisePropertyChanged(nameof(ActiveLyricText));

            // Karaoke column: small window of lines that scrolls as the active line advances.
            const int before = 3;
            const int after = 4;
            var start = Math.Max(0, index - before);
            var end = Math.Min(lyrics.Lines.Count - 1, Math.Max(index, 0) + after);
            if (index < 0)
            {
                start = 0;
                end = Math.Min(lyrics.Lines.Count - 1, after);
            }

            var needed = end - start + 1;
            while (LyricDisplayLines.Count < needed)
                LyricDisplayLines.Add(new LyricDisplayLine());
            while (LyricDisplayLines.Count > needed)
                LyricDisplayLines.RemoveAt(LyricDisplayLines.Count - 1);

            for (var i = 0; i < needed; i++)
            {
                var lineIndex = start + i;
                var row = LyricDisplayLines[i];
                var dist = index < 0 ? lineIndex + 1 : Math.Abs(lineIndex - index);
                row.Text = lyrics.Lines[lineIndex].Text ?? string.Empty;
                row.IsActive = lineIndex == index;
                row.IsNear = !row.IsActive && dist == 1;
                row.FontSize = row.IsActive ? 22 : row.IsNear ? 15 : 12.5;
                row.Opacity = row.IsActive ? 1.0 : row.IsNear ? 0.72 : Math.Max(0.22, 0.55 - dist * 0.1);
            }
        }

        private void CycleSessionRepeatMode()
        {
            switch (SessionRepeatMode)
            {
                case SessionRepeatMode.Off:
                    SessionRepeatMode = SessionRepeatMode.One;
                    break;
                case SessionRepeatMode.One:
                    SessionRepeatMode = SessionRepeatMode.All;
                    break;
                default:
                    SessionRepeatMode = SessionRepeatMode.Off;
                    break;
            }

            Log($"Session repeat: {SessionRepeatMode}.", verbose: true);
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
                JukeboxRandomBranches = profile == null || profile.RandomBranches;
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
            profile.RandomBranches = JukeboxRandomBranches;

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
                    ApplyLocalModifierForJump(e.FromBeatIndex, e.ToBeatIndex);
                }

                var chance = _loopController.NavigatorBranchChance;
                var suffix = chance.HasValue ? $" · now {chance.Value:P0}" : "";
                JukeboxBranchChanceText = e.IsPlanned
                    ? $"planning (dist {e.BranchDistance:0}){suffix}"
                    : $"jumped (dist {e.BranchDistance:0}){suffix}";
            });
        }

        /// <summary>Local WAV only — engage the effect chain for a modifier on this hop.</summary>
        private void ApplyLocalModifierForJump(int fromBeat, int toBeat)
        {
            if (_transportRouter.Source != JukeboxPlaybackSource.Local)
            {
                _transportRouter.Local.ClearBranchModifier();
                return;
            }

            var mod = RingBranchModifiers?.FirstOrDefault(m =>
                m.Enabled && m.FromBeatIndex == fromBeat && m.ToBeatIndex == toBeat);

            if (mod != null)
                _transportRouter.Local.ApplyBranchModifier(mod);
            else
                _transportRouter.Local.ClearBranchModifier();
        }

        private void ToggleRingLock(RingBranchClick click)
        {
            if (click == null)
                return;

            var graph = RingGraph;
            var trackId = _loopController.CurrentTrackId;
            var fromBeat = click.FromBeatIndex;
            var toBeat = click.ToBeatIndex;

            if (graph == null || trackId == null || fromBeat < 0 || fromBeat >= graph.Beats.Count ||
                toBeat < 0 || toBeat >= graph.Beats.Count)
                return;

            var profile = _loopController.GetProfileForTrack(trackId);

            if (profile.LockedBranches == null)
                profile.LockedBranches = new List<BranchLock>();

            var existing = profile.LockedBranches
                .FirstOrDefault(l => l.FromBeatIndex == fromBeat && l.ToBeatIndex == toBeat);

            if (existing != null)
            {
                profile.LockedBranches.Remove(existing);
                if (profile.BranchModifiers != null)
                {
                    profile.BranchModifiers.RemoveAll(m =>
                        m.FromBeatIndex == fromBeat && m.ToBeatIndex == toBeat);
                }

                if (_transportRouter.Source == JukeboxPlaybackSource.Local)
                    _transportRouter.Local.ClearBranchModifier();

                Log($"Ring: unlocked branch beat {fromBeat} → {toBeat}.");
            }
            else
            {
                profile.LockedBranches.Add(new BranchLock
                {
                    FromBeatIndex = fromBeat,
                    ToBeatIndex = toBeat,
                    Probability = 1.0
                });

                Log($"Ring: locked branch beat {fromBeat} → {toBeat}.");
            }

            _loopController.ApplyProfile(profile);
            RefreshRingLocks(profile);
        }

        private void StretchRingModifier(RingModifierStretch stretch)
        {
            if (stretch == null || _transportRouter.Source != JukeboxPlaybackSource.Local)
                return;

            var trackId = _loopController.CurrentTrackId;
            if (trackId == null)
                return;

            var profile = _loopController.GetProfileForTrack(trackId);
            if (profile.LockedBranches == null ||
                !profile.LockedBranches.Any(l =>
                    l.FromBeatIndex == stretch.FromBeatIndex && l.ToBeatIndex == stretch.ToBeatIndex))
            {
                Log("Ring: lock a branch before stretching a Local WAV modifier.");
                return;
            }

            if (profile.BranchModifiers == null)
                profile.BranchModifiers = new List<BranchModifier>();

            var existing = profile.BranchModifiers.FirstOrDefault(m =>
                m.FromBeatIndex == stretch.FromBeatIndex && m.ToBeatIndex == stretch.ToBeatIndex);

            var tier = stretch.Turbo ? ModifierTiers.Turbocharge : ModifierTiers.Supercharge;
            if (existing == null)
            {
                existing = BranchModifier.CreatePreset(stretch.FromBeatIndex, stretch.ToBeatIndex, tier);
                profile.BranchModifiers.Add(existing);
                Log($"Ring: {tier} modifier on beat {stretch.FromBeatIndex} → {stretch.ToBeatIndex} (Local WAV).");
            }

            existing.Stretch = Math.Max(0.05, Math.Min(1.0, stretch.Stretch01));
            existing.Enabled = true;
            if (stretch.Turbo)
                existing.Tier = ModifierTiers.Turbocharge;

            _loopController.ApplyProfile(profile);
            RefreshRingLocks(profile);
            _transportRouter.Local.ApplyBranchModifier(existing);
        }

        private void CycleRingModifier(RingBranchClick click)
        {
            if (click == null || _transportRouter.Source != JukeboxPlaybackSource.Local)
                return;

            var trackId = _loopController.CurrentTrackId;
            if (trackId == null)
                return;

            var profile = _loopController.GetProfileForTrack(trackId);
            if (profile.LockedBranches == null ||
                !profile.LockedBranches.Any(l =>
                    l.FromBeatIndex == click.FromBeatIndex && l.ToBeatIndex == click.ToBeatIndex))
                return;

            if (profile.BranchModifiers == null)
                profile.BranchModifiers = new List<BranchModifier>();

            var existing = profile.BranchModifiers.FirstOrDefault(m =>
                m.FromBeatIndex == click.FromBeatIndex && m.ToBeatIndex == click.ToBeatIndex);

            if (existing == null)
            {
                existing = BranchModifier.CreatePreset(click.FromBeatIndex, click.ToBeatIndex,
                    ModifierTiers.Supercharge);
                profile.BranchModifiers.Add(existing);
                Log($"Ring: supercharge beat {click.FromBeatIndex} → {click.ToBeatIndex} (Local WAV).");
            }
            else if (string.Equals(existing.Tier, ModifierTiers.Supercharge, StringComparison.OrdinalIgnoreCase))
            {
                var turbo = BranchModifier.CreatePreset(click.FromBeatIndex, click.ToBeatIndex,
                    ModifierTiers.Turbocharge);
                turbo.Stretch = Math.Max(existing.Stretch, 0.7);
                profile.BranchModifiers.Remove(existing);
                profile.BranchModifiers.Add(turbo);
                existing = turbo;
                Log($"Ring: turbocharge beat {click.FromBeatIndex} → {click.ToBeatIndex} (Local WAV).");
            }
            else
            {
                profile.BranchModifiers.Remove(existing);
                _transportRouter.Local.ClearBranchModifier();
                existing = null;
                Log($"Ring: removed modifier beat {click.FromBeatIndex} → {click.ToBeatIndex}.");
            }

            _loopController.ApplyProfile(profile);
            RefreshRingLocks(profile);
            if (existing != null)
                _transportRouter.Local.ApplyBranchModifier(existing);
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
            if (profile.BranchModifiers != null)
                profile.BranchModifiers.Clear();
            if (_transportRouter.Source == JukeboxPlaybackSource.Local)
                _transportRouter.Local.ClearBranchModifier();
            _loopController.ApplyProfile(profile);
            RefreshRingLocks(profile);
            Log("Ring: cleared all locked branches.");
        }

        private void ClearBranchPreferences()
        {
            _loopController.ClearBranchPreferences();
            RaisePropertyChanged(nameof(BranchPreferenceStatusText));
            ClearBranchPreferencesCommand?.RaiseCanExecuteChanged();
            Log("Jukebox: cleared preference labels (w_pref has nothing to steer until new hops/scrubs).");
        }

        private void RefreshRingLocks(LoopProfile profile)
        {
            RingLockedBranches = profile?.LockedBranches?.ToList()
                                 ?? (IReadOnlyList<BranchLock>)Array.Empty<BranchLock>();
            RingBranchModifiers = profile?.BranchModifiers?.ToList()
                                  ?? (IReadOnlyList<BranchModifier>)Array.Empty<BranchModifier>();
            RingLockCountText = RingLockedBranches.Count == 0
                ? "no locks"
                : RingBranchModifiers.Count > 0
                    ? $"{RingLockedBranches.Count} locked · {RingBranchModifiers.Count} mod"
                    : $"{RingLockedBranches.Count} locked";
            RaisePropertyChanged(nameof(RingLockCount));
            RefreshBranchPresetsForSelection();
        }

        private void RefreshBranchPresetsForSelection()
        {
            BranchPresets.Clear();
            SelectedBranchPreset = null;

            var trackId = SelectedSessionTrack?.TrackId ?? _loopController.CurrentTrackId;

            if (string.IsNullOrEmpty(trackId))
            {
                LoadBranchPresetCommand.RaiseCanExecuteChanged();
                SaveBranchPresetCommand.RaiseCanExecuteChanged();
                return;
            }

            var profile = _loopController.GetProfileForTrack(trackId);

            if (profile?.LockPresets == null)
            {
                LoadBranchPresetCommand.RaiseCanExecuteChanged();
                SaveBranchPresetCommand.RaiseCanExecuteChanged();
                return;
            }

            foreach (var preset in profile.LockPresets.OrderBy(p => p.Name))
                BranchPresets.Add(preset);

            LoadBranchPresetCommand.RaiseCanExecuteChanged();
            SaveBranchPresetCommand.RaiseCanExecuteChanged();
        }

        private bool CanSaveBranchPreset()
        {
            return SelectedSessionTrack != null ||
                   !string.IsNullOrEmpty(_loopController.CurrentTrackId);
        }

        private bool CanLoadBranchPreset()
        {
            if (ResolvePresetToLoad() == null)
                return false;

            return SelectedSessionTrack != null ||
                   !string.IsNullOrEmpty(_loopController.CurrentTrackId);
        }

        private BranchLockPreset ResolvePresetToLoad()
        {
            if (SelectedBranchPreset != null)
                return SelectedBranchPreset;

            var typed = (BranchPresetName ?? string.Empty).Trim();

            if (string.IsNullOrEmpty(typed))
                return null;

            return BranchPresets.FirstOrDefault(p =>
                string.Equals(p.Name, typed, StringComparison.OrdinalIgnoreCase));
        }

        private void LoadSelectedBranchPreset()
        {
            var preset = ResolvePresetToLoad();
            var trackId = SelectedSessionTrack?.TrackId ?? _loopController.CurrentTrackId;

            if (preset == null || string.IsNullOrEmpty(trackId))
                return;

            SelectedBranchPreset = preset;
            BranchPresetName = preset.Name;

            var track = SelectedSessionTrack ??
                        SessionTracks.FirstOrDefault(t => t.TrackId == trackId);
            var displayName = track?.DisplayName ?? trackId;

            var profile = _loopController.GetProfileForTrack(trackId);
            profile.LockedBranches = preset.LockedBranches?
                .Select(CloneBranchLock).ToList() ?? new List<BranchLock>();
            profile.BranchModifiers = preset.BranchModifiers?
                .Select(CloneBranchModifier).ToList() ?? new List<BranchModifier>();

            profile.RandomBranches = preset.RandomBranches;

            var currentFingerprint = AnalysisCache.ComputeFingerprint(trackId);

            if (!string.IsNullOrEmpty(preset.AnalysisFingerprint) &&
                !string.IsNullOrEmpty(currentFingerprint) &&
                !string.Equals(preset.AnalysisFingerprint, currentFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                Log(
                    $"Warning: branch preset \"{preset.Name}\" was saved against a different analysis " +
                    $"(fingerprint mismatch). Locked beat indices may be wrong — re-lock after re-analyze.");
            }

            if (preset.SettingsSnapshot != null)
            {
                ApplySettingsSnapshot(preset.SettingsSnapshot);
            }

            _loopRegionStore.Save(profile);

            if (trackId == _loopController.CurrentTrackId)
            {
                _loopController.ApplyProfile(profile);
                JukeboxRandomBranches = preset.RandomBranches;
            }

            RefreshRingLocks(profile);
            Log($"Loaded branch preset \"{preset.Name}\" for {displayName}.");
        }

        private void ApplySettingsSnapshot(JukeboxSettings snapshot)
        {
            if (snapshot == null)
                return;

            _jukeboxSettingsModel.BranchSimilarityThresholdMax = snapshot.BranchSimilarityThresholdMax;
            _jukeboxSettingsModel.BranchProbabilityMin = snapshot.BranchProbabilityMin;
            _jukeboxSettingsModel.BranchProbabilityMax = snapshot.BranchProbabilityMax;
            _jukeboxSettingsModel.BranchProbabilityRampPerBeat = snapshot.BranchProbabilityRampPerBeat;
            _jukeboxSettingsModel.SeekLeadMs = snapshot.SeekLeadMs;
            _jukeboxSettingsModel.AllowOnlyReverseBranches = snapshot.AllowOnlyReverseBranches;
            _jukeboxSettingsModel.AllowOnlyLongBranches = snapshot.AllowOnlyLongBranches;
            _jukeboxSettingsModel.LongBranchMinBeats = snapshot.LongBranchMinBeats;
            _jukeboxSettingsModel.EnableEndLoop = snapshot.EnableEndLoop;
            if (!string.IsNullOrEmpty(snapshot.PlaybackSource))
                _jukeboxSettingsModel.PlaybackSource = snapshot.PlaybackSource;
            _jukeboxSettingsModel.MinimumJumpBeats = snapshot.MinimumJumpBeats;
            _jukeboxSettingsModel.ClassicMaxNeighbors = snapshot.ClassicMaxNeighbors;
            if (!string.IsNullOrEmpty(snapshot.PhasePenaltyMode))
                _jukeboxSettingsModel.PhasePenaltyMode = snapshot.PhasePenaltyMode;
            _jukeboxSettingsModel.PhraseAlignBeats = snapshot.PhraseAlignBeats;
            if (!string.IsNullOrEmpty(snapshot.BeatTrackerMode))
                _jukeboxSettingsModel.BeatTrackerMode = snapshot.BeatTrackerMode;
            if (!string.IsNullOrEmpty(snapshot.GraphMetricMode))
                _jukeboxSettingsModel.GraphMetricMode = snapshot.GraphMetricMode;
            _jukeboxSettingsModel.MinBeatsBetweenJumps = snapshot.MinBeatsBetweenJumps;
            _jukeboxSettingsModel.SoftmaxTemperature = snapshot.SoftmaxTemperature;
            _jukeboxSettingsModel.VisitNoveltyLambda = snapshot.VisitNoveltyLambda;
            _jukeboxSettingsModel.VisitRegionRadiusBeats = snapshot.VisitRegionRadiusBeats;
            _jukeboxSettingsModel.UseMutualKnn = snapshot.UseMutualKnn;
            _jukeboxSettingsModel.EnableSccBridges = snapshot.EnableSccBridges;
            _jukeboxSettingsModel.PreferenceWeight = snapshot.PreferenceWeight;
            _jukeboxSettingsModel.PreferenceSkipWindowMs = snapshot.PreferenceSkipWindowMs > 0
                ? snapshot.PreferenceSkipWindowMs
                : 8000;
            _jukeboxSettingsModel.EssentiaRegionGate = snapshot.EssentiaRegionGate;
            _jukeboxSettingsModel.GateRegionNeighborCount = snapshot.GateRegionNeighborCount;
            _jukeboxSettingsModel.LyricPhraseWeight = snapshot.LyricPhraseWeight;
            _jukeboxSettingsModel.LyricFlowPhraseCuts = snapshot.LyricFlowPhraseCuts;
            _jukeboxSettingsModel.LyricFlowSameSection = snapshot.LyricFlowSameSection;
            _jukeboxSettingsModel.LyricFlowBlockClean = snapshot.LyricFlowBlockClean;
            _jukeboxSettingsModel.ShowLyrics = snapshot.ShowLyrics;

            RaisePropertyChanged(nameof(JukeboxSimilarityThresholdMax));
            RaisePropertyChanged(nameof(JukeboxSimilarityThresholdMaxText));
            RaisePropertyChanged(nameof(JukeboxMinBeatsBetweenJumps));
            RaisePropertyChanged(nameof(JukeboxMinBeatsBetweenJumpsText));
            RaisePropertyChanged(nameof(JukeboxPhraseAlignBeats));
            RaisePropertyChanged(nameof(JukeboxPhraseAlignBeatsText));
            RaisePropertyChanged(nameof(SelectedPhasePenaltyMode));
            RaisePropertyChanged(nameof(JukeboxSoftmaxTemperature));
            RaisePropertyChanged(nameof(JukeboxSoftmaxTemperatureText));
            RaisePropertyChanged(nameof(JukeboxVisitNoveltyLambda));
            RaisePropertyChanged(nameof(JukeboxVisitNoveltyLambdaText));
            RaisePropertyChanged(nameof(JukeboxUseMutualKnn));
            RaisePropertyChanged(nameof(JukeboxEnableSccBridges));
            RaisePropertyChanged(nameof(JukeboxPreferenceWeightPercent));
            RaisePropertyChanged(nameof(JukeboxPreferenceWeightText));
            RaisePropertyChanged(nameof(JukeboxPreferenceSkipWindowSeconds));
            RaisePropertyChanged(nameof(JukeboxPreferenceSkipWindowText));
            RaisePropertyChanged(nameof(BranchPreferenceStatusText));
            RaisePropertyChanged(nameof(JukeboxEssentiaRegionGate));
            RaisePropertyChanged(nameof(LyricPhraseWeight));
            RaisePropertyChanged(nameof(LyricPhraseWeightText));
            RaisePropertyChanged(nameof(LyricFlowPhraseCuts));
            RaisePropertyChanged(nameof(LyricFlowSameSection));
            RaisePropertyChanged(nameof(LyricFlowBlockClean));
            RaisePropertyChanged(nameof(ShowLyricsPanel));
            RaisePropertyChanged(nameof(JukeboxBranchProbabilityMinPercent));
            RaisePropertyChanged(nameof(JukeboxBranchProbabilityMinText));
            RaisePropertyChanged(nameof(JukeboxBranchProbabilityMaxPercent));
            RaisePropertyChanged(nameof(JukeboxBranchProbabilityMaxText));
            RaisePropertyChanged(nameof(JukeboxBranchRampPerBeatPercent));
            RaisePropertyChanged(nameof(JukeboxBranchRampText));
            RaisePropertyChanged(nameof(JukeboxSeekLeadMs));
            RaisePropertyChanged(nameof(JukeboxSeekLeadMsText));
            RaisePropertyChanged(nameof(JukeboxAllowOnlyReverseBranches));
            RaisePropertyChanged(nameof(JukeboxAllowOnlyLongBranches));
            RaisePropertyChanged(nameof(JukeboxEnableEndLoop));
            RaisePropertyChanged(nameof(SelectedBeatTrackerMode));
            RaisePropertyChanged(nameof(SelectedGraphMetricMode));
            ApplyPlaybackSourceFromSettings();
            RaisePropertyChanged(nameof(UseLocalPlayback));
            RaisePropertyChanged(nameof(PlaybackSourceLabel));
            PushLyricFlowToNavigator();

            PersistJukeboxSettings(invalidateGraph: true);
        }

        private void SaveBranchPreset()
        {
            var trackId = SelectedSessionTrack?.TrackId ?? _loopController.CurrentTrackId;

            if (trackId == null)
                return;

            var profile = _loopController.GetProfileForTrack(trackId);

            if (profile.LockPresets == null)
                profile.LockPresets = new List<BranchLockPreset>();

            var typedName = (BranchPresetName ?? string.Empty).Trim();
            var name = string.IsNullOrWhiteSpace(typedName)
                ? $"Setup {profile.LockPresets.Count + 1}"
                : typedName;

            var fingerprint = AnalysisCache.ComputeFingerprint(trackId);
            var existing = profile.LockPresets.FirstOrDefault(p =>
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

            BranchLockPreset preset;

            if (existing != null)
            {
                existing.RandomBranches = JukeboxRandomBranches;
                existing.AnalysisFingerprint = fingerprint;
                existing.SettingsSnapshot = CloneJukeboxSettings(_jukeboxSettingsModel);
                existing.LockedBranches = profile.LockedBranches?
                    .Select(CloneBranchLock).ToList() ?? new List<BranchLock>();
                existing.BranchModifiers = profile.BranchModifiers?
                    .Select(CloneBranchModifier).ToList() ?? new List<BranchModifier>();
                preset = existing;
            }
            else
            {
                preset = new BranchLockPreset
                {
                    Name = name,
                    RandomBranches = JukeboxRandomBranches,
                    AnalysisFingerprint = fingerprint,
                    SettingsSnapshot = CloneJukeboxSettings(_jukeboxSettingsModel),
                    LockedBranches = profile.LockedBranches?
                        .Select(CloneBranchLock).ToList() ?? new List<BranchLock>(),
                    BranchModifiers = profile.BranchModifiers?
                        .Select(CloneBranchModifier).ToList() ?? new List<BranchModifier>()
                };
                profile.LockPresets.Add(preset);
            }

            _loopRegionStore.Save(profile);
            RefreshBranchPresetsForSelection();
            SelectedBranchPreset = BranchPresets.FirstOrDefault(p =>
                string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase)) ?? preset;
            BranchPresetName = preset.Name;
            SaveBranchPresetCommand.RaiseCanExecuteChanged();
            LoadBranchPresetCommand.RaiseCanExecuteChanged();
            Log($"Saved tune + branches \"{preset.Name}\" ({preset.LockedBranches.Count} locks" +
                (preset.BranchModifiers?.Count > 0 ? $", {preset.BranchModifiers.Count} mods" : "") +
                (fingerprint != null ? ", fingerprint ok" : ", no analysis yet") + ").");
        }

        private static BranchLock CloneBranchLock(BranchLock source)
        {
            if (source == null)
                return null;

            // Clamp; missing JSON field deserializes via property initializer as 1.0.
            var probability = Math.Max(0, Math.Min(1, source.Probability));

            return new BranchLock
            {
                FromBeatIndex = source.FromBeatIndex,
                ToBeatIndex = source.ToBeatIndex,
                Probability = probability
            };
        }

        private static BranchModifier CloneBranchModifier(BranchModifier source)
        {
            if (source == null)
                return null;

            return new BranchModifier
            {
                FromBeatIndex = source.FromBeatIndex,
                ToBeatIndex = source.ToBeatIndex,
                Tier = source.Tier,
                Stretch = source.Stretch,
                Enabled = source.Enabled,
                EqLowDb = source.EqLowDb,
                EqMidDb = source.EqMidDb,
                EqHighDb = source.EqHighDb,
                GainDb = source.GainDb,
                Drive = source.Drive,
                OverlayPaths = source.OverlayPaths != null
                    ? new List<string>(source.OverlayPaths)
                    : new List<string>()
            };
        }

        private static JukeboxSettings CloneJukeboxSettings(JukeboxSettings source)
        {
            if (source == null)
                return JukeboxSettings.CreateDefaults();

            return new JukeboxSettings
            {
                BranchSimilarityThresholdMax = source.BranchSimilarityThresholdMax,
                BranchProbabilityMin = source.BranchProbabilityMin,
                BranchProbabilityMax = source.BranchProbabilityMax,
                BranchProbabilityRampPerBeat = source.BranchProbabilityRampPerBeat,
                SeekLeadMs = source.SeekLeadMs,
                AllowOnlyReverseBranches = source.AllowOnlyReverseBranches,
                AllowOnlyLongBranches = source.AllowOnlyLongBranches,
                LongBranchMinBeats = source.LongBranchMinBeats,
                EnableEndLoop = source.EnableEndLoop,
                PlaybackSource = source.PlaybackSource,
                MinimumJumpBeats = source.MinimumJumpBeats,
                ClassicMaxNeighbors = source.ClassicMaxNeighbors,
                PhasePenaltyMode = source.PhasePenaltyMode,
                PhraseAlignBeats = source.PhraseAlignBeats,
                BeatTrackerMode = source.BeatTrackerMode,
                GraphMetricMode = source.GraphMetricMode,
                MinBeatsBetweenJumps = source.MinBeatsBetweenJumps,
                SoftmaxTemperature = source.SoftmaxTemperature,
                VisitNoveltyLambda = source.VisitNoveltyLambda,
                VisitRegionRadiusBeats = source.VisitRegionRadiusBeats,
                UseMutualKnn = source.UseMutualKnn,
                EnableSccBridges = source.EnableSccBridges,
                PreferenceWeight = source.PreferenceWeight,
                PreferenceSkipWindowMs = source.PreferenceSkipWindowMs,
                EssentiaRegionGate = source.EssentiaRegionGate,
                GateRegionNeighborCount = source.GateRegionNeighborCount,
                LyricPhraseWeight = source.LyricPhraseWeight,
                LyricFlowPhraseCuts = source.LyricFlowPhraseCuts,
                LyricFlowSameSection = source.LyricFlowSameSection,
                LyricFlowBlockClean = source.LyricFlowBlockClean,
                ShowLyrics = source.ShowLyrics
            };
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

        private void PersistJukeboxSettings(bool invalidateGraph = false)
        {
            if (_sliderDragDepth > 0)
            {
                _jukeboxSettingsDirty = true;
                return;
            }

            PersistJukeboxSettingsNow(invalidateGraph);
        }

        private void PersistJukeboxSettingsNow(bool invalidateGraph = false)
        {
            // Save raises SettingsChanged → LoopController rearms navigator.
            _jukeboxSettings.Save(_jukeboxSettingsModel);

            var trackId = _loopController.CurrentTrackId;

            if (invalidateGraph)
                _loopController.InvalidateGraphCache();

            if (!_jukeboxSuppressedForCapture && trackId != null)
                RebuildBeatGraphForTrack(trackId, invalidateCache: false);
        }

        private int _ringGraphBuildGeneration;

        private void SaveWeightsDeferred()
        {
            if (_sliderDragDepth > 0)
            {
                _weightsDirty = true;
                return;
            }

            _predictor.SaveWeights(_weights);
        }

        /// <summary>
        /// Rebuilds the beat graph from cached analysis using current tuning — no audio capture or librosa.
        /// </summary>
        private void RebuildBeatGraphForTrack(string trackId, bool invalidateCache = true)
        {
            if (string.IsNullOrEmpty(trackId))
                return;

            var analysis = AnalysisCache.Load(trackId);

            if (analysis == null)
            {
                // Still drop stale graphs when analysis was deleted mid-rebuild race.
                if (invalidateCache)
                    _loopController.InvalidateGraphCache();

                var activeMissing = _loopController.CurrentTrackId ?? _currentPlay?.TrackId;
                if (string.Equals(trackId, activeMissing, StringComparison.Ordinal))
                    RefreshRingVisualization(trackId);

                return;
            }

            if (invalidateCache)
                _loopController.InvalidateGraphCache();

            var activeId = _loopController.CurrentTrackId ?? _currentPlay?.TrackId;

            if (!string.IsNullOrEmpty(activeId) &&
                !string.Equals(trackId, activeId, StringComparison.Ordinal))
                return;

            RefreshRingVisualization(trackId);

            if (!_jukeboxSuppressedForCapture)
                ApplyLoopSettings();
        }

        private void RefreshRingVisualization(string trackId)
        {
            RingPlannedFromBeat = -1;
            RingPlannedToBeat = -1;

            // Invalidate in-flight async builds (delete / re-analyze / tracker switch).
            var buildGeneration = ++_ringGraphBuildGeneration;

            if (string.IsNullOrEmpty(trackId))
            {
                RingGraph = null;
                RingSectionStartsSec = Array.Empty<double>();
                RingStackedFeatures = null;
                RingBeatFeatures = null;
                RingInspectBeat = -1;
                RingSegmentCountText = "No track playing";
                _visualEnergy.Clear();
                return;
            }

            var analysis = AnalysisCache.Load(trackId);

            if (analysis == null)
            {
                _loopController.InvalidateGraphCache();
                RingGraph = null;
                RingSectionStartsSec = Array.Empty<double>();
                RingStackedFeatures = null;
                RingBeatFeatures = null;
                RingSegmentCountText = "No beat map — analyze track to build the ring";
                _visualEnergy.Clear();
                RefreshTrackStatusHud();
                return;
            }

            var fingerprint = AnalysisCache.ComputeFingerprint(trackId) ?? string.Empty;

            _visualEnergy.LoadAnalysis(analysis);

            RingDurationMs = (long)(analysis.DurationSec * 1000);
            RingSectionStartsSec = analysis.Sections != null && analysis.Sections.Count > 0
                ? analysis.Sections.Select(s => s.Start).ToList()
                : (IReadOnlyList<double>)Array.Empty<double>();
            RingStackedFeatures = analysis.HasClassicFeatures ? analysis.StackedFeatures : null;
            RingBeatFeatures = analysis.HasClassicFeatures ? analysis.BeatFeatures : null;
            // Clear immediately so BeatThis→DP doesn't keep showing the old ring while building.
            RingGraph = null;
            RingSegmentCountText = "Building beat graph…";
            RefreshTrackStatusHud();

            // Clear cache without sync Rearm — ApplyLoopSettings / async completion will rearm.
            // Avoids an O(beats²) UI-thread rebuild before Task.Run finishes.
            _loopController.InvalidateGraphCache(rearmIfActive: false);

            // The graph is O(beats²) to build; keep the UI thread free.
            Task.Run(() => _loopController.GetGraphForTrack(trackId)).ContinueWith(task =>
                RunOnUiThread(() =>
                {
                    // Drop superseded builds (delete, re-analyze, tracker switch, track change).
                    if (buildGeneration != _ringGraphBuildGeneration)
                        return;

                    var activeId = _loopController.CurrentTrackId ?? _currentPlay?.TrackId;
                    if (!string.IsNullOrEmpty(activeId) &&
                        !string.Equals(trackId, activeId, StringComparison.Ordinal))
                        return;

                    var currentFingerprint = AnalysisCache.ComputeFingerprint(trackId) ?? string.Empty;
                    if (!string.Equals(fingerprint, currentFingerprint, StringComparison.Ordinal))
                        return;

                    if (task.IsFaulted)
                    {
                        RingGraph = null;
                        RingSegmentCountText = "Beat graph failed — see activity log";
                        Log($"Beat graph failed for {trackId}: {task.Exception?.GetBaseException().Message}");
                        return;
                    }

                    var graph = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
                    RingGraph = graph;
                    RingSegmentCountText = graph == null
                        ? "No beat map — analyze track to build the ring"
                        : $"{graph.Beats.Count} beats · {graph.TotalBranchCount} branches · {FormatMetricModeLabel(graph.MetricMode)}";
                    RefreshTrackStatusHud();

                    // Remap lyric Softmax context once the beat graph exists (navigator rearm only).
                    if (graph?.Beats != null && CurrentLyrics?.Lines != null &&
                        string.Equals(CurrentLyrics.TrackId, trackId, StringComparison.OrdinalIgnoreCase))
                    {
                        PushLyricFlowToNavigator(trackId);
                    }
                }));
        }

        /// <summary>Invoked when a track finishes without user intervention (non-loop mode).</summary>
        private async void OnTrackEndedNaturally(string trackId)
        {
            _lastEndedTrackId = trackId;

            await RefreshPredictionsAsync();

            // Repeat one: replay the same session track when the song actually ends
            // (usually only when End loop is off).
            if (SessionRepeatMode == SessionRepeatMode.One &&
                !string.IsNullOrEmpty(trackId) &&
                !_loopController.IsLoopActive)
            {
                var same = SessionTracks.FirstOrDefault(t => t.TrackId == trackId)
                           ?? SelectedSessionTrack;
                if (same != null)
                {
                    Log("Repeat one — replaying current track.");
                    await PlaySessionTrackAsync(same);
                    return;
                }
            }

            if (SessionRepeatMode == SessionRepeatMode.All &&
                SessionTracks.Count > 0 &&
                !_loopController.IsLoopActive)
            {
                await NavigateAndPlaySessionTrackAsync(1);
                return;
            }

            if (AutoPlayNext && !IsAnalyzing && !_loopController.IsLoopActive && Predictions.Count > 0)
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
            var selectedId = SelectedSessionTrack?.TrackId;

            SessionTracks.Clear();

            foreach (var track in _sessionStore.Tracks)
            {
                SyncSessionTrackCacheFlags(track);
                SessionTracks.Add(track);
            }

            if (!string.IsNullOrEmpty(selectedId))
            {
                var restored = SessionTracks.FirstOrDefault(t => t.TrackId == selectedId);

                if (restored != null && !ReferenceEquals(SelectedSessionTrack, restored))
                    SelectedSessionTrack = restored;
            }
            else
            {
                var currentId = _loopController.CurrentTrackId ?? _currentPlay?.TrackId;
                var playing = !string.IsNullOrEmpty(currentId)
                    ? SessionTracks.FirstOrDefault(t => t.TrackId == currentId)
                    : null;

                if (playing != null)
                    SelectedSessionTrack = playing;
            }

            ClearSessionCommand?.RaiseCanExecuteChanged();
            PreviousSessionTrackCommand?.RaiseCanExecuteChanged();
            NextSessionTrackCommand?.RaiseCanExecuteChanged();
            SaveBranchPresetCommand?.RaiseCanExecuteChanged();
            LoadBranchPresetCommand?.RaiseCanExecuteChanged();
            DeleteSessionCapturesCommand?.RaiseCanExecuteChanged();
            DeleteSessionAnalysisCommand?.RaiseCanExecuteChanged();
            DeleteSessionTuneCommand?.RaiseCanExecuteChanged();
            ExportSessionAudioCommand?.RaiseCanExecuteChanged();
            ExportSessionBothCommand?.RaiseCanExecuteChanged();
            AbortSessionWorkCommand?.RaiseCanExecuteChanged();
            RaisePropertyChanged(nameof(SessionPlayButtonText));
            RaisePropertyChanged(nameof(SessionActiveTrackId));
        }

        private static void SyncSessionTrackCacheFlags(LoopLabSessionTrack track)
        {
            if (track == null || string.IsNullOrEmpty(track.TrackId))
                return;

            track.HasCapture = WavCaptureValidator.HasCompleteCapture(track.TrackId);
            track.HasAnalysis = AnalysisCache.Exists(track.TrackId);

            if (track.AnalysisStatus != SessionAnalysisStatus.Analyzing &&
                track.AnalysisStatus != SessionAnalysisStatus.Failed)
            {
                track.AnalysisStatus = track.HasAnalysis
                    ? SessionAnalysisStatus.Ready
                    : SessionAnalysisStatus.Pending;
            }
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
                ReportAnalysisMessage("Enter a valid track ID, URI, or open.spotify.com link.");
                return;
            }

            TrackInput = trackId;
            UpsertSessionTrack(trackId, trackId, "");

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

            if (SessionRepeatMode == SessionRepeatMode.All || SessionRepeatMode == SessionRepeatMode.One)
                return true;

            var index = GetSessionTrackIndex();
            var targetIndex = index < 0
                ? (delta > 0 ? 0 : SessionTracks.Count - 1)
                : index + delta;

            return targetIndex >= 0 && targetIndex < SessionTracks.Count;
        }

        private async Task NavigateAndPlaySessionTrackAsync(int delta)
        {
            if (SessionTracks.Count == 0)
                return;

            var index = GetSessionTrackIndex();
            int targetIndex;
            if (index < 0)
            {
                targetIndex = delta > 0 ? 0 : SessionTracks.Count - 1;
            }
            else if (SessionRepeatMode == SessionRepeatMode.One)
            {
                // Stay on / replay the current track.
                targetIndex = index;
            }
            else if (SessionRepeatMode == SessionRepeatMode.All)
            {
                targetIndex = (index + delta) % SessionTracks.Count;
                if (targetIndex < 0)
                    targetIndex += SessionTracks.Count;
            }
            else
            {
                targetIndex = index + delta;
            }

            if (targetIndex < 0 || targetIndex >= SessionTracks.Count)
                return;

            SelectedSessionTrack = SessionTracks[targetIndex];
            await PlaySessionTrackAsync(SelectedSessionTrack);
        }

        private void NavigateSessionTrack(int delta)
        {
            _ = NavigateAndPlaySessionTrackAsync(delta);
        }

        private void RefreshLibraryPlaylists()
        {
            if (_playlistLocalStore == null)
                return;

            var items = _playlistLocalStore.LoadAvailablePlaylists().Values
                .OrderBy(p => p.Name)
                .ToList();

            LibraryPlaylists.Clear();
            foreach (var item in items)
                LibraryPlaylists.Add(item);

            RaisePropertyChanged(nameof(HasLibraryPlaylists));
        }

        public bool HasLibraryPlaylists => LibraryPlaylists.Count > 0;

        private async Task AddLibraryPlaylistToSessionAsync(PlaylistCacheItem playlist)
        {
            if (playlist == null || string.IsNullOrWhiteSpace(playlist.Id) || _spotify.Api == null)
                return;

            try
            {
                Status = $"Adding tracks from '{playlist.Name}'…";
                var added = 0;
                var offset = 0;

                while (true)
                {
                    var page = await _spotify.Api.Playlists.GetItems(playlist.Id,
                        new SpotifyAPI.Web.PlaylistGetItemsRequest(SpotifyAPI.Web.PlaylistGetItemsRequest.AdditionalTypes.All)
                        {
                            Limit = 50,
                            Offset = offset
                        });

                    if (page?.Items == null || page.Items.Count == 0)
                        break;

                    foreach (var item in page.Items)
                    {
                        if (!(item.Track is SpotifyAPI.Web.FullTrack track) || string.IsNullOrWhiteSpace(track.Id))
                            continue;

                        var artist = track.Artists != null && track.Artists.Count > 0
                            ? string.Join(", ", track.Artists.Select(a => a.Name))
                            : "";
                        UpsertSessionTrack(track.Id, track.Name ?? track.Id, artist);
                        added++;
                    }

                    offset += page.Items.Count;
                    if (page.Next == null || page.Items.Count == 0)
                        break;
                }

                Status = added > 0
                    ? $"Added {added} track(s) from '{playlist.Name}'."
                    : $"No playable tracks in '{playlist.Name}'.";
                Log(Status);
            }
            catch (Exception ex)
            {
                Status = "Failed to add playlist tracks.";
                Log($"Failed to add playlist '{playlist.Name}' to session: {ex.Message}");
            }
        }

        private CancellationTokenSource _trackSearchCts;

        private async Task RunTrackSearchAsync(string query)
        {
            _trackSearchCts?.Cancel();
            _trackSearchCts?.Dispose();
            _trackSearchCts = new CancellationTokenSource();
            var token = _trackSearchCts.Token;

            if (_spotify.Api == null || string.IsNullOrWhiteSpace(query) || query.Length < 2)
            {
                TrackSearchResults.Clear();
                IsTrackSearchOpen = false;
                return;
            }

            // Skip pure IDs / URIs — those go through PlayFromInput.
            if (ParseTrackId(query) != null && query.IndexOf(' ') < 0)
            {
                TrackSearchResults.Clear();
                IsTrackSearchOpen = false;
                return;
            }

            try
            {
                await Task.Delay(220, token);

                var req = new SpotifyAPI.Web.SearchRequest(SpotifyAPI.Web.SearchRequest.Types.Track, query)
                {
                    Limit = 8,
                    Offset = 0
                };
                var resp = await _spotify.Api.Search.Item(req);
                token.ThrowIfCancellationRequested();

                var hits = resp?.Tracks?.Items?
                    .Where(t => t != null && !string.IsNullOrWhiteSpace(t.Id))
                    .Select(t => new TrackSearchHit
                    {
                        Id = t.Id,
                        Title = t.Name,
                        Artist = t.Artists != null && t.Artists.Count > 0
                            ? string.Join(", ", t.Artists.Select(a => a.Name))
                            : ""
                    })
                    .ToList() ?? new List<TrackSearchHit>();

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested)
                        return;

                    TrackSearchResults.Clear();
                    foreach (var hit in hits)
                        TrackSearchResults.Add(hit);
                    IsTrackSearchOpen = TrackSearchResults.Count > 0;
                });
            }
            catch (OperationCanceledException)
            {
                // superseded by a newer query
            }
            catch (Exception ex)
            {
                Log($"Track search failed: {ex.Message}", true);
                TrackSearchResults.Clear();
                IsTrackSearchOpen = false;
            }
        }

        private async Task SelectTrackSearchHitAsync(TrackSearchHit hit)
        {
            if (hit == null || string.IsNullOrWhiteSpace(hit.Id))
                return;

            IsTrackSearchOpen = false;
            TrackInput = hit.Id;
            UpsertSessionTrack(hit.Id, hit.Title ?? hit.Id, hit.Artist ?? "");
            await PlayTrackAsync(hit.Id, "search-picker");
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

        private void ReloadSessionTracks(bool silent = false)
        {
            WavCaptureValidator.PurgeIncompleteArtifacts();
            _sessionStore.Load();

            var known = new HashSet<string>(_sessionStore.GetTrackIds(), StringComparer.Ordinal);

            foreach (var trackId in WavCaptureValidator.EnumerateCompleteCaptureTrackIds()
                         .Concat(AnalysisCache.EnumerateCachedTrackIds()))
            {
                if (string.IsNullOrWhiteSpace(trackId) || !known.Add(trackId))
                    continue;

                _sessionStore.AddOrUpdate(new LoopLabSessionTrack
                {
                    TrackId = trackId,
                    Title = trackId,
                    Artist = "",
                    AnalysisStatus = AnalysisCache.Exists(trackId)
                        ? SessionAnalysisStatus.Ready
                        : SessionAnalysisStatus.Pending
                });
            }

            foreach (var track in _sessionStore.Tracks.ToList())
            {
                SyncSessionTrackCacheFlags(track);
                _sessionStore.AddOrUpdate(track);
            }

            RefreshSessionTracks();
            DeleteSessionCacheCommand?.RaiseCanExecuteChanged();

            if (!silent)
                Log($"Refreshed from cache ({SessionTracks.Count} tracks).");
        }

        private void DeleteSelectedSessionCache()
        {
            DeleteSelectedSessionCaptures();
            DeleteSelectedSessionAnalysis();
        }

        private void DeleteSelectedSessionCaptures()
        {
            var track = SelectedSessionTrack;

            if (track == null || IsAnalyzing)
                return;

            WavCaptureValidator.TryDeleteCapture(track.TrackId);
            SyncSessionTrackCacheFlags(track);
            _sessionStore.AddOrUpdate(track);
            RefreshSessionTracks();
            RefreshLocalPlaybackAvailability();
            Log($"Deleted saved captures for {track.DisplayName}.");
        }

        private void DeleteSelectedSessionAnalysis()
        {
            var track = SelectedSessionTrack;

            if (track == null || IsAnalyzing)
                return;

            AnalysisCache.Delete(track.TrackId);
            UpdateSessionTrackAnalysisStatus(track.TrackId, SessionAnalysisStatus.Pending);
            _loopController.InvalidateGraphCache();
            _ringGraphBuildGeneration++;

            var activeId = _loopController.CurrentTrackId ?? _currentPlay?.TrackId;

            if (string.Equals(track.TrackId, activeId, StringComparison.Ordinal))
                RefreshRingVisualization(track.TrackId);

            Log($"Deleted saved analysis for {track.DisplayName}.");
        }

        private void DeleteSelectedSessionTune()
        {
            var track = SelectedSessionTrack;

            if (track == null)
                return;

            _loopRegionStore.ClearTuneAndBranches(track.TrackId);
            RefreshBranchPresetsForSelection();

            if (track.TrackId == _loopController.CurrentTrackId)
                RefreshLoopSettingsFromStore(track.TrackId);

            Log($"Deleted tune+branches for {track.DisplayName}.");
        }

        private void RemoveSelectedSessionTrack()
        {
            var track = SelectedSessionTrack;

            if (track == null)
                return;

            _sessionStore.Remove(track.TrackId);
            SelectedSessionTrack = null;
            RefreshSessionTracks();
            Log($"Removed {track.DisplayName} from the list (cache kept).");
        }

        private void ClearSession()
        {
            foreach (var trackId in _sessionStore.GetTrackIds().ToList())
                _sessionStore.Remove(trackId);

            SelectedSessionTrack = null;
            RefreshSessionTracks();
            Log("Removed all tracks from the list (cache kept). Use Refresh from cache to restore.");
        }

        private async Task AnalyzeTrackByIdAsync(string trackId, string displayName)
        {
            if (IsAnalyzing)
            {
                ReportAnalysisMessage($"Already analyzing {AnalyzingDisplayName}.");
                return;
            }

            var cachedAnalysis = AnalysisCache.Load(trackId);

            if (cachedAnalysis != null)
            {
                RebuildBeatGraphForTrack(trackId);
                ReportAnalysisMessage(
                    $"Beat graph rebuilt: {cachedAnalysis.Beats.Count} beats, " +
                    $"{cachedAnalysis.Segments.Count} segments (cached analysis, tuning applied).");
                UpdateSessionTrackAnalysisStatus(trackId, SessionAnalysisStatus.Ready);
                return;
            }

            if (!await _playbackSessionGate.WaitAsync(0).ConfigureAwait(true))
            {
                ReportAnalysisMessage(
                    $"Player busy — analyzing {AnalyzingDisplayName}. Wait for it to finish.");
                return;
            }

            try
            {
                ITrackAnalysisProvider provider;

                try
                {
                    provider = await _analysisProviderSelector.GetProviderAsync().ConfigureAwait(true);
                }
                catch (InvalidOperationException ex)
                {
                    ReportAnalysisError(ex.Message);
                    return;
                }

                var needsCapture = provider.RequiresPlaybackCapture(trackId);

                _analysisCancellation?.Cancel();
                _analysisCancellation?.Dispose();
                _analysisCancellation = new CancellationTokenSource();
                var analysisToken = _analysisCancellation.Token;

                _analyzingTrackId = trackId;
                _analyzingDisplayName = displayName ?? trackId;
                RaisePropertyChanged(nameof(AnalyzingDisplayName));
                IsAnalyzing = true;
                AnalysisProgressPercent = double.NaN;
                UpdateSessionTrackAnalysisStatus(trackId, SessionAnalysisStatus.Analyzing);

                // Clear previous track's ring immediately so analyze doesn't show stale visuals.
                _ringGraphBuildGeneration++;
                RingGraph = null;
                RingSectionStartsSec = Array.Empty<double>();
                RingStackedFeatures = null;
                RingBeatFeatures = null;
                RingPlannedFromBeat = -1;
                RingPlannedToBeat = -1;
                RingInspectBeat = -1;
                RingResetPlaysToken++;
                RingSegmentCountText = "Analyzing…";
                _visualEnergy.Clear();
                RefreshTrackStatusHud();

                if (needsCapture)
                {
                    _transportRouter.Local.Stop();
                    _captureInProgress = true;
                    NotifyTransportStateChanged();
                    _jukeboxSuppressedForCapture = true;
                    ApplyLoopSettings();
                    Log($"Capturing {displayName ?? trackId} from the start — mute other apps until the track ends.");
                }
                else if (provider.Source == AnalysisSource.Local && !provider.IsCached(trackId))
                {
                    Log("Using cached WAV — running librosa sidecar only.");
                }

                var progress = new Progress<string>(ApplyAnalysisProgress);

                var analysis = await provider.GetAnalysisAsync(trackId, progress, analysisToken)
                    .ConfigureAwait(true);

                AnalysisProgressPercent = 100;
                ReportAnalysisMessage(
                    $"Analysis ready: {analysis.Beats.Count} beats, " +
                    $"{analysis.Segments.Count} segments ({analysis.SourceType}).");
                OnAnalysisCompleted(trackId, analysis);
            }
            catch (OperationCanceledException)
            {
                ReportAnalysisError("Analysis aborted.");
                UpdateSessionTrackAnalysisStatus(trackId, SessionAnalysisStatus.Pending);
            }
            catch (Exception ex)
            {
                ReportAnalysisError($"Analysis failed: {ex.Message}");
                UpdateSessionTrackAnalysisStatus(trackId, SessionAnalysisStatus.Failed);
            }
            finally
            {
                CompleteAnalysisSession();
            }
        }

        private void CompleteAnalysisSession()
        {
            RunOnUiThreadSync(() =>
            {
                _captureInProgress = false;
                AnalysisProgressPercent = double.NaN;
                IsAnalyzing = false;
                _analyzingTrackId = null;
                _analyzingDisplayName = null;
                RaisePropertyChanged(nameof(AnalyzingDisplayName));

                if (_jukeboxSuppressedForCapture)
                {
                    _jukeboxSuppressedForCapture = false;
                    ApplyLoopSettings();
                }

                NotifyTransportStateChanged();
            });

            _analysisCancellation?.Dispose();
            _analysisCancellation = null;
            _playbackSessionGate.Release();
        }

        private void ApplyAnalysisProgress(string message)
        {
            RunOnUiThread(() =>
            {
                AnalysisStatusText = message;
                Status = FormatAnalysisStatusMessage(message);
                Log(message, verbose: true);

                var match = Regex.Match(message ?? string.Empty, @"(\d+)%");

                if (match.Success && int.TryParse(match.Groups[1].Value, out var percent))
                    AnalysisProgressPercent = percent;
                else if (message != null &&
                         (message.IndexOf("sidecar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                          message.IndexOf("Analyzing audio", StringComparison.OrdinalIgnoreCase) >= 0))
                    AnalysisProgressPercent = double.NaN;
            });
        }

        private string FormatAnalysisStatusMessage(string message)
        {
            if (string.IsNullOrEmpty(_analyzingTrackId))
                return message;

            return string.IsNullOrEmpty(AnalyzingDisplayName)
                ? message
                : $"{AnalyzingDisplayName}: {message}";
        }

        private void ReportAnalysisMessage(string message, bool verboseLog = false)
        {
            RunOnUiThread(() =>
            {
                Status = message;

                if (IsAnalyzing)
                    AnalysisStatusText = message;

                Log(message, verbose: verboseLog);
            });
        }

        private void ReportAnalysisError(string message)
        {
            RunOnUiThread(() =>
            {
                Status = message;
                Log(message);
            });
        }

        #endregion

        #region Analysis

        private async Task AnalyzeCurrentTrackAsync()
        {
            var trackId = _loopController.CurrentTrackId;

            if (trackId == null)
            {
                ReportAnalysisMessage("Play a track first, then analyze it.");
                return;
            }

            await AnalyzeTrackByIdAsync(trackId, NowPlayingTitle);
        }

        /// <summary>Re-arms the jukebox once analysis lands for the current track.</summary>
        private void OnAnalysisCompleted(string trackId, Model.Prediction.TrackAnalysis analysis)
        {
            RebuildBeatGraphForTrack(trackId);
            RefreshLocalPlaybackAvailability();

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

            RefreshTrackStatusHud();
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

        private void ExportSessionAudio()
        {
            var track = SelectedSessionTrack;

            if (track == null)
                return;

            var source = PredictionPaths.ResolveAudioCachePath(track.TrackId);

            if (string.IsNullOrEmpty(source) || !File.Exists(source))
            {
                Status = "No capture WAV to export.";
                return;
            }

            var dialog = new SaveFileDialog
            {
                Filter = "WAV audio (*.wav)|*.wav|All files (*.*)|*.*",
                FileName = $"{track.TrackId}.wav",
                Title = "Export capture audio"
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                File.Copy(source, dialog.FileName, overwrite: true);
                Log($"Exported capture audio for {track.DisplayName}.");
                Status = "Capture audio exported.";
            }
            catch (Exception ex)
            {
                Status = $"Export failed: {ex.Message}";
                Log($"Export audio failed: {ex.Message}");
            }
        }

        private void ExportSessionBoth()
        {
            var dialog = new SaveFileDialog
            {
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                FileName = $"spotifywpf-export-{DateTime.Now:yyyyMMdd-HHmmss}.json",
                Title = "Export analysis JSON (WAVs copy beside it)"
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                ExportLoopDataToPath(dialog.FileName);
                var audioDir = Path.Combine(
                    Path.GetDirectoryName(dialog.FileName) ?? ".",
                    Path.GetFileNameWithoutExtension(dialog.FileName) + "-audio");
                Directory.CreateDirectory(audioDir);

                var copied = 0;

                foreach (var trackId in _sessionStore.GetTrackIds())
                {
                    var source = PredictionPaths.ResolveAudioCachePath(trackId);

                    if (string.IsNullOrEmpty(source) || !File.Exists(source))
                        continue;

                    File.Copy(source, Path.Combine(audioDir, trackId + ".wav"), overwrite: true);

                    var meta = PredictionPaths.GetAudioCacheMetadataPath(trackId);

                    if (File.Exists(meta))
                        File.Copy(meta, Path.Combine(audioDir, trackId + ".capture.json"), overwrite: true);

                    copied++;
                }

                Log($"Exported analysis + {copied} capture(s) to {audioDir}.");
                Status = "Exported analysis and audio.";
            }
            catch (Exception ex)
            {
                Status = $"Export failed: {ex.Message}";
                Log($"Export both failed: {ex.Message}");
            }
        }

        private void ExportLoopData()
        {
            var dialog = new SaveFileDialog
            {
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                FileName = $"spotifywpf-loop-data-{DateTime.Now:yyyyMMdd-HHmmss}.json",
                Title = "Export analysis + tune/branches"
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                ExportLoopDataToPath(dialog.FileName);
                Status = "Analysis exported.";
            }
            catch (Exception ex)
            {
                Status = $"Export failed: {ex.Message}";
                Log($"Export failed: {ex.Message}");
            }
        }

        private void ExportLoopDataToPath(string path)
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

            File.WriteAllText(path, JsonSerializer.Serialize(export));
            Log($"Exported {export.LoopRegions.Count} loop profiles and {export.Analyses.Count} analyses for {sessionIds.Count} session tracks.");
        }

        private void ImportLoopData()
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Analysis or audio|*.json;*.wav|JSON analysis (*.json)|*.json|WAV capture (*.wav)|*.wav|All files (*.*)|*.*",
                Title = "Import analysis JSON and/or capture WAV",
                Multiselect = true
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                var analysisCount = 0;
                var audioCount = 0;
                var profileCount = 0;

                foreach (var file in dialog.FileNames)
                {
                    var ext = Path.GetExtension(file);

                    if (string.Equals(ext, ".wav", StringComparison.OrdinalIgnoreCase))
                    {
                        var trackId = Path.GetFileNameWithoutExtension(file);

                        if (string.IsNullOrWhiteSpace(trackId))
                            continue;

                        Directory.CreateDirectory(PredictionPaths.AudioCacheDirectory);
                        var dest = PredictionPaths.GetAudioCachePath(trackId);
                        File.Copy(file, dest, overwrite: true);
                        audioCount++;
                        _sessionStore.AddOrUpdate(new LoopLabSessionTrack
                        {
                            TrackId = trackId,
                            Title = trackId,
                            AnalysisStatus = AnalysisCache.Exists(trackId)
                                ? SessionAnalysisStatus.Ready
                                : SessionAnalysisStatus.Pending
                        });
                        continue;
                    }

                    if (!string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Sidecar capture metadata next to a WAV import, or LoopDataExport.
                    if (file.EndsWith(".capture.json", StringComparison.OrdinalIgnoreCase))
                    {
                        var trackId = Path.GetFileName(file);
                        trackId = trackId.Substring(0, trackId.Length - ".capture.json".Length);
                        Directory.CreateDirectory(PredictionPaths.AudioCacheDirectory);
                        File.Copy(file, PredictionPaths.GetAudioCacheMetadataPath(trackId), overwrite: true);
                        audioCount++;
                        continue;
                    }

                    var import = JsonSerializer.Deserialize<LoopDataExport>(File.ReadAllText(file));

                    if (import == null)
                        continue;

                    if (import.LoopRegions != null)
                    {
                        _loopRegionStore.ImportAll(import.LoopRegions);
                        profileCount += import.LoopRegions.Count;
                    }

                    foreach (var analysis in import.Analyses ?? new List<TrackAnalysis>())
                    {
                        if (analysis == null || string.IsNullOrEmpty(analysis.TrackId))
                            continue;

                        AnalysisCache.Save(analysis);
                        analysisCount++;
                        _sessionStore.AddOrUpdate(new LoopLabSessionTrack
                        {
                            TrackId = analysis.TrackId,
                            AnalysisStatus = SessionAnalysisStatus.Ready
                        });
                    }

                    foreach (var profile in import.LoopRegions ?? new List<LoopProfile>())
                    {
                        if (profile == null || string.IsNullOrEmpty(profile.TrackId))
                            continue;

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
                Log($"Imported {profileCount} tune profile(s), {analysisCount} analysis file(s), {audioCount} audio file(s).");
                Status = "Import complete.";
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

        private static void RunOnUiThreadSync(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;

            if (dispatcher == null || dispatcher.CheckAccess())
                action();
            else
                dispatcher.Invoke(action);
        }

        #endregion
    }
}
