using System;
using System.Collections.Generic;
using SpotifyWPF.Model.Prediction;

namespace SpotifyWPF.Service.Prediction
{
    /// <summary>
    /// Builds a <see cref="BeatEnergyProfile"/> from a cached <see cref="TrackAnalysis"/>.
    ///
    /// Tier 2 (band detail from the analyze_track.py <c>beatEnergy</c> block) is preferred when
    /// present. Otherwise tier 1 derives a usable curve from data every analysis already has —
    /// segment loudness on both providers, plus the z-scored RMS row of Classic beat features when
    /// the local pipeline produced them. Old caches therefore degrade silently: no error, no
    /// re-analyze, no user action.
    /// </summary>
    public class BeatEnergyProfileBuilder
    {
        /// <summary>Beat-feature layout is [chroma(12) | MFCC 1..12 (12) | RMS-dB (1)] = 25 dims.</summary>
        private const int ClassicFeatureLength = 25;

        private const int ClassicRmsIndex = 24;

        /// <summary>Blend weights for tier 1: segment loudness carries level, z-RMS carries shape.</summary>
        private const double SegmentBlend = 0.65;

        private const double ZRmsBlend = 0.35;

        /// <summary>Floor on the percentile span so a heavily-limited master still spreads over 0–1.</summary>
        private const double MinDynamicRangeDb = 6.0;

        /// <summary>Echo-Nest loudnessMax − loudnessStart saturates as an attack measure around here.</summary>
        private const double AttackFullScaleDb = 12.0;

        public BeatEnergyProfile Build(TrackAnalysis analysis)
        {
            if (analysis?.Beats == null || analysis.Beats.Count == 0)
                return null;

            return analysis.HasBeatEnergy
                ? BuildTier2(analysis)
                : BuildTier1(analysis);
        }

        // ── Tier 1 ──────────────────────────────────────────────────────────────────────

        private BeatEnergyProfile BuildTier1(TrackAnalysis analysis)
        {
            var beatCount = analysis.Beats.Count;
            var segmentDb = new double[beatCount];
            var attack01 = new double[beatCount];
            var hasSegments = analysis.Segments != null && analysis.Segments.Count > 0;

            if (hasSegments)
                MapSegmentsOntoBeats(analysis, segmentDb, attack01);

            var energy01 = hasSegments
                ? NormalizeByPercentile(segmentDb)
                : new double[beatCount];

            var usedZRms = false;

            if (TryReadClassicRms(analysis, out var zRms))
            {
                usedZRms = true;

                for (var i = 0; i < beatCount; i++)
                {
                    // tanh keeps the z-score bounded without clipping the useful middle.
                    var z01 = 0.5 + (0.5 * Math.Tanh(zRms[i] / 2.0));
                    energy01[i] = hasSegments
                        ? (SegmentBlend * energy01[i]) + (ZRmsBlend * z01)
                        : z01;
                }
            }

            energy01 = MedianSmooth3(energy01);

            var description = hasSegments
                ? (usedZRms ? "segment loudness + z-RMS" : "segment loudness")
                : (usedZRms ? "z-RMS" : "flat (no loudness data)");

            return new BeatEnergyProfile
            {
                TrackId = analysis.TrackId,
                Tier = BeatEnergyProfile.TierDerived,
                SourceDescription = description,
                Energy01 = energy01,
                // No band split available: the attack proxy stands in for both, and is documented
                // as such so the gate can degrade to plain rising-loudness detection.
                OnsetStrength01 = attack01,
                Percussive01 = attack01,
                MedianEnergy01 = Median(energy01),
                MedianLowBand01 = 0
            };
        }

        /// <summary>
        /// Overlap-weighted average of segment loudness across each beat span. Works for both
        /// providers: the local sidecar emits 0.75 s segments on a 0.25 s hop (overlapping), Spotify
        /// emits contiguous ones.
        /// </summary>
        private static void MapSegmentsOntoBeats(TrackAnalysis analysis, double[] segmentDb, double[] attack01)
        {
            var segments = analysis.Segments;
            var beats = analysis.Beats;
            var cursor = 0;

            for (var i = 0; i < beats.Count; i++)
            {
                var beatStart = beats[i].Start;
                var beatEnd = beatStart + Math.Max(beats[i].Duration, 1e-4);

                // Segments are time-ordered; walk the cursor forward instead of rescanning.
                while (cursor > 0 && segments[cursor].Start + segments[cursor].Duration > beatStart)
                    cursor--;

                while (cursor < segments.Count &&
                       segments[cursor].Start + segments[cursor].Duration <= beatStart)
                    cursor++;

                double weightSum = 0;
                double loudnessSum = 0;
                double dominantWeight = 0;
                double dominantAttack = 0;

                for (var s = cursor; s < segments.Count; s++)
                {
                    var segment = segments[s];
                    var segStart = segment.Start;
                    var segEnd = segStart + segment.Duration;

                    if (segStart >= beatEnd)
                        break;

                    var overlap = Math.Min(beatEnd, segEnd) - Math.Max(beatStart, segStart);

                    if (overlap <= 0)
                        continue;

                    weightSum += overlap;
                    loudnessSum += overlap * segment.LoudnessMax;

                    if (overlap > dominantWeight)
                    {
                        dominantWeight = overlap;
                        dominantAttack = segment.LoudnessMax - segment.LoudnessStart;
                    }
                }

                if (weightSum > 0)
                {
                    segmentDb[i] = loudnessSum / weightSum;
                    attack01[i] = Clamp01(dominantAttack / AttackFullScaleDb);
                }
                else
                {
                    // Gap in the segment list — inherit the previous beat rather than punching a hole.
                    segmentDb[i] = i > 0 ? segmentDb[i - 1] : -60.0;
                    attack01[i] = i > 0 ? attack01[i - 1] : 0;
                }
            }
        }

        private static bool TryReadClassicRms(TrackAnalysis analysis, out double[] zRms)
        {
            zRms = null;
            var features = analysis.BeatFeatures;

            if (features == null || features.Count != analysis.Beats.Count || features.Count == 0)
                return false;

            var values = new double[features.Count];

            for (var i = 0; i < features.Count; i++)
            {
                var row = features[i];

                if (row == null || row.Count < ClassicFeatureLength)
                    return false;

                values[i] = row[ClassicRmsIndex];
            }

            zRms = values;
            return true;
        }

        // ── Tier 2 ──────────────────────────────────────────────────────────────────────

        private BeatEnergyProfile BuildTier2(TrackAnalysis analysis)
        {
            var block = analysis.BeatEnergy;
            var beatCount = analysis.Beats.Count;

            var low = NormalizeByPercentile(ToArray(block.LowDb, beatCount));
            var high = NormalizeByPercentile(ToArray(block.HighDb, beatCount));
            var mid = NormalizeByPercentile(ToArray(block.MidDb, beatCount));
            var lowMid = NormalizeByPercentile(ToArray(block.LowMidDb, beatCount));

            // Broadband energy weighted toward the bands a listener reads as "drive": kick and body
            // dominate, air contributes, mids matter least because vocals sit there whatever the
            // section is doing.
            var energy01 = new double[beatCount];

            for (var i = 0; i < beatCount; i++)
                energy01[i] = Clamp01((0.40 * low[i]) + (0.25 * lowMid[i]) + (0.15 * mid[i]) + (0.20 * high[i]));

            energy01 = MedianSmooth3(energy01);

            var percussive = NormalizeUnitRange(ToArray(block.PercussiveFraction, beatCount));
            var onset = NormalizeByPercentile(ToArray(block.OnsetStrength, beatCount));
            var brightness = NormalizeByPercentile(ToArray(block.CentroidHz, beatCount));

            return new BeatEnergyProfile
            {
                TrackId = analysis.TrackId,
                Tier = BeatEnergyProfile.TierAnalyzed,
                SourceDescription = "analyzed bands + HPSS",
                Energy01 = energy01,
                Percussive01 = percussive,
                OnsetStrength01 = onset,
                LowBand01 = low,
                HighBand01 = high,
                Brightness01 = brightness,
                MedianEnergy01 = Median(energy01),
                MedianLowBand01 = Median(low)
            };
        }

        private static double[] ToArray(List<double> values, int beatCount)
        {
            var result = new double[beatCount];

            if (values == null || values.Count == 0)
                return result;

            for (var i = 0; i < beatCount; i++)
                result[i] = i < values.Count ? values[i] : values[values.Count - 1];

            return result;
        }

        // ── Shared numerics ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Map to 0–1 using the track's OWN p5/p95 range, not a fixed dB window.
        ///
        /// This is the single most important line in the file. With a fixed scale (as the visualizer
        /// uses: (dB+60)/60) a quiet or heavily-compressed track sits near one end of the range for
        /// its entire duration, the PID error is ~constant, and the whole feature silently does
        /// nothing. Percentiles rather than min/max so one clipped transient cannot flatten the rest.
        /// </summary>
        private static double[] NormalizeByPercentile(double[] values)
        {
            var count = values.Length;
            var result = new double[count];

            if (count == 0)
                return result;

            var sorted = (double[])values.Clone();
            Array.Sort(sorted);

            var lo = Percentile(sorted, 0.05);
            var hi = Percentile(sorted, 0.95);
            var span = Math.Max(hi - lo, MinDynamicRangeDb);

            for (var i = 0; i < count; i++)
                result[i] = Clamp01((values[i] - lo) / span);

            return result;
        }

        /// <summary>For values already in 0–1 (HPSS fraction): clamp only, no re-scaling.</summary>
        private static double[] NormalizeUnitRange(double[] values)
        {
            var result = new double[values.Length];

            for (var i = 0; i < values.Length; i++)
                result[i] = Clamp01(values[i]);

            return result;
        }

        private static double Percentile(double[] sorted, double fraction)
        {
            if (sorted.Length == 0)
                return 0;

            if (sorted.Length == 1)
                return sorted[0];

            var position = fraction * (sorted.Length - 1);
            var lower = (int)Math.Floor(position);
            var upper = Math.Min(lower + 1, sorted.Length - 1);
            var weight = position - lower;

            return (sorted[lower] * (1 - weight)) + (sorted[upper] * weight);
        }

        private static double Median(double[] values)
        {
            if (values == null || values.Length == 0)
                return 0;

            var sorted = (double[])values.Clone();
            Array.Sort(sorted);
            return Percentile(sorted, 0.5);
        }

        /// <summary>
        /// 3-beat centred median on the energy curve only — kills single-beat segment artefacts.
        /// Onset strength is deliberately left unsmoothed: the buildup gate needs its spikiness.
        /// </summary>
        private static double[] MedianSmooth3(double[] values)
        {
            var count = values.Length;

            if (count < 3)
                return values;

            var result = new double[count];
            result[0] = values[0];
            result[count - 1] = values[count - 1];

            for (var i = 1; i < count - 1; i++)
            {
                var a = values[i - 1];
                var b = values[i];
                var c = values[i + 1];
                result[i] = Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
            }

            return result;
        }

        private static double Clamp01(double value) => Math.Max(0, Math.Min(1, value));
    }
}
