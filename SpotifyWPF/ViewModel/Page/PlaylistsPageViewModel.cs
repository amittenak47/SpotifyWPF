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
        private readonly string _playlistStoreDirectory;
        private readonly string _availablePlaylistsPath;
        private readonly string _deletionQueuePath;

        private Paging<FullPlaylist> _currentPlaylistPage;

        private readonly List<LogEntry> _allLogMessages = new List<LogEntry>();

        private Visibility _progressVisibility = Visibility.Hidden;

        private string _status = "Ready";

        private string _selectedLogFilter = "Default";

        private CancellationTokenSource _currentActionCancellationTokenSource;

        private bool _isActionRunning;

        public PlaylistsPageViewModel(ISpotify spotify, IMapper mapper, IMessageBoxService messageBoxService)
        {
            _spotify = spotify;
            _mapper = mapper;
            _messageBoxService = messageBoxService;

            LoadPlaylistsCommand = new RelayCommand(async () => await LoadPlaylistsAsync());
            LoadMorePlaylistsCommand = new RelayCommand(async () => await LoadMorePlaylistsAsync(), CanLoadMorePlaylists);
            LoadAllPlaylistsCommand = new RelayCommand(async () => await LoadAllPlaylistsAsync());
            LoadTracksCommand = new RelayCommand<PlaylistCacheItem>(async playlist => await LoadTracksAsync(playlist));
            CancelCurrentActionCommand = new RelayCommand(CancelCurrentAction, CanCancelCurrentAction);
            StagePlaylistsCommand = new RelayCommand<IList>(StagePlaylists);
            UnstagePlaylistsCommand = new RelayCommand<IList>(UnstagePlaylists);
            MarkForDeletionCommand = new RelayCommand(MarkForDeletion, CanMarkForDeletion);
            DeletePlaylistsCommand = new RelayCommand(async () => await DeletePlaylistsAsync(), CanDeleteMarkedPlaylists);
            RefreshDeletionResultsCommand = new RelayCommand(RefreshDeletionResults);
            CopySelectedLogMessagesCommand = new RelayCommand<IList>(CopySelectedLogMessages);
            CopyAllLogMessagesCommand = new RelayCommand(CopyAllLogMessages);
            ExportToJsonCommand = new RelayCommand(ExportToJson);
            ImportFromJsonCommand = new RelayCommand(() => { }, () => false);

            _playlistStoreDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpotifyWPF", "Playlists");
            _availablePlaylistsPath = Path.Combine(_playlistStoreDirectory, "available-playlists.json");
            _deletionQueuePath = Path.Combine(_playlistStoreDirectory, "deletion-queue.json");
            Directory.CreateDirectory(_playlistStoreDirectory);

            _playlistGridRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _playlistGridRefreshTimer.Tick += (sender, args) => RefreshGridFromLocalFiles();
            _playlistGridRefreshTimer.Start();

            Log("Playlists view model created.");
            RefreshGridFromLocalFiles();
        }

        public ObservableCollection<PlaylistCacheItem> Playlists { get; } = new ObservableCollection<PlaylistCacheItem>();

        public ObservableCollection<DeletionQueueItem> StagedForDeletion { get; } = new ObservableCollection<DeletionQueueItem>();

        public ObservableCollection<Track> Tracks { get; } = new ObservableCollection<Track>();

        public ObservableCollection<string> LogMessages { get; } = new ObservableCollection<string>();

        public ObservableCollection<string> LogFilterOptions { get; } = new ObservableCollection<string>
        {
            "Default",
            "Verbose"
        };

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
                    CancelCurrentActionCommand?.RaiseCanExecuteChanged();
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

        public RelayCommand CancelCurrentActionCommand { get; }

        public RelayCommand DeletePlaylistsCommand { get; }

        public RelayCommand MarkForDeletionCommand { get; }

        public RelayCommand RefreshDeletionResultsCommand { get; }

        public RelayCommand LoadPlaylistsCommand { get; }

        public RelayCommand LoadMorePlaylistsCommand { get; }

        public RelayCommand LoadAllPlaylistsCommand { get; }

        public RelayCommand<IList> StagePlaylistsCommand { get; }

        public RelayCommand<IList> UnstagePlaylistsCommand { get; }

        public RelayCommand ExportToJsonCommand { get; }

        public RelayCommand ImportFromJsonCommand { get; }

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

            _currentActionCancellationTokenSource.Cancel();
            Status = "Cancelling...";
            Log("Cancellation requested for current playlist action.");
        }

        private bool CanCancelCurrentAction()
        {
            return IsActionRunning && _currentActionCancellationTokenSource?.IsCancellationRequested != true;
        }

        public async Task DeletePlaylistsAsync()
        {
            var playlists = StagedForDeletion
                .Where(playlist => playlist.IsMarkedForDeletion && playlist.DeletionStatus != DeletionStatus.Deleted)
                .ToList();

            if (!playlists.Any()) return;

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

            var cancellationToken = BeginCancelableAction();

            try
            {
                Status = $"Deleting {playlists.Count} staged playlist(s)...";
                Log($"Deleting {playlists.Count} staged playlist(s) with up to {MaxConcurrentPlaylistDeletes} concurrent request(s).");

                foreach (var playlist in playlists)
                    playlist.DeletionStatus = DeletionStatus.Pending;

                var deleteBatches = CreateDeleteBatches(playlists);
                var cancellationTokenSource = _currentActionCancellationTokenSource;
                var deleteTasks = deleteBatches.Select(batch => DeletePlaylistBatchAsync(batch, cancellationTokenSource)).ToList();
                var deleteResults = (await Task.WhenAll(deleteTasks)).SelectMany(result => result).ToList();
                var rateLimited = deleteResults.Any(result => result.Status == DeletionStatus.RateLimited);
                var cancelled = cancellationToken.IsCancellationRequested && !rateLimited;

                await Application.Current.Dispatcher.BeginInvoke((Action) (() =>
                {
                    foreach (var deleteResult in deleteResults)
                        deleteResult.Playlist.DeletionStatus = deleteResult.Status == DeletionStatus.Deleted ? DeletionStatus.Deleted : DeletionStatus.Failed;

                    if (rateLimited)
                    {
                        foreach (var playlist in playlists.Where(playlist => playlist.DeletionStatus == DeletionStatus.Pending))
                            playlist.DeletionStatus = DeletionStatus.Failed;
                    }
                    else if (cancelled)
                    {
                        foreach (var playlist in playlists.Where(playlist => playlist.DeletionStatus == DeletionStatus.Pending))
                            playlist.DeletionStatus = DeletionStatus.Failed;
                    }
                }));

                var deletedCount = deleteResults.Count(result => result.Status == DeletionStatus.Deleted);
                Log(rateLimited
                    ? $"Rate limited while deleting. Stopped remaining deletion work. Deleted {deletedCount} of {playlists.Count} staged playlist(s)."
                    : cancelled
                        ? $"Cancelled staged deletion. Deleted {deletedCount} of {playlists.Count} staged playlist(s)."
                    : $"Deleted {deletedCount} of {playlists.Count} staged playlist(s).");

                SaveDeletionQueue(StagedForDeletion);
                RaiseDeletionCommandStates();
            }
            finally
            {
                EndCancelableAction();
                Status = "Ready";
            }
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

                results.Add(new DeletePlaylistResult(
                    playlist,
                    await DeletePlaylistWithRetryAsync(playlist.Playlist, cancellationTokenSource)
                ));
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
                    await _spotify.Api.Follow.UnfollowPlaylist(playlist.Id);
                    await Task.Delay(150, cancellationTokenSource.Token);
                    return DeletionStatus.Deleted;
                }
                catch (APITooManyRequestsException ex)
                {
                    Log($"Spotify rate limit while deleting '{playlist.Name}'. Cancelling remaining staged deletions. RetryAfter: {GetRetryDelay(ex)}.");
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
                    await Task.Delay(retryDelay, cancellationTokenSource.Token);
                }
                catch (Exception ex) when (IsTransientDeleteException(ex) && transientAttempt < MaxTransientDeleteAttempts)
                {
                    transientAttempt++;
                    var retryDelay = GetTransientRetryDelay(transientAttempt);

                    Log($"Transient connection error while deleting '{playlist.Name}'. Attempt {transientAttempt}/{MaxTransientDeleteAttempts}; retrying after {retryDelay}.");
                    Log($"Transient delete exception for '{playlist.Name}': {ex}", true);
                    await Task.Delay(retryDelay, cancellationTokenSource.Token);
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

            if (Playlists.Count > 0)
            {
                Log("Skipping playlist load because playlists are already loaded.");
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

                var request = new PlaylistCurrentUsersRequest { Limit = 50, Offset = 0 };
                Log($"Requesting first playlist page. Limit: {request.Limit}. Offset: {request.Offset}.");
                LogPlaylistRequest("CurrentUsers", request);

                _currentPlaylistPage = await _spotify.Api.Playlists.CurrentUsers(request);
                cancellationToken.ThrowIfCancellationRequested();
                LogPage("Loaded first playlist page", _currentPlaylistPage);
                LogPlaylistResponse("CurrentUsers", _currentPlaylistPage);
                await CompareWithDefaultPlaylistRequestIfEmptyAsync(_currentPlaylistPage);
                cancellationToken.ThrowIfCancellationRequested();
                SaveAvailablePlaylists(_currentPlaylistPage.Items.Select(ToPlaylistCacheItem));
                RefreshGridFromLocalFiles();

                Log($"Playlist grid now contains {Playlists.Count} item(s).");
                LoadMorePlaylistsCommand.RaiseCanExecuteChanged();
            }
            catch (APITooManyRequestsException ex)
            {
                var retryDelay = GetRetryDelay(ex);
                Log($"Spotify rate limited playlist loading. Retry after {retryDelay}. Keeping cached playlists visible.");
                Log($"Playlist load rate-limit exception: {ex}", true);
                RefreshGridFromLocalFiles();
                Status = $"Rate limited. Retry after {retryDelay}.";
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
            Log("LoadMorePlaylistsAsync invoked.");

            if (_currentPlaylistPage == null)
            {
                Log("No current page exists; loading the first page instead.");
                await LoadPlaylistsAsync();
                return;
            }

            if (_currentPlaylistPage.Next == null)
            {
                Log("No additional playlist page is available.");
                return;
            }

            var cancellationToken = BeginCancelableAction();
            Status = "Loading more playlists...";

            try
            {
                Log($"Requesting next playlist page from {_currentPlaylistPage.Next}.", true);

                _currentPlaylistPage = await _spotify.Api.NextPage(_currentPlaylistPage);
                cancellationToken.ThrowIfCancellationRequested();
                LogPage("Loaded next playlist page", _currentPlaylistPage);
                LogPlaylistResponse("NextPage", _currentPlaylistPage);
                SaveAvailablePlaylists(_currentPlaylistPage.Items.Select(ToPlaylistCacheItem));
                RefreshGridFromLocalFiles();

                Log($"Playlist grid now contains {Playlists.Count} item(s).");
                LoadMorePlaylistsCommand.RaiseCanExecuteChanged();
            }
            catch (APITooManyRequestsException ex)
            {
                var retryDelay = GetRetryDelay(ex);
                Log($"Spotify rate limited Load More. Retry after {retryDelay}. Keeping cached playlists visible.");
                Log($"Load More rate-limit exception: {ex}", true);
                RefreshGridFromLocalFiles();
                Status = $"Rate limited. Retry after {retryDelay}.";
                return;
            }
            catch (OperationCanceledException)
            {
                Log("Cancelled loading more playlists.");
                Status = "Cancelled loading more playlists.";
                return;
            }
            catch (Exception ex)
            {
                Log($"Failed to load more playlists: {ex}");
                Status = "Failed to load more playlists.";
                return;
            }
            finally
            {
                EndCancelableAction();

                if (Status == "Loading more playlists...")
                    Status = "Ready";
            }
        }

        private void LogPage(string message, Paging<FullPlaylist> page)
        {
            var itemCount = page?.Items?.Count ?? 0;
            var total = page?.Total?.ToString() ?? "unknown";
            var hasNextPage = page?.Next != null;

            Log($"{message}. Items: {itemCount}. Total: {total}. Has next page: {hasNextPage}.");
        }

        public async Task LoadAllPlaylistsAsync()
        {
            Log("LoadAllPlaylistsAsync invoked.");

            if (_currentPlaylistPage == null)
            {
                await LoadPlaylistsAsync();

                if (_currentPlaylistPage == null || _currentPlaylistPage.Next == null)
                    return;
            }

            var cancellationToken = BeginCancelableAction();

            try
            {
                while (_currentPlaylistPage?.Next != null)
                {
                    Status = "Loading all playlists...";

                    await Task.Delay(150, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    Log($"Requesting next playlist page from {_currentPlaylistPage.Next}.", true);

                    _currentPlaylistPage = await _spotify.Api.NextPage(_currentPlaylistPage);
                    cancellationToken.ThrowIfCancellationRequested();
                    LogPage("Loaded playlist page during Load All", _currentPlaylistPage);
                    LogPlaylistResponse("NextPage", _currentPlaylistPage);
                    SaveAvailablePlaylists(_currentPlaylistPage.Items.Select(ToPlaylistCacheItem));

                    Log($"Playlist grid now contains {Playlists.Count} item(s).");
                }

                RefreshGridFromLocalFiles();
                Log("Finished loading all available playlist pages.");
                LoadMorePlaylistsCommand.RaiseCanExecuteChanged();

                Status = "Ready";
            }
            catch (APITooManyRequestsException ex)
            {
                var retryDelay = GetRetryDelay(ex);
                Log($"Spotify rate limited Load All. Stopping page fetches. Retry after {retryDelay}.");
                Log($"Load All rate-limit exception: {ex}", true);
                RefreshGridFromLocalFiles();
                Status = $"Rate limited. Retry after {retryDelay}.";
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
            }
        }

        private bool CanLoadMorePlaylists()
        {
            return _currentPlaylistPage?.Next != null;
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
                        deletionQueue.Remove(stagedPlaylist.Playlist.Id);
                        break;
                    case DeletionStatus.Failed:
                        stagedPlaylist.DeletionStatus = DeletionStatus.Pending;
                        stagedPlaylist.IsMarkedForDeletion = false;

                        availablePlaylists[stagedPlaylist.Playlist.Id] = stagedPlaylist.Playlist;
                        deletionQueue.Remove(stagedPlaylist.Playlist.Id);
                        break;
                }
            }

            SaveAvailablePlaylistDictionary(availablePlaylists);
            SaveDeletionQueueDictionary(deletionQueue);
            RefreshGridFromLocalFiles();
            RaiseDeletionCommandStates();
            Log("Refreshed staged deletion results.");
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

        private void MarkForDeletion()
        {
            var deletionQueue = LoadDeletionQueueDictionary();

            foreach (var item in deletionQueue.Values)
            {
                if (item.DeletionStatus == DeletionStatus.Deleted) continue;

                item.IsMarkedForDeletion = true;
                item.DeletionStatus = DeletionStatus.Pending;
            }

            SaveDeletionQueueDictionary(deletionQueue);
            RefreshGridFromLocalFiles();
            RaiseDeletionCommandStates();
            Log($"Marked {deletionQueue.Values.Count(item => item.IsMarkedForDeletion)} playlist(s) for deletion.");
        }

        private bool CanMarkForDeletion()
        {
            return StagedForDeletion.Any(item => !item.IsMarkedForDeletion && item.DeletionStatus != DeletionStatus.Deleted);
        }

        private bool CanDeleteMarkedPlaylists()
        {
            return StagedForDeletion.Any(item => item.IsMarkedForDeletion && item.DeletionStatus != DeletionStatus.Deleted);
        }

        private void RaiseDeletionCommandStates()
        {
            MarkForDeletionCommand.RaiseCanExecuteChanged();
            DeletePlaylistsCommand.RaiseCanExecuteChanged();
        }

        private void RefreshGridFromLocalFiles()
        {
            var availablePlaylists = LoadAvailablePlaylistDictionary().Values
                .OrderBy(playlist => playlist.Name)
                .ToList();
            var deletionQueue = LoadDeletionQueueDictionary().Values
                .OrderBy(playlist => playlist.Playlist?.Name)
                .ToList();

            Application.Current.Dispatcher.BeginInvoke((Action) (() =>
            {
                ReplaceCollection(Playlists, availablePlaylists);
                ReplaceCollection(StagedForDeletion, deletionQueue);
                RaiseDeletionCommandStates();
            }));
        }

        private void SaveAvailablePlaylists(IEnumerable<PlaylistCacheItem> playlists)
        {
            var availablePlaylists = LoadAvailablePlaylistDictionary();
            var deletionQueue = LoadDeletionQueueDictionary();

            foreach (var playlist in playlists)
            {
                if (string.IsNullOrWhiteSpace(playlist.Id) || deletionQueue.ContainsKey(playlist.Id)) continue;

                availablePlaylists[playlist.Id] = playlist;
            }

            SaveAvailablePlaylistDictionary(availablePlaylists);
        }

        private Dictionary<string, PlaylistCacheItem> LoadAvailablePlaylistDictionary()
        {
            return LoadDictionary<PlaylistCacheItem>(_availablePlaylistsPath);
        }

        private Dictionary<string, DeletionQueueItem> LoadDeletionQueueDictionary()
        {
            return LoadDictionary<DeletionQueueItem>(_deletionQueuePath);
        }

        private void SaveAvailablePlaylistDictionary(Dictionary<string, PlaylistCacheItem> playlists)
        {
            SaveDictionary(_availablePlaylistsPath, playlists);
        }

        private void SaveDeletionQueue(IEnumerable<DeletionQueueItem> playlists)
        {
            SaveDeletionQueueDictionary(playlists
                .Where(item => !string.IsNullOrWhiteSpace(item.Playlist?.Id))
                .ToDictionary(item => item.Playlist.Id, item => item));
        }

        private void SaveDeletionQueueDictionary(Dictionary<string, DeletionQueueItem> playlists)
        {
            SaveDictionary(_deletionQueuePath, playlists);
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
            Directory.CreateDirectory(_playlistStoreDirectory);

            var json = JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
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

        private async Task CompareWithDefaultPlaylistRequestIfEmptyAsync(Paging<FullPlaylist> page)
        {
            if (page?.Total != 0 && page?.Items?.Count != 0) return;

            try
            {
                Log("CurrentUsers request returned empty; comparing with parameterless CurrentUsers() used by master.", true);

                var defaultPage = await _spotify.Api.Playlists.CurrentUsers();
                LogPlaylistResponse("CurrentUsers default overload", defaultPage);

                if (defaultPage?.Items?.Count > 0 || defaultPage?.Total > 0)
                    Log("Default CurrentUsers() returned playlists while the explicit request returned none.");
            }
            catch (Exception ex)
            {
                Log($"Default CurrentUsers() comparison failed: {ex.Message}");
                Log($"Default CurrentUsers() comparison exception: {ex}", true);
            }
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

        public async Task LoadTracksAsync(PlaylistCacheItem playlist)
        {
            if (playlist == null) return;

            var cancellationToken = BeginCancelableAction();

            try
            {
                Status = "Loading tracks...";

                await Application.Current.Dispatcher.BeginInvoke((Action) (() => { Tracks.Clear(); }));

                var req = new PlaylistGetItemsRequest { Limit = 100 };
                var page = await _spotify.Api.Playlists.GetItems(playlist.Id, req);

                while (page != null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var tracks = page.Items.Select(item => _mapper.Map<Track>(item)).ToList();

                    await Application.Current.Dispatcher.BeginInvoke((Action) (() =>
                    {
                        foreach (var track in tracks)
                            Tracks.Add(track);
                    }));

                    if (page.Next != null)
                        page = await _spotify.Api.NextPage(page);
                    else
                        break;
                }

                Status = "Ready";
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

        public class PlaylistCacheItem
        {
            public string Id { get; set; }

            public string Name { get; set; }

            public string OwnerDisplayName { get; set; }

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
        }

        public enum DeletionStatus
        {
            Pending,
            Deleted,
            Failed,
            RateLimited
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