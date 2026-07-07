using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
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
using MessageBoxButton = SpotifyWPF.Service.MessageBoxes.MessageBoxButton;
using MessageBoxResult = SpotifyWPF.Service.MessageBoxes.MessageBoxResult;
// ReSharper disable AsyncVoidLambda

namespace SpotifyWPF.ViewModel.Page
{
    public class PlaylistsPageViewModel : ViewModelBase
    {
        private const int MaxConcurrentPlaylistDeletes = 4;
        private const int MaxTransientDeleteAttempts = 4;

        private readonly IMapper _mapper;

        private readonly IMessageBoxService _messageBoxService;
        private readonly ISpotify _spotify;
        private readonly DispatcherTimer _playlistGridRefreshTimer;
        private readonly string _playlistStoreRootDirectory;

        private Paging<FullPlaylist> _currentPlaylistPage;

        private readonly List<LogEntry> _allLogMessages = new List<LogEntry>();

        private Visibility _progressVisibility = Visibility.Hidden;

        private string _status = "Ready";

        private string _selectedLogFilter = "Default";

        private CancellationTokenSource _currentActionCancellationTokenSource;

        private bool _isActionRunning;

        private string _newPlaylistName;

        private string _newPlaylistDescription;

        private bool _newPlaylistIsPublic;

        private bool _newPlaylistIsCollaborative;

        private string _playlistsFilterText;

        private string _stagedPlaylistsFilterText;

        private int _playlistLoadLimit = 50;

        private readonly SemaphoreSlim _requestSpacing = new SemaphoreSlim(1, 1);

        private int _spotifyFetchOffset;

        private int? _lastKnownPlaylistTotal;

        private bool _isActionQueueExecuting;

        private bool _isActionQueuePaused;

        private readonly List<QueuedPlaylistAction> _actionQueue = new List<QueuedPlaylistAction>();

        public PlaylistsPageViewModel(ISpotify spotify, IMapper mapper, IMessageBoxService messageBoxService)
        {
            _spotify = spotify;
            _mapper = mapper;
            _messageBoxService = messageBoxService;

            LoadPlaylistsCommand = new RelayCommand(async () => await LoadPlaylistsAsync());
            LoadMorePlaylistsCommand = new RelayCommand(async () => await LoadMorePlaylistsAsync(), CanLoadMorePlaylists);
            LoadAllPlaylistsCommand = new RelayCommand(async () => await LoadAllPlaylistsAsync());
            LoadTracksCommand = new RelayCommand<PlaylistCacheItem>(async playlist => await LoadTracksAsync(playlist));
            RefreshSelectedPlaylistsCommand = new RelayCommand<IList>(async playlists => await RefreshSelectedPlaylistsAsync(playlists));
            CancelCurrentActionCommand = new RelayCommand(CancelCurrentAction, CanCancelCurrentAction);
            ExecuteOrPauseCommand = new RelayCommand(async () => await ExecuteOrPauseAsync(), CanExecuteOrPause);
            StagePlaylistsCommand = new RelayCommand<IList>(StagePlaylists);
            UnstagePlaylistsCommand = new RelayCommand<IList>(UnstagePlaylists);
            MarkForDeletionCommand = new RelayCommand<IList>(MarkForDeletion, CanMarkForDeletion);
            UnmarkForDeletionCommand = new RelayCommand<IList>(UnmarkForDeletion, CanUnmarkForDeletion);
            DeletePlaylistsCommand = new RelayCommand(async () => await DeletePlaylistsAsync(), CanDeleteMarkedPlaylists);
            RefreshDeletionResultsCommand = new RelayCommand(RefreshDeletionResults);
            CopySelectedLogMessagesCommand = new RelayCommand<IList>(CopySelectedLogMessages);
            CopyAllLogMessagesCommand = new RelayCommand(CopyAllLogMessages);
            ExportToJsonCommand = new RelayCommand(ExportToJson);
            ImportFromJsonCommand = new RelayCommand(() => { }, () => false);
            CreatePlaylistCommand = new RelayCommand(async () => await CreatePlaylistAsync(), CanCreatePlaylist);
            ApplyPlaylistsFilterCommand = new RelayCommand(RefreshGridFromLocalFiles);
            ClearPlaylistsFilterCommand = new RelayCommand(ClearPlaylistsFilter);
            ApplyStagedPlaylistsFilterCommand = new RelayCommand(RefreshGridFromLocalFiles);
            ClearStagedPlaylistsFilterCommand = new RelayCommand(ClearStagedPlaylistsFilter);
            EnqueueLoadLimitCommand = new RelayCommand(EnqueueLoadLimit, CanEnqueueActions);
            EnqueueLoadAllCommand = new RelayCommand(EnqueueLoadAll, CanEnqueueActions);
            EnqueueDeleteSelectionCommand = new RelayCommand<IList>(EnqueueDeleteSelection, CanEnqueueDeleteSelection);
            ClearActionQueueCommand = new RelayCommand(ClearActionQueue, () => QueuedActions.Any());
            RemoveSelectedQueuedActionsCommand = new RelayCommand<IList>(RemoveSelectedQueuedActions);

            _playlistStoreRootDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpotifyWPF", "Playlists");
            Directory.CreateDirectory(GetPlaylistStoreDirectory());

            _playlistGridRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _playlistGridRefreshTimer.Tick += (sender, args) => RefreshGridFromLocalFiles();
            _playlistGridRefreshTimer.Start();

            Log("Playlists view model created.");
            RefreshGridFromLocalFiles();
            LoadPaginationState();
        }

        public ObservableCollection<PlaylistCacheItem> Playlists { get; } = new ObservableCollection<PlaylistCacheItem>();

        public ObservableCollection<DeletionQueueItem> StagedForDeletion { get; } = new ObservableCollection<DeletionQueueItem>();

        public ObservableCollection<Track> Tracks { get; } = new ObservableCollection<Track>();

        public ObservableCollection<string> LogMessages { get; } = new ObservableCollection<string>();

        public ObservableCollection<QueuedPlaylistAction> QueuedActions { get; } = new ObservableCollection<QueuedPlaylistAction>();

        public ObservableCollection<string> LogFilterOptions { get; } = new ObservableCollection<string>
        {
            "Default",
            "Verbose"
        };

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

        private int _requestSpacingMilliseconds = 150;

        public int RequestSpacingMilliseconds
        {
            get => _requestSpacingMilliseconds;
            set => Set(ref _requestSpacingMilliseconds, Math.Max(0, value));
        }

        public string SelectedLogFilter
        {
            get => _selectedLogFilter;
            set
            {
                if (Set(ref _selectedLogFilter, value))
                    RefreshVisibleLogMessages();
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

        public RelayCommand<IList> RefreshSelectedPlaylistsCommand { get; }

        public RelayCommand CancelCurrentActionCommand { get; }

        public RelayCommand ExecuteOrPauseCommand { get; }

        public string ExecuteOrPauseButtonLabel
        {
            get
            {
                if (IsActionRunning && _isActionQueueExecuting && _isActionQueuePaused)
                    return "Resume";

                if (IsActionRunning)
                    return "Pause";

                return "Execute";
            }
        }

        public RelayCommand DeletePlaylistsCommand { get; }

        public RelayCommand<IList> MarkForDeletionCommand { get; }

        public RelayCommand<IList> UnmarkForDeletionCommand { get; }

        public RelayCommand RefreshDeletionResultsCommand { get; }

        public RelayCommand LoadPlaylistsCommand { get; }

        public RelayCommand LoadMorePlaylistsCommand { get; }

        public RelayCommand LoadAllPlaylistsCommand { get; }

        public RelayCommand<IList> StagePlaylistsCommand { get; }

        public RelayCommand<IList> UnstagePlaylistsCommand { get; }

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

        public RelayCommand<IList> CopySelectedLogMessagesCommand { get; }

        public RelayCommand CopyAllLogMessagesCommand { get; }

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

            _isActionQueuePaused = false;

            if (_isActionQueueExecuting)
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

        private bool CanExecuteOrPause()
        {
            return IsActionRunning || QueuedActions.Any();
        }

        private async Task ExecuteOrPauseAsync()
        {
            if (IsActionRunning)
            {
                if (_isActionQueueExecuting)
                {
                    if (_isActionQueuePaused)
                    {
                        _isActionQueuePaused = false;
                        Status = "Resuming queued actions...";
                        Log("Resumed queued action execution.");
                    }
                    else
                    {
                        _isActionQueuePaused = true;
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

        private async Task WaitWhilePausedAsync(CancellationToken cancellationToken)
        {
            while (_isActionQueuePaused)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(200, cancellationToken);
            }
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
                Log($"Deleting {playlists.Count} staged playlist(s) with up to {MaxConcurrentPlaylistDeletes} concurrent request(s) and {RequestSpacingMilliseconds} ms spacing between requests.");

                foreach (var playlist in playlists)
                {
                    playlist.DeletionStatus = DeletionStatus.Pending;
                    SyncStagedDeletionItem(playlist.Playlist?.Id, item => item.DeletionStatus = DeletionStatus.Pending);
                }

                var deleteBatches = CreateDeleteBatches(playlists);
                var cancellationTokenSource = _currentActionCancellationTokenSource;
                var deleteTasks = deleteBatches.Select(batch => DeletePlaylistBatchAsync(batch, cancellationTokenSource)).ToList();
                var deleteResults = (await Task.WhenAll(deleteTasks)).SelectMany(result => result).ToList();
                var rateLimited = deleteResults.Any(result => result.Status == DeletionStatus.RateLimited);
                var cancelled = cancellationToken.IsCancellationRequested && !rateLimited;

                await Application.Current.Dispatcher.InvokeAsync((Action) (() =>
                {
                    ApplyDeletionResultsToStagedItems(deleteResults);

                    if (rateLimited)
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
                    else if (cancelled)
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
                Log(rateLimited
                    ? $"Rate limited while deleting. Stopped remaining deletion work. Deleted {deletedCount} of {playlists.Count} staged playlist(s); failed or skipped {failedCount}."
                    : cancelled
                        ? $"Cancelled staged deletion. Deleted {deletedCount} of {playlists.Count} staged playlist(s); failed or skipped {failedCount}."
                    : $"Deleted {deletedCount} of {playlists.Count} staged playlist(s); failed {failedCount}.");

                PersistStagedDeletionGridToStore();
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
            var deletionQueue = LoadDeletionQueueDictionary();

            foreach (var stagedItem in StagedForDeletion)
            {
                if (string.IsNullOrWhiteSpace(stagedItem.Playlist?.Id)) continue;

                deletionQueue[stagedItem.Playlist.Id] = stagedItem;
            }

            SaveDeletionQueueDictionary(deletionQueue);
        }

        private static List<List<DeletionQueueItem>> CreateDeleteBatches(IReadOnlyList<DeletionQueueItem> playlists)
        {
            var batches = new List<List<DeletionQueueItem>>();

            if (!playlists.Any())
                return batches;

            var starts = new[]
            {
                0,
                playlists.Count / 4,
                playlists.Count / 2,
                playlists.Count * 3 / 4,
                playlists.Count
            }.Distinct().OrderBy(index => index).ToList();

            for (var i = 0; i < starts.Count - 1; i++)
            {
                var start = starts[i];
                var end = starts[i + 1];
                var batch = new List<DeletionQueueItem>();

                for (var playlistIndex = start; playlistIndex < end; playlistIndex++)
                    batch.Add(playlists[playlistIndex]);

                if (batch.Any())
                    batches.Add(batch);
            }

            return batches;
        }

        private async Task<List<DeletePlaylistResult>> DeletePlaylistBatchAsync(List<DeletionQueueItem> playlists, CancellationTokenSource cancellationTokenSource)
        {
            var results = new List<DeletePlaylistResult>();

            if (!playlists.Any())
                return results;

            Log($"Delete worker starting at '{playlists[0].Playlist.Name}' with {playlists.Count} playlist(s).", true);

            foreach (var playlist in playlists)
            {
                if (cancellationTokenSource.IsCancellationRequested)
                    break;

                try
                {
                    results.Add(new DeletePlaylistResult(
                        playlist,
                        await DeletePlaylistWithRetryAsync(playlist.Playlist, cancellationTokenSource)
                    ));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            return results;
        }

        private async Task<DeletionStatus> DeletePlaylistWithRetryAsync(PlaylistCacheItem playlist, CancellationTokenSource cancellationTokenSource)
        {
            var transientAttempt = 0;

            while (true)
            {
                if (cancellationTokenSource.IsCancellationRequested)
                    return DeletionStatus.Failed;

                try
                {
                    Log($"Deleting playlist: {playlist.Name}", true);
                    await _requestSpacing.WaitAsync(cancellationTokenSource.Token);
                    try
                    {
                        if (RequestSpacingMilliseconds > 0)
                            await Task.Delay(RequestSpacingMilliseconds, cancellationTokenSource.Token);

                        await _spotify.Api.Follow.UnfollowPlaylist(playlist.Id);
                    }
                    finally
                    {
                        _requestSpacing.Release();
                    }

                    Log($"Successfully deleted playlist '{playlist.Name}'.");
                    return DeletionStatus.Deleted;
                }
                catch (APITooManyRequestsException ex)
                {
                    Log($"Spotify rate limit while deleting '{playlist.Name}'. Cancelling remaining staged deletions. Retry-After: {FormatRetryDelay(GetRetryDelay(ex))}.");
                    cancellationTokenSource.Cancel();
                    return DeletionStatus.RateLimited;
                }
                catch (APIException ex) when (IsInsufficientScope(ex))
                {
                    Log($"Cannot delete playlist '{playlist.Name}': Spotify says the token has insufficient scope. Re-login may be required to grant playlist-modify-private and playlist-modify-public.");
                    Log($"Insufficient scope exception for '{playlist.Name}': {ex}", true);
                    return DeletionStatus.Failed;
                }
                catch (APIException ex) when (IsTransientDeleteException(ex) && transientAttempt < MaxTransientDeleteAttempts)
                {
                    transientAttempt++;
                    var retryDelay = GetTransientRetryDelay(transientAttempt);

                    Log($"Transient Spotify/API connection error while deleting '{playlist.Name}'. Attempt {transientAttempt}/{MaxTransientDeleteAttempts}; retrying after {retryDelay}.");
                    Log($"Transient API exception for '{playlist.Name}': {ex}", true);
                    try
                    {
                        await Task.Delay(retryDelay, cancellationTokenSource.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return DeletionStatus.Failed;
                    }
                }
                catch (Exception ex) when (IsTransientDeleteException(ex) && transientAttempt < MaxTransientDeleteAttempts)
                {
                    transientAttempt++;
                    var retryDelay = GetTransientRetryDelay(transientAttempt);

                    Log($"Transient connection error while deleting '{playlist.Name}'. Attempt {transientAttempt}/{MaxTransientDeleteAttempts}; retrying after {retryDelay}.");
                    Log($"Transient delete exception for '{playlist.Name}': {ex}", true);
                    try
                    {
                        await Task.Delay(retryDelay, cancellationTokenSource.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return DeletionStatus.Failed;
                    }
                }
                catch (OperationCanceledException)
                {
                    return DeletionStatus.Failed;
                }
                catch (Exception ex)
                {
                    Log($"Failed to delete playlist '{playlist.Name}': {ex.Message}");
                    Log($"Delete playlist exception for '{playlist.Name}': {ex}", true);
                    return DeletionStatus.Failed;
                }
            }
        }

        private static bool IsInsufficientScope(Exception ex)
        {
            return ContainsExceptionMessage(ex, "insufficient client scope");
        }

        private static bool IsTransientDeleteException(Exception ex)
        {
            return ex is HttpRequestException ||
                   ex is WebException ||
                   ex is IOException ||
                   ex is TaskCanceledException ||
                   ContainsExceptionMessage(ex, "underlying connection was closed") ||
                   ContainsExceptionMessage(ex, "connection was closed") ||
                   ContainsExceptionMessage(ex, "request was aborted") ||
                   ContainsExceptionMessage(ex, "temporarily unavailable");
        }

        private static bool ContainsExceptionMessage(Exception ex, string value)
        {
            for (var currentException = ex; currentException != null; currentException = currentException.InnerException)
            {
                if (currentException.Message?.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        private static TimeSpan GetTransientRetryDelay(int attempt)
        {
            return TimeSpan.FromMilliseconds(500 * attempt);
        }

        private class DeletePlaylistResult
        {
            public DeletePlaylistResult(DeletionQueueItem playlist, DeletionStatus status)
            {
                Playlist = playlist;
                Status = status;
            }

            public DeletionQueueItem Playlist { get; }

            public DeletionStatus Status { get; }
        }

        public async Task LoadPlaylistsAsync()
        {
            Log($"LoadPlaylistsAsync invoked. Current playlist count: {Playlists.Count}.");

            if (_spotifyFetchOffset > 0 || Playlists.Count > 0)
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

                var addedCount = await FetchPlaylistPageAtOffsetAsync(0, cancellationToken, useDefaultRequestFallback: true);
                Log($"Playlist grid now contains {Playlists.Count} item(s). Added {addedCount} new playlist(s). Next Spotify offset: {_spotifyFetchOffset}.");
                LoadMorePlaylistsCommand.RaiseCanExecuteChanged();
            }
            catch (APITooManyRequestsException ex)
            {
                var retryDelay = GetRetryDelay(ex);
                Log($"Spotify rate limited playlist loading. Retry after {FormatRetryDelay(retryDelay)}. Keeping cached playlists visible.");
                Log($"Playlist load rate-limit exception: {ex}", true);
                RefreshGridFromLocalFiles();
                Status = $"Rate limited. Retry after {FormatRetryDelay(retryDelay)}.";
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
                var retryDelay = GetRetryDelay(ex);
                Log($"Spotify rate limited Load More. Retry after {FormatRetryDelay(retryDelay)}. Keeping cached playlists visible.");
                Log($"Load More rate-limit exception: {ex}", true);
                RefreshGridFromLocalFiles();
                Status = $"Rate limited. Retry after {FormatRetryDelay(retryDelay)}.";
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

            if (HasReachedSpotifyPlaylistEnd())
            {
                Log("No additional playlist page is available.");
                return;
            }

            Status = "Loading more playlists...";
            var addedCount = await FetchPlaylistPageAtOffsetAsync(_spotifyFetchOffset, cancellationToken);
            Log($"Playlist grid now contains {Playlists.Count} item(s). Added {addedCount} new playlist(s). Next Spotify offset: {_spotifyFetchOffset}.");
            LoadMorePlaylistsCommand.RaiseCanExecuteChanged();
        }

        private async Task<int> FetchPlaylistPageAtOffsetAsync(int offset, CancellationToken cancellationToken, bool useDefaultRequestFallback = false)
        {
            offset = ResolveSpotifyFetchOffset(offset);

            if (HasReachedSpotifyPlaylistEnd())
            {
                Log($"Spotify offset {offset} is already at or beyond the last known playlist total ({_lastKnownPlaylistTotal}).");
                return 0;
            }

            await WaitForRequestSpacingAsync(cancellationToken);

            var request = new PlaylistCurrentUsersRequest { Limit = PlaylistLoadLimit, Offset = offset };
            Log($"Requesting playlist page. Limit: {request.Limit}. Offset: {request.Offset}.");
            LogPlaylistRequest("CurrentUsers", request);

            _currentPlaylistPage = await _spotify.Api.Playlists.CurrentUsers(request);
            cancellationToken.ThrowIfCancellationRequested();

            if (useDefaultRequestFallback)
                _currentPlaylistPage = await UseDefaultPlaylistRequestIfExplicitRequestIsEmptyAsync(_currentPlaylistPage);

            cancellationToken.ThrowIfCancellationRequested();
            LogPage("Loaded playlist page", _currentPlaylistPage);
            LogPlaylistResponse("CurrentUsers", _currentPlaylistPage);

            var itemsReturned = _currentPlaylistPage.Items?.Count ?? 0;
            var addedCount = SaveAvailablePlaylists(_currentPlaylistPage.Items.Select(ToPlaylistCacheItem));

            if (itemsReturned > 0)
                AdvanceSpotifyFetchOffset(offset, itemsReturned, addedCount);

            RefreshGridFromLocalFiles();
            return addedCount;
        }

        private int ResolveSpotifyFetchOffset(int requestedOffset)
        {
            var knownCount = GetKnownPlaylistCount();

            if (requestedOffset >= knownCount)
                return requestedOffset;

            Log($"Adjusting Spotify fetch offset from {requestedOffset} to {knownCount} because {knownCount} playlist(s) are already tracked locally.");
            _spotifyFetchOffset = knownCount;
            SavePaginationState();
            return knownCount;
        }

        private void AdvanceSpotifyFetchOffset(int fetchedOffset, int itemsReturned, int addedCount)
        {
            var linearAdvance = fetchedOffset + itemsReturned;
            var knownCount = GetKnownPlaylistCount();

            if (addedCount == 0 && knownCount > linearAdvance)
            {
                _spotifyFetchOffset = knownCount;
                Log($"Fetched {itemsReturned} playlist(s) at offset {fetchedOffset}; all were already local. Jumped Spotify offset to {knownCount} to skip refetching known pages.");
            }
            else
            {
                _spotifyFetchOffset = linearAdvance;

                if (addedCount == 0)
                    Log($"Fetched {itemsReturned} playlist(s) at offset {fetchedOffset}; all were already local. Advanced Spotify offset to {_spotifyFetchOffset}.");
            }

            SavePaginationState();
        }

        private async Task WaitForRequestSpacingAsync(CancellationToken cancellationToken)
        {
            await _requestSpacing.WaitAsync(cancellationToken);

            try
            {
                if (RequestSpacingMilliseconds > 0)
                    await Task.Delay(RequestSpacingMilliseconds, cancellationToken);
            }
            finally
            {
                _requestSpacing.Release();
            }
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
                var retryDelay = GetRetryDelay(ex);
                Log($"Spotify rate limited Load All. Stopping page fetches. Retry after {FormatRetryDelay(retryDelay)}.");
                Log($"Load All rate-limit exception: {ex}", true);
                RefreshGridFromLocalFiles();
                Status = $"Rate limited. Retry after {FormatRetryDelay(retryDelay)}.";
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

            while (!HasReachedSpotifyPlaylistEnd())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var offsetBeforeFetch = _spotifyFetchOffset;
                var addedCount = await FetchPlaylistPageAtOffsetAsync(_spotifyFetchOffset, cancellationToken);

                if (_spotifyFetchOffset == offsetBeforeFetch)
                    break;

                Log($"Loaded playlist page during Load All. Added {addedCount} new playlist(s). Next Spotify offset: {_spotifyFetchOffset}.", true);
            }

            Log("Finished loading all available playlist pages.");
            LoadMorePlaylistsCommand.RaiseCanExecuteChanged();
        }

        private void LogPage(string message, Paging<FullPlaylist> page)
        {
            var itemCount = page?.Items?.Count ?? 0;
            var total = page?.Total?.ToString() ?? "unknown";
            var hasNextPage = page?.Next != null;

            if (page?.Total != null)
                _lastKnownPlaylistTotal = page.Total;

            SavePaginationState();

            Log($"{message}. Items: {itemCount}. Total: {total}. Has next page: {hasNextPage}.");
        }

        private bool HasReachedSpotifyPlaylistEnd()
        {
            return _lastKnownPlaylistTotal.HasValue && _spotifyFetchOffset >= _lastKnownPlaylistTotal.Value;
        }

        private bool CanLoadMorePlaylists()
        {
            return _spotify.Api != null && !IsActionRunning;
        }

        private void StagePlaylists(IList items)
        {
            var playlists = items?.Cast<PlaylistCacheItem>().ToList();

            if (playlists == null || !playlists.Any()) return;

            var availablePlaylists = LoadAvailablePlaylistDictionary();
            var deletionQueue = LoadDeletionQueueDictionary();

            foreach (var playlist in playlists)
            {
                if (string.IsNullOrWhiteSpace(playlist.Id)) continue;

                availablePlaylists.Remove(playlist.Id);

                if (!deletionQueue.ContainsKey(playlist.Id))
                    deletionQueue[playlist.Id] = new DeletionQueueItem(playlist);
            }

            SaveAvailablePlaylistDictionary(availablePlaylists);
            SaveDeletionQueueDictionary(deletionQueue);
            RefreshGridFromLocalFiles();
            RaiseDeletionCommandStates();
        }

        private void UnstagePlaylists(IList items)
        {
            var playlists = items?.Cast<DeletionQueueItem>().ToList();

            if (playlists == null || !playlists.Any()) return;

            var availablePlaylists = LoadAvailablePlaylistDictionary();
            var deletionQueue = LoadDeletionQueueDictionary();

            foreach (var stagedPlaylist in playlists)
            {
                if (string.IsNullOrWhiteSpace(stagedPlaylist.Playlist?.Id)) continue;

                if (stagedPlaylist.DeletionStatus != DeletionStatus.Deleted)
                    availablePlaylists[stagedPlaylist.Playlist.Id] = stagedPlaylist.Playlist;

                deletionQueue.Remove(stagedPlaylist.Playlist.Id);
            }

            SaveAvailablePlaylistDictionary(availablePlaylists);
            SaveDeletionQueueDictionary(deletionQueue);
            RefreshGridFromLocalFiles();
            RaiseDeletionCommandStates();
        }

        private void RefreshDeletionResults()
        {
            var availablePlaylists = LoadAvailablePlaylistDictionary();
            var deletionQueue = LoadDeletionQueueDictionary();

            foreach (var stagedPlaylist in deletionQueue.Values.ToList())
            {
                if (string.IsNullOrWhiteSpace(stagedPlaylist.Playlist?.Id)) continue;

                switch (stagedPlaylist.DeletionStatus)
                {
                    case DeletionStatus.Deleted:
                        if (stagedPlaylist.ResultsAcknowledged)
                            deletionQueue.Remove(stagedPlaylist.Playlist.Id);
                        else
                            stagedPlaylist.ResultsAcknowledged = true;
                        break;
                    case DeletionStatus.Failed:
                        if (stagedPlaylist.ResultsAcknowledged)
                        {
                            stagedPlaylist.DeletionStatus = DeletionStatus.Pending;
                            stagedPlaylist.IsMarkedForDeletion = false;
                            stagedPlaylist.ResultsAcknowledged = false;
                        }
                        else
                        {
                            stagedPlaylist.ResultsAcknowledged = true;
                        }
                        break;
                }
            }

            SaveAvailablePlaylistDictionary(availablePlaylists);
            SaveDeletionQueueDictionary(deletionQueue);
            RefreshGridFromLocalFiles();
            RaiseDeletionCommandStates();
            Log("Refreshed staged deletion results.");
        }

        private async Task RefreshSelectedPlaylistsAsync(IList selectedItems)
        {
            var selectedPlaylists = selectedItems?.Cast<PlaylistCacheItem>().ToList();

            if (selectedPlaylists == null || !selectedPlaylists.Any()) return;

            var cancellationToken = BeginCancelableAction();

            try
            {
                Status = $"Refreshing {selectedPlaylists.Count} selected playlist(s)...";
                var availablePlaylists = LoadAvailablePlaylistDictionary();
                var deletionQueue = LoadDeletionQueueDictionary();

                foreach (var playlist in selectedPlaylists)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (string.IsNullOrWhiteSpace(playlist.Id)) continue;

                    try
                    {
                        var refreshedPlaylist = await _spotify.Api.Playlists.Get(playlist.Id);
                        var cacheItem = ToPlaylistCacheItem(refreshedPlaylist);

                        if (deletionQueue.ContainsKey(cacheItem.Id))
                            deletionQueue[cacheItem.Id].Playlist = cacheItem;
                        else
                            availablePlaylists[cacheItem.Id] = cacheItem;

                        Log($"Refreshed playlist '{cacheItem.Name}' ({cacheItem.Id}).", true);
                    }
                    catch (APITooManyRequestsException ex)
                    {
                        var retryDelay = GetRetryDelay(ex);
                        Log($"Spotify rate limited selected playlist refresh. Retry after {FormatRetryDelay(retryDelay)}.");
                        Status = $"Rate limited. Retry after {FormatRetryDelay(retryDelay)}.";
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

                SaveAvailablePlaylistDictionary(availablePlaylists);
                SaveDeletionQueueDictionary(deletionQueue);
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

                SaveAvailablePlaylists(new[] { ToPlaylistCacheItem(createdPlaylist) });
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
                var retryDelay = GetRetryDelay(ex);
                Log($"Spotify rate limited playlist creation. Retry after {FormatRetryDelay(retryDelay)}.");
                Log($"Playlist create rate-limit exception: {ex}", true);
                Status = $"Rate limited. Retry after {FormatRetryDelay(retryDelay)}.";
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

        private void CopySelectedLogMessages(IList selectedMessages)
        {
            var messages = selectedMessages?.Cast<string>().ToList();

            if (messages == null || !messages.Any()) return;

            Clipboard.SetText(string.Join(Environment.NewLine, messages));
        }

        private void CopyAllLogMessages()
        {
            if (!LogMessages.Any()) return;

            Clipboard.SetText(string.Join(Environment.NewLine, LogMessages));
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
            var deletionQueue = LoadDeletionQueueDictionary();
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

            SaveDeletionQueueDictionary(deletionQueue);
            RaiseDeletionCommandStates();
            Log($"{(isMarked ? "Marked" : "Unmarked")} {changedCount} playlist(s) for deletion.");
        }

        private static List<DeletionQueueItem> GetSelectedDeletionItems(IList items)
        {
            return items?.Cast<DeletionQueueItem>()
                .Where(item => item != null)
                .ToList() ?? new List<DeletionQueueItem>();
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
            RaiseActionQueueStates();
        }

        private void RaiseActionQueueStates()
        {
            ExecuteOrPauseCommand.RaiseCanExecuteChanged();
            EnqueueLoadLimitCommand.RaiseCanExecuteChanged();
            EnqueueLoadAllCommand.RaiseCanExecuteChanged();
            EnqueueDeleteSelectionCommand.RaiseCanExecuteChanged();
            ClearActionQueueCommand.RaiseCanExecuteChanged();
            RaisePropertyChanged(nameof(ExecuteOrPauseButtonLabel));
        }

        private bool CanEnqueueActions()
        {
            return _spotify.Api != null && (!IsActionRunning || _isActionQueuePaused);
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
            AddQueuedAction(action);
        }

        private void EnqueueLoadAll()
        {
            var action = new QueuedPlaylistAction
            {
                ActionType = PlaylistActionType.LoadAll
            };
            action.DetailItems.Add(new QueuedActionDetailItem("Fetch all remaining playlist pages", canRemove: false));
            action.RefreshDisplayName();
            AddQueuedAction(action);
        }

        private void EnqueueDeleteSelection(IList items)
        {
            var selectedItems = GetSelectedDeletionItems(items)
                .Where(item => item.IsMarkedForDeletion && item.DeletionStatus != DeletionStatus.Deleted)
                .ToList();

            if (!selectedItems.Any())
            {
                Log("Select one or more marked staged playlists to enqueue delete.");
                return;
            }

            var action = new QueuedPlaylistAction
            {
                ActionType = PlaylistActionType.DeleteSelection
            };

            foreach (var item in selectedItems)
            {
                action.PlaylistIds.Add(item.Playlist.Id);
                action.DetailItems.Add(new QueuedActionDetailItem(item.Playlist?.Name ?? item.Playlist?.Id, item.Playlist?.Id));
            }

            action.RefreshDisplayName();
            AddQueuedAction(action);
        }

        private void AddQueuedAction(QueuedPlaylistAction action)
        {
            _actionQueue.Add(action);
            QueuedActions.Add(action);
            Log($"Enqueued action: {action.DisplayName}");
            RaiseActionQueueStates();
        }

        public void RemoveQueuedAction(QueuedPlaylistAction action)
        {
            if (action == null) return;

            _actionQueue.Remove(action);
            QueuedActions.Remove(action);
            Log($"Removed queued action: {action.DisplayName}");
            RaiseActionQueueStates();
        }

        public void RemoveQueuedActionDetail(QueuedPlaylistAction action, QueuedActionDetailItem detail)
        {
            if (action == null || detail == null || !detail.CanRemove) return;

            action.DetailItems.Remove(detail);

            if (!string.IsNullOrWhiteSpace(detail.PlaylistId))
                action.PlaylistIds.Remove(detail.PlaylistId);

            if (!action.DetailItems.Any())
            {
                RemoveQueuedAction(action);
                return;
            }

            action.RefreshDisplayName();
            Log($"Removed playlist from queued action: {detail.DisplayName}");
            RaiseActionQueueStates();
        }

        public QueuedPlaylistAction FindQueuedActionForDetail(QueuedActionDetailItem detail)
        {
            return QueuedActions.FirstOrDefault(action => action.DetailItems.Contains(detail));
        }

        private void ClearActionQueue()
        {
            _actionQueue.Clear();
            QueuedActions.Clear();
            Log("Cleared queued actions.");
            RaiseActionQueueStates();
        }

        private void RemoveSelectedQueuedActions(IList items)
        {
            var selectedActions = items?.Cast<QueuedPlaylistAction>().ToList();
            if (selectedActions == null || !selectedActions.Any()) return;

            foreach (var action in selectedActions)
            {
                _actionQueue.Remove(action);
                QueuedActions.Remove(action);
            }

            Log($"Removed {selectedActions.Count} queued action(s).");
            RaiseActionQueueStates();
        }

        private async Task ExecuteActionQueueAsync()
        {
            if (!QueuedActions.Any()) return;

            var cancellationToken = BeginCancelableAction();
            _isActionQueueExecuting = true;
            _isActionQueuePaused = false;

            try
            {
                while (_actionQueue.Any())
                {
                    await WaitWhilePausedAsync(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();

                    var action = _actionQueue[0];
                    _actionQueue.RemoveAt(0);
                    QueuedActions.RemoveAt(0);
                    RaiseActionQueueStates();

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

                Log("Finished executing queued actions.");
            }
            catch (OperationCanceledException)
            {
                Log("Cancelled queued action execution.");
                Status = "Cancelled queued action execution.";
            }
            catch (APITooManyRequestsException ex)
            {
                var retryDelay = GetRetryDelay(ex);
                Log($"Spotify rate limited queued action execution. Retry after {FormatRetryDelay(retryDelay)}.");
                Status = $"Rate limited. Retry after {FormatRetryDelay(retryDelay)}.";
            }
            catch (Exception ex)
            {
                Log($"Failed while executing queued actions: {ex}");
                Status = "Failed while executing queued actions.";
            }
            finally
            {
                _isActionQueueExecuting = false;
                _isActionQueuePaused = false;
                EndCancelableAction();
                RaiseActionQueueStates();

                if (Status.StartsWith("Executing:") || Status.StartsWith("Paused") || Status.StartsWith("Resuming"))
                    Status = "Ready";
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

        private void LoadPaginationState()
        {
            try
            {
                var path = GetPlaylistPaginationPath();
                if (!File.Exists(path))
                {
                    _spotifyFetchOffset = GetKnownPlaylistCount();
                    return;
                }

                var json = File.ReadAllText(path);
                var state = JsonSerializer.Deserialize<PlaylistPaginationState>(json);
                _spotifyFetchOffset = state?.SpotifyFetchOffset ?? GetKnownPlaylistCount();
                _lastKnownPlaylistTotal = state?.LastKnownTotal;

                var knownCount = GetKnownPlaylistCount();
                if (knownCount > _spotifyFetchOffset)
                {
                    _spotifyFetchOffset = knownCount;
                    SavePaginationState();
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to load playlist pagination state: {ex.Message}");
                _spotifyFetchOffset = GetKnownPlaylistCount();
            }
        }

        private void SavePaginationState()
        {
            try
            {
                var state = new PlaylistPaginationState
                {
                    SpotifyFetchOffset = _spotifyFetchOffset,
                    LastKnownTotal = _lastKnownPlaylistTotal
                };

                Directory.CreateDirectory(GetPlaylistStoreDirectory());
                var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(GetPlaylistPaginationPath(), json);
            }
            catch (Exception ex)
            {
                Log($"Failed to save playlist pagination state: {ex.Message}");
            }
        }

        private string GetPlaylistPaginationPath()
        {
            return Path.Combine(GetPlaylistStoreDirectory(), "playlist-pagination.json");
        }

        private void RefreshGridFromLocalFiles()
        {
            var playlistsFilter = PlaylistsFilterText?.Trim();
            var stagedFilter = StagedPlaylistsFilterText?.Trim();

            var availablePlaylists = LoadAvailablePlaylistDictionary().Values
                .Where(playlist => MatchesPlaylistFilter(playlist, playlistsFilter))
                .OrderBy(playlist => playlist.Name)
                .ToList();
            var deletionQueue = LoadDeletionQueueDictionary().Values
                .Where(item => MatchesPlaylistFilter(item.Playlist, stagedFilter))
                .OrderBy(playlist => playlist.Playlist?.Name)
                .ToList();

            Application.Current.Dispatcher.BeginInvoke((Action) (() =>
            {
                if (!PlaylistCollectionsMatch(Playlists, availablePlaylists))
                    ReplaceCollection(Playlists, availablePlaylists);

                if (!DeletionQueueCollectionsMatch(StagedForDeletion, deletionQueue))
                    ReplaceCollection(StagedForDeletion, deletionQueue);

                RaiseDeletionCommandStates();
            }));
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

        private int SaveAvailablePlaylists(IEnumerable<PlaylistCacheItem> playlists)
        {
            var availablePlaylists = LoadAvailablePlaylistDictionary();
            var deletionQueue = LoadDeletionQueueDictionary();
            var addedCount = 0;

            foreach (var playlist in playlists)
            {
                if (string.IsNullOrWhiteSpace(playlist.Id) || deletionQueue.ContainsKey(playlist.Id)) continue;

                var isNew = !availablePlaylists.ContainsKey(playlist.Id);
                availablePlaylists[playlist.Id] = playlist;

                if (isNew)
                    addedCount++;
            }

            SaveAvailablePlaylistDictionary(availablePlaylists);
            return addedCount;
        }

        private int GetKnownPlaylistCount()
        {
            return LoadAvailablePlaylistDictionary().Count + LoadDeletionQueueDictionary().Count;
        }

        private Dictionary<string, PlaylistCacheItem> LoadAvailablePlaylistDictionary()
        {
            return LoadDictionary<PlaylistCacheItem>(GetAvailablePlaylistsPath());
        }

        private Dictionary<string, DeletionQueueItem> LoadDeletionQueueDictionary()
        {
            return LoadDictionary<DeletionQueueItem>(GetDeletionQueuePath());
        }

        private void SaveAvailablePlaylistDictionary(Dictionary<string, PlaylistCacheItem> playlists)
        {
            SaveDictionary(GetAvailablePlaylistsPath(), playlists);
        }

        private void SaveDeletionQueue(IEnumerable<DeletionQueueItem> playlists)
        {
            SaveDeletionQueueDictionary(playlists
                .Where(item => !string.IsNullOrWhiteSpace(item.Playlist?.Id))
                .ToDictionary(item => item.Playlist.Id, item => item));
        }

        private void SaveDeletionQueueDictionary(Dictionary<string, DeletionQueueItem> playlists)
        {
            SaveDictionary(GetDeletionQueuePath(), playlists);
        }

        private Dictionary<string, T> LoadDictionary<T>(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return new Dictionary<string, T>();

                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<Dictionary<string, T>>(json) ?? new Dictionary<string, T>();
            }
            catch (Exception ex)
            {
                Log($"Failed to read local playlist store {path}: {ex.Message}");
                Log($"Local playlist store read exception: {ex}", true);
                return new Dictionary<string, T>();
            }
        }

        private void SaveDictionary<T>(string path, Dictionary<string, T> values)
        {
            Directory.CreateDirectory(GetPlaylistStoreDirectory());

            var json = JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }

        private string GetPlaylistStoreDirectory()
        {
            return Path.Combine(_playlistStoreRootDirectory, GetSafeClientId());
        }

        private string GetAvailablePlaylistsPath()
        {
            return Path.Combine(GetPlaylistStoreDirectory(), "available-playlists.json");
        }

        private string GetDeletionQueuePath()
        {
            return Path.Combine(GetPlaylistStoreDirectory(), "deletion-queue.json");
        }

        private static string GetSafeClientId()
        {
            var clientId = Properties.Settings.Default.SpotifyClientId ?? "default";
            var safeClientId = new string(clientId.Where(char.IsLetterOrDigit).ToArray());

            return string.IsNullOrWhiteSpace(safeClientId) ? "default" : safeClientId;
        }

        private static void ReplaceCollection<T>(ObservableCollection<T> collection, IEnumerable<T> items)
        {
            collection.Clear();

            foreach (var item in items)
                collection.Add(item);
        }

        private static PlaylistCacheItem ToPlaylistCacheItem(FullPlaylist playlist)
        {
            return new PlaylistCacheItem
            {
                Id = playlist.Id,
                Name = playlist.Name,
                OwnerId = playlist.Owner?.Id,
                OwnerDisplayName = playlist.Owner?.DisplayName ?? playlist.Owner?.Id,
                TracksTotal = playlist.Tracks?.Total,
                SnapshotUpdatedAtUtc = DateTime.UtcNow
            };
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

        private void LogPlaylistRequest(string operation, PlaylistCurrentUsersRequest request)
        {
            Log($"{operation} request: limit={request.Limit}, offset={request.Offset}, locale={request.Locale ?? "(default)"}.", true);
        }

        private void LogPlaylistResponse(string operation, Paging<FullPlaylist> page)
        {
            Log($"{operation} response: total={page?.Total?.ToString() ?? "unknown"}, limit={page?.Limit?.ToString() ?? "unknown"}, offset={page?.Offset?.ToString() ?? "unknown"}, items={page?.Items?.Count ?? 0}, next={page?.Next ?? "(none)"}, previous={page?.Previous ?? "(none)"}.", true);

            if (page?.Items == null || page.Items.Count == 0)
            {
                Log($"{operation} response items: none.", true);
                return;
            }

            var itemSummary = string.Join("; ", page.Items.Take(10).Select(playlist =>
                $"{playlist.Name ?? "(unnamed)"} id={playlist.Id ?? "(no id)"} owner={playlist.Owner?.DisplayName ?? playlist.Owner?.Id ?? "(unknown)"} tracks={playlist.Tracks?.Total.ToString() ?? "unknown"}"));

            Log($"{operation} response first {Math.Min(page.Items.Count, 10)} item(s): {itemSummary}.", true);
        }

        private async Task<Paging<FullPlaylist>> UseDefaultPlaylistRequestIfExplicitRequestIsEmptyAsync(Paging<FullPlaylist> page)
        {
            if (HasPlaylistItems(page)) return page;

            try
            {
                Log("CurrentUsers request returned empty; trying parameterless CurrentUsers() fallback used by master.", true);

                var defaultPage = await _spotify.Api.Playlists.CurrentUsers();
                LogPlaylistResponse("CurrentUsers default overload", defaultPage);

                if (HasPlaylistItems(defaultPage))
                {
                    Log("Default CurrentUsers() returned playlists while the explicit request returned none. Using default result for cache and grid.");
                    return defaultPage;
                }
            }
            catch (Exception ex)
            {
                Log($"Default CurrentUsers() comparison failed: {ex.Message}");
                Log($"Default CurrentUsers() comparison exception: {ex}", true);
            }

            return page;
        }

        private static bool HasPlaylistItems(Paging<FullPlaylist> page)
        {
            return (page?.Items?.Count ?? 0) > 0;
        }

        private void Log(string message, bool verbose = false)
        {
            var logEntry = new LogEntry(DateTime.Now, message, verbose);
            Console.WriteLine(logEntry.FormattedMessage);

            Application.Current.Dispatcher.BeginInvoke((Action) (() =>
            {
                _allLogMessages.Add(logEntry);

                while (_allLogMessages.Count > 200)
                    _allLogMessages.RemoveAt(0);

                if (ShouldShowLog(logEntry))
                    LogMessages.Add(logEntry.FormattedMessage);

                while (LogMessages.Count > 200)
                    LogMessages.RemoveAt(0);
            }));
        }

        private void RefreshVisibleLogMessages()
        {
            LogMessages.Clear();

            foreach (var logEntry in _allLogMessages.Where(ShouldShowLog))
                LogMessages.Add(logEntry.FormattedMessage);
        }

        private bool ShouldShowLog(LogEntry logEntry)
        {
            return SelectedLogFilter == "Verbose" || !logEntry.IsVerbose;
        }

        private static TimeSpan GetRetryDelay(APITooManyRequestsException ex)
        {
            object retryAfter = ex.RetryAfter;

            switch (retryAfter)
            {
                case TimeSpan timeSpan:
                    return timeSpan;
                case int seconds:
                    return TimeSpan.FromSeconds(seconds);
                case long seconds:
                    return TimeSpan.FromSeconds(seconds);
                case double seconds:
                    return TimeSpan.FromSeconds(seconds);
                default:
                    return TimeSpan.FromSeconds(1);
            }
        }

        private static string FormatRetryDelay(TimeSpan retryDelay)
        {
            return retryDelay.TotalHours >= 1
                ? $"{retryDelay:c} ({retryDelay.TotalHours:N1} hour(s))"
                : retryDelay.ToString();
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

                if (currentUser != null)
                {
                    Log($"Track load context: playlistOwner={playlist.OwnerDisplayName ?? playlist.OwnerId ?? "(unknown)"}, currentUser={currentUser.DisplayName ?? currentUser.Id} ({currentUser.Id}).", true);

                    if (!string.IsNullOrWhiteSpace(playlist.OwnerId) &&
                        !string.Equals(playlist.OwnerId, currentUser.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        Log("Note: Spotify only returns playlist track items for playlists you own or collaborate on. This playlist appears to belong to another account.");
                    }
                }

                await Application.Current.Dispatcher.InvokeAsync(() => Tracks.Clear());

                var request = new PlaylistGetItemsRequest(PlaylistGetItemsRequest.AdditionalTypes.All)
                {
                    Limit = 100,
                    Offset = 0
                };

                var page = await _spotify.Api.Playlists.GetItems(playlist.Id, request, cancellationToken);
                var loadedCount = 0;
                var position = 1;

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

                    Log($"Loaded playlist track page: offset={page.Offset?.ToString() ?? "unknown"}, items={mappedTracks.Count}, loadedTotal={loadedCount}.", true);

                    if (page.Next == null)
                        break;

                    await WaitForRequestSpacingAsync(cancellationToken);
                    page = await _spotify.Api.NextPage(page);
                }

                Log($"Loaded {loadedCount} track(s) for playlist '{playlist.Name}'.");
                Status = loadedCount == 0
                    ? $"No tracks found in '{playlist.Name}'."
                    : $"Loaded {loadedCount} track(s) from '{playlist.Name}'.";
            }
            catch (APIException ex) when (IsPlaylistTracksForbidden(ex))
            {
                Log("Spotify returned Forbidden for playlist tracks. Web Playback is not required for this feature.");
                Log("Spotify only allows Get Playlist Items for playlists you own or where you are a collaborator. Followed/liked playlists from other users will return 403.");
                Log($"Track load forbidden response: {ex.Message}", true);
                Status = "Forbidden: track list only available for your own or collaborative playlists.";
            }
            catch (APITooManyRequestsException ex)
            {
                var retryDelay = GetRetryDelay(ex);
                Log($"Spotify rate limited track loading. Retry after {FormatRetryDelay(retryDelay)}.");
                Log($"Track load rate-limit exception: {ex}", true);
                Status = $"Rate limited. Retry after {FormatRetryDelay(retryDelay)}.";
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

        private static bool IsPlaylistTracksForbidden(APIException ex)
        {
            return ex != null && ex.Message?.IndexOf("Forbidden", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public class PlaylistCacheItem
        {
            public string Id { get; set; }

            public string Name { get; set; }

            public string OwnerDisplayName { get; set; }

            public string OwnerId { get; set; }

            public int? TracksTotal { get; set; }

            public DateTime SnapshotUpdatedAtUtc { get; set; }
        }

        public class DeletionQueueItem : ViewModelBase
        {
            private DeletionStatus _deletionStatus = DeletionStatus.Pending;
            private bool _isMarkedForDeletion;

            public DeletionQueueItem() { }

            public DeletionQueueItem(PlaylistCacheItem playlist)
            {
                Playlist = playlist;
            }

            public PlaylistCacheItem Playlist { get; set; }

            public bool IsMarkedForDeletion
            {
                get => _isMarkedForDeletion;
                set
                {
                    if (Set(ref _isMarkedForDeletion, value))
                        RaisePropertyChanged(nameof(MarkStatus));
                }
            }

            public DeletionStatus DeletionStatus
            {
                get => _deletionStatus;
                set
                {
                    if (Set(ref _deletionStatus, value))
                        RaisePropertyChanged(nameof(DeletionStatusName));
                }
            }

            public string DeletionStatusName => DeletionStatus.ToString();

            public string MarkStatus => IsMarkedForDeletion ? "Marked" : "Queued";

            public bool ResultsAcknowledged { get; set; }
        }

        public enum DeletionStatus
        {
            Pending,
            Deleted,
            Failed,
            RateLimited
        }

        public enum PlaylistActionType
        {
            LoadLimit,
            LoadAll,
            DeleteSelection
        }

        public class QueuedPlaylistAction : ViewModelBase
        {
            private bool _isExpanded;

            public PlaylistActionType ActionType { get; set; }

            public List<string> PlaylistIds { get; } = new List<string>();

            public ObservableCollection<QueuedActionDetailItem> DetailItems { get; } = new ObservableCollection<QueuedActionDetailItem>();

            public string DisplayName { get; private set; }

            public bool IsExpanded
            {
                get => _isExpanded;
                set => Set(ref _isExpanded, value);
            }

            public void RefreshDisplayName()
            {
                switch (ActionType)
                {
                    case PlaylistActionType.LoadLimit:
                        DisplayName = "Load limit";
                        break;
                    case PlaylistActionType.LoadAll:
                        DisplayName = "Load all";
                        break;
                    case PlaylistActionType.DeleteSelection:
                        DisplayName = $"Delete selection ({DetailItems.Count})";
                        break;
                }

                RaisePropertyChanged(nameof(DisplayName));
            }
        }

        public class QueuedActionDetailItem : ViewModelBase
        {
            public QueuedActionDetailItem(string displayName, string playlistId = null, bool canRemove = true)
            {
                DisplayName = displayName;
                PlaylistId = playlistId;
                CanRemove = canRemove;
            }

            public string DisplayName { get; }

            public string PlaylistId { get; }

            public bool CanRemove { get; }
        }

        private class PlaylistPaginationState
        {
            public int SpotifyFetchOffset { get; set; }

            public int? LastKnownTotal { get; set; }
        }

        private class LogEntry
        {
            public LogEntry(DateTime timestamp, string message, bool isVerbose)
            {
                Timestamp = timestamp;
                Message = message;
                IsVerbose = isVerbose;
            }

            public DateTime Timestamp { get; }

            public string Message { get; }

            public bool IsVerbose { get; }

            public string FormattedMessage => $"[{Timestamp:HH:mm:ss}] {(IsVerbose ? "[Verbose] " : string.Empty)}{Message}";
        }
    }
}