using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SpotifyWPF.Model.Prediction
{
    /// <summary>
    /// A user-locked jukebox branch: a beat-to-beat jump pinned from the ring UI.
    /// Beat indices refer to the cached analysis for the track, which is stable once stored.
    /// </summary>
    public class BranchLock
    {
        [JsonPropertyName("fromBeatIndex")]
        public int FromBeatIndex { get; set; }

        [JsonPropertyName("toBeatIndex")]
        public int ToBeatIndex { get; set; }
    }

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

        /// <summary>Branches the user locked on the jukebox ring (click-to-lock).</summary>
        [JsonPropertyName("lockedBranches")]
        public List<BranchLock> LockedBranches { get; set; } = new List<BranchLock>();

        /// <summary>When true, jukebox jumps happen only via <see cref="LockedBranches"/>.</summary>
        [JsonPropertyName("locksOnly")]
        public bool LocksOnly { get; set; }

        [JsonIgnore]
        public bool IsValidRegion => LoopEndMs > LoopStartMs && LoopStartMs >= 0;
    }

    public static class LoopModes
    {
        public const string Simple = "Simple";

        public const string Jukebox = "Jukebox";
    }
}
