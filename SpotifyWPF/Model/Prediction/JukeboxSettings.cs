using System.Text.Json.Serialization;

namespace SpotifyWPF.Model.Prediction
{
    /// <summary>
    /// User-tunable Infinite Jukebox parameters (mirrors the original Tune dialog sliders).
    /// </summary>
    public class JukeboxSettings
    {
        /// <summary>Auto-tune stops raising the branch similarity threshold above this value.</summary>
        [JsonPropertyName("branchSimilarityThresholdMax")]
        public double BranchSimilarityThresholdMax { get; set; } = 55;

        /// <summary>Minimum random branch chance (0–1). Original Tune dialog low end of range.</summary>
        [JsonPropertyName("branchProbabilityMin")]
        public double BranchProbabilityMin { get; set; } = 0.05;

        /// <summary>Maximum random branch chance (0–1). Original Tune dialog high end of range.</summary>
        [JsonPropertyName("branchProbabilityMax")]
        public double BranchProbabilityMax { get; set; } = 0.25;

        /// <summary>Added branch chance per linear beat until a jump is taken (0–1).</summary>
        [JsonPropertyName("branchProbabilityRampPerBeat")]
        public double BranchProbabilityRampPerBeat { get; set; } = 0.01;

        /// <summary>Fire seeks this many ms before the beat boundary to compensate for SDK latency.</summary>
        [JsonPropertyName("seekLeadMs")]
        public int SeekLeadMs { get; set; } = 100;

        [JsonPropertyName("allowOnlyReverseBranches")]
        public bool AllowOnlyReverseBranches { get; set; }

        [JsonPropertyName("allowOnlyLongBranches")]
        public bool AllowOnlyLongBranches { get; set; }

        /// <summary>Minimum beat distance when <see cref="AllowOnlyLongBranches"/> is enabled.</summary>
        [JsonPropertyName("longBranchMinBeats")]
        public int LongBranchMinBeats { get; set; } = 16;

        public static JukeboxSettings CreateDefaults() => new JukeboxSettings();
    }
}
