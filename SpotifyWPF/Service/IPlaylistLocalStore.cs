using System;
using System.Collections.Generic;
using SpotifyWPF.Model;

namespace SpotifyWPF.Service
{
    /// <summary>
    /// JSON persistence for the Playlists page under
    /// %LocalAppData%\SpotifyWPF\Playlists\&lt;clientId&gt;\: available playlists,
    /// the deletion queue, and the Spotify paging position.
    /// </summary>
    public interface IPlaylistLocalStore
    {
        /// <summary>Raised for store-level problems: (message, isVerbose).</summary>
        event Action<string, bool> LogMessage;

        Dictionary<string, PlaylistCacheItem> LoadAvailablePlaylists();

        void SaveAvailablePlaylists(Dictionary<string, PlaylistCacheItem> playlists);

        /// <summary>
        /// Merges playlists into the available-playlists file, skipping any that
        /// are staged for deletion. Returns how many were new to the store.
        /// </summary>
        int AddOrUpdateAvailablePlaylists(IEnumerable<PlaylistCacheItem> playlists);

        Dictionary<string, DeletionQueueItem> LoadDeletionQueue();

        void SaveDeletionQueue(Dictionary<string, DeletionQueueItem> playlists);

        /// <summary>Total playlists tracked locally (available + staged for deletion).</summary>
        int GetKnownPlaylistCount();

        /// <summary>Returns null when no pagination state has been persisted (or it cannot be read).</summary>
        PlaylistPaginationState LoadPaginationState();

        void SavePaginationState(PlaylistPaginationState state);
    }
}
