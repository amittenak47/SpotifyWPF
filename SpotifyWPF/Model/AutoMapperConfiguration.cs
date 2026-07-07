using System.Collections.Generic;
using System.Linq;
using AutoMapper;
using SpotifyAPI.Web;

namespace SpotifyWPF.Model
{
    // ReSharper disable once ClassNeverInstantiated.Global
    public class AutoMapperConfiguration
    {
        public static MapperConfiguration Configure()
        {
            return new MapperConfiguration(cfg => { });
        }

        public static Track MapPlaylistItem(PlaylistTrack<IPlayableItem> item, int position)
        {
            if (item?.Track == null)
            {
                return new Track
                {
                    Position = position,
                    TrackName = "(unavailable)",
                    Artists = string.Empty,
                    Album = string.Empty,
                    StatusNote = "Removed or restricted on Spotify"
                };
            }

            switch (item.Track)
            {
                case FullTrack fullTrack:
                    return new Track
                    {
                        Position = position,
                        TrackName = fullTrack.Name,
                        Artists = FormatArtists(fullTrack.Artists),
                        Album = fullTrack.Album?.Name,
                        DiscNumber = fullTrack.DiscNumber,
                        TrackNumber = fullTrack.TrackNumber,
                        DurationMs = fullTrack.DurationMs,
                        Duration = FormatDuration(fullTrack.DurationMs),
                        SpotifyId = fullTrack.Id,
                        ItemType = "Track"
                    };
                case FullEpisode episode:
                    return new Track
                    {
                        Position = position,
                        TrackName = episode.Name,
                        Artists = episode.Show?.Publisher ?? episode.Show?.Name ?? string.Empty,
                        Album = episode.Show?.Name,
                        DurationMs = episode.DurationMs,
                        Duration = FormatDuration(episode.DurationMs),
                        SpotifyId = episode.Id,
                        ItemType = "Episode"
                    };
                default:
                    return new Track
                    {
                        Position = position,
                        TrackName = item.Track.ToString(),
                        ItemType = item.Track.Type.ToString(),
                        StatusNote = "Unsupported playlist item type"
                    };
            }
        }

        private static string FormatArtists(IEnumerable<SimpleArtist> artists)
        {
            return string.Join(", ", (artists ?? new List<SimpleArtist>()).Select(artist => artist.Name));
        }

        private static string FormatDuration(int? durationMs)
        {
            if (!durationMs.HasValue || durationMs.Value < 0)
                return string.Empty;

            var duration = System.TimeSpan.FromMilliseconds(durationMs.Value);

            return duration.Hours > 0
                ? duration.ToString(@"h\:mm\:ss")
                : duration.ToString(@"m\:ss");
        }
    }
}
