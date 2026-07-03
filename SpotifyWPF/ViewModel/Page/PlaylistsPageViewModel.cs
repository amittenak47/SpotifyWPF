using System;
using System.Collections;
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

        private Visibility _progressVisibility = Visibility.Hidden;

        private string _status = "Ready";

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
        }

        public ObservableCollection<FullPlaylist> Playlists { get; } = new ObservableCollection<FullPlaylist>();

        public ObservableCollection<FullPlaylist> StagedForDeletion { get; } = new ObservableCollection<FullPlaylist>();

        public ObservableCollection<Track> Tracks { get; } = new ObservableCollection<Track>();

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
            if (Playlists.Count > 0) return;

            Status = "Loading playlists...";

            var request = new PlaylistCurrentUsersRequest { Limit = 50 };
            _currentPlaylistPage = await _spotify.Api.Playlists.CurrentUsers(request);

            await Application.Current.Dispatcher.BeginInvoke((Action) (() =>
            {
                Playlists.Clear();
                StagedForDeletion.Clear();

                foreach (var playlist in _currentPlaylistPage.Items)
                    Playlists.Add(playlist);
            }));

            LoadMorePlaylistsCommand.RaiseCanExecuteChanged();

            Status = "Ready";
        }

        public async Task LoadMorePlaylistsAsync()
        {
            if (_currentPlaylistPage == null)
            {
                await LoadPlaylistsAsync();
                return;
            }

            if (_currentPlaylistPage.Next == null) return;

            Status = "Loading more playlists...";

            _currentPlaylistPage = await _spotify.Api.NextPage(_currentPlaylistPage);

            await Application.Current.Dispatcher.BeginInvoke((Action) (() =>
            {
                foreach (var playlist in _currentPlaylistPage.Items)
                    Playlists.Add(playlist);
            }));

            LoadMorePlaylistsCommand.RaiseCanExecuteChanged();

            Status = "Ready";
        }

        public async Task LoadAllPlaylistsAsync()
        {
            if (_currentPlaylistPage == null)
                await LoadPlaylistsAsync();

            while (_currentPlaylistPage?.Next != null)
            {
                Status = "Loading all playlists...";

                await Task.Delay(150);

                _currentPlaylistPage = await _spotify.Api.NextPage(_currentPlaylistPage);

                await Application.Current.Dispatcher.BeginInvoke((Action) (() =>
                {
                    foreach (var playlist in _currentPlaylistPage.Items)
                        Playlists.Add(playlist);
                }));
            }

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
    }
}