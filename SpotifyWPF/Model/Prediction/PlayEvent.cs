using System;
using System.Text.Json.Serialization;

namespace SpotifyWPF.Model.Prediction
{
    /// <summary>
    /// One append-only entry in listening-log.jsonl describing a single play of a track.
    /// This is metadata logging only (no audio) and feeds next-track prediction.
    /// </summary>
    public class PlayEvent
    {
        [JsonPropertyName("trackId")]
        public string TrackId { get; set; }

        [JsonPropertyName("trackName")]
        public string TrackName { get; set; }

        [JsonPropertyName("artistName")]
        public string ArtistName { get; set; }

        [JsonPropertyName("startedAt")]
        public DateTime StartedAt { get; set; }

        [JsonPropertyName("endedAt")]
        public DateTime EndedAt { get; set; }

        [JsonPropertyName("maxPositionMs")]
        public long MaxPositionMs { get; set; }

        [JsonPropertyName("durationMs")]
        public long DurationMs { get; set; }

        /// <summary>Track reached its end (or close enough) without user intervention.</summary>
        [JsonPropertyName("endedNaturally")]
        public bool EndedNaturally { get; set; }

        /// <summary>User moved on before the track finished.</summary>
        [JsonPropertyName("userSkipped")]
        public bool UserSkipped { get; set; }

        /// <summary>Where the play originated, e.g. "prediction-page", "loop-lab".</summary>
        [JsonPropertyName("source")]
        public string Source { get; set; }

        /// <summary>True while a loop mode (simple or jukebox) was active for this play.</summary>
        [JsonPropertyName("loopActive")]
        public bool LoopActive { get; set; }
    }
}
