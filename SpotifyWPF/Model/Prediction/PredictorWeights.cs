using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SpotifyWPF.Model.Prediction
{
    /// <summary>
    /// Interpretable scoring weights for next-track prediction (persisted to
    /// predictor-weights.json and adjustable via UI sliders):
    /// score = w1*transitionProb + w2*repeatAffinity + w3*sameArtist
    ///       + w4*tempoSimilarity - w5*recencyPenalty + w6*userPinned
    /// </summary>
    public class PredictorWeights
    {
        [JsonPropertyName("transition")]
        public double Transition { get; set; } = 1.0;

        [JsonPropertyName("repeatAffinity")]
        public double RepeatAffinity { get; set; } = 0.6;

        [JsonPropertyName("sameArtist")]
        public double SameArtist { get; set; } = 0.3;

        [JsonPropertyName("tempoSimilarity")]
        public double TempoSimilarity { get; set; } = 0.2;

        [JsonPropertyName("recencyPenalty")]
        public double RecencyPenalty { get; set; } = 0.4;

        [JsonPropertyName("pinned")]
        public double Pinned { get; set; } = 1.0;

        /// <summary>Tracks the user pinned so they always surface near the top.</summary>
        [JsonPropertyName("pinnedTrackIds")]
        public List<string> PinnedTrackIds { get; set; } = new List<string>();
    }
}
