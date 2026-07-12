using System;
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

        /// <summary>
        /// When true (default), the graph gets a guaranteed end-loop edge and the navigator forces
        /// a backward jump at the last branch point so playback never runs off the end.
        /// When false, the song can play out linearly.
        /// </summary>
        [JsonPropertyName("enableEndLoop")]
        public bool EnableEndLoop { get; set; } = true;

        /// <summary>Spotify Web Playback SDK vs local cached WAV (Slice 1B). Values: "Spotify" | "Local".</summary>
        [JsonPropertyName("playbackSource")]
        public string PlaybackSource { get; set; } = "Spotify";

        /// <summary>
        /// Theiler window: minimum beat separation for Classic graph edges (Slice 2). Default 8.
        /// </summary>
        [JsonPropertyName("minimumJumpBeats")]
        public int MinimumJumpBeats { get; set; } = 8;

        /// <summary>Max outgoing Classic kNN neighbors per beat.</summary>
        [JsonPropertyName("classicMaxNeighbors")]
        public int ClassicMaxNeighbors { get; set; } = 6;

        /// <summary>
        /// Phase penalty mode for Classic distance: "off", "soft" (default), or "hard".
        /// </summary>
        [JsonPropertyName("phasePenaltyMode")]
        public string PhasePenaltyMode { get; set; } = "soft";

        public static JukeboxSettings CreateDefaults() => new JukeboxSettings();

        /// <summary>True when a settings change requires rebuilding the beat graph (not just re-arming).</summary>
        public static bool AffectsGraphTopology(JukeboxSettings before, JukeboxSettings after)
        {
            if (before == null || after == null)
                return true;

            return Math.Abs(before.BranchSimilarityThresholdMax - after.BranchSimilarityThresholdMax) > 0.01 ||
                   before.EnableEndLoop != after.EnableEndLoop ||
                   before.MinimumJumpBeats != after.MinimumJumpBeats ||
                   before.ClassicMaxNeighbors != after.ClassicMaxNeighbors ||
                   !string.Equals(before.PhasePenaltyMode ?? "", after.PhasePenaltyMode ?? "",
                       StringComparison.OrdinalIgnoreCase);
        }
    }
}
