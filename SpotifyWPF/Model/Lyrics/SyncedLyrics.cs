using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SpotifyWPF.Model.Lyrics
{
    /// <summary>One timed lyric line (LRC-style).</summary>
    public class LyricLine
    {
        [JsonPropertyName("startMs")]
        public long StartMs { get; set; }

        [JsonPropertyName("text")]
        public string Text { get; set; }

        /// <summary>Beat index covering <see cref="StartMs"/> once mapped against analysis; -1 if unknown.</summary>
        [JsonPropertyName("beatIndex")]
        public int BeatIndex { get; set; } = -1;
    }

    /// <summary>Cached synced lyrics for a Spotify track (LRCLIB or other source).</summary>
    public class SyncedLyrics
    {
        [JsonPropertyName("trackId")]
        public string TrackId { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; }

        [JsonPropertyName("artist")]
        public string Artist { get; set; }

        [JsonPropertyName("album")]
        public string Album { get; set; }

        [JsonPropertyName("durationSec")]
        public int DurationSec { get; set; }

        [JsonPropertyName("instrumental")]
        public bool Instrumental { get; set; }

        [JsonPropertyName("source")]
        public string Source { get; set; } = "lrclib";

        [JsonPropertyName("plainLyrics")]
        public string PlainLyrics { get; set; }

        [JsonPropertyName("lines")]
        public List<LyricLine> Lines { get; set; } = new List<LyricLine>();

        [JsonIgnore]
        public bool HasSyncedLines => Lines != null && Lines.Count > 0;
    }
}
