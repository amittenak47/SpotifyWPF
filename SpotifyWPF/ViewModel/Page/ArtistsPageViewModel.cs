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
    public class ArtistsPageViewModel : ViewModelBase
    {
        private const int PageSize = 50;
        private readonly ISpotify _spotify;
        private readonly string _artistStoreRootDirectory;
        private CancellationTokenSource _currentActionCancellationTokenSource;
        private string _afterArtistId;
        private string _filterText;
        private Visibility _progressVisibility = Visibility.Hidden;
        private string _status = "Ready";

        public ArtistsPageViewModel(ISpotify spotify)
        {
            _spotify = spotify;
            _artistStoreRootDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpotifyWPF", "Artists");

            LoadArtistsCommand = new RelayCommand(async () => await LoadArtistsAsync());
            LoadAllArtistsCommand = new RelayCommand(async () => await LoadAllArtistsAsync());
            RefreshSelectedArtistsCommand = new RelayCommand<IList>(async artists => await RefreshSelectedArtistsAsync(artists));
            UnfollowSelectedArtistsCommand = new RelayCommand<IList>(async artists => await UnfollowSelectedArtistsAsync(artists));
            CancelCurrentActionCommand = new RelayCommand(CancelCurrentAction, () => _currentActionCancellationTokenSource?.IsCancellationRequested == false);

            RefreshGridFromLocalFile();
        }

        public ObservableCollection<ArtistCacheItem> Artists { get; } = new ObservableCollection<ArtistCacheItem>();

        public RelayCommand LoadArtistsCommand { get; }

        public RelayCommand LoadAllArtistsCommand { get; }

        public RelayCommand<IList> RefreshSelectedArtistsCommand { get; }

        public RelayCommand<IList> UnfollowSelectedArtistsCommand { get; }

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

        private async Task LoadArtistsAsync()
        {
            var cancellationToken = BeginCancelableAction();

            try
            {
                await LoadArtistPageInternalAsync(cancellationToken);
                Status = "Ready";
            }
            catch (APITooManyRequestsException ex)
            {
                Status = $"Rate limited. Retry after {GetRetryDelay(ex)}.";
            }
            catch (OperationCanceledException)
            {
                Status = "Cancelled artist loading.";
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                Status = "Failed to load artists.";
            }
            finally
            {
                EndCancelableAction();
            }
        }

        private async Task LoadAllArtistsAsync()
        {
            var cancellationToken = BeginCancelableAction();

            try
            {
                while (true)
                {
                    var loaded = await LoadArtistPageInternalAsync(cancellationToken);
                    if (loaded < PageSize || string.IsNullOrWhiteSpace(_afterArtistId)) break;
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
                Status = "Cancelled artist loading.";
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                Status = "Failed to load artists.";
            }
            finally
            {
                EndCancelableAction();
            }
        }

        private async Task<int> LoadArtistPageInternalAsync(CancellationToken cancellationToken)
        {
            Status = "Loading followed artists...";
            var request = new FollowOfCurrentUserRequest(FollowOfCurrentUserRequest.Type.Artist)
            {
                Limit = PageSize,
                After = _afterArtistId
            };

            var page = await _spotify.Api.Follow.OfCurrentUser(request, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var artists = page.Artists.Items.Select(ToArtistCacheItem).Where(artist => !string.IsNullOrWhiteSpace(artist.Id)).ToList();
            SaveArtists(artists);
            _afterArtistId = artists.LastOrDefault()?.Id;
            RefreshGridFromLocalFile();

            return artists.Count;
        }

        private async Task RefreshSelectedArtistsAsync(IList selectedItems)
        {
            var selectedArtists = selectedItems?.Cast<ArtistCacheItem>().ToList();
            if (selectedArtists == null || !selectedArtists.Any()) return;

            var cancellationToken = BeginCancelableAction();
            var artists = LoadArtistDictionary();

            try
            {
                foreach (var selectedArtist in selectedArtists)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var followed = await _spotify.Api.Follow.CheckCurrentUser(
                        new FollowCheckCurrentUserRequest(FollowCheckCurrentUserRequest.Type.Artist, new List<string> { selectedArtist.Id }),
                        cancellationToken);

                    if (followed.FirstOrDefault())
                    {
                        var refreshedArtist = await _spotify.Api.Artists.Get(selectedArtist.Id, cancellationToken);
                        artists[selectedArtist.Id] = ToArtistCacheItem(refreshedArtist);
                    }
                    else
                    {
                        artists.Remove(selectedArtist.Id);
                    }
                }

                SaveArtistDictionary(artists);
                RefreshGridFromLocalFile();
                Status = "Ready";
            }
            catch (OperationCanceledException)
            {
                Status = "Cancelled artist refresh.";
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                Status = "Failed to refresh artists.";
            }
            finally
            {
                EndCancelableAction();
            }
        }

        private async Task UnfollowSelectedArtistsAsync(IList selectedItems)
        {
            var selectedArtists = selectedItems?.Cast<ArtistCacheItem>().ToList();
            if (selectedArtists == null || !selectedArtists.Any()) return;

            var cancellationToken = BeginCancelableAction();

            try
            {
                Status = $"Unfollowing {selectedArtists.Count} artist(s)...";

                foreach (var batch in Batch(selectedArtists.Select(artist => artist.Id).Where(id => !string.IsNullOrWhiteSpace(id)), 50))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await _spotify.Api.Follow.Unfollow(new UnfollowRequest(UnfollowRequest.Type.Artist, batch), cancellationToken);
                    await Task.Delay(150, cancellationToken);
                }

                var artists = LoadArtistDictionary();
                foreach (var artist in selectedArtists)
                    artists.Remove(artist.Id);

                SaveArtistDictionary(artists);
                RefreshGridFromLocalFile();
                Status = "Ready";
            }
            catch (APITooManyRequestsException ex)
            {
                Status = $"Rate limited. Retry after {GetRetryDelay(ex)}.";
            }
            catch (OperationCanceledException)
            {
                Status = "Cancelled artist unfollow.";
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                Status = "Failed to unfollow artists.";
            }
            finally
            {
                EndCancelableAction();
            }
        }

        private void RefreshGridFromLocalFile()
        {
            var filter = FilterText?.Trim();
            var artists = LoadArtistDictionary().Values
                .Where(artist => string.IsNullOrWhiteSpace(filter) ||
                                 (artist.Name?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
                                 (artist.Genres?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0)
                .OrderBy(artist => artist.Name)
                .ToList();

            Application.Current.Dispatcher.BeginInvoke((Action)(() =>
            {
                Artists.Clear();
                foreach (var artist in artists)
                    Artists.Add(artist);
            }));
        }

        private void SaveArtists(IEnumerable<ArtistCacheItem> artists)
        {
            var dictionary = LoadArtistDictionary();
            foreach (var artist in artists)
                dictionary[artist.Id] = artist;
            SaveArtistDictionary(dictionary);
        }

        private Dictionary<string, ArtistCacheItem> LoadArtistDictionary()
        {
            var path = GetArtistsPath();
            if (!File.Exists(path)) return new Dictionary<string, ArtistCacheItem>();
            return JsonSerializer.Deserialize<Dictionary<string, ArtistCacheItem>>(File.ReadAllText(path)) ?? new Dictionary<string, ArtistCacheItem>();
        }

        private void SaveArtistDictionary(Dictionary<string, ArtistCacheItem> artists)
        {
            Directory.CreateDirectory(GetArtistStoreDirectory());
            File.WriteAllText(GetArtistsPath(), JsonSerializer.Serialize(artists, new JsonSerializerOptions { WriteIndented = true }));
        }

        private string GetArtistStoreDirectory()
        {
            var clientId = Properties.Settings.Default.SpotifyClientId ?? "default";
            var safeClientId = new string(clientId.Where(char.IsLetterOrDigit).ToArray());
            return Path.Combine(_artistStoreRootDirectory, string.IsNullOrWhiteSpace(safeClientId) ? "default" : safeClientId);
        }

        private string GetArtistsPath()
        {
            return Path.Combine(GetArtistStoreDirectory(), "followed-artists.json");
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

        private static ArtistCacheItem ToArtistCacheItem(FullArtist artist)
        {
            return new ArtistCacheItem
            {
                Id = artist.Id,
                Name = artist.Name,
                FollowersTotal = artist.Followers?.Total,
                Popularity = artist.Popularity,
                Genres = artist.Genres == null ? string.Empty : string.Join(", ", artist.Genres),
                SnapshotUpdatedAtUtc = DateTime.UtcNow
            };
        }

        public class ArtistCacheItem
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public int? FollowersTotal { get; set; }
            public int? Popularity { get; set; }
            public string Genres { get; set; }
            public DateTime SnapshotUpdatedAtUtc { get; set; }
        }
    }
}
