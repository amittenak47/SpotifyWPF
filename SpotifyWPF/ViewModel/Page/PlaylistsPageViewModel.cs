using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AutoMapper;
using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Command;
using Microsoft.Win32;
using SpotifyAPI.Web;
using SpotifyWPF.Model;
using SpotifyWPF.Service;
using SpotifyWPF.Service.MessageBoxes;
using SpotifyWPF.ViewModel.Component;
using static SpotifyWPF.Service.SpotifyApiErrorHelper;
using MessageBoxButton = SpotifyWPF.Service.MessageBoxes.MessageBoxButton;
using MessageBoxResult = SpotifyWPF.Service.MessageBoxes.MessageBoxResult;
// ReSharper disable AsyncVoidLambda

namespace SpotifyWPF.ViewModel.Page
{
    /// <summary>
    /// Orchestrates the Playlists page. Business logic lives in services:
    /// persistence in <see cref="IPlaylistLocalStore"/>, page fetching in
    /// <see cref="IPlaylistPagingService"/>, deletion in
    /// <see cref="IPlaylistDeletionService"/>, the action queue in
    /// <see cref="IPlaylistActionQueueService"/>, request pacing in
    /// <see cref="IRequestSpacingService"/>, and logging in
    /// <see cref="ActivityLogViewModel"/>.
    /// </summary>
    public class PlaylistsPageViewModel : ViewModelBase, IPageLifecycle
    {
        private readonly IMapper _mapper;

        private readonly IMessageBoxService _messageBoxService;
        private readonly ISpotify _spotify;
        private readonly IPlaylistLocalStore _localStore;
        private readonly IPlaylistPagingService _paging;
        private readonly IPlaylistDeletionService _deletionService;
        private readonly IPlaylistActionQueueService _actionQueue;
        private readonly IRequestSpacingService _requestSpacing;
        private readonly DispatcherTimer _playlistGridRefreshTimer;

        private Visibility _progressVisibility = Visibility.Hidden;

        private string _status = "Ready";

        private CancellationTokenSource _currentActionCancellationTokenSource;

        private bool _isActionRunning;

        private QueuedPlaylistAction _selectedQueuedAction;

        private QueuedActionDetailItem _selectedQueuedDetail;

        private string _newPlaylistName;

        private string _newPlaylistDescription;

        private bool _newPlaylistIsPublic;

        private bool _newPlaylistIsCollaborative;

        private string _playlistsFilterText;

        private string _stagedPlaylistsFilterText;

        private int _playlistLoadLimit = 50;

        public PlaylistsPageViewModel(
            ISpotify spotify,
            IMapper mapper,
            IMessageBoxService messageBoxService,
            IPlaylistLocalStore localStore,
            IPlaylistPagingService paging,
            IPlaylistDeletionService deletionService,
            IPlaylistActionQueueService actionQueue,
            IRequestSpacingService requestSpacing)
        {
            _spotify = spotify;
            _mapper = mapper;
            _messageBoxService = messageBoxService;
            _localStore = localStore;
            _paging = paging;
            _deletionService = deletionService;
            _actionQueue = actionQueue;
            _requestSpacing = requestSpacing;

            ActivityLog = new ActivityLogViewModel();

            _localStore.LogMessage += Log;
            _paging.LogMessage += Log;
            _deletionService.LogMessage += Log;
            _actionQueue.LogMessage += Log;
            _actionQueue.StateChanged += RaiseActionQueueStates;

            LoadPlaylistsCommand = new RelayCommand(async () => await LoadPlaylistsAsync());
            LoadMorePlaylistsCommand = new RelayCommand(async () => await LoadMorePlaylistsAsync(), CanLoadMorePlaylists);
            LoadAllPlaylistsCommand = new RelayCommand(async () => await LoadAllPlaylistsAsync());
            LoadTracksCommand = new RelayCommand<PlaylistCacheItem>(async playlist => await LoadTracksAsync(playlist));
            OpenInLoopLabCommand = new RelayCommand<PlaylistCacheItem>(OpenInLoopLab);
            RefreshSelectedPlaylistsCommand = new RelayCommand<IList>(async playlists => await RefreshSelectedPlaylistsAsync(playlists));
            CancelCurrentActionCommand = new RelayCommand(CancelCurrentAction, CanCancelCurrentAction);
            AbortQueuedActionCommand = new RelayCommand(AbortQueuedAction, CanAbortQueuedAction);
            ExecuteOrPauseCommand = new RelayCommand(async () => await ExecuteOrPauseAsync(), CanExecuteOrPause);
            StagePlaylistsCommand = new RelayCommand<IList>(StagePlaylists);
            UnstagePlaylistsCommand = new RelayCommand<IList>(UnstagePlaylists);
            ToggleStageCommand = new RelayCommand<IList>(ToggleStage);
            MarkForDeletionCommand = new RelayCommand<IList>(MarkForDeletion, CanMarkForDeletion);
            UnmarkForDeletionCommand = new RelayCommand<IList>(UnmarkForDeletion, CanUnmarkForDeletion);
            ToggleMarkCommand = new RelayCommand<IList>(ToggleMark);
            DeletePlaylistsCommand = new RelayCommand(async () => await DeletePlaylistsAsync(), CanDeleteMarkedPlaylists);
            ToggleDeleteQueueCommand = new RelayCommand<IList>(ToggleDeleteQueue);
            EnqueueDeleteKeyCommand = new RelayCommand<IList>(EnqueueDeleteSelectionOnly);
            DeleteAllToQueueCommand = new RelayCommand(DeleteAllToQueue, CanDeleteAllToQueue);
            RefreshDeletionResultsCommand = new RelayCommand(RefreshDeletionResults);
            RefreshCombinedCommand = new RelayCommand<IList>(RefreshCombined);
            ExportToJsonCommand = new RelayCommand(ExportToJson);
            ImportFromJsonCommand = new RelayCommand(ImportFromJson);
            CreatePlaylistCommand = new RelayCommand(async () => await CreatePlaylistAsync(), CanCreatePlaylist);
            ApplyPlaylistsFilterCommand = new RelayCommand(() => RefreshGridFromLocalFiles());
            ClearPlaylistsFilterCommand = new RelayCommand(ClearPlaylistsFilter);
            ApplyStagedPlaylistsFilterCommand = new RelayCommand(() => RefreshGridFromLocalFiles());
            ClearStagedPlaylistsFilterCommand = new RelayCommand(ClearStagedPlaylistsFilter);
            EnqueueLoadLimitCommand = new RelayCommand(EnqueueLoadLimit, CanEnqueueActions);
            EnqueueLoadAllCommand = new RelayCommand(EnqueueLoadAll, CanEnqueueActions);
            EnqueueDeleteSelectionCommand = new RelayCommand<IList>(EnqueueDeleteSelection, CanEnqueueDeleteSelection);
            ClearActionQueueCommand = new RelayCommand(ClearActionQueue, () => QueuedActions.Any());
            RemoveSelectedQueuedActionsCommand = new RelayCommand<IList>(RemoveSelectedQueuedActions);

            _playlistGridRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _playlistGridRefreshTimer.Tick += (sender, args) => RefreshGridFromLocalFiles();
            _playlistGridRefreshTimer.Start();

            Log("Playlists view model created.");
            RefreshGridFromLocalFiles();
            _paging.LoadPaginationState();
        }

        public ActivityLogViewModel ActivityLog { get; }

        public ObservableCollection<PlaylistCacheItem> Playlists { get; } = new ObservableCollection<PlaylistCacheItem>();

        public ObservableCollection<DeletionQueueItem> StagedForDeletion { get; } = new ObservableCollection<DeletionQueueItem>();

        /// <summary>Combined projection of available + staged playlists for the single grid UI.</summary>
        public ObservableCollection<PlaylistGridItem> CombinedPlaylists { get; } = new ObservableCollection<PlaylistGridItem>();

        public ObservableCollection<Track> Tracks { get; } = new ObservableCollection<Track>();

        private PlaylistGridItem _selectedPlaylistRow;

        public PlaylistGridItem SelectedPlaylistRow
        {
            get => _selectedPlaylistRow;
            set
            {
                if (Set(ref _selectedPlaylistRow, value))
                    SelectedPlaylist = value?.Playlist;
            }
        }

        private PlaylistCacheItem _selectedPlaylist;

        public PlaylistCacheItem SelectedPlaylist
        {
            get => _selectedPlaylist;
            set => Set(ref _selectedPlaylist, value);
        }

        private bool _isControlsPanelExpanded = true;

        public bool IsControlsPanelExpanded
        {
            get => _isControlsPanelExpanded;
            set
            {
                if (Set(ref _isControlsPanelExpanded, value))
                    RaisePropertyChanged(nameof(FillControlsPanel));
            }
        }

        private bool _isPlaylistsSectionExpanded = true;

        public bool IsPlaylistsSectionExpanded
        {
            get => _isPlaylistsSectionExpanded;
            set
            {
                if (Set(ref _isPlaylistsSectionExpanded, value))
                    RaisePropertyChanged(nameof(FillControlsPanel));
            }
        }

        /// <summary>
        /// When no Manage section is open and Controls is expanded, Controls stretches to fill leftover space.
        /// Managed from ManagePage layout; kept for binding compatibility.
        /// </summary>
        public bool FillControlsPanel => IsControlsPanelExpanded && !IsPlaylistsSectionExpanded;

        private double _controlsPanelHeight = 280;

        public double ControlsPanelHeight
        {
            get => _controlsPanelHeight;
            set => Set(ref _controlsPanelHeight, value);
        }

        /// <summary>Multi-selection from the Playlists grid (for Delete / keyboard enqueue).</summary>
        public System.Collections.IList SelectedPlaylistItems { get; private set; }

        public void SetSelectedPlaylistItems(System.Collections.IList items)
        {
            SelectedPlaylistItems = items;
            RaisePropertyChanged(nameof(SelectedPlaylistItems));
            ToggleDeleteQueueCommand?.RaiseCanExecuteChanged();
            EnqueueDeleteKeyCommand?.RaiseCanExecuteChanged();
        }

        public ObservableCollection<QueuedPlaylistAction> QueuedActions => _actionQueue.QueuedActions;

        /// <summary>
        /// Fired after the playlists grid is rebuilt so the view can re-select rows by Spotify id.
        /// </summary>
        public event Action<IReadOnlyList<string>> GridSelectionRestoreRequested;

        public string NewPlaylistName
        {
            get => _newPlaylistName;
            set
            {
                if (Set(ref _newPlaylistName, value))
                    CreatePlaylistCommand?.RaiseCanExecuteChanged();
            }
        }

        public string NewPlaylistDescription
        {
            get => _newPlaylistDescription;
            set => Set(ref _newPlaylistDescription, value);
        }

        public bool NewPlaylistIsPublic
        {
            get => _newPlaylistIsPublic;
            set
            {
                if (Set(ref _newPlaylistIsPublic, value) && value)
                    NewPlaylistIsCollaborative = false;
            }
        }

        public bool NewPlaylistIsCollaborative
        {
            get => _newPlaylistIsCollaborative;
            set
            {
                if (Set(ref _newPlaylistIsCollaborative, value) && value)
                    NewPlaylistIsPublic = false;
            }
        }

        private string _tracksPlaylistTitle = "Tracks";

        public string TracksPlaylistTitle
        {
            get => _tracksPlaylistTitle;
            private set => Set(ref _tracksPlaylistTitle, value);
        }

        public int PlaylistLoadLimit
        {
            get => _playlistLoadLimit;
            set => Set(ref _playlistLoadLimit, Math.Max(1, Math.Min(50, value)));
        }

        public string PlaylistsFilterText
        {
            get => _playlistsFilterText;
            set
            {
                if (Set(ref _playlistsFilterText, value))
                    RefreshGridFromLocalFiles();
            }
        }

        public string StagedPlaylistsFilterText
        {
            get => _stagedPlaylistsFilterText;
            set
            {
                if (Set(ref _stagedPlaylistsFilterText, value))
                    RefreshGridFromLocalFiles();
            }
        }

        public int RequestSpacingMilliseconds
        {
            get => _requestSpacing.SpacingMilliseconds;
            set
            {
                var clamped = Math.Max(0, value);
                if (_requestSpacing.SpacingMilliseconds == clamped) return;

                _requestSpacing.SpacingMilliseconds = clamped;
                RaisePropertyChanged();
            }
        }

        public bool IsActionRunning
        {
            get => _isActionRunning;
            set
            {
                if (Set(ref _isActionRunning, value))
                {
                    CancelCurrentActionCommand?.RaiseCanExecuteChanged();
                    AbortQueuedActionCommand?.RaiseCanExecuteChanged();
                    RaiseActionQueueStates();
                    CreatePlaylistCommand?.RaiseCanExecuteChanged();
                    LoadMorePlaylistsCommand?.RaiseCanExecuteChanged();
                }
            }
        }

        public string Status
        {
            get => _status;

            set
            {
                _status = value;
                RaisePropertyChanged();

                if (value == "Ready" ||
                    value.StartsWith("Cancelled") ||
                    value.StartsWith("Failed") ||
                    value.StartsWith("Rate limited") ||
                    value.StartsWith("Login required"))
                    ProgressVisibility = Visibility.Hidden;

                else
                    ProgressVisibility = Visibility.Visible;
            }
        }

        public Visibility ProgressVisibility
        {
            get => _progressVisibility;

            set
            {
                _progressVisibility = value;
                RaisePropertyChanged();
            }
        }

        public RelayCommand<PlaylistCacheItem> LoadTracksCommand { get; }

        public RelayCommand<PlaylistCacheItem> OpenInLoopLabCommand { get; }

        public RelayCommand<IList> RefreshSelectedPlaylistsCommand { get; }

        public RelayCommand CancelCurrentActionCommand { get; }

        public RelayCommand AbortQueuedActionCommand { get; }

        public RelayCommand ExecuteOrPauseCommand { get; }

        public string ExecuteOrPauseButtonLabel
        {
            get
            {
                if (IsActionRunning && _actionQueue.IsExecuting && _actionQueue.IsPaused)
                    return "Resume";

                if (IsActionRunning)
                    return "Pause";

                return "Execute";
            }
        }

        public RelayCommand DeletePlaylistsCommand { get; }

        public RelayCommand<IList> ToggleDeleteQueueCommand { get; }

        public RelayCommand<IList> EnqueueDeleteKeyCommand { get; }

        public RelayCommand DeleteAllToQueueCommand { get; }

        public RelayCommand<IList> MarkForDeletionCommand { get; }

        public RelayCommand<IList> UnmarkForDeletionCommand { get; }

        public RelayCommand RefreshDeletionResultsCommand { get; }

        public RelayCommand LoadPlaylistsCommand { get; }

        public RelayCommand LoadMorePlaylistsCommand { get; }

        public RelayCommand LoadAllPlaylistsCommand { get; }

        public RelayCommand<IList> StagePlaylistsCommand { get; }

        public RelayCommand<IList> UnstagePlaylistsCommand { get; }

        public RelayCommand<IList> ToggleStageCommand { get; }

        public RelayCommand<IList> ToggleMarkCommand { get; }

        public RelayCommand<IList> RefreshCombinedCommand { get; }

        public RelayCommand ExportToJsonCommand { get; }

        public RelayCommand ImportFromJsonCommand { get; }

        public RelayCommand CreatePlaylistCommand { get; }

        public RelayCommand ApplyPlaylistsFilterCommand { get; }

        public RelayCommand ClearPlaylistsFilterCommand { get; }

        public RelayCommand ApplyStagedPlaylistsFilterCommand { get; }

        public RelayCommand ClearStagedPlaylistsFilterCommand { get; }

        public RelayCommand EnqueueLoadLimitCommand { get; }

        public RelayCommand EnqueueLoadAllCommand { get; }

        public RelayCommand<IList> EnqueueDeleteSelectionCommand { get; }

        public RelayCommand ClearActionQueueCommand { get; }

        public RelayCommand<IList> RemoveSelectedQueuedActionsCommand { get; }

        public Task OnNavigatedToAsync()
        {
            // Idempotent: grid state lives in the local store and the periodic
            // refresh timer performs the same call, so revisiting is always safe.
            RefreshGridFromLocalFiles();
            return Task.CompletedTask;
        }

        public Task OnNavigatedFromAsync()
        {
            return Task.CompletedTask;
        }

        private void Log(string message, bool verbose = false)
        {
            ActivityLog.Log(message, verbose);
        }

        private CancellationToken BeginCancelableAction()
        {
            _currentActionCancellationTokenSource?.Dispose();
            _currentActionCancellationTokenSource = new CancellationTokenSource();
            IsActionRunning = true;

            return _currentActionCancellationTokenSource.Token;
        }

        private void EndCancelableAction()
        {
            IsActionRunning = false;
            _currentActionCancellationTokenSource?.Dispose();
            _currentActionCancellationTokenSource = null;
        }

        private void CancelCurrentAction()
        {
            if (_currentActionCancellationTokenSource == null || _currentActionCancellationTokenSource.IsCancellationRequested) return;

            _actionQueue.Resume();

            if (_actionQueue.IsExecuting)
            {
                ClearActionQueue();
                Log("Aborted queued action execution and cleared the action queue.");
            }

            _currentActionCancellationTokenSource.Cancel();
            Status = "Cancelling...";
            Log("Cancellation requested for current playlist action.");
        }

        private bool CanCancelCurrentAction()
        {
            return IsActionRunning && _currentActionCancellationTokenSource?.IsCancellationRequested != true;
        }

        /// <summary>
        /// Abort the selected queued action/detail (reverts delete staging in the
        /// playlists table). With no selection while work is running, cancels
        /// execution like the old Abort button.
        /// </summary>
        private void AbortQueuedAction()
        {
            if (_selectedQueuedDetail != null)
            {
                var parent = _selectedQueuedAction ?? FindQueuedActionForDetail(_selectedQueuedDetail);
                if (parent != null)
                {
                    RemoveQueuedActionDetail(parent, _selectedQueuedDetail);
                    ClearQueuedActionSelection();
                    Log("Aborted selected playlist from the action queue.");
                    return;
                }
            }

            if (_selectedQueuedAction != null)
            {
                RemoveQueuedAction(_selectedQueuedAction);
                ClearQueuedActionSelection();
                Log("Aborted selected queued action.");
                return;
            }

            CancelCurrentAction();
        }

        private bool CanAbortQueuedAction()
        {
            if (_selectedQueuedAction != null || _selectedQueuedDetail != null)
                return true;

            return CanCancelCurrentAction();
        }

        public void SetSelectedQueuedActionItem(object selectedItem)
        {
            _selectedQueuedAction = selectedItem as QueuedPlaylistAction;
            _selectedQueuedDetail = selectedItem as QueuedActionDetailItem;

            if (_selectedQueuedDetail != null && _selectedQueuedAction == null)
                _selectedQueuedAction = FindQueuedActionForDetail(_selectedQueuedDetail);

            AbortQueuedActionCommand?.RaiseCanExecuteChanged();
        }

        private void ClearQueuedActionSelection()
        {
            _selectedQueuedAction = null;
            _selectedQueuedDetail = null;
            AbortQueuedActionCommand?.RaiseCanExecuteChanged();
        }

        private bool CanExecuteOrPause()
        {
            return IsActionRunning || QueuedActions.Any();
        }

        private async Task ExecuteOrPauseAsync()
        {
            if (IsActionRunning)
            {
                if (_actionQueue.IsExecuting)
                {
                    if (_actionQueue.IsPaused)
                    {
                        _actionQueue.Resume();
                        Status = "Resuming queued actions...";
                        Log("Resumed queued action execution.");
                    }
                    else
                    {
                        _actionQueue.Pause();
                        Status = "Paused queued actions.";
                        Log("Paused queued action execution.");
                    }

                    RaiseActionQueueStates();
                    return;
                }

                CancelCurrentAction();
                return;
            }

            await ExecuteActionQueueAsync();
        }

        public async Task DeletePlaylistsAsync()
        {
            var playlists = StagedForDeletion
                .Where(playlist => playlist.IsMarkedForDeletion && playlist.DeletionStatus != DeletionStatus.Deleted)
                .ToList();

            if (!playlists.Any()) return;

            var cancellationToken = BeginCancelableAction();

            try
            {
                await DeletePlaylistsCoreAsync(playlists, cancellationToken, confirm: true);
            }
            finally
            {
                EndCancelableAction();
                Status = "Ready";
            }
        }

        private async Task DeletePlaylistsCoreAsync(
            IReadOnlyList<DeletionQueueItem> playlists,
            CancellationToken cancellationToken,
            bool confirm)
        {
            if (!playlists.Any()) return;

            if (confirm)
            {
                var message = playlists.Count == 1
                    ? $"Are you sure you want to delete playlist {playlists[0].Playlist.Name}?"
                    : $"Are you sure you want to delete these {playlists.Count} playlists?";

                var result = _messageBoxService.ShowMessageBox(
                    message,
                    "Confirm",
                    MessageBoxButton.YesNo,
                    MessageBoxIcon.Exclamation
                );

                if (result != MessageBoxResult.Yes) return;
            }

            try
            {
                Status = $"Deleting {playlists.Count} staged playlist(s)...";
                Log($"Deleting {playlists.Count} staged playlist(s) with up to {PlaylistDeletionService.MaxConcurrentPlaylistDeletes} concurrent request(s) and {RequestSpacingMilliseconds} ms spacing between requests.");

                foreach (var playlist in playlists)
                {
                    playlist.DeletionStatus = DeletionStatus.Pending;
                    SyncStagedDeletionItem(playlist.Playlist?.Id, item => item.DeletionStatus = DeletionStatus.Pending);
                }

                var cancellationTokenSource = _currentActionCancellationTokenSource;
                var deleteResults = await _deletionService.DeletePlaylistsAsync(playlists, cancellationTokenSource);
                var rateLimited = deleteResults.Any(result => result.Status == DeletionStatus.RateLimited);
                var cancelled = cancellationToken.IsCancellationRequested && !rateLimited;

                await Application.Current.Dispatcher.InvokeAsync((Action) (() =>
                {
                    ApplyDeletionResultsToStagedItems(deleteResults);

                    if (rateLimited || cancelled)
                    {
                        foreach (var playlist in playlists.Where(playlist => playlist.DeletionStatus == DeletionStatus.Pending))
                        {
                            SyncStagedDeletionItem(playlist.Playlist?.Id, item =>
                            {
                                item.DeletionStatus = DeletionStatus.Failed;
                                item.ResultsAcknowledged = false;
                            });
                        }
                    }
                }));

                var deletedCount = deleteResults.Count(result => result.Status == DeletionStatus.Deleted);
                var failedCount = playlists.Count - deletedCount;
                var retryAfter = deleteResults
                    .Where(result => result.Status == DeletionStatus.RateLimited && result.RetryAfter.HasValue)
                    .Select(result => result.RetryAfter.Value)
                    .DefaultIfEmpty(TimeSpan.Zero)
                    .FirstOrDefault();
                Log(rateLimited
                    ? retryAfter > TimeSpan.Zero
                        ? $"Rate limited while deleting. Stopped remaining deletion work. Retry-After: {(int)Math.Ceiling(retryAfter.TotalSeconds)} ({FormatRetryDelay(retryAfter)}). Deleted {deletedCount} of {playlists.Count} staged playlist(s); failed or skipped {failedCount}."
                        : $"Rate limited while deleting. Stopped remaining deletion work. Deleted {deletedCount} of {playlists.Count} staged playlist(s); failed or skipped {failedCount}."
                    : cancelled
                        ? $"Cancelled staged deletion. Deleted {deletedCount} of {playlists.Count} staged playlist(s); failed or skipped {failedCount}."
                    : $"Deleted {deletedCount} of {playlists.Count} staged playlist(s); failed {failedCount}.");

                // Only successful Spotify unfollows shrink Spotify's list; walk the
                // fetch cursor back by that count (3 pass + 1 fail + 5 pass => -8).
                if (deletedCount > 0)
                    _paging.RetreatForSuccessfulDeletes(deletedCount);

                PersistStagedDeletionGridToStore();
                RefreshGridFromLocalFiles();
                RaiseDeletionCommandStates();
            }
            catch (OperationCanceledException)
            {
                foreach (var playlist in playlists.Where(playlist => playlist.DeletionStatus == DeletionStatus.Pending))
                {
                    SyncStagedDeletionItem(playlist.Playlist?.Id, item =>
                    {
                        item.DeletionStatus = DeletionStatus.Failed;
                        item.ResultsAcknowledged = false;
                    });
                }

                PersistStagedDeletionGridToStore();
                RefreshGridFromLocalFiles();
                Log("Cancelled staged deletion. Pending items were marked as failed for review.");
                throw;
            }
        }

        private void ApplyDeletionResultsToStagedItems(IEnumerable<DeletePlaylistResult> deleteResults)
        {
            foreach (var deleteResult in deleteResults)
            {
                var playlistId = deleteResult.Playlist?.Playlist?.Id;
                if (string.IsNullOrWhiteSpace(playlistId)) continue;

                var status = deleteResult.Status == DeletionStatus.Deleted ? DeletionStatus.Deleted : DeletionStatus.Failed;
                SyncStagedDeletionItem(playlistId, item =>
                {
                    item.DeletionStatus = status;
                    item.ResultsAcknowledged = false;
                });
            }
        }

        private void SyncStagedDeletionItem(string playlistId, Action<DeletionQueueItem> update)
        {
            if (string.IsNullOrWhiteSpace(playlistId)) return;

            var stagedItem = StagedForDeletion.FirstOrDefault(item => item.Playlist?.Id == playlistId);
            if (stagedItem != null)
                update(stagedItem);
        }

        private void PersistStagedDeletionGridToStore()
        {
            var deletionQueue = _localStore.LoadDeletionQueue();

            foreach (var stagedItem in StagedForDeletion)
            {
                if (string.IsNullOrWhiteSpace(stagedItem.Playlist?.Id)) continue;

                deletionQueue[stagedItem.Playlist.Id] = stagedItem;
            }

            _localStore.SaveDeletionQueue(deletionQueue);
        }

        public async Task LoadPlaylistsAsync()
        {
            Log($"LoadPlaylistsAsync invoked. Current playlist count: {Playlists.Count}.");

            if (_paging.SpotifyFetchOffset > 0 || Playlists.Count > 0)
            {
                Log("Skipping first playlist page load because playlists are already tracked locally.");
                return;
            }

            if (_spotify.Api == null)
            {
                Log("Spotify API client is not available yet. Complete login before loading playlists.");
                Status = "Login required before loading playlists.";
                return;
            }

            var cancellationToken = BeginCancelableAction();
            Status = "Loading playlists...";

            try
            {
                await LogCurrentUserAsync();
                cancellationToken.ThrowIfCancellationRequested();

                var addedCount = await _paging.FetchPageAtOffsetAsync(0, PlaylistLoadLimit, cancellationToken, useDefaultRequestFallback: true);
                RefreshGridFromLocalFiles();
                Log($"Playlist grid now contains {Playlists.Count} item(s). Added {addedCount} new playlist(s). Next Spotify offset: {_paging.SpotifyFetchOffset}.");
                LoadMorePlaylistsCommand.RaiseCanExecuteChanged();
            }
            catch (APITooManyRequestsException ex)
            {
                Log($"Spotify rate limited playlist loading. {FormatRetryAfter(ex)}. Keeping cached playlists visible.");
                Log($"Playlist load rate-limit exception: {ex}", true);
                RefreshGridFromLocalFiles();
                Status = $"Rate limited. {FormatRetryAfter(ex)}.";
                return;
            }
            catch (OperationCanceledException)
            {
                Log("Cancelled playlist loading.");
                Status = "Cancelled playlist loading.";
                return;
            }
            catch (Exception ex)
            {
                Log($"Failed to load playlists: {ex}");
                Status = "Failed to load playlists.";
                return;
            }
            finally
            {
                EndCancelableAction();

                if (Status == "Loading playlists...")
                    Status = "Ready";
            }
        }

        public async Task LoadMorePlaylistsAsync()
        {
            var cancellationToken = BeginCancelableAction();

            try
            {
                await LoadMorePlaylistsCoreAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Log("Cancelled loading more playlists.");
                Status = "Cancelled loading more playlists.";
            }
            catch (APITooManyRequestsException ex)
            {
                Log($"Spotify rate limited Load More. {FormatRetryAfter(ex)}. Keeping cached playlists visible.");
                Log($"Load More rate-limit exception: {ex}", true);
                RefreshGridFromLocalFiles();
                Status = $"Rate limited. {FormatRetryAfter(ex)}.";
            }
            catch (Exception ex)
            {
                Log($"Failed to load more playlists: {ex}");
                Status = "Failed to load more playlists.";
            }
            finally
            {
                EndCancelableAction();

                if (Status == "Loading more playlists...")
                    Status = "Ready";
            }
        }

        private async Task LoadMorePlaylistsCoreAsync(CancellationToken cancellationToken)
        {
            Log("LoadMorePlaylistsAsync invoked.");

            if (_spotify.Api == null)
            {
                Log("Spotify API client is not available yet. Complete login before loading playlists.");
                Status = "Login required before loading playlists.";
                return;
            }

            if (_paging.HasReachedSpotifyPlaylistEnd())
            {
                Log("No additional playlist page is available.");
                return;
            }

            Status = "Loading more playlists...";
            var addedCount = await _paging.FetchPageAtOffsetAsync(_paging.SpotifyFetchOffset, PlaylistLoadLimit, cancellationToken);
            RefreshGridFromLocalFiles();
            Log($"Playlist grid now contains {Playlists.Count} item(s). Added {addedCount} new playlist(s). Next Spotify offset: {_paging.SpotifyFetchOffset}.");
            LoadMorePlaylistsCommand.RaiseCanExecuteChanged();
        }

        public async Task LoadAllPlaylistsAsync()
        {
            var cancellationToken = BeginCancelableAction();

            try
            {
                await LoadAllPlaylistsCoreAsync(cancellationToken);
            }
            catch (APITooManyRequestsException ex)
            {
                Log($"Spotify rate limited Load All. Stopping page fetches. {FormatRetryAfter(ex)}.");
                Log($"Load All rate-limit exception: {ex}", true);
                RefreshGridFromLocalFiles();
                Status = $"Rate limited. {FormatRetryAfter(ex)}.";
            }
            catch (OperationCanceledException)
            {
                Log("Cancelled Load All.");
                Status = "Cancelled Load All.";
            }
            catch (Exception ex)
            {
                Log($"Failed while loading all playlists: {ex}");
                Status = "Failed while loading all playlists.";
            }
            finally
            {
                EndCancelableAction();

                if (Status == "Loading all playlists...")
                    Status = "Ready";
            }
        }

        private async Task LoadAllPlaylistsCoreAsync(CancellationToken cancellationToken)
        {
            Log("LoadAllPlaylistsAsync invoked.");

            if (_spotify.Api == null)
            {
                Log("Spotify API client is not available yet. Complete login before loading playlists.");
                Status = "Login required before loading playlists.";
                return;
            }

            Status = "Loading all playlists...";

            while (!_paging.HasReachedSpotifyPlaylistEnd())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var offsetBeforeFetch = _paging.SpotifyFetchOffset;
                var addedCount = await _paging.FetchPageAtOffsetAsync(_paging.SpotifyFetchOffset, PlaylistLoadLimit, cancellationToken);
                RefreshGridFromLocalFiles();

                if (_paging.SpotifyFetchOffset == offsetBeforeFetch)
                    break;

                Log($"Loaded playlist page during Load All. Added {addedCount} new playlist(s). Next Spotify offset: {_paging.SpotifyFetchOffset}.", true);
            }

            Log("Finished loading all available playlist pages.");
            LoadMorePlaylistsCommand.RaiseCanExecuteChanged();
        }

        private bool CanLoadMorePlaylists()
        {
            return _spotify.Api != null && !IsActionRunning;
        }

        private void StagePlaylists(IList items)
        {
            var playlists = ExtractLoadedPlaylists(items);

            if (playlists == null || !playlists.Any()) return;

            var preserveIds = CaptureSelectionIds(items);
            var availablePlaylists = _localStore.LoadAvailablePlaylists();
            var deletionQueue = _localStore.LoadDeletionQueue();
            var stagedIds = new List<string>();

            foreach (var playlist in playlists)
            {
                if (string.IsNullOrWhiteSpace(playlist.Id)) continue;

                availablePlaylists.Remove(playlist.Id);

                if (!deletionQueue.TryGetValue(playlist.Id, out var queued))
                {
                    queued = new DeletionQueueItem(playlist);
                    deletionQueue[playlist.Id] = queued;
                }
                else
                {
                    queued.Playlist = playlist;
                }

                queued.IsMarkedForDeletion = true;
                if (queued.DeletionStatus == DeletionStatus.Deleted)
                    queued.DeletionStatus = DeletionStatus.Pending;

                stagedIds.Add(playlist.Id);
            }

            _localStore.SaveAvailablePlaylists(availablePlaylists);
            _localStore.SaveDeletionQueue(deletionQueue);
            RefreshGridFromLocalFiles(preserveIds ?? stagedIds);
            RaiseDeletionCommandStates();

            if (stagedIds.Count > 0)
                EnqueueDeleteForIds(stagedIds);
        }

        private void UnstagePlaylists(IList items)
        {
            var playlists = ExtractStagedItems(items);

            if (playlists == null || !playlists.Any()) return;

            var preserveIds = CaptureSelectionIds(items);
            var availablePlaylists = _localStore.LoadAvailablePlaylists();
            var deletionQueue = _localStore.LoadDeletionQueue();
            var removedIds = new List<string>();

            foreach (var stagedPlaylist in playlists)
            {
                if (string.IsNullOrWhiteSpace(stagedPlaylist.Playlist?.Id)) continue;

                if (stagedPlaylist.DeletionStatus != DeletionStatus.Deleted)
                    availablePlaylists[stagedPlaylist.Playlist.Id] = stagedPlaylist.Playlist;

                deletionQueue.Remove(stagedPlaylist.Playlist.Id);
                removedIds.Add(stagedPlaylist.Playlist.Id);
            }

            _localStore.SaveAvailablePlaylists(availablePlaylists);
            _localStore.SaveDeletionQueue(deletionQueue);
            RefreshGridFromLocalFiles(preserveIds);
            RaiseDeletionCommandStates();
            RemovePlaylistIdsFromQueuedDeletes(removedIds);
        }

        private void ToggleStage(IList items)
        {
            ToggleDeleteQueue(items);
        }

        /// <summary>
        /// Toggle selection in/out of the deletion queue. If every selected row is already queued,
        /// remove them; otherwise enqueue the ones that are not yet queued.
        /// </summary>
        private void ToggleDeleteQueue(IList items)
        {
            if (items == null || items.Count == 0)
                items = SelectedPlaylistItems as IList;

            var rows = ExtractGridItems(items);
            if (rows.Count == 0)
            {
                Log("Select one or more playlists to toggle in the deletion queue.");
                return;
            }

            if (rows.All(r => r.IsStaged))
            {
                UnstagePlaylists(rows.Where(r => r.DeletionItem != null).Select(r => r.DeletionItem).ToList());
                return;
            }

            var toStage = rows.Where(r => r.IsLoaded && r.Playlist != null).Select(r => r.Playlist).ToList();
            if (toStage.Count > 0)
                StagePlaylists(toStage);
        }

        private void DeleteAllToQueue()
        {
            var available = _localStore.LoadAvailablePlaylists().Values.ToList();
            if (available.Count == 0)
            {
                Log("No loaded playlists to enqueue for deletion.");
                return;
            }

            StagePlaylists(available);
            Log($"Enqueued {available.Count} playlist(s) for deletion.");
        }

        private bool CanDeleteAllToQueue()
        {
            return Playlists.Count > 0 || _localStore.LoadAvailablePlaylists().Count > 0;
        }

        private void ToggleMark(IList items)
        {
            var staged = ExtractStagedItems(items);
            if (staged.Count == 0)
            {
                Log("Select one or more staged playlists to mark/unmark for deletion.");
                return;
            }

            var shouldMark = staged.Any(item => !item.IsMarkedForDeletion);
            SetDeletionMark(staged, shouldMark);
        }

        private async void RefreshCombined(IList items)
        {
            var loaded = ExtractLoadedPlaylists(items);
            if (loaded.Count > 0)
                await RefreshSelectedPlaylistsAsync(loaded);

            RefreshDeletionResults();
        }

        private void RefreshDeletionResults()
        {
            var availablePlaylists = _localStore.LoadAvailablePlaylists();
            var deletionQueue = _localStore.LoadDeletionQueue();
            var acknowledged = 0;
            var clearedDeleted = 0;
            var restoredFailed = 0;
            var restoredIds = new List<string>();

            foreach (var stagedPlaylist in deletionQueue.Values.ToList())
            {
                if (string.IsNullOrWhiteSpace(stagedPlaylist.Playlist?.Id)) continue;

                switch (stagedPlaylist.DeletionStatus)
                {
                    case DeletionStatus.Deleted:
                        if (stagedPlaylist.ResultsAcknowledged)
                        {
                            deletionQueue.Remove(stagedPlaylist.Playlist.Id);
                            clearedDeleted++;
                        }
                        else
                        {
                            stagedPlaylist.ResultsAcknowledged = true;
                            acknowledged++;
                        }
                        break;
                    case DeletionStatus.Failed:
                        if (stagedPlaylist.ResultsAcknowledged)
                        {
                            // Second refresh: restore failed playlists to the normal list.
                            availablePlaylists[stagedPlaylist.Playlist.Id] = stagedPlaylist.Playlist;
                            deletionQueue.Remove(stagedPlaylist.Playlist.Id);
                            restoredIds.Add(stagedPlaylist.Playlist.Id);
                            restoredFailed++;
                        }
                        else
                        {
                            stagedPlaylist.ResultsAcknowledged = true;
                            acknowledged++;
                        }
                        break;
                }
            }

            _localStore.SaveAvailablePlaylists(availablePlaylists);
            _localStore.SaveDeletionQueue(deletionQueue);

            if (restoredIds.Count > 0)
                RemovePlaylistIdsFromQueuedDeletes(restoredIds);

            RefreshGridFromLocalFiles();
            RaiseDeletionCommandStates();

            if (clearedDeleted > 0 || restoredFailed > 0)
                Log($"Refresh: cleared {clearedDeleted} deleted row(s), restored {restoredFailed} failed playlist(s) to the list.");
            else if (acknowledged > 0)
                Log($"Refresh: acknowledged {acknowledged} delete result(s). Refresh again to clear deleted / restore failed.");
            else
                Log("Refresh: no delete results to update.");
        }

        /// <summary>
        /// Enqueue selected playlists for deletion (does not unqueue). Used by Delete/Backspace keys.
        /// </summary>
        private void EnqueueDeleteSelectionOnly(IList items)
        {
            if (items == null || items.Count == 0)
                items = SelectedPlaylistItems as IList;

            var rows = ExtractGridItems(items);
            var toStage = rows
                .Where(r => r.IsLoaded && r.Playlist != null)
                .Select(r => r.Playlist)
                .ToList();

            if (toStage.Count == 0)
            {
                // Already queued selection: still ensure they are marked and present in the action queue.
                var staged = rows
                    .Where(r => r.IsStaged && r.DeletionItem != null &&
                                r.DeletionItem.DeletionStatus != DeletionStatus.Deleted)
                    .Select(r => r.DeletionItem)
                    .ToList();

                if (staged.Count == 0)
                {
                    Log("Select one or more playlists to enqueue for deletion.");
                    return;
                }

                foreach (var item in staged)
                    item.IsMarkedForDeletion = true;

                EnqueueDeleteForIds(staged.Select(s => s.Playlist.Id).Where(id => !string.IsNullOrWhiteSpace(id)).ToList());
                PersistStagedDeletionGridToStore();
                return;
            }

            StagePlaylists(toStage);
        }

        private async Task RefreshSelectedPlaylistsAsync(IList selectedItems)
        {
            var selectedPlaylists = selectedItems?.Cast<PlaylistCacheItem>().ToList();

            if (selectedPlaylists == null || !selectedPlaylists.Any()) return;

            var cancellationToken = BeginCancelableAction();

            try
            {
                Status = $"Refreshing {selectedPlaylists.Count} selected playlist(s)...";
                var availablePlaylists = _localStore.LoadAvailablePlaylists();
                var deletionQueue = _localStore.LoadDeletionQueue();

                foreach (var playlist in selectedPlaylists)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (string.IsNullOrWhiteSpace(playlist.Id)) continue;

                    try
                    {
                        var refreshedPlaylist = await _spotify.Api.Playlists.Get(playlist.Id);
                        var cacheItem = PlaylistCacheItem.FromPlaylist(refreshedPlaylist);

                        if (deletionQueue.ContainsKey(cacheItem.Id))
                            deletionQueue[cacheItem.Id].Playlist = cacheItem;
                        else
                            availablePlaylists[cacheItem.Id] = cacheItem;

                        Log($"Refreshed playlist '{cacheItem.Name}' ({cacheItem.Id}).", true);
                    }
                    catch (APITooManyRequestsException ex)
                    {
                        Log($"Spotify rate limited selected playlist refresh. {FormatRetryAfter(ex)}.");
                        Status = $"Rate limited. {FormatRetryAfter(ex)}.";
                        break;
                    }
                    catch (APIException ex) when (ContainsExceptionMessage(ex, "not found") || ContainsExceptionMessage(ex, "404"))
                    {
                        availablePlaylists.Remove(playlist.Id);
                        deletionQueue.Remove(playlist.Id);
                        Log($"Playlist '{playlist.Name}' ({playlist.Id}) no longer exists; removed it from local cache.");
                    }
                    catch (Exception ex)
                    {
                        Log($"Failed to refresh playlist '{playlist.Name}' ({playlist.Id}): {ex.Message}");
                        Log($"Refresh selected playlist exception: {ex}", true);
                    }
                }

                _localStore.SaveAvailablePlaylists(availablePlaylists);
                _localStore.SaveDeletionQueue(deletionQueue);
                RefreshGridFromLocalFiles();

                if (Status.StartsWith("Refreshing"))
                    Status = "Ready";
            }
            catch (OperationCanceledException)
            {
                Log("Cancelled selected playlist refresh.");
                Status = "Cancelled selected playlist refresh.";
            }
            finally
            {
                EndCancelableAction();
            }
        }

        private async Task CreatePlaylistAsync()
        {
            if (!CanCreatePlaylist()) return;

            var cancellationToken = BeginCancelableAction();

            try
            {
                if (_spotify.Api == null)
                {
                    Log("Spotify API client is not available yet. Complete login before creating playlists.");
                    Status = "Login required before creating playlists.";
                    return;
                }

                var currentUser = await _spotify.GetPrivateProfileAsync();
                cancellationToken.ThrowIfCancellationRequested();

                if (currentUser == null || string.IsNullOrWhiteSpace(currentUser.Id))
                {
                    Log("Unable to create playlist because the current Spotify user profile is unavailable.");
                    Status = "Failed to create playlist.";
                    return;
                }

                var playlistName = NewPlaylistName.Trim();
                Status = $"Creating playlist {playlistName}...";
                Log($"Creating playlist '{playlistName}' for user {currentUser.Id}.");

                var isCollaborative = NewPlaylistIsCollaborative;
                var isPublic = NewPlaylistIsPublic && !isCollaborative;

                var request = new PlaylistCreateRequest(playlistName)
                {
                    Description = string.IsNullOrWhiteSpace(NewPlaylistDescription) ? null : NewPlaylistDescription.Trim(),
                    Public = isPublic,
                    Collaborative = isCollaborative
                };

                var createdPlaylist = await _spotify.Api.Playlists.Create(currentUser.Id, request, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                _localStore.AddOrUpdateAvailablePlaylists(new[] { PlaylistCacheItem.FromPlaylist(createdPlaylist) });
                RefreshGridFromLocalFiles();

                NewPlaylistName = string.Empty;
                NewPlaylistDescription = string.Empty;
                NewPlaylistIsPublic = false;
                NewPlaylistIsCollaborative = false;

                Log($"Created playlist '{createdPlaylist.Name}' ({createdPlaylist.Id}) and added it to the local cache.");
                Status = $"Created playlist '{createdPlaylist.Name}'.";
            }
            catch (APITooManyRequestsException ex)
            {
                Log($"Spotify rate limited playlist creation. {FormatRetryAfter(ex)}.");
                Log($"Playlist create rate-limit exception: {ex}", true);
                Status = $"Rate limited. {FormatRetryAfter(ex)}.";
            }
            catch (OperationCanceledException)
            {
                Status = "Cancelled playlist creation.";
                Log("Cancelled playlist creation.");
            }
            catch (Exception ex)
            {
                Log($"Failed to create playlist: {ex.Message}");
                Log($"Playlist create exception: {ex}", true);
                Status = "Failed to create playlist.";
            }
            finally
            {
                EndCancelableAction();
            }
        }

        private bool CanCreatePlaylist()
        {
            return !IsActionRunning && !string.IsNullOrWhiteSpace(NewPlaylistName);
        }

        private void MarkForDeletion(IList items)
        {
            var selectedItems = GetSelectedDeletionItems(items);
            if (!selectedItems.Any())
            {
                Log("Select one or more staged playlists to mark for deletion.");
                return;
            }

            SetDeletionMark(selectedItems, true);
        }

        private void UnmarkForDeletion(IList items)
        {
            var selectedItems = GetSelectedDeletionItems(items);
            if (!selectedItems.Any())
            {
                Log("Select one or more staged playlists to unmark for deletion.");
                return;
            }

            SetDeletionMark(selectedItems, false);
        }

        private void SetDeletionMark(IReadOnlyList<DeletionQueueItem> selectedItems, bool isMarked)
        {
            var deletionQueue = _localStore.LoadDeletionQueue();
            var changedCount = 0;

            foreach (var selectedItem in selectedItems)
            {
                var id = selectedItem.Playlist?.Id;
                if (string.IsNullOrWhiteSpace(id) || !deletionQueue.TryGetValue(id, out var queuedItem)) continue;
                if (queuedItem.DeletionStatus == DeletionStatus.Deleted) continue;

                queuedItem.IsMarkedForDeletion = isMarked;
                queuedItem.DeletionStatus = DeletionStatus.Pending;
                queuedItem.ResultsAcknowledged = false;

                selectedItem.IsMarkedForDeletion = isMarked;
                selectedItem.DeletionStatus = DeletionStatus.Pending;
                selectedItem.ResultsAcknowledged = false;
                changedCount++;
            }

            if (changedCount == 0) return;

            _localStore.SaveDeletionQueue(deletionQueue);
            RaiseDeletionCommandStates();
            Log($"{(isMarked ? "Marked" : "Unmarked")} {changedCount} playlist(s) for deletion.");
        }

        private static List<DeletionQueueItem> GetSelectedDeletionItems(IList items)
        {
            return ExtractStagedItems(items);
        }

        private static List<PlaylistGridItem> ExtractGridItems(IList items)
        {
            if (items == null)
                return new List<PlaylistGridItem>();

            return items.OfType<PlaylistGridItem>()
                .Concat(items.OfType<PlaylistCacheItem>().Select(p => new PlaylistGridItem(p)))
                .Concat(items.OfType<DeletionQueueItem>().Select(d => new PlaylistGridItem(d)))
                .GroupBy(r => r.Id)
                .Select(g => g.First())
                .ToList();
        }

        private static List<PlaylistCacheItem> ExtractLoadedPlaylists(IList items)
        {
            if (items == null)
                return new List<PlaylistCacheItem>();

            return ExtractGridItems(items)
                .Where(r => r.IsLoaded && r.Playlist != null)
                .Select(r => r.Playlist)
                .Concat(items.OfType<PlaylistCacheItem>())
                .GroupBy(p => p.Id)
                .Select(g => g.First())
                .ToList();
        }

        private static List<DeletionQueueItem> ExtractStagedItems(IList items)
        {
            if (items == null)
                return new List<DeletionQueueItem>();

            return ExtractGridItems(items)
                .Where(r => r.IsStaged && r.DeletionItem != null)
                .Select(r => r.DeletionItem)
                .Concat(items.OfType<DeletionQueueItem>())
                .GroupBy(d => d.Playlist?.Id)
                .Select(g => g.First())
                .ToList();
        }

        private bool CanMarkForDeletion(IList items)
        {
            var selectedItems = GetSelectedDeletionItems(items);
            if (selectedItems.Any())
                return selectedItems.Any(item => !item.IsMarkedForDeletion && item.DeletionStatus != DeletionStatus.Deleted);

            return StagedForDeletion.Any(item => !item.IsMarkedForDeletion && item.DeletionStatus != DeletionStatus.Deleted);
        }

        private bool CanUnmarkForDeletion(IList items)
        {
            var selectedItems = GetSelectedDeletionItems(items);
            if (selectedItems.Any())
                return selectedItems.Any(item => item.IsMarkedForDeletion && item.DeletionStatus != DeletionStatus.Deleted);

            return StagedForDeletion.Any(item => item.IsMarkedForDeletion && item.DeletionStatus != DeletionStatus.Deleted);
        }

        private bool CanDeleteMarkedPlaylists()
        {
            return StagedForDeletion.Any(item => item.IsMarkedForDeletion && item.DeletionStatus != DeletionStatus.Deleted);
        }

        private void RaiseDeletionCommandStates()
        {
            MarkForDeletionCommand.RaiseCanExecuteChanged();
            UnmarkForDeletionCommand.RaiseCanExecuteChanged();
            DeletePlaylistsCommand.RaiseCanExecuteChanged();
            DeleteAllToQueueCommand.RaiseCanExecuteChanged();
            RaiseActionQueueStates();
        }

        private void RaiseActionQueueStates()
        {
            ExecuteOrPauseCommand.RaiseCanExecuteChanged();
            AbortQueuedActionCommand?.RaiseCanExecuteChanged();
            EnqueueLoadLimitCommand.RaiseCanExecuteChanged();
            EnqueueLoadAllCommand.RaiseCanExecuteChanged();
            EnqueueDeleteSelectionCommand.RaiseCanExecuteChanged();
            ClearActionQueueCommand.RaiseCanExecuteChanged();
            RaisePropertyChanged(nameof(ExecuteOrPauseButtonLabel));
        }

        private void ClearActionQueue()
        {
            foreach (var action in QueuedActions.ToList())
                RemoveQueuedAction(action);

            ClearQueuedActionSelection();
        }

        private bool CanEnqueueActions()
        {
            return _spotify.Api != null && (!IsActionRunning || _actionQueue.IsPaused);
        }

        private bool CanEnqueueDeleteSelection(IList items)
        {
            if (!CanEnqueueActions()) return false;

            var selectedItems = GetSelectedDeletionItems(items);
            return selectedItems.Any(item => item.IsMarkedForDeletion && item.DeletionStatus != DeletionStatus.Deleted);
        }

        private void EnqueueLoadLimit()
        {
            var action = new QueuedPlaylistAction
            {
                ActionType = PlaylistActionType.LoadLimit
            };
            action.DetailItems.Add(new QueuedActionDetailItem($"Fetch 1 page (limit {PlaylistLoadLimit})", canRemove: false));
            action.RefreshDisplayName();
            _actionQueue.Enqueue(action);
        }

        private void EnqueueLoadAll()
        {
            var action = new QueuedPlaylistAction
            {
                ActionType = PlaylistActionType.LoadAll
            };
            action.DetailItems.Add(new QueuedActionDetailItem("Fetch all remaining playlist pages", canRemove: false));
            action.RefreshDisplayName();
            _actionQueue.Enqueue(action);
        }

        private void EnqueueDeleteSelection(IList items)
        {
            var selectedItems = GetSelectedDeletionItems(items)
                .Where(item => item.IsMarkedForDeletion && item.DeletionStatus != DeletionStatus.Deleted)
                .ToList();

            if (!selectedItems.Any())
            {
                Log("Select one or more queued playlists to enqueue delete.");
                return;
            }

            EnqueueDeleteForIds(selectedItems.Select(item => item.Playlist.Id).ToList());
        }

        private void EnqueueDeleteForIds(IReadOnlyList<string> playlistIds)
        {
            var ids = playlistIds?
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToList() ?? new List<string>();

            if (ids.Count == 0) return;

            // Each stage operation is its own atomic queue item (do not merge into prior deletes).
            var action = new QueuedPlaylistAction
            {
                ActionType = PlaylistActionType.DeleteSelection
            };

            var deletionQueue = _localStore.LoadDeletionQueue();
            foreach (var id in ids)
            {
                action.PlaylistIds.Add(id);
                var name = deletionQueue.TryGetValue(id, out var item)
                    ? item.Playlist?.Name ?? id
                    : StagedForDeletion.FirstOrDefault(i => i.Playlist?.Id == id)?.Playlist?.Name ?? id;
                action.DetailItems.Add(new QueuedActionDetailItem(name, id));
            }

            action.RefreshDisplayName();
            _actionQueue.Enqueue(action);
            Log($"Enqueued delete for {ids.Count} playlist(s).");
        }

        private void RemovePlaylistIdsFromQueuedDeletes(IReadOnlyList<string> playlistIds)
        {
            if (playlistIds == null || playlistIds.Count == 0) return;

            var idSet = new HashSet<string>(playlistIds.Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.Ordinal);
            if (idSet.Count == 0) return;

            foreach (var action in QueuedActions.Where(a => a.ActionType == PlaylistActionType.DeleteSelection).ToList())
            {
                var detailsToRemove = action.DetailItems
                    .Where(d => !string.IsNullOrWhiteSpace(d.PlaylistId) && idSet.Contains(d.PlaylistId))
                    .ToList();

                foreach (var detail in detailsToRemove)
                    _actionQueue.RemoveDetail(action, detail);

                action.PlaylistIds.RemoveAll(id => idSet.Contains(id));

                if (action.PlaylistIds.Count == 0)
                    _actionQueue.Remove(action);
                else
                    action.RefreshDisplayName();
            }

            RaiseActionQueueStates();
        }

        public void RemoveQueuedAction(QueuedPlaylistAction action)
        {
            if (action == null)
                return;

            var deleteIds = action.ActionType == PlaylistActionType.DeleteSelection
                ? action.PlaylistIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList()
                : null;

            _actionQueue.Remove(action);

            if (deleteIds != null && deleteIds.Count > 0)
                UnstagePlaylistsByIds(deleteIds, preserveSelection: true);
        }

        public void RemoveQueuedActionDetail(QueuedPlaylistAction action, QueuedActionDetailItem detail)
        {
            if (action == null || detail == null)
                return;

            var playlistId = detail.PlaylistId;
            var isDelete = action.ActionType == PlaylistActionType.DeleteSelection;

            _actionQueue.RemoveDetail(action, detail);

            if (isDelete && !string.IsNullOrWhiteSpace(playlistId))
                UnstagePlaylistsByIds(new[] { playlistId }, preserveSelection: true);
        }

        public QueuedPlaylistAction FindQueuedActionForDetail(QueuedActionDetailItem detail)
        {
            return _actionQueue.FindActionForDetail(detail);
        }

        private void RemoveSelectedQueuedActions(IList items)
        {
            var selectedActions = items?.OfType<QueuedPlaylistAction>().ToList();
            if (selectedActions == null || !selectedActions.Any()) return;

            foreach (var action in selectedActions)
                RemoveQueuedAction(action);

            ClearQueuedActionSelection();
        }

        private async Task ExecuteActionQueueAsync()
        {
            if (!QueuedActions.Any()) return;

            var cancellationToken = BeginCancelableAction();

            try
            {
                await _actionQueue.ExecuteAsync(ExecuteQueuedActionAsync, cancellationToken);
                Log("Finished executing queued actions.");
            }
            catch (OperationCanceledException)
            {
                Log("Cancelled queued action execution.");
                Status = "Cancelled queued action execution.";
            }
            catch (APITooManyRequestsException ex)
            {
                Log($"Spotify rate limited queued action execution. {FormatRetryAfter(ex)}.");
                Status = $"Rate limited. {FormatRetryAfter(ex)}.";
            }
            catch (Exception ex)
            {
                Log($"Failed while executing queued actions: {ex}");
                Status = "Failed while executing queued actions.";
            }
            finally
            {
                EndCancelableAction();
                RaiseActionQueueStates();

                if (Status.StartsWith("Executing:") || Status.StartsWith("Paused") || Status.StartsWith("Resuming"))
                    Status = "Ready";
            }
        }

        private async Task ExecuteQueuedActionAsync(QueuedPlaylistAction action, CancellationToken cancellationToken)
        {
            Log($"Executing queued action: {action.DisplayName}");
            Status = $"Executing: {action.DisplayName}";

            switch (action.ActionType)
            {
                case PlaylistActionType.LoadLimit:
                    await LoadMorePlaylistsCoreAsync(cancellationToken);
                    break;
                case PlaylistActionType.LoadAll:
                    await LoadAllPlaylistsCoreAsync(cancellationToken);
                    break;
                case PlaylistActionType.DeleteSelection:
                    var playlists = ResolveDeleteSelection(action.PlaylistIds);
                    await DeletePlaylistsCoreAsync(playlists, cancellationToken, confirm: false);
                    break;
            }
        }

        private List<DeletionQueueItem> ResolveDeleteSelection(IReadOnlyList<string> playlistIds)
        {
            if (playlistIds == null || !playlistIds.Any()) return new List<DeletionQueueItem>();

            var idSet = new HashSet<string>(playlistIds.Where(id => !string.IsNullOrWhiteSpace(id)));

            return StagedForDeletion
                .Where(item => item.Playlist?.Id != null && idSet.Contains(item.Playlist.Id))
                .Where(item => item.IsMarkedForDeletion && item.DeletionStatus != DeletionStatus.Deleted)
                .ToList();
        }

        private void UnstagePlaylistsByIds(IReadOnlyList<string> playlistIds, bool preserveSelection)
        {
            if (playlistIds == null || playlistIds.Count == 0)
                return;

            var idSet = new HashSet<string>(playlistIds.Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.Ordinal);
            if (idSet.Count == 0)
                return;

            var preserveIds = preserveSelection
                ? (CaptureSelectionIds(SelectedPlaylistItems as IList) ?? idSet.ToList())
                : null;

            var availablePlaylists = _localStore.LoadAvailablePlaylists();
            var deletionQueue = _localStore.LoadDeletionQueue();
            var removedIds = new List<string>();

            foreach (var id in idSet)
            {
                if (!deletionQueue.TryGetValue(id, out var staged))
                    continue;

                if (staged.DeletionStatus != DeletionStatus.Deleted && staged.Playlist != null)
                    availablePlaylists[id] = staged.Playlist;

                deletionQueue.Remove(id);
                removedIds.Add(id);
            }

            if (removedIds.Count == 0)
                return;

            _localStore.SaveAvailablePlaylists(availablePlaylists);
            _localStore.SaveDeletionQueue(deletionQueue);
            RefreshGridFromLocalFiles(preserveIds);
            RaiseDeletionCommandStates();
            // Details were already removed from the action tree; strip any remaining copies.
            RemovePlaylistIdsFromQueuedDeletes(removedIds);
        }

        private List<string> CaptureSelectionIds(IList items)
        {
            var source = items ?? SelectedPlaylistItems as IList;
            if (source == null || source.Count == 0)
                return null;

            var ids = ExtractGridItems(source)
                .Select(r => r.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            return ids.Count > 0 ? ids : null;
        }

        private void RefreshGridFromLocalFiles(IReadOnlyList<string> preserveSelectionIds = null)
        {
            var playlistsFilter = PlaylistsFilterText?.Trim();
            var stagedFilter = StagedPlaylistsFilterText?.Trim();
            // Single search box drives both sides of the combined grid.
            var combinedFilter = string.IsNullOrWhiteSpace(playlistsFilter) ? stagedFilter : playlistsFilter;

            var availablePlaylists = _localStore.LoadAvailablePlaylists().Values
                .Where(playlist => MatchesPlaylistFilter(playlist, combinedFilter))
                .OrderBy(playlist => playlist.Name)
                .ToList();

            var deletionQueueDict = _localStore.LoadDeletionQueue();
            var coerced = false;
            foreach (var item in deletionQueueDict.Values)
            {
                if (item == null) continue;
                if (item.DeletionStatus == DeletionStatus.Deleted) continue;
                if (item.IsMarkedForDeletion) continue;

                item.IsMarkedForDeletion = true;
                coerced = true;
            }

            if (coerced)
                _localStore.SaveDeletionQueue(deletionQueueDict);

            var deletionQueue = deletionQueueDict.Values
                .Where(item => MatchesPlaylistFilter(item.Playlist, combinedFilter))
                .OrderBy(playlist => playlist.Playlist?.Name)
                .ToList();

            var combined = availablePlaylists
                .Select(p => new PlaylistGridItem(p))
                .Concat(deletionQueue.Select(d => new PlaylistGridItem(d)))
                .OrderBy(r => r.Name)
                .ToList();

            var restoreIds = preserveSelectionIds?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();

            Application.Current.Dispatcher.BeginInvoke((Action) (() =>
            {
                if (!PlaylistCollectionsMatch(Playlists, availablePlaylists))
                    ReplaceCollection(Playlists, availablePlaylists);

                if (!DeletionQueueCollectionsMatch(StagedForDeletion, deletionQueue))
                    ReplaceCollection(StagedForDeletion, deletionQueue);

                if (!CombinedCollectionsMatch(CombinedPlaylists, combined))
                    ReplaceCollection(CombinedPlaylists, combined);

                RaiseDeletionCommandStates();
                DeleteAllToQueueCommand.RaiseCanExecuteChanged();

                if (restoreIds != null && restoreIds.Count > 0)
                    GridSelectionRestoreRequested?.Invoke(restoreIds);
            }));
        }

        private static bool CombinedCollectionsMatch(IList<PlaylistGridItem> currentItems, IList<PlaylistGridItem> newItems)
        {
            if (currentItems.Count != newItems.Count) return false;

            for (var i = 0; i < currentItems.Count; i++)
            {
                var current = currentItems[i];
                var next = newItems[i];

                if (current.Id != next.Id ||
                    current.Name != next.Name ||
                    current.OwnerDisplayName != next.OwnerDisplayName ||
                    current.TracksTotal != next.TracksTotal ||
                    current.IsLoaded != next.IsLoaded ||
                    current.QueueStatus != next.QueueStatus ||
                    current.DeletionStatusName != next.DeletionStatusName)
                    return false;
            }

            return true;
        }

        private static bool MatchesPlaylistFilter(PlaylistCacheItem playlist, string filter)
        {
            if (playlist == null) return false;

            if (string.IsNullOrWhiteSpace(filter))
                return true;

            return (playlist.Name?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                   || (playlist.OwnerDisplayName?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                   || (playlist.OwnerId?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                   || (playlist.Id?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;
        }

        private void ClearPlaylistsFilter()
        {
            PlaylistsFilterText = string.Empty;
        }

        private void ClearStagedPlaylistsFilter()
        {
            StagedPlaylistsFilterText = string.Empty;
        }

        private static bool PlaylistCollectionsMatch(IList<PlaylistCacheItem> currentItems, IList<PlaylistCacheItem> newItems)
        {
            if (currentItems.Count != newItems.Count) return false;

            for (var i = 0; i < currentItems.Count; i++)
            {
                var current = currentItems[i];
                var next = newItems[i];

                if (current.Id != next.Id ||
                    current.Name != next.Name ||
                    current.OwnerDisplayName != next.OwnerDisplayName ||
                    current.OwnerId != next.OwnerId ||
                    current.TracksTotal != next.TracksTotal)
                    return false;
            }

            return true;
        }

        private static bool DeletionQueueCollectionsMatch(IList<DeletionQueueItem> currentItems, IList<DeletionQueueItem> newItems)
        {
            if (currentItems.Count != newItems.Count) return false;

            for (var i = 0; i < currentItems.Count; i++)
            {
                var current = currentItems[i];
                var next = newItems[i];

                if (current.Playlist?.Id != next.Playlist?.Id ||
                    current.Playlist?.Name != next.Playlist?.Name ||
                    current.Playlist?.OwnerDisplayName != next.Playlist?.OwnerDisplayName ||
                    current.Playlist?.OwnerId != next.Playlist?.OwnerId ||
                    current.Playlist?.TracksTotal != next.Playlist?.TracksTotal ||
                    current.IsMarkedForDeletion != next.IsMarkedForDeletion ||
                    current.DeletionStatus != next.DeletionStatus ||
                    current.ResultsAcknowledged != next.ResultsAcknowledged)
                    return false;
            }

            return true;
        }

        private static void ReplaceCollection<T>(ObservableCollection<T> collection, IEnumerable<T> items)
        {
            collection.Clear();

            foreach (var item in items)
                collection.Add(item);
        }

        private void ExportToJson()
        {
            var dialog = new SaveFileDialog
            {
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                FileName = "playlists.json"
            };

            if (dialog.ShowDialog() != true) return;

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(Playlists.ToList(), options);

            File.WriteAllText(dialog.FileName, json);
            Log($"Exported {Playlists.Count} playlist(s) to {dialog.FileName}.");
        }

        private void ImportFromJson()
        {
            var dialog = new OpenFileDialog
            {
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                Title = "Import playlists"
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                var json = File.ReadAllText(dialog.FileName);
                var imported = JsonSerializer.Deserialize<List<PlaylistCacheItem>>(json)
                               ?? new List<PlaylistCacheItem>();

                var available = _localStore.LoadAvailablePlaylists();
                var added = 0;
                var updated = 0;

                foreach (var item in imported)
                {
                    if (item == null || string.IsNullOrWhiteSpace(item.Id))
                        continue;

                    if (available.ContainsKey(item.Id))
                        updated++;
                    else
                        added++;

                    if (item.SnapshotUpdatedAtUtc == default)
                        item.SnapshotUpdatedAtUtc = DateTime.UtcNow;

                    available[item.Id] = item;
                }

                _localStore.SaveAvailablePlaylists(available);
                RefreshGridFromLocalFiles();
                RaiseDeletionCommandStates();
                DeleteAllToQueueCommand?.RaiseCanExecuteChanged();
                Log($"Imported {added + updated} playlist(s) from {dialog.FileName} ({added} new, {updated} updated).");
                Status = $"Imported {added + updated} playlist(s).";
            }
            catch (Exception ex)
            {
                Log($"Failed to import playlists: {ex.Message}");
                Status = "Failed to import playlists.";
            }
        }

        private async Task LogCurrentUserAsync()
        {
            try
            {
                var currentUser = await _spotify.GetPrivateProfileAsync();

                if (currentUser == null)
                {
                    Log("Spotify current user profile returned null.");
                    return;
                }

                Log($"Authenticated Spotify user: {currentUser.DisplayName ?? "(no display name)"} ({currentUser.Id}).");
                Log($"Current user profile: country={currentUser.Country}, product={currentUser.Product}, emailPresent={!string.IsNullOrWhiteSpace(currentUser.Email)}.", true);
            }
            catch (Exception ex)
            {
                Log($"Unable to read current Spotify user profile: {ex.Message}");
                Log($"Current user profile exception: {ex}", true);
            }
        }

        /// <summary>Hands the playlist to the Experimental → Prediction page (Loop Lab) for playback.</summary>
        private void OpenInLoopLab(PlaylistCacheItem playlist)
        {
            if (playlist == null || string.IsNullOrEmpty(playlist.Id))
                return;

            Log($"Opening playlist '{playlist.Name}' in Loop Lab.");
            MessengerInstance.Send($"spotify:playlist:{playlist.Id}", MessageType.OpenInLoopLab);
        }

        public async Task LoadTracksAsync(PlaylistCacheItem playlist)
        {
            if (playlist == null)
            {
                Log("Select a playlist before loading tracks.");
                return;
            }

            if (string.IsNullOrWhiteSpace(playlist.Id))
            {
                Log($"Cannot load tracks for playlist '{playlist.Name ?? "(unnamed)"}' because it has no Spotify ID.");
                Status = "Failed to load tracks.";
                return;
            }

            if (_spotify.Api == null)
            {
                Log("Spotify API client is not available yet. Complete login before loading tracks.");
                Status = "Login required before loading tracks.";
                return;
            }

            var cancellationToken = BeginCancelableAction();

            try
            {
                TracksPlaylistTitle = $"Tracks — {playlist.Name ?? "(unnamed)"}";
                Status = $"Loading tracks for {playlist.Name}...";
                Log($"Loading tracks for playlist '{playlist.Name}' ({playlist.Id}).");

                var currentUser = await _spotify.GetPrivateProfileAsync();
                cancellationToken.ThrowIfCancellationRequested();

                var fullPlaylist = await _spotify.Api.Playlists.Get(playlist.Id, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                playlist.OwnerId = fullPlaylist.Owner?.Id;
                playlist.OwnerDisplayName = fullPlaylist.Owner?.DisplayName ?? fullPlaylist.Owner?.Id;

                if (currentUser != null)
                {
                    Log($"Track load context: playlistOwner={playlist.OwnerDisplayName ?? "(unknown)"} ({playlist.OwnerId ?? "no owner id"}), currentUser={currentUser.DisplayName ?? currentUser.Id} ({currentUser.Id}).", true);

                    if (!string.IsNullOrWhiteSpace(playlist.OwnerId) &&
                        !string.Equals(playlist.OwnerId, currentUser.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        Log("Note: Spotify only returns playlist track items for playlists you own or collaborate on. This playlist appears to belong to another account.");
                    }
                }

                await Application.Current.Dispatcher.InvokeAsync(() => Tracks.Clear());

                var loadedCount = 0;
                var position = 1;
                var detailsResult = await TryLoadPlaylistTrackPagesAsync(
                    GetPlaylistTrackPage(fullPlaylist),
                    "Get Playlist",
                    position,
                    cancellationToken);
                loadedCount += detailsResult.LoadedCount;
                position = detailsResult.NextPosition;

                if (!detailsResult.LoadedAny)
                {
                    Log("Get Playlist did not return track items. Trying Get Playlist Items endpoint.", true);

                    var request = new PlaylistGetItemsRequest(PlaylistGetItemsRequest.AdditionalTypes.All)
                    {
                        Limit = 100,
                        Offset = 0
                    };

                    var itemsResult = await TryLoadPlaylistTrackPagesAsync(
                        await _spotify.Api.Playlists.GetItems(playlist.Id, request, cancellationToken),
                        "Get Playlist Items",
                        position,
                        cancellationToken);
                    loadedCount += itemsResult.LoadedCount;
                }

                if (loadedCount == 0 && (fullPlaylist.Tracks?.Total ?? fullPlaylist.Items?.Total) > 0)
                {
                    Log("Spotify reported tracks on this playlist, but no track rows were returned to the app.");
                    Log("If this is your playlist, your developer app may be in Development Mode. Confirm your Spotify account email is added under User Management in the Spotify Developer Dashboard.");
                }

                Log($"Loaded {loadedCount} track(s) for playlist '{playlist.Name}'.");
                Status = loadedCount == 0
                    ? $"No tracks found in '{playlist.Name}'."
                    : $"Loaded {loadedCount} track(s) from '{playlist.Name}'.";
            }
            catch (APIException ex) when (IsPlaylistTracksForbidden(ex))
            {
                Log("Spotify returned Forbidden while loading playlist tracks. Web Playback is not required for this feature.");
                Log("This usually means the playlist is not owned by or shared with your account, or your developer app is restricted from playlist item access in Development Mode.");
                Log("Confirm your Spotify email is listed in the app's User Management allowlist, then re-login. Owned playlists should load via Get Playlist before Get Playlist Items.");
                Log($"Track load forbidden response: {ex.Message}", true);
                Status = "Forbidden: Spotify blocked playlist track access for this app/account.";
            }
            catch (APITooManyRequestsException ex)
            {
                Log($"Spotify rate limited track loading. {FormatRetryAfter(ex)}.");
                Log($"Track load rate-limit exception: {ex}", true);
                Status = $"Rate limited. {FormatRetryAfter(ex)}.";
            }
            catch (OperationCanceledException)
            {
                Log("Cancelled track loading.");
                Status = "Cancelled track loading.";
            }
            catch (Exception ex)
            {
                Log($"Failed to load tracks: {ex}");
                Status = "Failed to load tracks.";
            }
            finally
            {
                EndCancelableAction();
            }
        }

        private static Paging<PlaylistTrack<IPlayableItem>> GetPlaylistTrackPage(FullPlaylist playlist)
        {
            return playlist?.Items ?? playlist?.Tracks;
        }

        private async Task<PlaylistTrackLoadResult> TryLoadPlaylistTrackPagesAsync(
            Paging<PlaylistTrack<IPlayableItem>> page,
            string source,
            int position,
            CancellationToken cancellationToken)
        {
            if (page?.Items == null || !page.Items.Any())
                return PlaylistTrackLoadResult.Empty(position);

            Log($"Loading playlist tracks via {source}.", true);

            var loadedCount = 0;

            while (page != null)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var mappedTracks = page.Items
                    .Select(item => AutoMapperConfiguration.MapPlaylistItem(item, position++))
                    .ToList();

                loadedCount += mappedTracks.Count;

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    foreach (var track in mappedTracks)
                        Tracks.Add(track);
                });

                Log($"Loaded playlist track page via {source}: offset={page.Offset?.ToString() ?? "unknown"}, items={mappedTracks.Count}, loadedTotal={loadedCount}.", true);

                if (page.Next == null)
                    break;

                await _requestSpacing.WaitForSpacingAsync(cancellationToken);
                page = await _spotify.Api.NextPage(page);
            }

            return new PlaylistTrackLoadResult(loadedCount > 0, loadedCount, position);
        }

        private sealed class PlaylistTrackLoadResult
        {
            public PlaylistTrackLoadResult(bool loadedAny, int loadedCount, int nextPosition)
            {
                LoadedAny = loadedAny;
                LoadedCount = loadedCount;
                NextPosition = nextPosition;
            }

            public bool LoadedAny { get; }

            public int LoadedCount { get; }

            public int NextPosition { get; }

            public static PlaylistTrackLoadResult Empty(int position)
            {
                return new PlaylistTrackLoadResult(false, 0, position);
            }
        }
    }
}
