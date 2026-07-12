using System;
using System.Collections.Generic;
using System.Linq;
using SpotifyWPF.Model.Prediction;

namespace SpotifyWPF.Service.Prediction
{
    public class BeatEdge
    {
        public int DestinationIndex { get; set; }

        public double Distance { get; set; }

        /// <summary>True when this edge was added as an inter-component / orphan rescue bridge (Slice 4).</summary>
        public bool IsBridge { get; set; }
    }

    public class BeatNode
    {
        public int Index { get; set; }

        public double StartSec { get; set; }

        public double DurationSec { get; set; }

        /// <summary>Position of this beat within its bar (0-based), used as a rhythm gate.</summary>
        public int IndexInBar { get; set; }

        public List<BeatEdge> Neighbors { get; } = new List<BeatEdge>();

        public long StartMs => (long)(StartSec * 1000);

        public long EndMs => (long)((StartSec + DurationSec) * 1000);
    }

    public class BeatGraph
    {
        public string TrackId { get; set; }

        public List<BeatNode> Beats { get; } = new List<BeatNode>();

        /// <summary>Quality distance cap settled for this build (percentile threshold or legacy auto-tune).</summary>
        public double BranchDistanceThreshold { get; set; }

        /// <summary>Latest beat that has a backward branch — the "point of no return" guard.</summary>
        public int LastBranchPointIndex { get; set; }

        /// <summary>"classic" (Slice 2 vectors) or "legacy" (Echo Nest segment distance).</summary>
        public string MetricMode { get; set; } = "legacy";

        /// <summary>True when Classic edges were filtered to mutual kNN.</summary>
        public bool UsedMutualKnn { get; set; }

        /// <summary>Connected-component id per beat (mutual undirected graph). -1 = unset.</summary>
        public int[] ComponentIds { get; set; }

        public int ComponentCount { get; set; }

        public int BridgeEdgeCount { get; set; }

        public int TotalBranchCount => Beats.Sum(b => b.Neighbors.Count);

        public int BranchableBeatCount => Beats.Count(b => b.Neighbors.Count > 0);
    }

    /// <summary>
    /// Builds the Infinite Jukebox beat graph. Slice 2 Classic path: beat-synchronous stacked
    /// vectors + z-scored Euclidean + continuation-oriented kNN (edge i→j scores stack[i+1] vs
    /// stack[j]) with a track-wide percentile quality cap. Legacy Path A / old caches without
    /// Classic vectors still use Echo Nest-style segment distance.
    /// </summary>
    public class BeatGraphBuilder
    {
        public int MaxNeighbors { get; set; } = 6;

        public int MaxNearestNeighbors { get; set; } = 3;

        public int MaxFarthestNeighbors { get; set; } = 3;

        public double MaxBranchDistance { get; set; } = 80;

        public double InitialBranchDistance { get; set; } = 15;

        public double BranchDistanceStep { get; set; } = 5;

        public double TargetBranchableBeatRatio { get; set; } = 0.1;

        public int MinimumJumpDistanceInBeats { get; set; } = 4;

        private const double TimbreWeight = 1;
        private const double PitchWeight = 10;
        private const double LoudStartWeight = 1;
        private const double LoudMaxWeight = 1;
        private const double DurationWeight = 100;
        private const double ConfidenceWeight = 1;
        private const double MissingSegmentPenalty = 100;
        private const double DifferentBarPositionPenalty = 100;

        public BeatGraph Build(TrackAnalysis analysis, JukeboxSettings settings = null)
        {
            settings = settings ?? JukeboxSettings.CreateDefaults();

            if (analysis == null)
                throw new ArgumentNullException(nameof(analysis));

            if (analysis.Beats == null || analysis.Beats.Count == 0)
                throw new InvalidOperationException("Analysis has no beats.");

            var metricMode = (settings.GraphMetricMode ?? "auto").Trim().ToLowerInvariant();

            if (metricMode == "legacy")
                return BuildLegacy(analysis, settings);

            if (metricMode == "classic")
            {
                if (!analysis.HasClassicFeatures)
                    throw new InvalidOperationException(
                        "Graph metric is Classic but this analysis has no stackedFeatures. " +
                        "Re-analyze the track (Delete cache → Analyze).");

                return BuildClassic(analysis, settings);
            }

            // auto
            if (analysis.HasClassicFeatures)
                return BuildClassic(analysis, settings);

            return BuildLegacy(analysis, settings);
        }

        private BeatGraph BuildClassic(TrackAnalysis analysis, JukeboxSettings settings)
        {
            var graph = CreateGraphSkeleton(analysis);
            AssignBarPositions(graph, analysis);
            AssignDownbeatBarPositions(graph, analysis);

            var minJump = Math.Max(4, settings.MinimumJumpBeats > 0 ? settings.MinimumJumpBeats : 8);
            // Higher quality percentile (= looser) also unlocks more neighbor slots so Softmax
            // has in-phase options instead of only a handful of mutual-kNN survivors.
            var baseK = Math.Max(1, settings.ClassicMaxNeighbors > 0 ? settings.ClassicMaxNeighbors : MaxNeighbors);
            var qualityPercentile = Math.Max(5, Math.Min(95, settings.BranchSimilarityThresholdMax));
            var k = Math.Max(baseK, baseK + (int)Math.Round((qualityPercentile - 35) / 12.0));
            k = Math.Max(4, Math.Min(24, k));

            var vectors = analysis.StackedFeatures;
            var count = graph.Beats.Count;
            var distances = new double[count][];
            var allPairDistances = new List<double>(count * 8);

            for (var i = 0; i < count; i++)
            {
                distances[i] = new double[count];
                for (var j = 0; j < count; j++)
                    distances[i][j] = i == j ? 0 : double.MaxValue;
            }

            // Continuation-oriented: edge i→j scores how well j continues after i
            // (stack[i+1] ≈ stack[j]), not how alike twin beats i and j are.
            // Splice leaves end of i and lands on start of j — twin matching double-plays attacks.
            for (var i = 0; i < count - 1; i++)
            {
                for (var j = 0; j < count; j++)
                {
                    if (j == i || Math.Abs(i - j) < minJump)
                        continue;

                    var distance = ClassicDistance(
                        vectors[i + 1], vectors[j],
                        graph.Beats[i + 1], graph.Beats[j],
                        settings.PhasePenaltyMode);

                    distances[i][j] = distance;
                    allPairDistances.Add(distance);
                }
            }

            var threshold = allPairDistances.Count == 0
                ? 0
                : Percentile(allPairDistances, qualityPercentile);

            for (var i = 0; i < count; i++)
            {
                var candidates = new List<BeatEdge>();

                for (var j = 0; j < count; j++)
                {
                    if (Math.Abs(i - j) < minJump)
                        continue;

                    var distance = distances[i][j];

                    if (distance > threshold || double.IsInfinity(distance) || double.IsNaN(distance))
                        continue;

                    candidates.Add(new BeatEdge { DestinationIndex = j, Distance = distance });
                }

                // k nearest only — never "farthest under T".
                foreach (var edge in candidates.OrderBy(e => e.Distance).Take(k))
                    graph.Beats[i].Neighbors.Add(edge);
            }

            if (settings.EssentiaRegionGate && analysis.HasRegionEmbeddings)
                ApplyRegionGate(graph, analysis, settings, minJump);

            if (settings.UseMutualKnn)
            {
                ApplyMutualKnn(graph);
                // Mutual kNN often collapses degree to ~2–4 even when percentile/k rise.
                // Top up with the cheapest directed edges so Softmax has real options.
                EnsureMinimumBranchDegree(graph, distances, minJump, Math.Min(8, Math.Max(4, k / 2)));
                graph.UsedMutualKnn = true;
                AssignMutualComponents(graph);

                if (settings.EnableSccBridges)
                    AddCheapestComponentBridges(graph, distances, minJump);
            }
            else
            {
                graph.UsedMutualKnn = false;
                AssignMutualComponents(graph); // still label weak components of directed graph as undirected
            }

            graph.BranchDistanceThreshold = threshold;
            graph.MetricMode = "classic";
            FinishGraph(graph, distances, settings);
            return graph;
        }

        /// <summary>
        /// Slice 5: drop Classic neighbors whose region embedding is not among the nearest
        /// GateRegionNeighborCount for that origin (embeddings choose region; Classic already chose beat).
        /// </summary>
        private static void ApplyRegionGate(BeatGraph graph, TrackAnalysis analysis, JukeboxSettings settings,
            int minJump)
        {
            var kRegion = Math.Max(1, settings.GateRegionNeighborCount);
            var embeds = analysis.RegionEmbeddings;
            var count = graph.Beats.Count;

            for (var i = 0; i < count; i++)
            {
                var beat = graph.Beats[i];

                if (beat.Neighbors.Count == 0)
                    continue;

                var regionDists = new List<(int J, double Dist)>(count);

                for (var j = 0; j < count; j++)
                {
                    if (Math.Abs(i - j) < minJump)
                        continue;

                    regionDists.Add((j, VectorDistance(embeds[i], embeds[j])));
                }

                var allowed = new HashSet<int>(
                    regionDists.OrderBy(t => t.Dist).Take(kRegion).Select(t => t.J));

                beat.Neighbors.RemoveAll(e => !allowed.Contains(e.DestinationIndex));
            }
        }

        private static double VectorDistance(IReadOnlyList<double> a, IReadOnlyList<double> b)
        {
            if (a == null || b == null || a.Count == 0 || b.Count == 0)
                return double.MaxValue;

            var length = Math.Min(a.Count, b.Count);
            double sum = 0;

            for (var i = 0; i < length; i++)
            {
                var d = a[i] - b[i];
                sum += d * d;
            }

            return Math.Sqrt(sum / Math.Max(1, length));
        }

        /// <summary>Keep i→j only when j→i also exists (mutual kNN).</summary>
        private static void ApplyMutualKnn(BeatGraph graph)
        {
            var count = graph.Beats.Count;
            var destSets = new HashSet<int>[count];

            for (var i = 0; i < count; i++)
                destSets[i] = new HashSet<int>(graph.Beats[i].Neighbors.Select(e => e.DestinationIndex));

            for (var i = 0; i < count; i++)
            {
                graph.Beats[i].Neighbors.RemoveAll(e => !destSets[e.DestinationIndex].Contains(i));
            }
        }

        /// <summary>
        /// After mutual thinning, restore cheapest directed edges until each beat has enough hops.
        /// </summary>
        private static void EnsureMinimumBranchDegree(
            BeatGraph graph, double[][] distances, int minJump, int minDegree)
        {
            if (distances == null || minDegree <= 0)
                return;

            var count = graph.Beats.Count;

            for (var i = 0; i < count; i++)
            {
                var neighbors = graph.Beats[i].Neighbors;
                var have = new HashSet<int>(neighbors.Select(e => e.DestinationIndex));

                if (have.Count >= minDegree)
                    continue;

                var extras = new List<BeatEdge>();

                for (var j = 0; j < count; j++)
                {
                    if (j == i || Math.Abs(i - j) < minJump || have.Contains(j))
                        continue;

                    var distance = distances[i][j];

                    if (double.IsInfinity(distance) || double.IsNaN(distance) || distance >= double.MaxValue / 2)
                        continue;

                    extras.Add(new BeatEdge { DestinationIndex = j, Distance = distance });
                }

                foreach (var edge in extras.OrderBy(e => e.Distance))
                {
                    if (have.Count >= minDegree)
                        break;

                    neighbors.Add(edge);
                    have.Add(edge.DestinationIndex);
                }
            }
        }

        /// <summary>Undirected connected components over current neighbor lists.</summary>
        private static void AssignMutualComponents(BeatGraph graph)
        {
            var count = graph.Beats.Count;
            var ids = new int[count];
            var undirected = new List<int>[count];

            for (var i = 0; i < count; i++)
            {
                ids[i] = -1;
                undirected[i] = new List<int>();
            }

            // Init all lists first — destinations often point forward (j > i), so writing
            // undirected[j] before j is constructed was NullReferenceException → empty ring.
            for (var i = 0; i < count; i++)
            {
                foreach (var e in graph.Beats[i].Neighbors)
                {
                    if (e == null)
                        continue;

                    var j = e.DestinationIndex;

                    if (j < 0 || j >= count || j == i)
                        continue;

                    undirected[i].Add(j);
                    undirected[j].Add(i);
                }
            }

            var component = 0;

            for (var i = 0; i < count; i++)
            {
                if (ids[i] >= 0)
                    continue;

                var stack = new Stack<int>();
                stack.Push(i);
                ids[i] = component;

                while (stack.Count > 0)
                {
                    var u = stack.Pop();

                    foreach (var v in undirected[u])
                    {
                        if (ids[v] >= 0)
                            continue;

                        ids[v] = component;
                        stack.Push(v);
                    }
                }

                component++;
            }

            graph.ComponentIds = ids;
            graph.ComponentCount = component;
        }

        /// <summary>
        /// Cheapest bridges between components so mutual-kNN orphans/unique passages stay reachable.
        /// Marks edges with <see cref="BeatEdge.IsBridge"/>.
        /// </summary>
        private static void AddCheapestComponentBridges(BeatGraph graph, double[][] distances, int minJump)
        {
            if (graph.ComponentIds == null || graph.ComponentCount <= 1)
                return;

            var count = graph.Beats.Count;
            var ids = graph.ComponentIds;
            var best = new Dictionary<(int A, int B), (int I, int J, double Dist)>();

            for (var i = 0; i < count; i++)
            {
                for (var j = i + 1; j < count; j++)
                {
                    if (ids[i] == ids[j] || Math.Abs(i - j) < minJump)
                        continue;

                    var d = distances[i][j];

                    if (double.IsInfinity(d) || double.IsNaN(d) || d >= double.MaxValue / 2)
                        continue;

                    var a = Math.Min(ids[i], ids[j]);
                    var b = Math.Max(ids[i], ids[j]);
                    var key = (a, b);

                    if (!best.TryGetValue(key, out var cur) || d < cur.Dist)
                        best[key] = (i, j, d);
                }
            }

            // Kruskal-style: link components with cheapest bridges until one tree (or no more).
            var parent = Enumerable.Range(0, graph.ComponentCount).ToArray();

            int Find(int x)
            {
                while (parent[x] != x)
                    x = parent[x] = parent[parent[x]];
                return x;
            }

            void Union(int x, int y)
            {
                x = Find(x);
                y = Find(y);

                if (x != y)
                    parent[y] = x;
            }

            var bridges = 0;

            foreach (var pair in best.OrderBy(kv => kv.Value.Dist))
            {
                var (i, j, dist) = pair.Value;
                var ci = ids[i];
                var cj = ids[j];

                if (Find(ci) == Find(cj))
                    continue;

                Union(ci, cj);
                TryAddBridge(graph.Beats[i], j, dist);
                TryAddBridge(graph.Beats[j], i, dist);
                bridges += 2;
            }

            // Orphan rescue: beats with zero outgoing edges get their single cheapest directed edge.
            for (var i = 0; i < count; i++)
            {
                if (graph.Beats[i].Neighbors.Count > 0)
                    continue;

                var bestJ = -1;
                var bestD = double.MaxValue;

                for (var j = 0; j < count; j++)
                {
                    if (Math.Abs(i - j) < minJump)
                        continue;

                    var d = distances[i][j];

                    if (d < bestD)
                    {
                        bestD = d;
                        bestJ = j;
                    }
                }

                if (bestJ >= 0)
                {
                    TryAddBridge(graph.Beats[i], bestJ, bestD);
                    bridges++;
                }
            }

            graph.BridgeEdgeCount = bridges;
            AssignMutualComponents(graph);
        }

        private static void TryAddBridge(BeatNode beat, int dest, double dist)
        {
            if (beat.Neighbors.Any(e => e.DestinationIndex == dest))
                return;

            beat.Neighbors.Add(new BeatEdge
            {
                DestinationIndex = dest,
                Distance = dist,
                IsBridge = true
            });
        }

        private BeatGraph BuildLegacy(TrackAnalysis analysis, JukeboxSettings settings)
        {
            var maxDistance = Math.Max(InitialBranchDistance, settings.BranchSimilarityThresholdMax);
            var graph = CreateGraphSkeleton(analysis);
            AssignBarPositions(graph, analysis);

            var overlapping = ComputeOverlappingSegments(graph, analysis);
            var count = graph.Beats.Count;
            var distances = new double[count][];

            for (var i = 0; i < count; i++)
                distances[i] = new double[count];

            for (var i = 0; i < count; i++)
            {
                for (var j = i + 1; j < count; j++)
                {
                    var distance = BeatDistance(graph.Beats[i], graph.Beats[j], overlapping[i], overlapping[j]);
                    distances[i][j] = distance;
                    distances[j][i] = distance;
                }
            }

            var threshold = InitialBranchDistance;
            MinimumJumpDistanceInBeats = Math.Max(MinimumJumpDistanceInBeats,
                settings.MinimumJumpBeats > 0 ? settings.MinimumJumpBeats : MinimumJumpDistanceInBeats);

            while (true)
            {
                ConnectEdgesLegacy(graph, distances, threshold);

                var branchableBeats = graph.Beats.Count(b => b.Neighbors.Count > 0);

                if (branchableBeats >= Math.Max(2, count * TargetBranchableBeatRatio) ||
                    threshold >= maxDistance)
                {
                    break;
                }

                threshold += BranchDistanceStep;
            }

            graph.BranchDistanceThreshold = Math.Min(threshold, maxDistance);
            graph.MetricMode = "legacy";
            FinishGraph(graph, distances, settings);
            return graph;
        }

        private static BeatGraph CreateGraphSkeleton(TrackAnalysis analysis)
        {
            var graph = new BeatGraph { TrackId = analysis.TrackId };

            for (var i = 0; i < analysis.Beats.Count; i++)
            {
                var beat = analysis.Beats[i];
                graph.Beats.Add(new BeatNode
                {
                    Index = i,
                    StartSec = beat.Start,
                    DurationSec = beat.Duration
                });
            }

            return graph;
        }

        private void FinishGraph(BeatGraph graph, double[][] distances, JukeboxSettings settings)
        {
            if (settings.EnableEndLoop)
            {
                EnsureLastEdge(graph, distances, settings);
                graph.LastBranchPointIndex = FindLastBranchPoint(graph);
            }
            else
            {
                graph.LastBranchPointIndex = -1;
            }
        }

        private static double ClassicDistance(IReadOnlyList<double> a, IReadOnlyList<double> b,
            BeatNode beatA, BeatNode beatB, string phaseMode)
        {
            if (a == null || b == null || a.Count == 0 || b.Count == 0)
                return double.MaxValue;

            var length = Math.Min(a.Count, b.Count);
            double sum = 0;

            for (var i = 0; i < length; i++)
            {
                var delta = a[i] - b[i];
                sum += delta * delta;
            }

            var distance = Math.Sqrt(sum / Math.Max(1, length));
            return distance + PhasePenalty(beatA.IndexInBar, beatB.IndexInBar, phaseMode);
        }

        private static double PhasePenalty(int barPosA, int barPosB, string mode)
        {
            if (string.IsNullOrEmpty(mode) ||
                mode.Equals("off", StringComparison.OrdinalIgnoreCase))
                return 0;

            // BeatThis IndexInBar can grow in long undownbeat stretches — compare mod-4 phase
            // so "beat 2 of the bar" matches across the song instead of raw Δ13.
            var delta = CircularBarPhaseDelta(barPosA, barPosB);

            if (delta == 0)
                return 0;

            if (mode.Equals("hard", StringComparison.OrdinalIgnoreCase))
                return DifferentBarPositionPenalty;

            // Soft: graduated — same-bar-slot free; neighboring slots mild; opposite harder.
            return Math.Min(DifferentBarPositionPenalty, delta * 25.0);
        }

        private static int CircularBarPhaseDelta(int barPosA, int barPosB, int period = 4)
        {
            var a = ((barPosA % period) + period) % period;
            var b = ((barPosB % period) + period) % period;
            var d = Math.Abs(a - b);
            return Math.Min(d, period - d);
        }

        private static double Percentile(List<double> values, double percentile)
        {
            if (values == null || values.Count == 0)
                return 0;

            var ordered = values.OrderBy(v => v).ToList();
            var rank = (percentile / 100.0) * (ordered.Count - 1);
            var low = (int)Math.Floor(rank);
            var high = (int)Math.Ceiling(rank);

            if (low == high)
                return ordered[low];

            var weight = rank - low;
            return ordered[low] * (1 - weight) + ordered[high] * weight;
        }

        private void ConnectEdgesLegacy(BeatGraph graph, double[][] distances, double threshold)
        {
            foreach (var beat in graph.Beats)
                beat.Neighbors.Clear();

            var count = graph.Beats.Count;

            for (var i = 0; i < count; i++)
            {
                var edges = new List<BeatEdge>();

                for (var j = 0; j < count; j++)
                {
                    if (Math.Abs(i - j) < MinimumJumpDistanceInBeats)
                        continue;

                    if (distances[i][j] <= threshold)
                        edges.Add(new BeatEdge { DestinationIndex = j, Distance = distances[i][j] });
                }

                // Legacy path: nearest only (no farthest-under-T).
                graph.Beats[i].Neighbors.AddRange(
                    edges.OrderBy(e => e.Distance).Take(MaxNeighbors));
            }
        }

        private void EnsureLastEdge(BeatGraph graph, double[][] distances, JukeboxSettings settings)
        {
            var count = graph.Beats.Count;

            if (count < 2 || distances == null)
                return;

            var minJump = Math.Max(4, settings.MinimumJumpBeats > 0 ? settings.MinimumJumpBeats : 8);

            // Install a quality backward escape in the last ~10% of beats.
            // Prefer landings before the final fifth of the track so the outro cannot
            // trap the walk by always splicing to the start of the same ending section.
            var searchFrom = Math.Max(0, (int)(count * 0.9));
            var escapeBefore = Math.Max(minJump, (int)(count * 0.8));
            var bestSource = count - 1;
            var bestTarget = -1;
            var bestDistance = double.MaxValue;
            var bestEscapeSource = count - 1;
            var bestEscapeTarget = -1;
            var bestEscapeDistance = double.MaxValue;

            for (var source = count - 1; source >= searchFrom; source--)
            {
                for (var j = 0; j < source - minJump; j++)
                {
                    var distance = distances[source][j];

                    if (distance >= bestDistance && distance >= bestEscapeDistance)
                        continue;

                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestTarget = j;
                        bestSource = source;
                    }

                    if (j < escapeBefore && distance < bestEscapeDistance)
                    {
                        bestEscapeDistance = distance;
                        bestEscapeTarget = j;
                        bestEscapeSource = source;
                    }
                }
            }

            var useSource = bestEscapeTarget >= 0 ? bestEscapeSource : bestSource;
            var useTarget = bestEscapeTarget >= 0 ? bestEscapeTarget : bestTarget;
            var useDistance = bestEscapeTarget >= 0 ? bestEscapeDistance : bestDistance;

            if (useTarget < 0)
                return;

            if (graph.Beats[useSource].Neighbors.All(e => e.DestinationIndex != useTarget))
            {
                graph.Beats[useSource].Neighbors.Add(new BeatEdge
                {
                    DestinationIndex = useTarget,
                    Distance = useDistance
                });
            }
        }

        private static int FindLastBranchPoint(BeatGraph graph)
        {
            for (var i = graph.Beats.Count - 1; i >= 0; i--)
            {
                if (graph.Beats[i].Neighbors.Any(e => e.DestinationIndex < i))
                    return i;
            }

            return -1;
        }

        /// <summary>Prefer BeatThis downbeat flags for IndexInBar; fall back to bars list.</summary>
        private static void AssignDownbeatBarPositions(BeatGraph graph, TrackAnalysis analysis)
        {
            if (analysis.Beats == null || analysis.Beats.Count == 0)
                return;

            if (!analysis.Beats.Any(b => b.IsDownbeat))
                return;

            var indexInBar = 0;

            for (var i = 0; i < graph.Beats.Count; i++)
            {
                if (i > 0 && analysis.Beats[i].IsDownbeat)
                    indexInBar = 0;

                graph.Beats[i].IndexInBar = indexInBar;
                indexInBar++;
            }
        }

        private static void AssignBarPositions(BeatGraph graph, TrackAnalysis analysis)
        {
            if (analysis.Bars == null || analysis.Bars.Count == 0)
                return;

            var barIndex = 0;

            foreach (var beat in graph.Beats)
            {
                while (barIndex + 1 < analysis.Bars.Count &&
                       analysis.Bars[barIndex + 1].Start <= beat.StartSec + beat.DurationSec / 2)
                {
                    barIndex++;
                }

                var bar = analysis.Bars[barIndex];
                var beatsIntoBar = 0;

                foreach (var other in graph.Beats)
                {
                    if (other.Index >= beat.Index)
                        break;

                    if (other.StartSec >= bar.Start)
                        beatsIntoBar++;
                }

                beat.IndexInBar = beatsIntoBar;
            }
        }

        private static List<AnalysisSegment>[] ComputeOverlappingSegments(BeatGraph graph, TrackAnalysis analysis)
        {
            var result = new List<AnalysisSegment>[graph.Beats.Count];
            var segments = analysis.Segments ?? new List<AnalysisSegment>();

            for (var i = 0; i < graph.Beats.Count; i++)
            {
                var beat = graph.Beats[i];
                var beatEnd = beat.StartSec + beat.DurationSec;

                result[i] = segments
                    .Where(s => s.Start < beatEnd && s.Start + s.Duration > beat.StartSec)
                    .ToList();
            }

            return result;
        }

        private double BeatDistance(BeatNode beat1, BeatNode beat2,
            List<AnalysisSegment> segments1, List<AnalysisSegment> segments2)
        {
            if (segments1.Count == 0)
                return double.MaxValue;

            double sum = 0;

            for (var i = 0; i < segments1.Count; i++)
            {
                sum += i < segments2.Count
                    ? SegmentDistance(segments1[i], segments2[i])
                    : MissingSegmentPenalty;
            }

            var barPenalty = beat1.IndexInBar == beat2.IndexInBar ? 0 : DifferentBarPositionPenalty;
            return sum / segments1.Count + barPenalty;
        }

        private static double SegmentDistance(AnalysisSegment segment1, AnalysisSegment segment2)
        {
            var timbre = WeightedEuclidean(segment1.Timbre, segment2.Timbre) * TimbreWeight;
            var pitch = WeightedEuclidean(segment1.Pitches, segment2.Pitches) * PitchWeight;
            var loudStart = Math.Abs(segment1.LoudnessStart - segment2.LoudnessStart) * LoudStartWeight;
            var loudMax = Math.Abs(segment1.LoudnessMax - segment2.LoudnessMax) * LoudMaxWeight;
            // Duration/confidence dropped for Path B Classic direction; keep mild for legacy Spotify.
            var duration = Math.Abs(segment1.Duration - segment2.Duration) * DurationWeight * 0.25;
            var confidence = Math.Abs(segment1.Confidence - segment2.Confidence) * ConfidenceWeight * 0.25;
            return timbre + pitch + loudStart + loudMax + duration + confidence;
        }

        private static double WeightedEuclidean(IReadOnlyList<double> vector1, IReadOnlyList<double> vector2)
        {
            if (vector1 == null || vector2 == null)
                return 0;

            var length = Math.Min(vector1.Count, vector2.Count);
            double sum = 0;

            for (var i = 0; i < length; i++)
            {
                var delta = vector1[i] - vector2[i];
                sum += delta * delta;
            }

            return Math.Sqrt(sum);
        }
    }
}
