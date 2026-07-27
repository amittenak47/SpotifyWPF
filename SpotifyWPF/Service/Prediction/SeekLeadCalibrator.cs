using System;
using System.Collections.Generic;

namespace SpotifyWPF.Service.Prediction
{
    /// <summary>
    /// Slow PI loop that learns how late the transport actually fires an armed jump, and trims the
    /// seek lead to compensate. This is the piece that genuinely improves hop *sync*.
    ///
    /// The measurement is <c>FiredAtMs − TriggerMs</c>, taken where the armed action reports back.
    /// That is exactly the quantity SeekLeadMs compensates for, both transports already report it,
    /// and it is ~0 on Local WAV (the seek happens in-process on the same tick) — so the loop
    /// naturally does nothing there without needing a special case.
    ///
    /// Deliberately NOT derived from post-seek position deltas: that would mean subtracting
    /// wall-clock elapsed time from the SDK's own latency-delayed position report, where the noise
    /// comfortably exceeds the signal.
    ///
    /// This never writes the user's SeekLeadMs. It only produces <see cref="TrimMs"/>, which is
    /// stored separately and added at seek time.
    /// </summary>
    public class SeekLeadCalibrator
    {
        /// <summary>Samples before the loop is allowed to move at all.</summary>
        private const int MinimumSamples = 5;

        private const int WindowSize = 5;

        /// <summary>Beyond this, the sample is a pause/stall artefact rather than latency.</summary>
        private const double OutlierMs = 1000;

        private const double ProportionalGain = 0.2;

        private const double IntegralGain = 0.02;

        private const double IntegralClampMs = 400;

        /// <summary>Per-hop slew limit, so one bad round cannot yank the timing.</summary>
        private const double MaxStepMs = 10;

        private readonly Queue<double> _recent = new Queue<double>();

        private double _integral;

        private double _trim;

        private readonly int _maxTrimMs;

        public SeekLeadCalibrator(int maxTrimMs)
        {
            _maxTrimMs = Math.Max(0, maxTrimMs);
        }

        public int TrimMs => (int)Math.Round(_trim);

        public int SampleCount { get; private set; }

        public void Reset()
        {
            _recent.Clear();
            _integral = 0;
            _trim = 0;
            SampleCount = 0;
        }

        /// <summary>Feed one fired hop. Returns true when <see cref="TrimMs"/> changed.</summary>
        public bool NotifyHopFired(long plannedTriggerMs, long actualFiredAtMs)
        {
            var error = actualFiredAtMs - (double)plannedTriggerMs;

            if (Math.Abs(error) > OutlierMs)
                return false;

            _recent.Enqueue(error);

            while (_recent.Count > WindowSize)
                _recent.Dequeue();

            SampleCount++;

            if (SampleCount < MinimumSamples || _recent.Count < WindowSize)
                return false;

            // Median over the window: one stalled UI tick should not move the trim.
            var window = _recent.ToArray();
            Array.Sort(window);
            var median = window[window.Length / 2];

            _integral = Math.Max(-IntegralClampMs, Math.Min(IntegralClampMs, _integral + median));

            var raw = (ProportionalGain * median) + (IntegralGain * _integral);
            var step = Math.Max(-MaxStepMs, Math.Min(MaxStepMs, raw));
            var before = TrimMs;

            _trim = Math.Max(0, Math.Min(_maxTrimMs, _trim + step));

            return TrimMs != before;
        }
    }
}
