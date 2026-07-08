using System;
using System.Threading;
using System.Threading.Tasks;

namespace SpotifyWPF.Service
{
    /// <summary>
    /// Fetches the current user's playlist pages from Spotify into the local
    /// store and tracks the resumable paging position (offset + last known
    /// total), which is persisted across app restarts.
    /// </summary>
    public interface IPlaylistPagingService
    {
        /// <summary>Raised for fetch progress and diagnostics: (message, isVerbose).</summary>
        event Action<string, bool> LogMessage;

        /// <summary>The next Spotify offset to fetch.</summary>
        int SpotifyFetchOffset { get; }

        /// <summary>True once the offset reaches the last total Spotify reported.</summary>
        bool HasReachedSpotifyPlaylistEnd();

        /// <summary>
        /// Restores the persisted paging position, falling back to the number
        /// of locally tracked playlists.
        /// </summary>
        void LoadPaginationState();

        /// <summary>
        /// Fetches one playlist page and merges it into the local store.
        /// Returns how many playlists were new. The offset is advanced (and
        /// persisted) past pages that contained nothing new.
        /// </summary>
        Task<int> FetchPageAtOffsetAsync(int offset, int limit, CancellationToken cancellationToken, bool useDefaultRequestFallback = false);
    }
}
