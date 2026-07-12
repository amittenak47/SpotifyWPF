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
    /// Slice 4: Softmax(−dist/τ − λ·visits) among filtered candidates, plus a post-hop dwell
    /// (<see cref="JukeboxSettings.MinBeatsBetweenJumps"/>) so the walk stays stable before
    /// another random hop. Slice 6 optionally re-ranks with preference weight w_pref.
    /// </summary>
    public class BeatNavigator
    {
        private readonly Random _random;

        private readonly JukeboxSettings _settings;

        /// <summary>Locked branches keyed by packed (from &lt;&lt; 32 | to), value = probability.</summary>
        private readonly Dictionary<long, double> _lockedBranches = new Dictionary<long, double>();

        private readonly bool _randomBranches;

        private readonly BranchPreferenceStore _preferences;

        private readonly string _trackId;

        private double _currentBranchChance;

        /// <summary>
        /// Recent jump destinations. Exact-index memory is useless for chorus twins
        /// (44→45→46 all look "fresh"); we treat a radius around each as visited and
        /// refuse jumps into that pocket so the walk can leave the attractor.
        /// </summary>
        private readonly Queue<int> _recentDestinations = new Queue<int>();

        private readonly HashSet<int> _recentDestinationSet = new HashSet<int>();

        /// <summary>Visit counts per destination beat (Slice 4 novelty λ).</summary>
        private readonly Dictionary<int, int> _visitCounts = new Dictionary<int, int>();

        private const int RecentVisitMemory = 32;

        /// <summary>Linear beats walked since the last hop (for MinBeatsBetweenJumps dwell).</summary>
        private int _beatsSinceJump = int.MaxValue / 4;

        /// <summary>Last fired hop — used for skip-after-jump preference negatives.</summary>
        public int LastJumpFromBeat { get; private set; } = -1;

        public int LastJumpToBeat { get; private set; } = -1;

        /// <summary>UTC when the last hop actually fired (not when it was planned).</summary>
        private DateTime _lastJumpFiredUtc = DateTime.MinValue;

        /// <summary>Pending pairwise label recorded only when the hop fires.</summary>
        private int _pendingChoiceFrom = -1;

        private int _pendingChoiceTo = -1;

        private int[] _pendingAlternatives;

        public BeatGraph Graph { get; }

        public double CurrentBranchChance => _currentBranchChance;

        /// <summary>True when random branches are off and no locks are set (caller should warn).</summary>
        public bool IsIdleWithoutLocks => !_randomBranches && _lockedBranches.Count == 0;

        public BeatNavigator(BeatGraph graph, JukeboxSettings settings, LoopProfile profile = null,
            int? randomSeed = null, BranchPreferenceStore preferences = null)
        {
            Graph = graph ?? throw new ArgumentNullException(nameof(graph));
            _settings = settings ?? JukeboxSettings.CreateDefaults();
            _trackId = graph.TrackId;
            _preferences = preferences;
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

        private int VisitRadius => Math.Max(1, _settings.VisitRegionRadiusBeats > 0
            ? _settings.VisitRegionRadiusBeats
            : 16);

        private int DwellBeats => Math.Max(0, _settings.MinBeatsBetweenJumps);

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

            if (endLoopActive && start > lastBranch)
            {
                var escaped = TryCreateEndLoopJump(start) ?? TryCreateEndLoopJump(lastBranch);

                if (escaped != null)
                    return escaped;
            }

            for (var i = start; i < beats.Count; i++)
            {
                var beat = beats[i];

                if (endLoopActive && i >= lastBranch)
                {
                    var guard = TryCreateEndLoopJump(i) ?? TryCreateEndLoopJump(lastBranch);

                    if (guard != null)
                        return guard;
                }

                var lockedJump = TryTakeLockedJump(i, beat.Neighbors);

                if (lockedJump != null)
                    return lockedJump;

                if (!_randomBranches)
                {
                    _beatsSinceJump++;
                    continue;
                }

                if (beat.Neighbors.Count == 0)
                {
                    _beatsSinceJump++;
                    continue;
                }

                // Dwell: stay linear for MinBeatsBetweenJumps after a hop (stability window).
                if (_beatsSinceJump < DwellBeats)
                {
                    _beatsSinceJump++;
                    _currentBranchChance = ClampProbability(
                        _currentBranchChance + _settings.BranchProbabilityRampPerBeat);
                    continue;
                }

                if (_random.NextDouble() < _currentBranchChance)
                {
                    var edge = ChooseEdge(i, beat.Neighbors);

                    if (edge != null)
                    {
                        _currentBranchChance = ClampProbability(_settings.BranchProbabilityMin);
                        return MakeJump(i, edge, beat.Neighbors);
                    }
                }

                _beatsSinceJump++;
                _currentBranchChance = ClampProbability(
                    _currentBranchChance + _settings.BranchProbabilityRampPerBeat);
            }

            if (endLoopActive)
                return TryCreateEndLoopJump(Math.Min(start, lastBranch)) ?? TryCreateEndLoopJump(lastBranch);

            return null;
        }

        private JukeboxJump TryCreateEndLoopJump(int fromIndex)
        {
            if (Graph.Beats.Count == 0)
                return null;

            fromIndex = Math.Max(0, Math.Min(fromIndex, Graph.Beats.Count - 1));
            var count = Graph.Beats.Count;
            var earlyBefore = Math.Max(1, (int)(count * 0.5));

            // Prefer any nearby source that can actually land in the first half of the song.
            for (var i = fromIndex; i >= 0; i--)
            {
                if (!TryGetBackwardCandidates(i, out var source, out var candidates))
                    continue;

                var early = candidates.Where(e => e.DestinationIndex < earlyBefore).ToList();

                if (early.Count == 0)
                    continue;

                var edge = SoftmaxPick(source, early, ignoreVisits: true)
                           ?? early[_random.Next(early.Count)];
                return MakeJump(source, edge, candidates);
            }

            // No early landing available — use tiered ChooseEndLoopEdge from fromIndex backward.
            if (TryGetBackwardCandidates(fromIndex, out var fallbackSource, out var fallbackCandidates))
            {
                var edge = ChooseEndLoopEdge(fallbackSource, fallbackCandidates)
                           ?? fallbackCandidates[_random.Next(fallbackCandidates.Count)];
                return MakeJump(fallbackSource, edge, fallbackCandidates);
            }

            for (var i = fromIndex - 1; i >= 0; i--)
            {
                if (!TryGetBackwardCandidates(i, out fallbackSource, out fallbackCandidates))
                    continue;

                var edge = ChooseEndLoopEdge(fallbackSource, fallbackCandidates)
                           ?? fallbackCandidates[_random.Next(fallbackCandidates.Count)];
                return MakeJump(fallbackSource, edge, fallbackCandidates);
            }

            return null;
        }

        /// <summary>
        /// End-loop must leave the closing section when possible — short hops to the start of
        /// the same outro trap the walk. Prefer landings in the first half of the track;
        /// never Softmax into the final fifth when earlier options exist.
        /// </summary>
        private BeatEdge ChooseEndLoopEdge(int fromBeatIndex, List<BeatEdge> candidates)
        {
            if (candidates == null || candidates.Count == 0)
                return null;

            var filtered = FilterEdges(fromBeatIndex, candidates, exemptLongBranchFilter: true);

            if (filtered.Count == 0)
                filtered = candidates;

            var count = Graph.Beats.Count;
            var earlyBefore = Math.Max(1, (int)(count * 0.5));
            var midBefore = Math.Max(1, (int)(count * 0.7));
            var lateBefore = Math.Max(1, (int)(count * 0.8));
            var minEscapeBeats = Math.Max(16, (_settings.MinimumJumpBeats > 0
                ? _settings.MinimumJumpBeats
                : 8) * 2);

            // Tiered escape: early song first, then mid, then "long hop / before late".
            // Ignore visit novelty here — high visit counts on the intro are exactly why Softmax
            // was refusing to leave the outro after a long session / skip-ahead.
            var early = filtered.Where(e => e.DestinationIndex < earlyBefore).ToList();

            if (early.Count > 0)
                return SoftmaxPick(fromBeatIndex, early, ignoreVisits: true)
                       ?? early[_random.Next(early.Count)];

            var mid = filtered.Where(e => e.DestinationIndex < midBefore).ToList();

            if (mid.Count > 0)
                return SoftmaxPick(fromBeatIndex, mid, ignoreVisits: true)
                       ?? mid[_random.Next(mid.Count)];

            var escape = filtered
                .Where(e => e.DestinationIndex < lateBefore ||
                            fromBeatIndex - e.DestinationIndex >= minEscapeBeats)
                .ToList();

            if (escape.Count > 0)
                return SoftmaxPick(fromBeatIndex, escape, ignoreVisits: true)
                       ?? escape[_random.Next(escape.Count)];

            // Fall back: farthest backward hop among candidates (still better than looping outro).
            return filtered.OrderByDescending(e => fromBeatIndex - e.DestinationIndex).First();
        }

        private bool TryGetBackwardCandidates(int fromIndex, out int source, out List<BeatEdge> candidates)
        {
            source = fromIndex;
            candidates = Graph.Beats[fromIndex].Neighbors
                .Where(e => e.DestinationIndex < fromIndex)
                .ToList();
            return candidates.Count > 0;
        }

        private JukeboxJump TryTakeLockedJump(int fromIndex, IReadOnlyList<BeatEdge> neighbors)
        {
            if (neighbors == null || neighbors.Count == 0 || _lockedBranches.Count == 0)
                return null;

            var eligible = new List<(BeatEdge Edge, double Weight)>();

            foreach (var edge in neighbors)
            {
                if (!TryGetLockProbability(fromIndex, edge, out var probability) || probability <= 0)
                    continue;

                if (probability >= 1.0 || _random.NextDouble() < probability)
                    eligible.Add((edge, probability));
            }

            if (eligible.Count == 0)
                return null;

            var chosen = WeightedPick(eligible);
            return chosen == null ? null : MakeJump(fromIndex, chosen, neighbors);
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

            // Prefer landings that continue the next beat's bar phase when any exist.
            var phased = PreferContinuationPhase(fromBeatIndex, filtered);

            if (phased.Count > 0)
                filtered = phased;

            var fresh = filtered.Where(e => !IsNearRecentDestination(e.DestinationIndex)).ToList();

            if (fresh.Count > 0)
                return SoftmaxPick(fromBeatIndex, fresh);

            // Don't starve the walk into a linear run to the outro: once every pocket looks
            // "recent", Softmax with visit novelty still picks among the filtered set.
            return SoftmaxPick(fromBeatIndex, filtered);
        }

        private List<BeatEdge> PreferContinuationPhase(int fromBeatIndex, List<BeatEdge> edges)
        {
            if (edges == null || edges.Count == 0 || fromBeatIndex < 0 ||
                fromBeatIndex >= Graph.Beats.Count)
                return edges;

            var expect = fromBeatIndex + 1 < Graph.Beats.Count
                ? Graph.Beats[fromBeatIndex + 1].IndexInBar
                : Graph.Beats[fromBeatIndex].IndexInBar;

            var matched = edges
                .Where(e => e.DestinationIndex >= 0 &&
                            e.DestinationIndex < Graph.Beats.Count &&
                            CircularBarPhaseDelta(Graph.Beats[e.DestinationIndex].IndexInBar, expect) == 0)
                .ToList();

            return matched.Count > 0 ? matched : edges;
        }

        private static int CircularBarPhaseDelta(int barPosA, int barPosB, int period = 4)
        {
            var a = ((barPosA % period) + period) % period;
            var b = ((barPosB % period) + period) % period;
            var d = Math.Abs(a - b);
            return Math.Min(d, period - d);
        }

        private bool IsNearRecentDestination(int destinationIndex)
        {
            var radius = VisitRadius;

            foreach (var recent in _recentDestinations)
            {
                if (Math.Abs(destinationIndex - recent) <= radius)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Softmax(−dist/τ − λ·visits + w_pref·pref). Lower temperature → greedier nearest pick.
        /// </summary>
        private BeatEdge SoftmaxPick(int fromBeatIndex, List<BeatEdge> edges, bool ignoreVisits = false)
        {
            if (edges.Count == 1)
                return edges[0];

            var tau = Math.Max(0.05, _settings.SoftmaxTemperature > 0 ? _settings.SoftmaxTemperature : 1.0);
            var lambda = ignoreVisits ? 0 : Math.Max(0, _settings.VisitNoveltyLambda);
            var wPref = _settings.PreferenceWeight;
            var scores = new double[edges.Count];
            var maxScore = double.NegativeInfinity;

            for (var i = 0; i < edges.Count; i++)
            {
                var edge = edges[i];
                var visits = ignoreVisits ? 0 : GetVisitCountNear(edge.DestinationIndex);
                var pref = wPref != 0 && _preferences != null
                    ? _preferences.Score(_trackId, fromBeatIndex, edge.DestinationIndex)
                    : 0;
                var score = (-edge.Distance / tau) - (lambda * visits) + (wPref * pref);
                scores[i] = score;

                if (score > maxScore)
                    maxScore = score;
            }

            // Stable softmax
            double sum = 0;
            var weights = new double[edges.Count];

            for (var i = 0; i < edges.Count; i++)
            {
                weights[i] = Math.Exp(scores[i] - maxScore);
                sum += weights[i];
            }

            if (sum <= 0 || double.IsNaN(sum) || double.IsInfinity(sum))
                return edges[_random.Next(edges.Count)];

            var roll = _random.NextDouble() * sum;
            double cumulative = 0;

            for (var i = 0; i < edges.Count; i++)
            {
                cumulative += weights[i];

                if (roll <= cumulative)
                    return edges[i];
            }

            return edges[edges.Count - 1];
        }

        private int GetVisitCountNear(int destinationIndex)
        {
            var radius = VisitRadius;
            var total = 0;

            foreach (var kv in _visitCounts)
            {
                if (Math.Abs(kv.Key - destinationIndex) <= radius)
                    total += kv.Value;
            }

            return total;
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

            // Hypermeasure / long drum-phrase lock: only land on the same beat-within-phrase.
            // IndexInBar alone is usually 0–3; Clubbed to Death-style grooves often need 8–16.
            var phrase = _settings.PhraseAlignBeats;
            if (phrase > 1)
            {
                var fromPhase = ((fromBeatIndex % phrase) + phrase) % phrase;
                query = query.Where(e =>
                {
                    var to = e.DestinationIndex;
                    var toPhase = ((to % phrase) + phrase) % phrase;
                    return toPhase == fromPhase;
                });
            }

            if (_settings.EnableEndLoop && Graph.LastBranchPointIndex >= 0 && !exemptLongBranchFilter)
            {
                var last = Graph.LastBranchPointIndex;
                query = query.Where(e => e.DestinationIndex <= last);
            }

            return query.ToList();
        }

        private JukeboxJump MakeJump(int fromIndex, BeatEdge edge, IReadOnlyList<BeatEdge> candidates)
        {
            RememberDestination(edge.DestinationIndex);
            _beatsSinceJump = 0;

            // Defer pairwise labels until the hop actually fires (replan before fire must not label).
            _pendingChoiceFrom = fromIndex;
            _pendingChoiceTo = edge.DestinationIndex;
            _pendingAlternatives = candidates == null
                ? Array.Empty<int>()
                : candidates.Select(c => c.DestinationIndex).ToArray();

            var target = Graph.Beats[edge.DestinationIndex];
            // Seek lead compensates Spotify SDK latency only; local WAV seeks are in-process.
            var seekLead = string.Equals(_settings.PlaybackSource, "Local",
                StringComparison.OrdinalIgnoreCase)
                ? 0
                : Math.Max(0, _settings.SeekLeadMs);
            var triggerMs = Graph.Beats[fromIndex].EndMs - seekLead;

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

        /// <summary>
        /// Call when a planned hop actually seeks. Commits the pairwise preference label and
        /// starts the skip-negative window.
        /// </summary>
        public void NotifyJumpFired(int fromBeatIndex, int toBeatIndex)
        {
            LastJumpFromBeat = fromBeatIndex;
            LastJumpToBeat = toBeatIndex;
            _lastJumpFiredUtc = DateTime.UtcNow;

            if (_preferences == null)
                return;

            if (_pendingChoiceFrom == fromBeatIndex && _pendingChoiceTo == toBeatIndex)
            {
                _preferences.RecordChoice(
                    _trackId,
                    fromBeatIndex,
                    toBeatIndex,
                    _pendingAlternatives);
            }

            _pendingChoiceFrom = -1;
            _pendingChoiceTo = -1;
            _pendingAlternatives = null;
        }

        private void RememberDestination(int destinationIndex, bool countVisit = true)
        {
            if (countVisit)
            {
                if (!_visitCounts.ContainsKey(destinationIndex))
                    _visitCounts[destinationIndex] = 0;

                _visitCounts[destinationIndex]++;
            }

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
                RememberDestination(dest, countVisit: false);
        }

        public void ImportVisitCounts(IReadOnlyDictionary<int, int> counts)
        {
            if (counts == null)
                return;

            foreach (var kv in counts)
            {
                if (kv.Value <= 0)
                    continue;

                _visitCounts[kv.Key] = _visitCounts.TryGetValue(kv.Key, out var cur)
                    ? cur + kv.Value
                    : kv.Value;
            }
        }

        public IReadOnlyList<int> ExportVisitMemory() => _recentDestinations.ToArray();

        public IReadOnlyDictionary<int, int> ExportVisitCounts() =>
            new Dictionary<int, int>(_visitCounts);

        public int ExportBeatsSinceJump() => _beatsSinceJump;

        public void ImportBeatsSinceJump(int value) =>
            _beatsSinceJump = Math.Max(0, value);

        /// <summary>
        /// Scrub/skip-ahead: drop the "already visited" pocket memory so Softmax can use early
        /// song branches again instead of starving into the end-loop outro trap.
        /// Visit counts are decayed (not wiped) so novelty still works after the scrub.
        /// </summary>
        public void NotifySeekReplan(int fromBeatIndex)
        {
            _recentDestinations.Clear();
            _recentDestinationSet.Clear();
            _beatsSinceJump = Math.Max(DwellBeats, _beatsSinceJump);
            _currentBranchChance = ClampProbability(_settings.BranchProbabilityMin);

            if (_visitCounts.Count == 0)
                return;

            var keys = _visitCounts.Keys.ToList();

            foreach (var key in keys)
            {
                var next = (_visitCounts[key] + 1) / 2;

                if (next <= 0)
                    _visitCounts.Remove(key);
                else
                    _visitCounts[key] = next;
            }

            // Avoid immediately re-arming a hop into the pocket we just scrubbed into.
            if (fromBeatIndex >= 0)
                RememberDestination(fromBeatIndex, countVisit: false);
        }

        /// <summary>
        /// Record a scrub/skip shortly after the last hop as a preference negative.
        /// Only counts inside PreferenceSkipWindowMs after the hop fired.
        /// </summary>
        public bool NotifySkipAfterLastJump()
        {
            if (_preferences == null || LastJumpFromBeat < 0 || LastJumpToBeat < 0)
                return false;

            if (_lastJumpFiredUtc == DateTime.MinValue)
                return false;

            var windowMs = Math.Max(500, _settings.PreferenceSkipWindowMs > 0
                ? _settings.PreferenceSkipWindowMs
                : 8000);

            if ((DateTime.UtcNow - _lastJumpFiredUtc).TotalMilliseconds > windowMs)
            {
                ClearLastJumpPreferenceWindow();
                return false;
            }

            _preferences.RecordSkipAfterJump(_trackId, LastJumpFromBeat, LastJumpToBeat);
            ClearLastJumpPreferenceWindow();
            return true;
        }

        private void ClearLastJumpPreferenceWindow()
        {
            LastJumpFromBeat = -1;
            LastJumpToBeat = -1;
            _lastJumpFiredUtc = DateTime.MinValue;
        }
    }
}
