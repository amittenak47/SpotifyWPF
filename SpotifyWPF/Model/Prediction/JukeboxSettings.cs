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
        public int ClassicMaxNeighbors { get; set; } = 12;

        /// <summary>
        /// Phase penalty mode for Classic distance: "off", "soft" (default), or "hard".
        /// </summary>
        [JsonPropertyName("phasePenaltyMode")]
        public string PhasePenaltyMode { get; set; } = "soft";

        /// <summary>
        /// Require hop landings to share the same position within a hypermeasure of this many beats
        /// (0 = off). HARD navigation filter: keeps only edges with (fromIndex+1) % N == toIndex % N
        /// (continuation phase — matches Enhanced stack[i+1]≈stack[j] scoring).
        /// Independent of <see cref="PhasePenaltyMode"/> (graph-build IndexInBar soft/hard).
        /// On floating grooves (e.g. Dreams) or gap-split beat grids, N=4/8/16 can wipe almost all
        /// candidates — unrelated to lyric Softmax. See docs/lyric-flow.md.
        /// </summary>
        [JsonPropertyName("phraseAlignBeats")]
        public int PhraseAlignBeats { get; set; } = 0;

        /// <summary>
        /// Beat tracker for Path B analyze: "auto" | "beatthis" | "dp".
        /// </summary>
        [JsonPropertyName("beatTrackerMode")]
        public string BeatTrackerMode { get; set; } = "auto";

        /// <summary>
        /// Graph metric: "auto" (Classic if features exist) | "classic" | "legacy".
        /// </summary>
        [JsonPropertyName("graphMetricMode")]
        public string GraphMetricMode { get; set; } = "auto";

        /// <summary>
        /// After a hop, force this many linear beats before another random branch is considered
        /// (locks + end-loop still fire). Default 16 ≈ one phrase of stability.
        /// </summary>
        [JsonPropertyName("minBeatsBetweenJumps")]
        public int MinBeatsBetweenJumps { get; set; } = 16;

        /// <summary>Softmax temperature τ for Slice 4 edge pick. Lower = greedier (nearest). Default 1.</summary>
        [JsonPropertyName("softmaxTemperature")]
        public double SoftmaxTemperature { get; set; } = 1.0;

        /// <summary>Visit-count novelty λ: subtract λ·visits(dest) from softmax score. Default 0.35.</summary>
        [JsonPropertyName("visitNoveltyLambda")]
        public double VisitNoveltyLambda { get; set; } = 0.35;

        /// <summary>Beats within this radius of a recent landing count as the same pocket (tabu / visit bucket).</summary>
        [JsonPropertyName("visitRegionRadiusBeats")]
        public int VisitRegionRadiusBeats { get; set; } = 16;

        /// <summary>Slice 4: keep only mutual kNN edges (i→j and j→i). Safer components; can orphan unique passages.</summary>
        [JsonPropertyName("useMutualKnn")]
        public bool UseMutualKnn { get; set; } = true;

        /// <summary>
        /// Slice 4 optional: after mutual-kNN, bridge weakly disconnected components with cheapest edges
        /// so unique passages are not left edgeless.
        /// </summary>
        [JsonPropertyName("enableSccBridges")]
        public bool EnableSccBridges { get; set; } = true;

        /// <summary>
        /// Slice 6: weight for preference scores when re-ranking candidates (0 = off). Does not add edges.
        /// Small default so scrub/choice history gently steers Softmax once labels exist.
        /// </summary>
        [JsonPropertyName("preferenceWeight")]
        public double PreferenceWeight { get; set; } = 0.2;

        /// <summary>
        /// Slice 6: user scrub within this many ms after a hop lands counts as a skip negative.
        /// </summary>
        [JsonPropertyName("preferenceSkipWindowMs")]
        public int PreferenceSkipWindowMs { get; set; } = 8000;

        /// <summary>
        /// Slice 5: when region embeddings exist, only allow Classic hops whose region distance
        /// (continuation: embeds[i+1] vs embeds[j]) is among the nearest GateRegionNeighborCount
        /// (0 = disable gating).
        /// </summary>
        [JsonPropertyName("essentiaRegionGate")]
        public bool EssentiaRegionGate { get; set; } = true;

        [JsonPropertyName("gateRegionNeighborCount")]
        public int GateRegionNeighborCount { get; set; } = 8;

        /// <summary>
        /// Shared Softmax scale for lyric-flow layers (0 = all lyric steering off).
        /// See docs/lyric-flow.md (AutoMashUpper / Foote / LyricAlly lineage).
        /// </summary>
        [JsonPropertyName("lyricPhraseWeight")]
        public double LyricPhraseWeight { get; set; } = 0.35;

        /// <summary>Layer 1: prefer timed lyric line starts; penalize mid-word landings.</summary>
        [JsonPropertyName("lyricFlowPhraseCuts")]
        public bool LyricFlowPhraseCuts { get; set; } = true;

        /// <summary>Layer 2: prefer hops that stay inside the same analysis section.</summary>
        [JsonPropertyName("lyricFlowSameSection")]
        public bool LyricFlowSameSection { get; set; } = true;

        /// <summary>Layer 3: prefer clean lyric-block exits/landings (whole lines / hooks).</summary>
        [JsonPropertyName("lyricFlowBlockClean")]
        public bool LyricFlowBlockClean { get; set; } = true;

        /// <summary>When true, fetch/display synced lyrics in the Infinite Jukebox stage.</summary>
        [JsonPropertyName("showLyrics")]
        public bool ShowLyrics { get; set; } = true;

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
                   before.UseMutualKnn != after.UseMutualKnn ||
                   before.EnableSccBridges != after.EnableSccBridges ||
                   before.EssentiaRegionGate != after.EssentiaRegionGate ||
                   before.GateRegionNeighborCount != after.GateRegionNeighborCount ||
                   !string.Equals(before.PhasePenaltyMode ?? "", after.PhasePenaltyMode ?? "",
                       StringComparison.OrdinalIgnoreCase) ||
                   !string.Equals(before.GraphMetricMode ?? "", after.GraphMetricMode ?? "",
                       StringComparison.OrdinalIgnoreCase);
        }
    }
}
