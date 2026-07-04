using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
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
        private readonly IMapper _mapper;

        private readonly IMessageBoxService _messageBoxService;
        private readonly ISpotify _spotify;

        private Paging<FullPlaylist> _currentPlaylistPage;

        private readonly List<LogEntry> _allLogMessages = new List<LogEntry>();

        private Visibility _progressVisibility = Visibility.Hidden;

        private string _status = "Ready";

        private string _selectedLogFilter = "Default";

        public PlaylistsPageViewModel(ISpotify spotify, IMapper mapper, IMessageBoxService messageBoxService)
        {
            _spotify = spotify;
            _mapper = mapper;
            _messageBoxService = messageBoxService;

            LoadPlaylistsCommand = new RelayCommand(async () => await LoadPlaylistsAsync());
            LoadMorePlaylistsCommand = new RelayCommand(async () => await LoadMorePlaylistsAsync(), CanLoadMorePlaylists);
            LoadAllPlaylistsCommand = new RelayCommand(async () => await LoadAllPlaylistsAsync());
            LoadTracksCommand = new RelayCommand<FullPlaylist>(async playlist => await LoadTracksAsync(playlist));
            StagePlaylistsCommand = new RelayCommand<IList>(StagePlaylists);
            UnstagePlaylistsCommand = new RelayCommand<IList>(UnstagePlaylists);
            DeletePlaylistsCommand = new RelayCommand(async () => await DeletePlaylistsAsync());
            ExportToJsonCommand = new RelayCommand(ExportToJson);
            ImportFromJsonCommand = new RelayCommand(() => { }, () => false);

            Log("Playlists view model created.");
        }

        public ObservableCollection<FullPlaylist> Playlists { get; } = new ObservableCollection<FullPlaylist>();

        public ObservableCollection<FullPlaylist> StagedForDeletion { get; } = new ObservableCollection<FullPlaylist>();

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

        public string Status
        {
            get => _status;

            set
            {
                _status = value;
                RaisePropertyChanged();

                if (value == "Ready")
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

        public RelayCommand<FullPlaylist> LoadTracksCommand { get; }

        public RelayCommand DeletePlaylistsCommand { get; }

        public RelayCommand LoadPlaylistsCommand { get; }

        public RelayCommand LoadMorePlaylistsCommand { get; }

        public RelayCommand LoadAllPlaylistsCommand { get; }

        public RelayCommand<IList> StagePlaylistsCommand { get; }

        public RelayCommand<IList> UnstagePlaylistsCommand { get; }

        public RelayCommand ExportToJsonCommand { get; }

        public RelayCommand ImportFromJsonCommand { get; }

        public async Task DeletePlaylistsAsync()
        {
            var playlists = StagedForDeletion.ToList();

            if (!playlists.Any()) return;

            var message = playlists.Count == 1
                ? $"Are you sure you want to delete playlist {playlists[0].Name}?"
                : $"Are you sure you want to delete these {playlists.Count} playlists?";

            var result = _messageBoxService.ShowMessageBox(
                message,
                "Confirm",
                MessageBoxButton.YesNo,
                MessageBoxIcon.Exclamation
            );

            if (result != MessageBoxResult.Yes) return;

            for (var i = playlists.Count - 1; i >= 0; i--)
            {
                var playlist = playlists[i];

                Status = "Deleting playlist: " + playlist.Name;

                var deleted = false;

                while (!deleted)
                {
                    try
                    {
                        await _spotify.Api.Follow.UnfollowPlaylist(playlist.Id);
                        await Task.Delay(150);
                        deleted = true;
                    }
                    catch (APITooManyRequestsException ex)
                    {
                        var retryDelay = GetRetryDelay(ex);
                        Console.WriteLine($"Spotify rate limit while deleting '{playlist.Name}'. Retrying after {retryDelay}.");
                        await Task.Delay(retryDelay);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to delete playlist '{playlist.Name}': {ex}");
                        break;
                    }
                }

                if (!deleted) continue;

                await Application.Current.Dispatcher.BeginInvoke((Action) (() =>
                {
                    Playlists.Remove(playlist);
                    StagedForDeletion.Remove(playlist);
                }));
            }

            Status = "Ready";
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

            Status = "Loading playlists...";

            try
            {
                await LogCurrentUserAsync();

                var request = new PlaylistCurrentUsersRequest { Limit = 10, Offset = 0 };
                Log($"Requesting first playlist page. Limit: {request.Limit}. Offset: {request.Offset}.");
                LogPlaylistRequest("CurrentUsers", request);

                _currentPlaylistPage = await _spotify.Api.Playlists.CurrentUsers(request);
                LogPage("Loaded first playlist page", _currentPlaylistPage);
                LogPlaylistResponse("CurrentUsers", _currentPlaylistPage);
                await CompareWithDefaultPlaylistRequestIfEmptyAsync(_currentPlaylistPage);

                await Application.Current.Dispatcher.BeginInvoke((Action) (() =>
                {
                    Playlists.Clear();
                    StagedForDeletion.Clear();

                    foreach (var playlist in _currentPlaylistPage.Items)
                        Playlists.Add(playlist);
                }));

                Log($"Playlist grid now contains {Playlists.Count} item(s).");
                LoadMorePlaylistsCommand.RaiseCanExecuteChanged();
            }
            catch (Exception ex)
            {
                Log($"Failed to load playlists: {ex}");
                Status = "Failed to load playlists.";
                return;
            }
            finally
            {
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

            Status = "Loading more playlists...";

            try
            {
                Log($"Requesting next playlist page from {_currentPlaylistPage.Next}.", true);

                _currentPlaylistPage = await _spotify.Api.NextPage(_currentPlaylistPage);
                LogPage("Loaded next playlist page", _currentPlaylistPage);
                LogPlaylistResponse("NextPage", _currentPlaylistPage);

                await Application.Current.Dispatcher.BeginInvoke((Action) (() =>
                {
                    foreach (var playlist in _currentPlaylistPage.Items)
                        Playlists.Add(playlist);
                }));

                Log($"Playlist grid now contains {Playlists.Count} item(s).");
                LoadMorePlaylistsCommand.RaiseCanExecuteChanged();
            }
            catch (Exception ex)
            {
                Log($"Failed to load more playlists: {ex}");
                Status = "Failed to load more playlists.";
                return;
            }
            finally
            {
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
                await LoadPlaylistsAsync();

            while (_currentPlaylistPage?.Next != null)
            {
                Status = "Loading all playlists...";

                await Task.Delay(150);

                try
                {
                    Log($"Requesting next playlist page from {_currentPlaylistPage.Next}.", true);

                    _currentPlaylistPage = await _spotify.Api.NextPage(_currentPlaylistPage);
                    LogPage("Loaded playlist page during Load All", _currentPlaylistPage);
                    LogPlaylistResponse("NextPage", _currentPlaylistPage);

                    await Application.Current.Dispatcher.BeginInvoke((Action) (() =>
                    {
                        foreach (var playlist in _currentPlaylistPage.Items)
                            Playlists.Add(playlist);
                    }));

                    Log($"Playlist grid now contains {Playlists.Count} item(s).");
                }
                catch (Exception ex)
                {
                    Log($"Failed while loading all playlists: {ex}");
                    Status = "Failed while loading all playlists.";
                    return;
                }
            }

            Log("Finished loading all available playlist pages.");
            LoadMorePlaylistsCommand.RaiseCanExecuteChanged();

            Status = "Ready";
        }

        private bool CanLoadMorePlaylists()
        {
            return _currentPlaylistPage?.Next != null;
        }

        private void StagePlaylists(IList items)
        {
            var playlists = items?.Cast<FullPlaylist>().ToList();

            if (playlists == null || !playlists.Any()) return;

            foreach (var playlist in playlists)
            {
                if (!StagedForDeletion.Contains(playlist))
                    StagedForDeletion.Add(playlist);

                Playlists.Remove(playlist);
            }
        }

        private void UnstagePlaylists(IList items)
        {
            var playlists = items?.Cast<FullPlaylist>().ToList();

            if (playlists == null || !playlists.Any()) return;

            foreach (var playlist in playlists)
            {
                if (!Playlists.Contains(playlist))
                    Playlists.Add(playlist);

                StagedForDeletion.Remove(playlist);
            }
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

        public async Task LoadTracksAsync(FullPlaylist playlist)
        {
            if (playlist == null) return;

            Status = "Loading tracks...";

            await Application.Current.Dispatcher.BeginInvoke((Action) (() => { Tracks.Clear(); }));

            var req = new PlaylistGetItemsRequest { Limit = 100 };
            var page = await _spotify.Api.Playlists.GetItems(playlist.Id, req);

            while (page != null)
            {
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