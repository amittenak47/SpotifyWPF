using System.Text.Json.Serialization;

namespace SpotifyWPF.Model.Prediction
{
    /// <summary>
    /// Per-track loop settings persisted in loop-regions.json. Simple mode seeks back to
    /// LoopStartMs whenever playback reaches LoopEndMs (e.g. to skip an outro/dialogue).
    /// </summary>
    public class LoopProfile
    {
        [JsonPropertyName("trackId")]
        public string TrackId { get; set; }

        [JsonPropertyName("loopStartMs")]
        public long LoopStartMs { get; set; }

        [JsonPropertyName("loopEndMs")]
        public long LoopEndMs { get; set; }

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        /// <summary>"Simple" (seek LoopEnd → LoopStart) or "Jukebox" (beat-graph jumps).</summary>
        [JsonPropertyName("mode")]
        public string Mode { get; set; } = LoopModes.Simple;

        [JsonIgnore]
        public bool IsValidRegion => LoopEndMs > LoopStartMs && LoopStartMs >= 0;
    }

    public static class LoopModes
    {
        public const string Simple = "Simple";

        public const string Jukebox = "Jukebox";
    }
}
