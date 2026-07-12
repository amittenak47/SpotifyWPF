using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SpotifyWPF.Model.Prediction
{
    /// <summary>
    /// Normalized structural map of a track — the shared schema both analysis paths produce
    /// (Spotify's audio-analysis endpoint, or the local WASAPI + librosa pipeline) and the only
    /// shape the loop engine consumes. Slice 2 adds Classic beat-synchronous feature vectors.
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

        /// <summary>"beatthis" | "beatthis-onnx" | "librosa-dp".</summary>
        [JsonPropertyName("beatTracker")]
        public string BeatTracker { get; set; }

        [JsonPropertyName("stackSteps")]
        public int StackSteps { get; set; }

        [JsonPropertyName("gapSplitInserts")]
        public int GapSplitInserts { get; set; }

        [JsonPropertyName("dpAgreement")]
        public DpBeatAgreement DpAgreement { get; set; }

        /// <summary>Per-beat Classic feature vectors (z-scored, median-synced; no MFCC-0).</summary>
        [JsonPropertyName("beatFeatures")]
        public List<List<double>> BeatFeatures { get; set; }

        /// <summary>Time-delay stacked beat features (stack_memory, n_steps = StackSteps).</summary>
        [JsonPropertyName("stackedFeatures")]
        public List<List<double>> StackedFeatures { get; set; }

        /// <summary>True when Slice 2 Classic vectors are present for graph assembly.</summary>
        [JsonIgnore]
        public bool HasClassicFeatures =>
            StackedFeatures != null && StackedFeatures.Count > 0 &&
            Beats != null && StackedFeatures.Count == Beats.Count;
    }

    public class DpBeatAgreement
    {
        [JsonPropertyName("fMeasure")]
        public double FMeasure { get; set; }

        [JsonPropertyName("precision")]
        public double Precision { get; set; }

        [JsonPropertyName("recall")]
        public double Recall { get; set; }

        [JsonPropertyName("toleranceMs")]
        public int ToleranceMs { get; set; }

        [JsonPropertyName("dpBeatCount")]
        public int DpBeatCount { get; set; }

        [JsonPropertyName("beatCount")]
        public int BeatCount { get; set; }
    }

    public class AnalysisInterval
    {
        [JsonPropertyName("start")]
        public double Start { get; set; }

        [JsonPropertyName("duration")]
        public double Duration { get; set; }

        [JsonPropertyName("confidence")]
        public double Confidence { get; set; }

        /// <summary>True when BeatThis (or bar grid) marks this beat as a downbeat.</summary>
        [JsonPropertyName("isDownbeat")]
        public bool IsDownbeat { get; set; }
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
