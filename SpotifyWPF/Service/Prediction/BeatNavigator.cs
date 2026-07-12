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

        /// <summary>
        /// Recent jump destinations. Exact-index memory is useless for chorus twins
        /// (44→45→46 all look "fresh"); we treat a radius around each as visited and
        /// refuse jumps into that pocket so the walk can leave the attractor.
        /// </summary>
        private readonly Queue<int> _recentDestinations = new Queue<int>();

        private readonly HashSet<int> _recentDestinationSet = new HashSet<int>();

        private const int RecentVisitMemory = 32;

        /// <summary>Beats within this radius of a recent destination count as the same pocket.</summary>
        private const int VisitRegionRadiusBeats = 16;

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
            var lastBranch = Graph.LastBranchPointIndex;
            var endLoopActive = _settings.EnableEndLoop && lastBranch >= 0;

            // A prior hop can land AFTER the last escape beat; the old == guard never ran and the
            // song finished with "end loop" still checked. Escape immediately from wherever we are.
            if (endLoopActive && start > lastBranch)
            {
                var escaped = TryCreateEndLoopJump(start) ?? TryCreateEndLoopJump(lastBranch);

                if (escaped != null)
                    return escaped;
            }

            for (var i = start; i < beats.Count; i++)
            {
                var beat = beats[i];

                // Point of no return: at or past the last escape beat, always jump back.
                // Checked before locks so a locked forward hop cannot run the track out.
                if (endLoopActive && i >= lastBranch)
                {
                    var guard = TryCreateEndLoopJump(i) ?? TryCreateEndLoopJump(lastBranch);

                    if (guard != null)
                        return guard;
                }

                // Locked branches at this beat: each fires at its own probability.
                var lockedJump = TryTakeLockedJump(i, beat.Neighbors);

                if (lockedJump != null)
                    return lockedJump;

                // Random off: only locks (+ end-loop guard above). Never fall back to full random.
                if (!_randomBranches)
                    continue;

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

            // Last resort: still somehow past the escape beat with no plan.
            if (endLoopActive)
                return TryCreateEndLoopJump(Math.Min(start, lastBranch)) ?? TryCreateEndLoopJump(lastBranch);

            return null;
        }

        /// <summary>
        /// Force a backward jump for end-loop. Searches the from-beat, then the last branch point,
        /// then earlier beats until a usable backward edge exists.
        /// </summary>
        private JukeboxJump TryCreateEndLoopJump(int fromIndex)
        {
            if (Graph.Beats.Count == 0)
                return null;

            fromIndex = Math.Max(0, Math.Min(fromIndex, Graph.Beats.Count - 1));

            if (TryGetBackwardCandidates(fromIndex, out var source, out var candidates))
            {
                var edge = ChooseEdge(source, candidates, exemptLongBranchFilter: true)
                           ?? candidates[_random.Next(candidates.Count)];
                return MakeJump(source, edge);
            }

            for (var i = fromIndex - 1; i >= 0; i--)
            {
                if (!TryGetBackwardCandidates(i, out source, out candidates))
                    continue;

                var edge = ChooseEdge(source, candidates, exemptLongBranchFilter: true)
                           ?? candidates[_random.Next(candidates.Count)];
                return MakeJump(source, edge);
            }

            return null;
        }

        private bool TryGetBackwardCandidates(int fromIndex, out int source, out List<BeatEdge> candidates)
        {
            source = fromIndex;
            candidates = Graph.Beats[fromIndex].Neighbors
                .Where(e => e.DestinationIndex < fromIndex)
                .ToList();
            return candidates.Count > 0;
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

            var fresh = filtered.Where(e => !IsNearRecentDestination(e.DestinationIndex)).ToList();

            if (fresh.Count > 0)
                return PickAwayFromRecent(fresh);

            // Entire candidate set lands in a recent pocket (classic chorus A↔B).
            // Refuse the jump so PlanNextJump keeps walking linearly out of the attractor.
            // End-loop / last-branch guard must still fire (exemptLongBranchFilter).
            if (exemptLongBranchFilter)
                return PickAwayFromRecent(filtered);

            return null;
        }

        private bool IsNearRecentDestination(int destinationIndex)
        {
            foreach (var recent in _recentDestinations)
            {
                if (Math.Abs(destinationIndex - recent) <= VisitRegionRadiusBeats)
                    return true;
            }

            return false;
        }

        /// <summary>Weighted pick favoring destinations farthest from recent visit pockets.</summary>
        private BeatEdge PickAwayFromRecent(List<BeatEdge> edges)
        {
            if (edges.Count == 1)
                return edges[0];

            if (_recentDestinations.Count == 0)
                return edges[_random.Next(edges.Count)];

            var weighted = new List<(BeatEdge Edge, double Weight)>(edges.Count);

            foreach (var edge in edges)
            {
                var minDist = int.MaxValue;

                foreach (var recent in _recentDestinations)
                {
                    var dist = Math.Abs(edge.DestinationIndex - recent);

                    if (dist < minDist)
                        minDist = dist;
                }

                // Squared distance biases hard away from the pocket; +1 keeps weight > 0.
                var weight = (minDist + 1.0) * (minDist + 1.0);
                weighted.Add((edge, weight));
            }

            return WeightedPick(weighted);
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

            // Don't arm hops that land past the last escape beat — that used to finish the song.
            if (_settings.EnableEndLoop && Graph.LastBranchPointIndex >= 0 && !exemptLongBranchFilter)
            {
                var last = Graph.LastBranchPointIndex;
                query = query.Where(e => e.DestinationIndex <= last);
            }

            return query.ToList();
        }

        private JukeboxJump MakeJump(int fromIndex, BeatEdge edge)
        {
            RememberDestination(edge.DestinationIndex);

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

        private void RememberDestination(int destinationIndex)
        {
            if (!_recentDestinationSet.Add(destinationIndex))
                return;

            _recentDestinations.Enqueue(destinationIndex);

            while (_recentDestinations.Count > RecentVisitMemory)
            {
                var old = _recentDestinations.Dequeue();
                _recentDestinationSet.Remove(old);
            }
        }

        /// <summary>Copy visit memory across navigator recreations (rearm / lock edits).</summary>
        public void ImportVisitMemory(IEnumerable<int> destinations)
        {
            if (destinations == null)
                return;

            foreach (var dest in destinations)
                RememberDestination(dest);
        }

        public IReadOnlyList<int> ExportVisitMemory() => _recentDestinations.ToArray();
    }
}
