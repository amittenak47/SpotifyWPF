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

        /// <summary>Similarity threshold the tuning loop settled on.</summary>
        public double BranchDistanceThreshold { get; set; }

        /// <summary>Latest beat that has a backward branch — the "point of no return" guard.</summary>
        public int LastBranchPointIndex { get; set; }

        public int TotalBranchCount => Beats.Sum(b => b.Neighbors.Count);
    }

    /// <summary>
    /// Port of the Infinite Jukebox beat-similarity graph (after rigdern/InfiniteJukeboxAlgorithm,
    /// itself extracted from the Echo Nest jukebox player). For every beat it builds a feature view
    /// from the segments overlapping that beat (pitches, timbre, loudness, duration, confidence,
    /// position in bar) and connects beats whose pairwise segment distance falls under a threshold.
    /// The threshold is auto-tuned upward until enough branches exist for an "infinite" feel.
    /// </summary>
    public class BeatGraphBuilder
    {
        /// <summary>Max outgoing branches kept per beat (nearest first).</summary>
        public int MaxNeighbors { get; set; } = 4;

        /// <summary>Hard ceiling for the tuning loop; matches the original's maxBranchDistance.</summary>
        public double MaxBranchDistance { get; set; } = 80;

        /// <summary>Tuning starts tight and relaxes: threshold candidates.</summary>
        public double InitialBranchDistance { get; set; } = 15;

        public double BranchDistanceStep { get; set; } = 5;

        /// <summary>Tuning goal: this fraction of beats should have at least one branch.</summary>
        public double TargetBranchableBeatRatio { get; set; } = 0.1;

        /// <summary>Branches shorter than this many beats apart sound like stutters; skip them.</summary>
        public int MinimumJumpDistanceInBeats { get; set; } = 4;

        // Distance weights from the original algorithm.
        private const double TimbreWeight = 1;
        private const double PitchWeight = 10;
        private const double LoudStartWeight = 1;
        private const double LoudMaxWeight = 1;
        private const double DurationWeight = 100;
        private const double ConfidenceWeight = 1;

        /// <summary>Distance charged for a missing counterpart segment.</summary>
        private const double MissingSegmentPenalty = 100;

        /// <summary>Penalty for beats sitting at different positions within their bars.</summary>
        private const double DifferentBarPositionPenalty = 100;

        public BeatGraph Build(TrackAnalysis analysis, JukeboxSettings settings = null)
        {
            settings = settings ?? JukeboxSettings.CreateDefaults();
            var maxDistance = Math.Max(InitialBranchDistance, settings.BranchSimilarityThresholdMax);

            if (analysis == null)
                throw new ArgumentNullException(nameof(analysis));

            if (analysis.Beats == null || analysis.Beats.Count == 0)
                throw new InvalidOperationException("Analysis has no beats.");

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

            AssignBarPositions(graph, analysis);

            var overlapping = ComputeOverlappingSegments(graph, analysis);

            // Pairwise beat distances (symmetric); n is a few hundred, so O(n²) is fine.
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

            // Relax the threshold until enough beats can branch (the original's dial, automated).
            var threshold = InitialBranchDistance;

            while (true)
            {
                ConnectEdges(graph, distances, threshold);

                var branchableBeats = graph.Beats.Count(b => b.Neighbors.Count > 0);

                if (branchableBeats >= Math.Max(2, count * TargetBranchableBeatRatio) ||
                    threshold >= maxDistance)
                {
                    break;
                }

                threshold += BranchDistanceStep;
            }

            graph.BranchDistanceThreshold = Math.Min(threshold, maxDistance);

            EnsureLastEdge(graph, distances);

            graph.LastBranchPointIndex = FindLastBranchPoint(graph);

            return graph;
        }

        private void ConnectEdges(BeatGraph graph, double[][] distances, double threshold)
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

                graph.Beats[i].Neighbors.AddRange(
                    edges.OrderBy(e => e.Distance).Take(MaxNeighbors));
            }
        }

        /// <summary>
        /// The original's addLastEdge: if the tail of the song cannot branch backwards, playback
        /// would inevitably run off the end. Give the last branchable region a guaranteed edge to
        /// its most similar earlier beat, ignoring the threshold.
        /// </summary>
        private void EnsureLastEdge(BeatGraph graph, double[][] distances)
        {
            var count = graph.Beats.Count;
            var lastBranchPoint = FindLastBranchPoint(graph);

            // Backward branch already exists in the last quarter of the song — nothing to fix.
            if (lastBranchPoint >= count * 3 / 4)
                return;

            var sourceIndex = count - 1;
            var bestTarget = -1;
            var bestDistance = double.MaxValue;

            for (var j = 0; j < sourceIndex - MinimumJumpDistanceInBeats; j++)
            {
                if (distances[sourceIndex][j] < bestDistance)
                {
                    bestDistance = distances[sourceIndex][j];
                    bestTarget = j;
                }
            }

            if (bestTarget >= 0)
            {
                graph.Beats[sourceIndex].Neighbors.Add(new BeatEdge
                {
                    DestinationIndex = bestTarget,
                    Distance = bestDistance
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
            var duration = Math.Abs(segment1.Duration - segment2.Duration) * DurationWeight;
            var confidence = Math.Abs(segment1.Confidence - segment2.Confidence) * ConfidenceWeight;

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
