using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SpotifyWPF.Model.Prediction
{
    /// <summary>
    /// Normalized structural map of a track — the shared schema both analysis paths produce
    /// (Spotify's audio-analysis endpoint, or the local WASAPI + librosa pipeline) and the only
    /// shape the loop engine consumes. Mirrors Spotify's audio-analysis layout: beats are the
    /// jump-graph nodes, segments carry the pitch/timbre similarity features.
    /// </summary>
    public class TrackAnalysis
    {
        [JsonPropertyName("trackId")]
        public string TrackId { get; set; }

        /// <summary>"spotify" (Path A) or "local" (Path B).</summary>
        [JsonPropertyName("sourceType")]
        public string SourceType { get; set; }

        [JsonPropertyName("durationSec")]
        public double DurationSec { get; set; }

        [JsonPropertyName("tempo")]
        public double Tempo { get; set; }

        [JsonPropertyName("key")]
        public int Key { get; set; } = -1;

        [JsonPropertyName("mode")]
        public int Mode { get; set; } = -1;

        [JsonPropertyName("loudness")]
        public double Loudness { get; set; }

        [JsonPropertyName("bars")]
        public List<AnalysisInterval> Bars { get; set; } = new List<AnalysisInterval>();

        [JsonPropertyName("beats")]
        public List<AnalysisInterval> Beats { get; set; } = new List<AnalysisInterval>();

        [JsonPropertyName("tatums")]
        public List<AnalysisInterval> Tatums { get; set; } = new List<AnalysisInterval>();

        [JsonPropertyName("sections")]
        public List<AnalysisSection> Sections { get; set; } = new List<AnalysisSection>();

        [JsonPropertyName("segments")]
        public List<AnalysisSegment> Segments { get; set; } = new List<AnalysisSegment>();
    }

    public class AnalysisInterval
    {
        [JsonPropertyName("start")]
        public double Start { get; set; }

        [JsonPropertyName("duration")]
        public double Duration { get; set; }

        [JsonPropertyName("confidence")]
        public double Confidence { get; set; }
    }

    public class AnalysisSection : AnalysisInterval
    {
        [JsonPropertyName("tempo")]
        public double Tempo { get; set; }

        [JsonPropertyName("key")]
        public int Key { get; set; } = -1;

        [JsonPropertyName("mode")]
        public int Mode { get; set; } = -1;

        [JsonPropertyName("loudness")]
        public double Loudness { get; set; }
    }

    public class AnalysisSegment : AnalysisInterval
    {
        [JsonPropertyName("loudnessStart")]
        public double LoudnessStart { get; set; }

        [JsonPropertyName("loudnessMax")]
        public double LoudnessMax { get; set; }

        [JsonPropertyName("loudnessMaxTime")]
        public double LoudnessMaxTime { get; set; }

        /// <summary>12-bin chroma vector, each 0..1.</summary>
        [JsonPropertyName("pitches")]
        public List<double> Pitches { get; set; } = new List<double>();

        /// <summary>12-dimension timbre vector (Spotify PCA coefficients or MFCCs locally).</summary>
        [JsonPropertyName("timbre")]
        public List<double> Timbre { get; set; } = new List<double>();
    }
}
