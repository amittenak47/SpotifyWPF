using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Threading;
using SpotifyWPF.Model.Prediction;

namespace SpotifyWPF.Service.Visual
{
    /// <summary>
    /// Concrete <see cref="IVisualEnergyProvider"/> driven by the cached
    /// <see cref="TrackAnalysis"/> (segment loudness + chroma and beat timestamps) instead of a
    /// live FFT. One dispatcher timer batches the equalizer/fractal update so all consumers read
    /// the same frame. Everything runs on the UI thread.
    /// </summary>
    public class VisualEnergyState : IVisualEnergyProvider
    {
        public const int BarCount = 144;

        /// <summary>Accessibility cap: beat spikes never slam the visuals to full brightness.</summary>
        private const double MaxBeatPulse = 0.85;

        private const double BeatPulseDecayMs = 230;

        /// <summary>How long a peak cap hangs at its max before cascading (classic Winamp feel).</summary>
        private const double PeakHoldMs = 280;

        /// <summary>Peak fall speed in units/sec once hold expires.</summary>
        private const double PeakFallPerSec = 0.85;

        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        private readonly double[] _barHeights = new double[BarCount];

        private readonly double[] _peakHeights = new double[BarCount];

        private readonly double[] _peakHoldMsLeft = new double[BarCount];

        private DispatcherTimer _timer;

        // Precomputed from the analysis so per-frame work stays trivial.
        private double[] _segmentStartsSec;

        private double[] _segmentLoudness01;

        private double[][] _segmentPitches;

        private double[] _beatStartsSec;

        private double _tempo = 120;

        private int _segmentCursor;

        private int _beatCursor = -1;

        private long _lastPositionMs;

        private double _positionStampMs;

        private bool _isPaused = true;

        private double _lastTickMs;

        private double _wavePhase;

        private double _globalEnergy;

        private double _beatPulse;

        public double GlobalEnergy => _globalEnergy;

        public double BeatPulse => _beatPulse;

        public IReadOnlyList<double> BarHeights => _barHeights;

        public IReadOnlyList<double> PeakHeights => _peakHeights;

        public event Action Updated;

        /// <summary>Feed the transport position; call whenever the player reports progress.</summary>
        public void SetTransport(long positionMs, bool isPaused)
        {
            _lastPositionMs = positionMs;
            _positionStampMs = Clock.Elapsed.TotalMilliseconds;
            _isPaused = isPaused;

            if (_segmentStartsSec != null)
                EnsureTimer();
        }

        /// <summary>Load (or replace) the analysis backing the synthesized spectrum.</summary>
        public void LoadAnalysis(TrackAnalysis analysis)
        {
            if (analysis == null || analysis.Segments == null || analysis.Segments.Count == 0)
            {
                Clear();
                return;
            }

            var count = analysis.Segments.Count;
            _segmentStartsSec = new double[count];
            _segmentLoudness01 = new double[count];
            _segmentPitches = new double[count][];

            for (var i = 0; i < count; i++)
            {
                var segment = analysis.Segments[i];
                _segmentStartsSec[i] = segment.Start;
                _segmentLoudness01[i] = LoudnessDbTo01(segment.LoudnessMax);

                var pitches = new double[12];

                if (segment.Pitches != null)
                {
                    for (var p = 0; p < 12 && p < segment.Pitches.Count; p++)
                        pitches[p] = Clamp01(segment.Pitches[p]);
                }

                _segmentPitches[i] = pitches;
            }

            var beatCount = analysis.Beats?.Count ?? 0;
            _beatStartsSec = new double[beatCount];

            for (var i = 0; i < beatCount; i++)
                _beatStartsSec[i] = analysis.Beats[i].Start;

            _tempo = analysis.Tempo > 20 ? analysis.Tempo : 120;
            _segmentCursor = 0;
            _beatCursor = -1;
            EnsureTimer();
        }

        /// <summary>Drop the analysis; energy decays to zero and the timer stops.</summary>
        public void Clear()
        {
            _segmentStartsSec = null;
            _segmentLoudness01 = null;
            _segmentPitches = null;
            _beatStartsSec = null;
            _globalEnergy = 0;
            _beatPulse = 0;
            Array.Clear(_barHeights, 0, _barHeights.Length);
            Array.Clear(_peakHeights, 0, _peakHeights.Length);
            Array.Clear(_peakHoldMsLeft, 0, _peakHoldMsLeft.Length);
            _timer?.Stop();
            Updated?.Invoke();
        }

        private void EnsureTimer()
        {
            if (_timer == null)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Render)
                {
                    Interval = TimeSpan.FromMilliseconds(33)
                };
                _timer.Tick += (_, __) => Tick();
            }

            if (!_timer.IsEnabled)
            {
                _lastTickMs = Clock.Elapsed.TotalMilliseconds;
                _timer.Start();
            }
        }

        private void Tick()
        {
            var now = Clock.Elapsed.TotalMilliseconds;
            var dtMs = Math.Max(1, Math.Min(200, now - _lastTickMs));
            _lastTickMs = now;

            if (_segmentStartsSec == null)
            {
                _timer.Stop();
                return;
            }

            var positionSec = EstimatedPositionMs(now) / 1000.0;
            var segment = FindCursor(_segmentStartsSec, positionSec, ref _segmentCursor);
            var loudness = segment >= 0 ? _segmentLoudness01[segment] : 0;
            var pitches = segment >= 0 ? _segmentPitches[segment] : null;

            UpdateBeatPulse(positionSec, loudness, dtMs);
            UpdateGlobalEnergy(_isPaused ? 0 : loudness);
            UpdateBars(_isPaused ? 0 : loudness, pitches, dtMs);
            UpdatePeaks(dtMs);

            // Fully idle (paused and everything decayed): skip notifications, it's all zeros.
            if (_isPaused && _globalEnergy < 0.003 && _beatPulse < 0.003)
                return;

            Updated?.Invoke();
        }

        private long EstimatedPositionMs(double nowMs)
        {
            var position = _lastPositionMs;

            if (!_isPaused)
                position += (long)(nowMs - _positionStampMs);

            return Math.Max(0, position);
        }

        private void UpdateBeatPulse(double positionSec, double loudness, double dtMs)
        {
            _beatPulse *= Math.Exp(-dtMs / BeatPulseDecayMs);

            if (_beatPulse < 0.001)
                _beatPulse = 0;

            var beats = _beatStartsSec;

            if (beats == null || beats.Length == 0 || _isPaused)
                return;

            var previousBeat = _beatCursor;
            var index = FindCursor(beats, positionSec, ref _beatCursor);

            if (index >= 0 && index != previousBeat)
                _beatPulse = Math.Min(MaxBeatPulse, Math.Max(_beatPulse, 0.35 + 0.5 * loudness));
        }

        private void UpdateGlobalEnergy(double target)
        {
            // Fast attack, slow release — shared decay envelope for every consumer.
            _globalEnergy += target > _globalEnergy
                ? (target - _globalEnergy) * 0.35
                : (target - _globalEnergy) * 0.08;

            if (_globalEnergy < 0.001)
                _globalEnergy = 0;
        }

        private void UpdateBars(double loudness, double[] pitches, double dtMs)
        {
            // Subtle traveling wave around the ring, paced by tempo.
            _wavePhase += dtMs / 1000.0 * (_tempo / 60.0) * Math.PI;

            if (_wavePhase > Math.PI * 2000)
                _wavePhase -= Math.PI * 2000;

            // Winamp-style envelope: near-instant attack, slower gravity fall.
            // Frame-rate independent decay (~half-life ~90 ms at 60 fps feel).
            var fall = Math.Pow(0.86, dtMs / 16.0);

            for (var i = 0; i < BarCount; i++)
            {
                // Spread the 12 chroma bins around the ring on the circle of fifths so adjacent
                // sectors don't mirror each other — reads like a spectrum, not a repeat pattern.
                var chroma = pitches != null ? pitches[i * 7 % 12] : 0;
                var wave = 0.5 + 0.5 * Math.Sin(_wavePhase + i * (Math.PI * 2 * 5 / BarCount));
                // Stronger chroma contrast so adjacent bins read as distinct frequencies.
                var target = loudness * (0.12 + 0.78 * chroma) +
                             loudness * 0.08 * wave +
                             _beatPulse * 0.22;
                target = Clamp01(Math.Pow(target, 0.88) * 1.18);

                var current = _barHeights[i];

                // Snap up (Winamp bounce), then fall with gravity — no soft lerp on attack.
                _barHeights[i] = target >= current
                    ? target
                    : current * fall;

                if (_barHeights[i] < 0.001)
                    _barHeights[i] = 0;
            }
        }

        /// <summary>Classic Winamp peaks: snap up with the bar, hold, then cascade down.</summary>
        private void UpdatePeaks(double dtMs)
        {
            for (var i = 0; i < BarCount; i++)
            {
                var bar = _barHeights[i];

                if (bar >= _peakHeights[i])
                {
                    _peakHeights[i] = bar;
                    _peakHoldMsLeft[i] = PeakHoldMs;
                    continue;
                }

                if (_peakHoldMsLeft[i] > 0)
                {
                    _peakHoldMsLeft[i] -= dtMs;
                    continue;
                }

                _peakHeights[i] -= PeakFallPerSec * (dtMs / 1000.0);

                if (_peakHeights[i] < bar)
                    _peakHeights[i] = bar;

                if (_peakHeights[i] < 0.001)
                    _peakHeights[i] = 0;
            }
        }

        /// <summary>Cursor-walking lookup: O(1) during playback, binary search after seeks.</summary>
        private static int FindCursor(double[] starts, double positionSec, ref int cursor)
        {
            if (starts.Length == 0)
                return -1;

            var index = cursor >= 0 && cursor < starts.Length ? cursor : 0;

            if (index > 0 && starts[index] > positionSec)
            {
                // Seek backwards: restart from a binary search.
                var lo = 0;
                var hi = starts.Length - 1;

                while (lo < hi)
                {
                    var mid = (lo + hi + 1) / 2;

                    if (starts[mid] <= positionSec)
                        lo = mid;
                    else
                        hi = mid - 1;
                }

                index = lo;
            }

            while (index + 1 < starts.Length && starts[index + 1] <= positionSec)
                index++;

            cursor = index;
            return starts[index] <= positionSec ? index : -1;
        }

        /// <summary>Map segment loudness (≈ -60..0 dB) into 0–1.</summary>
        private static double LoudnessDbTo01(double db)
        {
            return Clamp01((db + 60.0) / 60.0);
        }

        private static double Clamp01(double value)
        {
            return value < 0 ? 0 : value > 1 ? 1 : value;
        }
    }
}
