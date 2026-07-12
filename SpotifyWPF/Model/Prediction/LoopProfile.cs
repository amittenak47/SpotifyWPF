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

        /// <summary>
        /// Chance this locked branch is taken when its from-beat is reached (0–1, default 1 = always).
        /// </summary>
        [JsonPropertyName("probability")]
        public double Probability { get; set; } = 1.0;
    }

    /// <summary>Named snapshot of tune settings + ring branch locks for a track.</summary>
    public class BranchLockPreset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        /// <summary>When true, random walk runs alongside locks. When false, only locked branches fire.</summary>
        [JsonPropertyName("randomBranches")]
        public bool RandomBranches { get; set; } = true;

        /// <summary>Legacy: inverted of <see cref="RandomBranches"/>. Read on load; not written.</summary>
        [JsonPropertyName("locksOnly")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool LocksOnly
        {
            get => false;
            set
            {
                if (value)
                    RandomBranches = false;
            }
        }

        [JsonPropertyName("lockedBranches")]
        public List<BranchLock> LockedBranches { get; set; } = new List<BranchLock>();

        /// <summary>Optional snapshot of jukebox tune settings at save time.</summary>
        [JsonPropertyName("settings")]
        public JukeboxSettings SettingsSnapshot { get; set; }

        /// <summary>SHA-256 hex of the analysis JSON this preset was authored against.</summary>
        [JsonPropertyName("analysisFingerprint")]
        public string AnalysisFingerprint { get; set; }
    }

    /// <summary>Beat-to-beat target passed from the ring canvas when locking a specific branch chord.</summary>
    public class RingBranchClick
    {
        public int FromBeatIndex { get; set; }

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

        /// <summary>
        /// When true (default), random walk + locks at their probability.
        /// When false, only locked branches fire (+ end-loop guard if enabled).
        /// </summary>
        [JsonPropertyName("randomBranches")]
        public bool RandomBranches { get; set; } = true;

        /// <summary>Legacy: inverted of <see cref="RandomBranches"/>. Read on load; not written.</summary>
        [JsonPropertyName("locksOnly")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool LocksOnly
        {
            get => false;
            set
            {
                if (value)
                    RandomBranches = false;
            }
        }

        /// <summary>Saved tune + branch-lock layouts for this track (load from session dropdown).</summary>
        [JsonPropertyName("lockPresets")]
        public List<BranchLockPreset> LockPresets { get; set; } = new List<BranchLockPreset>();

        [JsonIgnore]
        public bool IsValidRegion => LoopEndMs > LoopStartMs && LoopStartMs >= 0;
    }

    public static class LoopModes
    {
        public const string Simple = "Simple";

        public const string Jukebox = "Jukebox";
    }
}
