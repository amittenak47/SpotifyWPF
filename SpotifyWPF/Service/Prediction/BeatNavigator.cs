using System;
using System.Collections.Generic;
using System.Linq;
using SpotifyWPF.Model.Prediction;

namespace SpotifyWPF.Service.Prediction
{
    /// <summary>A planned seek: when playback reaches TriggerMs, seek to SeekToMs.</summary>
    public class JukeboxJump
    {
        public int FromBeatIndex { get; set; }

        public int TargetBeatIndex { get; set; }

        public long TriggerMs { get; set; }

        public long SeekToMs { get; set; }

        public double BranchDistance { get; set; }
    }

    /// <summary>
    /// Runtime half of the Infinite Jukebox port: walks the beat graph deciding, beat by beat,
    /// whether to advance linearly or jump to a similar beat. The branch probability ramps up the
    /// longer playback stays linear (min 18%, +1.8% per beat, capped at 50%) and resets after each
    /// jump — the original's anti-boredom dial. The last branchable beat always jumps backwards so
    /// playback never falls off the end of the song.
    ///
    /// Because playback between jumps is linear, the whole walk is planned ahead: PlanNextJump
    /// returns the single next seek to arm in the player, and is called again after it fires.
    /// </summary>
    public class BeatNavigator
    {
        private readonly Random _random;

        private readonly JukeboxSettings _settings;

        /// <summary>Locked branches as packed (from &lt;&lt; 32 | to) keys for O(1) lookup.</summary>
        private readonly HashSet<long> _lockedBranches = new HashSet<long>();

        private readonly bool _locksOnly;

        private double _currentBranchChance;

        public BeatGraph Graph { get; }

        public double CurrentBranchChance => _currentBranchChance;

        public BeatNavigator(BeatGraph graph, JukeboxSettings settings, LoopProfile profile = null,
            int? randomSeed = null)
        {
            Graph = graph ?? throw new ArgumentNullException(nameof(graph));
            _settings = settings ?? JukeboxSettings.CreateDefaults();
            _currentBranchChance = ClampProbability(_settings.BranchProbabilityMin);
            _random = randomSeed.HasValue ? new Random(randomSeed.Value) : new Random();

            if (profile?.LockedBranches != null)
            {
                foreach (var branchLock in profile.LockedBranches)
                    _lockedBranches.Add(PackBranch(branchLock.FromBeatIndex, branchLock.ToBeatIndex));
            }

            _locksOnly = profile?.LocksOnly == true && _lockedBranches.Count > 0;
        }

        private static long PackBranch(int from, int to) => ((long)from << 32) | (uint)to;

        private bool IsLocked(int from, BeatEdge edge) =>
            _lockedBranches.Contains(PackBranch(from, edge.DestinationIndex));

        private double ClampProbability(double value)
        {
            var min = Math.Min(_settings.BranchProbabilityMin, _settings.BranchProbabilityMax);
            var max = Math.Max(_settings.BranchProbabilityMin, _settings.BranchProbabilityMax);
            return Math.Max(min, Math.Min(max, value));
        }

        /// <summary>True when the graph has at least one usable branch.</summary>
        public bool CanJump => Graph.LastBranchPointIndex >= 0 ||
                               Graph.Beats.Any(b => b.Neighbors.Count > 0);

        /// <summary>Index of the beat containing the given position (clamped to the nearest beat).</summary>
        public int FindBeatIndexAtMs(long positionMs)
        {
            var beats = Graph.Beats;

            if (beats.Count == 0)
                return -1;

            // Beats are sorted by start time; linear scan is fine at this size.
            for (var i = 0; i < beats.Count; i++)
            {
                if (positionMs < beats[i].EndMs)
                    return i;
            }

            return beats.Count - 1;
        }

        /// <summary>
        /// Simulates the walk from the given beat and returns the next jump to arm, or null when the
        /// graph has no branches at all (playback then just runs out linearly).
        /// </summary>
        public JukeboxJump PlanNextJump(int fromBeatIndex)
        {
            var beats = Graph.Beats;

            if (beats.Count == 0)
                return null;

            var start = Math.Max(0, Math.Min(fromBeatIndex, beats.Count - 1));

            for (var i = start; i < beats.Count; i++)
            {
                var beat = beats[i];

                // Locks-only mode: the ring's locked branches are rails — always taken on arrival,
                // and no random branching happens anywhere else.
                if (_locksOnly)
                {
                    var lockedEdges = beat.Neighbors.Where(e => IsLocked(i, e)).ToList();

                    if (lockedEdges.Count > 0)
                        return MakeJump(i, lockedEdges[_random.Next(lockedEdges.Count)]);

                    // Still guard the point of no return so playback never falls off the end.
                    if (i == Graph.LastBranchPointIndex)
                    {
                        var guardEdge = ChooseEdge(i,
                            beat.Neighbors.Where(e => e.DestinationIndex < i).ToList());

                        if (guardEdge != null)
                            return MakeJump(i, guardEdge);
                    }

                    continue;
                }

                // Point of no return: always branch backwards here rather than running off the end.
                if (i == Graph.LastBranchPointIndex)
                {
                    var backwardEdge = ChooseEdge(i, beat.Neighbors.Where(e => e.DestinationIndex < i).ToList());

                    if (backwardEdge != null)
                        return MakeJump(i, backwardEdge);
                }

                if (beat.Neighbors.Count == 0)
                    continue;

                if (_random.NextDouble() < _currentBranchChance)
                {
                    var edge = ChooseEdge(i, beat.Neighbors);

                    if (edge != null)
                    {
                        _currentBranchChance = ClampProbability(_settings.BranchProbabilityMin);
                        return MakeJump(i, edge);
                    }
                }
                else
                {
                    _currentBranchChance = ClampProbability(
                        _currentBranchChance + _settings.BranchProbabilityRampPerBeat);
                }
            }

            return null;
        }

        private BeatEdge ChooseEdge(int fromBeatIndex, IReadOnlyList<BeatEdge> edges)
        {
            if (edges == null || edges.Count == 0)
                return null;

            var filtered = FilterEdges(fromBeatIndex, edges);

            if (filtered.Count == 0)
                return null;

            return filtered[_random.Next(filtered.Count)];
        }

        private List<BeatEdge> FilterEdges(int fromBeatIndex, IReadOnlyList<BeatEdge> edges)
        {
            IEnumerable<BeatEdge> query = edges;

            if (_settings.AllowOnlyReverseBranches)
                query = query.Where(e => e.DestinationIndex < fromBeatIndex);

            if (_settings.AllowOnlyLongBranches)
            {
                var minBeats = Math.Max(_settings.LongBranchMinBeats, 4);
                query = query.Where(e => Math.Abs(e.DestinationIndex - fromBeatIndex) >= minBeats);
            }

            return query.ToList();
        }

        private JukeboxJump MakeJump(int fromIndex, BeatEdge edge)
        {
            var target = Graph.Beats[edge.DestinationIndex];
            var triggerMs = Graph.Beats[fromIndex].EndMs - _settings.SeekLeadMs;

            if (triggerMs < Graph.Beats[fromIndex].StartMs)
                triggerMs = Graph.Beats[fromIndex].StartMs;

            return new JukeboxJump
            {
                FromBeatIndex = fromIndex,
                TargetBeatIndex = edge.DestinationIndex,
                TriggerMs = Math.Max(0, triggerMs),
                SeekToMs = target.StartMs,
                BranchDistance = edge.Distance
            };
        }
    }
}
