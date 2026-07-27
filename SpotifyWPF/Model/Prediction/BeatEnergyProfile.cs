using System;

namespace SpotifyWPF.Model.Prediction
{
    /// <summary>
    /// Per-beat energy curve for a track, on a 0–1 scale normalized to the track's own dynamic
    /// range. This is the measurement the energy PID controls against, and the lookahead the
    /// buildup gate inspects. It is deliberately separate from <see cref="TrackAnalysis.BeatFeatures"/>:
    /// those are z-scored and feed the similarity metric, so anything mixed into them would change
    /// graph topology and invalidate every tuned preset.
    ///
    /// Tier 1 is derived from data already cached (segment loudness, plus z-scored RMS when Classic
    /// features exist) and therefore works on the Spotify audio-analysis path too. Tier 2 comes from
    /// the <c>beatEnergy</c> block emitted by tools/analyze_track.py and adds band detail.
    /// </summary>
    public class BeatEnergyProfile
    {
        /// <summary>1 = derived from cached loudness. 2 = analyzed band detail from beatEnergy.</summary>
        public const int TierDerived = 1;

        public const int TierAnalyzed = 2;

        public string TrackId { get; set; }

        public int Tier { get; set; } = TierDerived;

        /// <summary>Human-readable source, used in the build log line.</summary>
        public string SourceDescription { get; set; }

        /// <summary>Primary controller measurement, one entry per beat, 0–1.</summary>
        public double[] Energy01 { get; set; }

        /// <summary>Percussive presence 0–1. Real HPSS on tier 2; an attack proxy on tier 1.</summary>
        public double[] Percussive01 { get; set; }

        /// <summary>Attack / transient strength 0–1. Deliberately not smoothed — the gate needs the spikes.</summary>
        public double[] OnsetStrength01 { get; set; }

        /// <summary>Kick band (~20–120 Hz) 0–1. Tier 2 only; null otherwise.</summary>
        public double[] LowBand01 { get; set; }

        /// <summary>Hat band (~6–12 kHz) 0–1. Tier 2 only; null otherwise.</summary>
        public double[] HighBand01 { get; set; }

        /// <summary>Spectral centroid mapped to 0–1. Tier 2 only; null otherwise.</summary>
        public double[] Brightness01 { get; set; }

        public double MedianEnergy01 { get; set; }

        public double MedianLowBand01 { get; set; }

        public int BeatCount => Energy01 == null ? 0 : Energy01.Length;

        /// <summary>True when band-resolved features are present (tier 2), enabling the full gate.</summary>
        public bool HasBandDetail => LowBand01 != null && HighBand01 != null && Brightness01 != null;

        public double EnergyAt(int beatIndex) => ValueAt(Energy01, beatIndex);

        public double PercussiveAt(int beatIndex) => ValueAt(Percussive01, beatIndex);

        public double OnsetAt(int beatIndex) => ValueAt(OnsetStrength01, beatIndex);

        public double LowBandAt(int beatIndex) => ValueAt(LowBand01, beatIndex);

        public double HighBandAt(int beatIndex) => ValueAt(HighBand01, beatIndex);

        public double BrightnessAt(int beatIndex) => ValueAt(Brightness01, beatIndex);

        /// <summary>Clamped accessor: the navigator simulates past the last beat during end-loop walks.</summary>
        private static double ValueAt(double[] values, int beatIndex)
        {
            if (values == null || values.Length == 0)
                return 0;

            if (beatIndex < 0)
                return values[0];

            return beatIndex >= values.Length ? values[values.Length - 1] : values[beatIndex];
        }
    }
}
