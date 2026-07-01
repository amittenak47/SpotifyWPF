using System;
using System.Collections;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using AutoMapper;
using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Command;
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

        private Visibility _progressVisibility = Visibility.Hidden;

        private string _status = "Ready";

        public PlaylistsPageViewModel(ISpotify spotify, IMapper mapper, IMessageBoxService messageBoxService)
        {
            _spotify = spotify;
            _mapper = mapper;
            _messageBoxService = messageBoxService;

            LoadPlaylistsCommand = new RelayCommand(async () => await LoadPlaylistsAsync());
            LoadTracksCommand = new RelayCommand<FullPlaylist>(async playlist => await LoadTracksAsync(playlist));
            DeletePlaylistsCommand = new RelayCommand<IList>(async playlists => await DeletePlaylistsAsync(playlists));
        }

        public ObservableCollection<FullPlaylist> Playlists { get; } = new ObservableCollection<FullPlaylist>();

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

        public RelayCommand<IList> DeletePlaylistsCommand { get; }

        public RelayCommand LoadPlaylistsCommand { get; }

        public async Task DeletePlaylistsAsync(IList items)
        {
            var playlists = items.Cast<FullPlaylist>().ToList();

            if (!playlists.Any()) return;

            var message = playlists.Count() == 1
                ? $"Are you sure you want to delete playlist {playlists.ElementAt(0).Name}?"
                : $"Are you sure you want to delete these {playlists.Count()} playlists?";

            var result = _messageBoxService.ShowMessageBox(
                message,
                "Confirm",
                MessageBoxButton.YesNo,
                MessageBoxIcon.Exclamation
            );

            if (result != MessageBoxResult.Yes) return;

            for (var i = playlists.Count() - 1; i >= 0; i--)
            {
                var playlist = playlists.ElementAt(i);

                Status = "Deleting playlist: " + playlist.Name;

                await _spotify.Api.Follow.UnfollowPlaylist(playlist.Id);

                await Application.Current.Dispatcher.BeginInvoke((Action) (() => { Playlists.Remove(playlist); }));
            }

            Status = "Ready";
        }

        public async Task LoadPlaylistsAsync()
        {
            if (Playlists.Count > 0) return;

            Status = "Loading playlists...";

            await Application.Current.Dispatcher.BeginInvoke((Action) (() => { Playlists.Clear(); }));

            var firstPage = await _spotify.Api.Playlists.CurrentUsers();

            await foreach (var playlist in _spotify.Api.Paginate(firstPage))
            {
                var playlistToAdd = playlist;

                await Application.Current.Dispatcher.BeginInvoke((Action) (() => { Playlists.Add(playlistToAdd); }));
            }

            Status = "Ready";
        }

        public async Task LoadTracksAsync(FullPlaylist playlist)
        {
            Status = "Loading tracks...";

            Tracks.Clear();

            var firstPage = await _spotify.Api.Playlists.GetItems(playlist.Id, new PlaylistGetItemsRequest
            {
                Limit = 100
            });

            await foreach (var item in _spotify.Api.Paginate(firstPage))
            {
                var mappedTrack = _mapper.Map<Track>(item);

                await Application.Current.Dispatcher.BeginInvoke((Action) (() => { Tracks.Add(mappedTrack); }));
            }

            Status = "Ready";
        }
    }
}