using System;
using SpotifyAPI.Web;

namespace SpotifyWPF.Model
{
    /// <summary>
    /// Locally cached snapshot of a Spotify playlist, persisted as JSON under
    /// %LocalAppData%\SpotifyWPF\Playlists\&lt;clientId&gt;\available-playlists.json.
    /// </summary>
    public class PlaylistCacheItem
    {
        public string Id { get; set; }

        public string Name { get; set; }

        public string OwnerDisplayName { get; set; }

        public string OwnerId { get; set; }

        public int? TracksTotal { get; set; }

        public DateTime SnapshotUpdatedAtUtc { get; set; }

        public static PlaylistCacheItem FromPlaylist(FullPlaylist playlist)
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
    }
}
