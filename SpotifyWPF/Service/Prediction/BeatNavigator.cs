using System;
using System.Collections.Generic;
using System.Linq;

namespace SpotifyWPF.Service.Prediction
{
    /// <summary>A planned seek: when playback reaches TriggerMs, seek to SeekToMs.</summary>
    public class JukeboxJump
    {
        public int FromBeatIndex { get; set; }

        public int TargetBeatIndex { get; set; }

        public long TriggerMs { get; set; }

        public long SeekToMs { get; set; }
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
        private const double MinRandomBranchChance = 0.18;

        private const double MaxRandomBranchChance = 0.50;

        private const double RandomBranchChanceDelta = 0.018;

        private readonly Random _random;

        private double _currentBranchChance = MinRandomBranchChance;

        public BeatGraph Graph { get; }

        public BeatNavigator(BeatGraph graph, int? randomSeed = null)
        {
            Graph = graph ?? throw new ArgumentNullException(nameof(graph));
            _random = randomSeed.HasValue ? new Random(randomSeed.Value) : new Random();
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

                // Point of no return: always branch backwards here rather than running off the end.
                if (i == Graph.LastBranchPointIndex)
                {
                    var backwardEdge = ChooseEdge(beat.Neighbors.Where(e => e.DestinationIndex < i).ToList());

                    if (backwardEdge != null)
                        return MakeJump(i, backwardEdge);
                }

                if (beat.Neighbors.Count == 0)
                    continue;

                if (_random.NextDouble() < _currentBranchChance)
                {
                    var edge = ChooseEdge(beat.Neighbors);

                    if (edge != null)
                    {
                        _currentBranchChance = MinRandomBranchChance;
                        return MakeJump(i, edge);
                    }
                }
                else
                {
                    _currentBranchChance = Math.Min(MaxRandomBranchChance,
                        _currentBranchChance + RandomBranchChanceDelta);
                }
            }

            return null;
        }

        private BeatEdge ChooseEdge(IReadOnlyList<BeatEdge> edges)
        {
            if (edges == null || edges.Count == 0)
                return null;

            return edges[_random.Next(edges.Count)];
        }

        private JukeboxJump MakeJump(int fromIndex, BeatEdge edge)
        {
            var target = Graph.Beats[edge.DestinationIndex];

            return new JukeboxJump
            {
                FromBeatIndex = fromIndex,
                TargetBeatIndex = edge.DestinationIndex,
                TriggerMs = Graph.Beats[fromIndex].EndMs,
                SeekToMs = target.StartMs
            };
        }
    }
}
