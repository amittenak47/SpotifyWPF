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
    /// longer playback stays linear and resets after each jump. When end-loop is enabled, the last
    /// branchable beat always jumps backwards so playback never falls off the end of the song.
    ///
    /// Because playback between jumps is linear, the whole walk is planned ahead: PlanNextJump
    /// returns the single next seek to arm in the player, and is called again after it fires.
    /// </summary>
    public class BeatNavigator
    {
        private readonly Random _random;

        private readonly JukeboxSettings _settings;

        /// <summary>Locked branches keyed by packed (from &lt;&lt; 32 | to), value = probability.</summary>
        private readonly Dictionary<long, double> _lockedBranches = new Dictionary<long, double>();

        private readonly bool _randomBranches;

        private double _currentBranchChance;

        public BeatGraph Graph { get; }

        public double CurrentBranchChance => _currentBranchChance;

        /// <summary>True when random branches are off and no locks are set (caller should warn).</summary>
        public bool IsIdleWithoutLocks => !_randomBranches && _lockedBranches.Count == 0;

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
                {
                    var key = PackBranch(branchLock.FromBeatIndex, branchLock.ToBeatIndex);
                    var probability = ClampUnit(branchLock.Probability);
                    _lockedBranches[key] = probability;
                }
            }

            // Default true when profile is null (fresh track).
            _randomBranches = profile?.RandomBranches ?? true;
        }

        private static long PackBranch(int from, int to) => ((long)from << 32) | (uint)to;

        private bool TryGetLockProbability(int from, BeatEdge edge, out double probability) =>
            _lockedBranches.TryGetValue(PackBranch(from, edge.DestinationIndex), out probability);

        private static double ClampUnit(double value) => Math.Max(0, Math.Min(1, value));

        private double ClampProbability(double value)
        {
            var min = Math.Min(_settings.BranchProbabilityMin, _settings.BranchProbabilityMax);
            var max = Math.Max(_settings.BranchProbabilityMin, _settings.BranchProbabilityMax);
            return Math.Max(min, Math.Min(max, value));
        }

        /// <summary>True when the graph has at least one usable branch (or end-loop can still fire).</summary>
        public bool CanJump =>
            (_settings.EnableEndLoop && Graph.LastBranchPointIndex >= 0) ||
            Graph.Beats.Any(b => b.Neighbors.Count > 0) ||
            _lockedBranches.Count > 0;

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

                // Locked branches at this beat: each fires at its own probability.
                var lockedJump = TryTakeLockedJump(i, beat.Neighbors);

                if (lockedJump != null)
                    return lockedJump;

                // Random off: only locks (+ end-loop guard below). Never fall back to full random.
                if (!_randomBranches)
                {
                    if (_settings.EnableEndLoop && i == Graph.LastBranchPointIndex)
                    {
                        var guardEdge = ChooseEdge(i,
                            beat.Neighbors.Where(e => e.DestinationIndex < i).ToList(),
                            exemptLongBranchFilter: true);

                        if (guardEdge != null)
                            return MakeJump(i, guardEdge);
                    }

                    continue;
                }

                // Point of no return: always branch backwards here rather than running off the end.
                if (_settings.EnableEndLoop && i == Graph.LastBranchPointIndex)
                {
                    var backwardEdge = ChooseEdge(i,
                        beat.Neighbors.Where(e => e.DestinationIndex < i).ToList(),
                        exemptLongBranchFilter: true);

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

        /// <summary>
        /// Among locked edges leaving this beat, pick one by probability weight. Each lock's
        /// Probability is the chance it is eligible; among eligible locks, weighted pick.
        /// </summary>
        private JukeboxJump TryTakeLockedJump(int fromIndex, IReadOnlyList<BeatEdge> neighbors)
        {
            if (neighbors == null || neighbors.Count == 0 || _lockedBranches.Count == 0)
                return null;

            var eligible = new List<(BeatEdge Edge, double Weight)>();

            foreach (var edge in neighbors)
            {
                if (!TryGetLockProbability(fromIndex, edge, out var probability) || probability <= 0)
                    continue;

                // Bernoulli gate per lock; Probability 1.0 always includes it.
                if (probability >= 1.0 || _random.NextDouble() < probability)
                    eligible.Add((edge, probability));
            }

            if (eligible.Count == 0)
                return null;

            var chosen = WeightedPick(eligible);
            return chosen == null ? null : MakeJump(fromIndex, chosen);
        }

        private BeatEdge WeightedPick(List<(BeatEdge Edge, double Weight)> items)
        {
            if (items.Count == 1)
                return items[0].Edge;

            var total = items.Sum(i => i.Weight);

            if (total <= 0)
                return items[_random.Next(items.Count)].Edge;

            var roll = _random.NextDouble() * total;
            double cumulative = 0;

            foreach (var item in items)
            {
                cumulative += item.Weight;

                if (roll <= cumulative)
                    return item.Edge;
            }

            return items[items.Count - 1].Edge;
        }

        private BeatEdge ChooseEdge(int fromBeatIndex, IReadOnlyList<BeatEdge> edges,
            bool exemptLongBranchFilter = false)
        {
            if (edges == null || edges.Count == 0)
                return null;

            var filtered = FilterEdges(fromBeatIndex, edges, exemptLongBranchFilter);

            if (filtered.Count == 0)
                return null;

            return filtered[_random.Next(filtered.Count)];
        }

        private List<BeatEdge> FilterEdges(int fromBeatIndex, IReadOnlyList<BeatEdge> edges,
            bool exemptLongBranchFilter)
        {
            IEnumerable<BeatEdge> query = edges;

            if (_settings.AllowOnlyReverseBranches)
                query = query.Where(e => e.DestinationIndex < fromBeatIndex);

            if (_settings.AllowOnlyLongBranches && !exemptLongBranchFilter)
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
