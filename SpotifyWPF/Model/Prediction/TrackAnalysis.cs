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

        /// <summary>
        /// Slice 5 optional: per-beat region embeddings (e.g. musicnn/effnet pooled to beat grid).
        /// Same length as Beats when present. Used only as a region gate over Classic candidates.
        /// </summary>
        [JsonPropertyName("regionEmbeddings")]
        public List<List<double>> RegionEmbeddings { get; set; }

        /// <summary>
        /// Optional beat-aligned, NON-z-scored energy detail (tools/analyze_track.py). Absent on
        /// Spotify analyses and on caches written before this block existed — consumers must fall
        /// back to the tier-1 derivation rather than requiring a re-analyze.
        /// </summary>
        [JsonPropertyName("beatEnergy")]
        public BeatEnergyBlock BeatEnergy { get; set; }

        /// <summary>True when Slice 2 Classic vectors are present for graph assembly.</summary>
        [JsonIgnore]
        public bool HasClassicFeatures =>
            StackedFeatures != null && StackedFeatures.Count > 0 &&
            Beats != null && StackedFeatures.Count == Beats.Count;

        [JsonIgnore]
        public bool HasRegionEmbeddings =>
            RegionEmbeddings != null && Beats != null &&
            RegionEmbeddings.Count == Beats.Count && RegionEmbeddings.Count > 0;

        /// <summary>True when tier-2 band-resolved energy detail is present and beat-aligned.</summary>
        [JsonIgnore]
        public bool HasBeatEnergy =>
            BeatEnergy != null && Beats != null && Beats.Count > 0 &&
            BeatEnergy.LowDb != null && BeatEnergy.LowDb.Count == Beats.Count;
    }

    /// <summary>
    /// Beat-synchronous, interpretable (non-z-scored) energy features. Stored columnar rather than
    /// as an array of objects: roughly 40% smaller JSON and cheaper to deserialize.
    ///
    /// These never enter <see cref="TrackAnalysis.BeatFeatures"/> or
    /// <see cref="TrackAnalysis.StackedFeatures"/> — doing so would change the similarity metric,
    /// hence the graph topology, hence every preset anyone has tuned.
    /// </summary>
    public class BeatEnergyBlock
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        /// <summary>Kick band ~20–120 Hz, dB.</summary>
        [JsonPropertyName("lowDb")]
        public List<double> LowDb { get; set; }

        /// <summary>Bass body ~120–500 Hz, dB.</summary>
        [JsonPropertyName("lowMidDb")]
        public List<double> LowMidDb { get; set; }

        /// <summary>Vocal / lead ~500 Hz–6 kHz, dB.</summary>
        [JsonPropertyName("midDb")]
        public List<double> MidDb { get; set; }

        /// <summary>Hats / air ~6–12 kHz, dB.</summary>
        [JsonPropertyName("highDb")]
        public List<double> HighDb { get; set; }

        /// <summary>HPSS percussive share, 0–1. Separates "loud pad" from "loud with drums".</summary>
        [JsonPropertyName("percussiveFraction")]
        public List<double> PercussiveFraction { get; set; }

        [JsonPropertyName("onsetStrength")]
        public List<double> OnsetStrength { get; set; }

        [JsonPropertyName("centroidHz")]
        public List<double> CentroidHz { get; set; }

        [JsonPropertyName("rolloffHz")]
        public List<double> RolloffHz { get; set; }

        /// <summary>dB reference the band values were taken against, so C# can reproduce the scale.</summary>
        [JsonPropertyName("refDb")]
        public double RefDb { get; set; }

        [JsonPropertyName("loDb")]
        public double LoDb { get; set; }

        [JsonPropertyName("hiDb")]
        public double HiDb { get; set; }
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
