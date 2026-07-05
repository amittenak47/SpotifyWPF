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
using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Command;
using SpotifyAPI.Web;
using SpotifyWPF.Service;

namespace SpotifyWPF.ViewModel.Page
{
    public class AlbumsPageViewModel : ViewModelBase
    {
        private const int PageSize = 50;
        private readonly ISpotify _spotify;
        private readonly string _albumStoreRootDirectory;
        private CancellationTokenSource _currentActionCancellationTokenSource;
        private string _filterText;
        private Visibility _progressVisibility = Visibility.Hidden;
        private string _status = "Ready";

        public AlbumsPageViewModel(ISpotify spotify)
        {
            _spotify = spotify;
            _albumStoreRootDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpotifyWPF", "Albums");

            LoadAlbumsCommand = new RelayCommand(async () => await LoadAlbumsAsync());
            LoadAllAlbumsCommand = new RelayCommand(async () => await LoadAllAlbumsAsync());
            RefreshSelectedAlbumsCommand = new RelayCommand<IList>(async albums => await RefreshSelectedAlbumsAsync(albums));
            RemoveSelectedAlbumsCommand = new RelayCommand<IList>(async albums => await RemoveSelectedAlbumsAsync(albums));
            CancelCurrentActionCommand = new RelayCommand(CancelCurrentAction, () => _currentActionCancellationTokenSource?.IsCancellationRequested == false);

            RefreshGridFromLocalFile();
        }

        public ObservableCollection<AlbumCacheItem> Albums { get; } = new ObservableCollection<AlbumCacheItem>();

        public RelayCommand LoadAlbumsCommand { get; }

        public RelayCommand LoadAllAlbumsCommand { get; }

        public RelayCommand<IList> RefreshSelectedAlbumsCommand { get; }

        public RelayCommand<IList> RemoveSelectedAlbumsCommand { get; }

        public RelayCommand CancelCurrentActionCommand { get; }

        public string FilterText
        {
            get => _filterText;
            set
            {
                if (Set(ref _filterText, value))
                    RefreshGridFromLocalFile();
            }
        }

        public string Status
        {
            get => _status;
            set
            {
                _status = value;
                RaisePropertyChanged();
                ProgressVisibility = value == "Ready" || value.StartsWith("Cancelled") || value.StartsWith("Failed") || value.StartsWith("Rate limited")
                    ? Visibility.Hidden
                    : Visibility.Visible;
            }
        }

        public Visibility ProgressVisibility
        {
            get => _progressVisibility;
            set => Set(ref _progressVisibility, value);
        }

        private async Task LoadAlbumsAsync()
        {
            await LoadAlbumPageAsync(GetKnownAlbumCount());
        }

        private async Task LoadAllAlbumsAsync()
        {
            var cancellationToken = BeginCancelableAction();

            try
            {
                var offset = GetKnownAlbumCount();

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var loaded = await LoadAlbumPageInternalAsync(offset, cancellationToken);

                    if (loaded < PageSize) break;
                    offset += loaded;

                    await Task.Delay(150, cancellationToken);
                }

                Status = "Ready";
            }
            catch (APITooManyRequestsException ex)
            {
                Status = $"Rate limited. Retry after {GetRetryDelay(ex)}.";
            }
            catch (OperationCanceledException)
            {
                Status = "Cancelled album loading.";
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                Status = "Failed to load albums.";
            }
            finally
            {
                EndCancelableAction();
            }
        }

        private async Task LoadAlbumPageAsync(int offset)
        {
            var cancellationToken = BeginCancelableAction();

            try
            {
                await LoadAlbumPageInternalAsync(offset, cancellationToken);
                Status = "Ready";
            }
            catch (APITooManyRequestsException ex)
            {
                Status = $"Rate limited. Retry after {GetRetryDelay(ex)}.";
            }
            catch (OperationCanceledException)
            {
                Status = "Cancelled album loading.";
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                Status = "Failed to load albums.";
            }
            finally
            {
                EndCancelableAction();
            }
        }

        private async Task<int> LoadAlbumPageInternalAsync(int offset, CancellationToken cancellationToken)
        {
            Status = $"Loading albums at offset {offset}...";
            var request = new LibraryAlbumsRequest { Limit = PageSize, Offset = offset };
            var page = await _spotify.Api.Library.GetAlbums(request);
            cancellationToken.ThrowIfCancellationRequested();

            var albums = page.Items.Select(ToAlbumCacheItem).Where(album => !string.IsNullOrWhiteSpace(album.Id)).ToList();
            SaveAlbums(albums);
            RefreshGridFromLocalFile();

            return albums.Count;
        }

        private async Task RefreshSelectedAlbumsAsync(IList selectedItems)
        {
            var selectedAlbums = selectedItems?.Cast<AlbumCacheItem>().ToList();
            if (selectedAlbums == null || !selectedAlbums.Any()) return;

            var albums = LoadAlbumDictionary();

            foreach (var selectedAlbum in selectedAlbums)
            {
                try
                {
                    var saved = await _spotify.Api.Library.CheckAlbums(new LibraryCheckAlbumsRequest(new List<string> { selectedAlbum.Id }));
                    if (saved.FirstOrDefault())
                    {
                        albums[selectedAlbum.Id] = selectedAlbum;
                    }
                    else
                    {
                        albums.Remove(selectedAlbum.Id);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                }
            }

            SaveAlbumDictionary(albums);
            RefreshGridFromLocalFile();
        }

        private async Task RemoveSelectedAlbumsAsync(IList selectedItems)
        {
            var selectedAlbums = selectedItems?.Cast<AlbumCacheItem>().ToList();
            if (selectedAlbums == null || !selectedAlbums.Any()) return;

            try
            {
                Status = $"Removing {selectedAlbums.Count} album(s)...";

                foreach (var batch in Batch(selectedAlbums.Select(album => album.Id).Where(id => !string.IsNullOrWhiteSpace(id)), 50))
                {
                    await _spotify.Api.Library.RemoveAlbums(new LibraryRemoveAlbumsRequest(batch));
                    await Task.Delay(150);
                }

                var albums = LoadAlbumDictionary();
                foreach (var album in selectedAlbums)
                    albums.Remove(album.Id);

                SaveAlbumDictionary(albums);
                RefreshGridFromLocalFile();
                Status = "Ready";
            }
            catch (APITooManyRequestsException ex)
            {
                Status = $"Rate limited. Retry after {GetRetryDelay(ex)}.";
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                Status = "Failed to remove albums.";
            }
        }

        private void RefreshGridFromLocalFile()
        {
            var filter = FilterText?.Trim();
            var albums = LoadAlbumDictionary().Values
                .Where(album => string.IsNullOrWhiteSpace(filter) ||
                                (album.Name?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
                                (album.Artists?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0)
                .OrderBy(album => album.Name)
                .ThenBy(album => album.Artists)
                .ToList();

            Application.Current.Dispatcher.BeginInvoke((Action)(() =>
            {
                Albums.Clear();
                foreach (var album in albums)
                    Albums.Add(album);
            }));
        }

        private void SaveAlbums(IEnumerable<AlbumCacheItem> albums)
        {
            var dictionary = LoadAlbumDictionary();
            foreach (var album in albums)
                dictionary[album.Id] = album;

            SaveAlbumDictionary(dictionary);
        }

        private Dictionary<string, AlbumCacheItem> LoadAlbumDictionary()
        {
            var path = GetAlbumsPath();
            if (!File.Exists(path)) return new Dictionary<string, AlbumCacheItem>();

            return JsonSerializer.Deserialize<Dictionary<string, AlbumCacheItem>>(File.ReadAllText(path)) ?? new Dictionary<string, AlbumCacheItem>();
        }

        private void SaveAlbumDictionary(Dictionary<string, AlbumCacheItem> albums)
        {
            Directory.CreateDirectory(GetAlbumStoreDirectory());
            File.WriteAllText(GetAlbumsPath(), JsonSerializer.Serialize(albums, new JsonSerializerOptions { WriteIndented = true }));
        }

        private int GetKnownAlbumCount()
        {
            return LoadAlbumDictionary().Count;
        }

        private string GetAlbumStoreDirectory()
        {
            var clientId = Properties.Settings.Default.SpotifyClientId ?? "default";
            var safeClientId = new string(clientId.Where(char.IsLetterOrDigit).ToArray());
            return Path.Combine(_albumStoreRootDirectory, string.IsNullOrWhiteSpace(safeClientId) ? "default" : safeClientId);
        }

        private string GetAlbumsPath()
        {
            return Path.Combine(GetAlbumStoreDirectory(), "saved-albums.json");
        }

        private CancellationToken BeginCancelableAction()
        {
            _currentActionCancellationTokenSource?.Dispose();
            _currentActionCancellationTokenSource = new CancellationTokenSource();
            CancelCurrentActionCommand.RaiseCanExecuteChanged();
            return _currentActionCancellationTokenSource.Token;
        }

        private void EndCancelableAction()
        {
            _currentActionCancellationTokenSource?.Dispose();
            _currentActionCancellationTokenSource = null;
            CancelCurrentActionCommand.RaiseCanExecuteChanged();
        }

        private void CancelCurrentAction()
        {
            _currentActionCancellationTokenSource?.Cancel();
            Status = "Cancelling...";
        }

        private static TimeSpan GetRetryDelay(APITooManyRequestsException ex)
        {
            object retryAfter = ex.RetryAfter;
            return retryAfter is TimeSpan timeSpan ? timeSpan : TimeSpan.FromSeconds(1);
        }

        private static IEnumerable<List<T>> Batch<T>(IEnumerable<T> source, int batchSize)
        {
            var batch = new List<T>();

            foreach (var item in source)
            {
                batch.Add(item);

                if (batch.Count < batchSize) continue;

                yield return batch;
                batch = new List<T>();
            }

            if (batch.Any())
                yield return batch;
        }

        private static AlbumCacheItem ToAlbumCacheItem(dynamic savedAlbum)
        {
            var album = savedAlbum.Album;
            return new AlbumCacheItem
            {
                Id = album.Id,
                Name = album.Name,
                Artists = string.Join(", ", ((IEnumerable<dynamic>)album.Artists).Select(artist => (string)artist.Name)),
                ReleaseDate = album.ReleaseDate,
                TotalTracks = album.TotalTracks,
                AddedAt = savedAlbum.AddedAt,
                SnapshotUpdatedAtUtc = DateTime.UtcNow
            };
        }

        public class AlbumCacheItem
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Artists { get; set; }
            public string ReleaseDate { get; set; }
            public int? TotalTracks { get; set; }
            public DateTime? AddedAt { get; set; }
            public DateTime SnapshotUpdatedAtUtc { get; set; }
        }
    }
}
