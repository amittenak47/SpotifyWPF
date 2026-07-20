using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SpotifyAPI.Web;
using SpotifyWPF.Model;

namespace SpotifyWPF.Service
{
    public class PlaylistPagingService : IPlaylistPagingService
    {
        private readonly ISpotify _spotify;
        private readonly IPlaylistLocalStore _localStore;
        private readonly IRequestSpacingService _requestSpacing;

        private Paging<FullPlaylist> _currentPlaylistPage;

        private int _spotifyFetchOffset;

        private int? _lastKnownPlaylistTotal;

        public PlaylistPagingService(ISpotify spotify, IPlaylistLocalStore localStore, IRequestSpacingService requestSpacing)
        {
            _spotify = spotify;
            _localStore = localStore;
            _requestSpacing = requestSpacing;
        }

        public event Action<string, bool> LogMessage;

        public int SpotifyFetchOffset => _spotifyFetchOffset;

        public bool HasReachedSpotifyPlaylistEnd()
        {
            // Total 0 is not a valid "finished" signal — an empty/failed response
            // used to poison LastKnownTotal and permanently block further fetches.
            return _lastKnownPlaylistTotal.HasValue
                && _lastKnownPlaylistTotal.Value > 0
                && _spotifyFetchOffset >= _lastKnownPlaylistTotal.Value;
        }

        public void LoadPaginationState()
        {
            var state = _localStore.LoadPaginationState();

            if (state == null)
            {
                _spotifyFetchOffset = _localStore.GetKnownPlaylistCount();
                return;
            }

            _spotifyFetchOffset = state.SpotifyFetchOffset;
            _lastKnownPlaylistTotal = state.LastKnownTotal;

            var knownCount = _localStore.GetKnownPlaylistCount();
            var stateChanged = false;

            // Heal poisoned state: Total 0/null must not keep a huge offset that
            // skips the real starting page and trips the end-of-list check.
            if (!_lastKnownPlaylistTotal.HasValue || _lastKnownPlaylistTotal.Value <= 0)
            {
                if (_lastKnownPlaylistTotal.HasValue)
                {
                    Log($"Clearing invalid LastKnownTotal ({_lastKnownPlaylistTotal}) from pagination state.");
                    _lastKnownPlaylistTotal = null;
                    stateChanged = true;
                }

                if (_spotifyFetchOffset > knownCount)
                {
                    Log($"Resetting Spotify fetch offset from {_spotifyFetchOffset} to {knownCount} because playlist total is unknown.");
                    _spotifyFetchOffset = knownCount;
                    stateChanged = true;
                }
            }
            else if (knownCount > _spotifyFetchOffset)
            {
                _spotifyFetchOffset = knownCount;
                stateChanged = true;
            }

            if (stateChanged)
                SavePaginationState();
        }

        public async Task<int> FetchPageAtOffsetAsync(int offset, int limit, CancellationToken cancellationToken, bool useDefaultRequestFallback = false)
        {
            offset = ResolveSpotifyFetchOffset(offset);

            if (HasReachedSpotifyPlaylistEnd())
            {
                Log($"Spotify offset {offset} is already at or beyond the last known playlist total ({_lastKnownPlaylistTotal}).");
                return 0;
            }

            await _requestSpacing.WaitForSpacingAsync(cancellationToken);

            var request = new PlaylistCurrentUsersRequest { Limit = limit, Offset = offset };
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
            var addedCount = _localStore.AddOrUpdateAvailablePlaylists(_currentPlaylistPage.Items.Select(PlaylistCacheItem.FromPlaylist));

            if (itemsReturned > 0)
                AdvanceSpotifyFetchOffset(offset, itemsReturned, addedCount);

            return addedCount;
        }

        private int ResolveSpotifyFetchOffset(int requestedOffset)
        {
            var knownCount = _localStore.GetKnownPlaylistCount();

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
            var knownCount = _localStore.GetKnownPlaylistCount();

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

        private void SavePaginationState()
        {
            _localStore.SavePaginationState(new PlaylistPaginationState
            {
                SpotifyFetchOffset = _spotifyFetchOffset,
                LastKnownTotal = _lastKnownPlaylistTotal
            });
        }

        private void LogPage(string message, Paging<FullPlaylist> page)
        {
            var itemCount = page?.Items?.Count ?? 0;
            var total = page?.Total?.ToString() ?? "unknown";
            var hasNextPage = page?.Next != null;

            // Only trust a positive total. Total 0 from an empty page at a bad
            // offset must not permanently mark paging as finished.
            if (page?.Total != null && page.Total > 0)
                _lastKnownPlaylistTotal = page.Total;

            SavePaginationState();

            Log($"{message}. Items: {itemCount}. Total: {total}. Has next page: {hasNextPage}.");
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
            LogMessage?.Invoke(message, verbose);
        }
    }
}
