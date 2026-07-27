using System;
using System.Collections.Generic;
using SpotifyWPF.Model.Prediction;

namespace SpotifyWPF.Service.Prediction
{
    /// <summary>Target trajectory the controller steers the remix's realized energy toward.</summary>
    public enum EnergyTargetMode
    {
        /// <summary>Setpoint tracks a slow average of realized energy — "match this section".</summary>
        HoldCurrent,

        /// <summary>Fixed setpoint from settings.</summary>
        FlatSetpoint,

        /// <summary>Ramp from the level at reset toward the setpoint over N bars, then wrap.</summary>
        Arc
    }

    /// <summary>One controller evaluation, surfaced to the activity log and the tuning status line.</summary>
    public class EnergyPidSample
    {
        public int BeatIndex { get; set; }

        public double Measurement { get; set; }

        public double Setpoint { get; set; }

        public double Error { get; set; }

        public double P { get; set; }

        public double I { get; set; }

        public double D { get; set; }

        public double Output { get; set; }

        public bool GateActive { get; set; }

        public int Tier { get; set; }
    }

    /// <summary>
    /// Energy-aware hop controller: a PID over (target energy − realized energy) plus a feedforward
    /// buildup gate.
    ///
    /// On the honesty of the PID framing — worth reading before tuning the gains. The "plant" here
    /// is not a system with dynamics; it is a discrete choice among whichever handful of graph edges
    /// exist, measured against a curve that is fully precomputed and can be read ahead in. So:
    ///
    ///   • Kp earns its keep. "We are below the level this section has been sitting at" is exactly
    ///     the signal that should bias toward hotter landings and shorten the wait.
    ///   • Ki is marginal — it accumulates against setpoints the graph often cannot reach. Small.
    ///   • Kd is close to useless AND symmetric: it damps a drop-out exactly as hard as a build-up.
    ///     Default 0.
    ///
    /// What actually stops hops landing in the middle of a riser is <see cref="SuppressHops"/> — a
    /// directional, lookahead detector, not the derivative term. Exploiting lookahead is the one
    /// real advantage this design has over a live-DSP controller; it would be a shame to leave it on
    /// the table in favour of a term that only reacts once you are already mid-riser.
    /// </summary>
    public class EnergyPidController : IHopBias
    {
        private readonly BeatEnergyProfile _profile;

        private readonly JukeboxSettings _settings;

        private readonly Random _random;

        /// <summary>Memoized gate detection per beat so repeated probes stay consistent.</summary>
        private readonly Dictionary<int, bool> _gateCache = new Dictionary<int, bool>();

        private double _y;

        private double _r;

        private double _integral;

        private double _dFilt;

        private double _lastY;

        private double _arcStart;

        private int _beatsSinceReset;

        private int _lastUpdatedBeat = -1;

        private EnergyPidSample _lastSample;

        public EnergyPidController(BeatEnergyProfile profile, JukeboxSettings settings, int? randomSeed = null)
        {
            _profile = profile ?? throw new ArgumentNullException(nameof(profile));
            _settings = settings ?? JukeboxSettings.CreateDefaults();
            _random = randomSeed.HasValue ? new Random(randomSeed.Value) : new Random();
        }

        public double Output { get; private set; }

        public bool BuildupGateActive { get; private set; }

        public EnergyPidSample LastSample => _lastSample;

        public int Tier => _profile.Tier;

        private EnergyTargetMode TargetMode
        {
            get
            {
                var mode = (_settings.EnergyTargetMode ?? "hold").Trim();

                if (mode.Equals("flat", StringComparison.OrdinalIgnoreCase))
                    return EnergyTargetMode.FlatSetpoint;

                return mode.Equals("arc", StringComparison.OrdinalIgnoreCase)
                    ? EnergyTargetMode.Arc
                    : EnergyTargetMode.HoldCurrent;
            }
        }

        /// <summary>
        /// Full reset: zeroes integral and derivative history and reseeds both filters from the
        /// profile. Use on track change and on user scrubs — a scrub is a change of intent, so the
        /// accumulated "I keep failing to reach the target" evidence is meaningless.
        /// </summary>
        public void Reset(int beatIndex)
        {
            var seed = _profile.EnergyAt(beatIndex);
            _y = seed;
            _lastY = seed;
            _r = TargetMode == EnergyTargetMode.FlatSetpoint ? Clamp01(_settings.EnergyFlatSetpoint) : seed;
            _arcStart = seed;
            _integral = 0;
            _dFilt = 0;
            _beatsSinceReset = 0;
            _lastUpdatedBeat = -1;
            Output = 0;
            BuildupGateActive = false;
            _lastSample = null;
        }

        /// <summary>
        /// Landing reseed: the measurement jumps discontinuously to a new part of the track, but the
        /// integral and the held setpoint are still the intent we are steering toward. Keep them.
        /// </summary>
        public void Reseed(int beatIndex)
        {
            var seed = _profile.EnergyAt(beatIndex);
            _y = seed;
            _lastY = seed;
            _lastUpdatedBeat = -1;

            if (TargetMode == EnergyTargetMode.HoldCurrent && _r <= 0)
                _r = seed;
        }

        /// <summary>A hop is the actuator firing, so the accumulated error evidence is now stale.</summary>
        public void NotifyHopFired(int fromBeatIndex, int toBeatIndex)
        {
            _integral *= 0.5;
        }

        /// <summary>
        /// Advance one beat. Idempotent for a repeated index: the transport reports position roughly
        /// every 250 ms while a beat at 160 BPM lasts 375 ms, so the same beat is very often seen
        /// twice and must not be integrated twice.
        /// </summary>
        public EnergyPidSample UpdateForBeat(int beatIndex)
        {
            if (beatIndex < 0)
                return _lastSample;

            if (_lastSample != null && beatIndex <= _lastUpdatedBeat)
                return _lastSample;

            _lastUpdatedBeat = beatIndex;
            _beatsSinceReset++;

            // Measurement: low-pass the realized energy.
            var alphaY = SmoothingAlpha(_settings.EnergyMeasureSmoothBeats);
            _lastY = _y;
            _y += alphaY * (_profile.EnergyAt(beatIndex) - _y);

            _r = ComputeSetpoint();
            var error = _r - _y;

            var p = _settings.EnergyKp * error;

            // Derivative on the MEASUREMENT, not the error, and low-passed: a setpoint or mode
            // change then produces no derivative kick at all. Negative sign so rising energy damps.
            var alphaD = SmoothingAlpha(3);
            _dFilt += alphaD * (-(_y - _lastY) - _dFilt);
            var d = _settings.EnergyKd * _dFilt;

            // Conditional integration: freeze while saturated and still pushing further into
            // saturation, then hard-clamp. Without both, a setpoint the graph cannot reach parks the
            // integral at its limit and the controller stops responding to anything else.
            var candidate = _integral + (_settings.EnergyKi * error);
            var unsaturated = p + candidate + d;

            if (Math.Abs(unsaturated) < 1.0 || Math.Sign(error) != Math.Sign(unsaturated))
            {
                var clamp = Math.Abs(_settings.EnergyIntegralClamp);
                _integral = Math.Max(-clamp, Math.Min(clamp, candidate));
            }

            Output = Math.Max(-1, Math.Min(1, p + _integral + d));
            BuildupGateActive = DetectBuildup(beatIndex);

            _lastSample = new EnergyPidSample
            {
                BeatIndex = beatIndex,
                Measurement = _y,
                Setpoint = _r,
                Error = error,
                P = p,
                I = _integral,
                D = d,
                Output = Output,
                GateActive = BuildupGateActive,
                Tier = _profile.Tier
            };

            return _lastSample;
        }

        private double ComputeSetpoint()
        {
            switch (TargetMode)
            {
                case EnergyTargetMode.FlatSetpoint:
                    return Clamp01(_settings.EnergyFlatSetpoint);

                case EnergyTargetMode.Arc:
                {
                    // Anchored to beats-since-reset and wrapped, so an infinite loop does not simply
                    // pin at the ceiling forever and stop meaning anything.
                    var span = Math.Max(1, _settings.EnergyArcBars) * 4;
                    var phase = (_beatsSinceReset % (span * 2)) / (double)span;
                    var fraction = phase <= 1 ? phase : 2 - phase;
                    return Clamp01(_arcStart + ((Clamp01(_settings.EnergyFlatSetpoint) - _arcStart) * fraction));
                }

                default:
                {
                    // Hold: r and y are the same signal at different bandwidths, so e = r − y is a
                    // band-pass of the energy curve — "am I below the level this section has been
                    // sitting at?". That is precisely the requested semantics, and it is why this
                    // is not the degenerate r == y it looks like at first glance.
                    var alphaR = SmoothingAlpha(_settings.EnergyHoldSmoothBeats);
                    return _r + (alphaR * (_y - _r));
                }
            }
        }

        // ── IHopBias ────────────────────────────────────────────────────────────────────

        public double BranchChanceMultiplier(int beatIndex)
        {
            if (!_settings.EnergyControlEnabled)
                return 1;

            return Math.Max(0, 1 + (_settings.EnergyHopChanceGain * Output));
        }

        public bool SuppressHops(int beatIndex)
        {
            if (!_settings.EnergyControlEnabled || !_settings.BuildupGateEnabled)
                return false;

            if (!DetectBuildup(beatIndex))
                return false;

            var strength = Clamp01(_settings.BuildupGateStrength);
            return strength >= 1.0 || _random.NextDouble() < strength;
        }

        public double EnergyScore(int fromBeatIndex, int toBeatIndex)
        {
            // Compared against the smoothed realized level rather than the exit beat: the question
            // is "does this landing move us toward the target", not "is it louder than right here".
            var deltaEnergy = _profile.EnergyAt(toBeatIndex) - _y;
            var deltaPercussive = _profile.Percussive01 != null
                ? _profile.PercussiveAt(toBeatIndex) - _profile.PercussiveAt(fromBeatIndex)
                : 0;

            var score = (0.75 * deltaEnergy) + (0.25 * deltaPercussive);
            return Math.Max(-1, Math.Min(1, score));
        }

        // ── Buildup gate ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Detection is a pure function of the precomputed profile — no controller feedback state.
        /// That is what makes it valid at simulated future beats inside PlanNextJump, where no
        /// feedback state exists. Memoized so repeated probes for the same beat agree.
        /// </summary>
        private bool DetectBuildup(int beatIndex)
        {
            if (_profile.BeatCount == 0)
                return false;

            if (_gateCache.TryGetValue(beatIndex, out var cached))
                return cached;

            var window = Math.Max(2, _settings.BuildupGateLookaheadBeats);
            var result = _profile.HasBandDetail
                ? DetectBuildupTier2(beatIndex, window)
                : DetectBuildupTier1(beatIndex, window);

            _gateCache[beatIndex] = result;
            return result;
        }

        /// <summary>
        /// Full detector. The canonical riser is highs and transient density climbing while the kick
        /// is absent or falling away — a filtered sweep or a snare roll. Requiring all three is what
        /// keeps a merely-loud chorus from tripping the gate: a chorus has high onset strength but
        /// flat onset *slope*, and its kick is present, not receding.
        /// </summary>
        private bool DetectBuildupTier2(int beatIndex, int window)
        {
            var onsetSlope = Slope(_profile.OnsetStrength01, beatIndex, window);
            var highSlope = Slope(_profile.HighBand01, beatIndex, window);
            var brightSlope = Slope(_profile.Brightness01, beatIndex, window);
            var lowSlope = Slope(_profile.LowBand01, beatIndex, window);

            var rise = Clamp01(onsetSlope / 0.04) * Clamp01(highSlope / 0.04);
            var bright = Clamp01(brightSlope / 0.03);
            var kickOut = Math.Max(
                Clamp01(-lowSlope / 0.03),
                Clamp01((_profile.MedianLowBand01 - _profile.LowBandAt(beatIndex)) / 0.2));

            var g = rise * (0.4 + (0.6 * bright)) * (0.3 + (0.7 * kickOut));
            return g > 0.45;
        }

        /// <summary>
        /// Tier-1 degrade: no bands and no centroid, so this is a plain monotonic rising-loudness
        /// detector. It will also fire on a chorus arriving. That is the honest cost of not
        /// re-analyzing, and the tuning InfoTip says so.
        /// </summary>
        private bool DetectBuildupTier1(int beatIndex, int window)
        {
            var rise = Slope(_profile.Energy01, beatIndex, window);

            if (rise <= 0.02)
                return false;

            var positive = 0;
            var steps = 0;

            for (var i = beatIndex; i < beatIndex + window - 1; i++)
            {
                if (_profile.EnergyAt(i + 1) > _profile.EnergyAt(i))
                    positive++;

                steps++;
            }

            if (steps == 0 || positive / (double)steps <= 0.7)
                return false;

            var headroom = MaxOf(_profile.Energy01) - _profile.EnergyAt(beatIndex);
            return headroom > 0.15;
        }

        /// <summary>Least-squares slope per beat over a forward window (clamped at the track end).</summary>
        private static double Slope(double[] values, int start, int window)
        {
            if (values == null || values.Length == 0)
                return 0;

            var count = 0;
            double sumX = 0, sumY = 0, sumXy = 0, sumXx = 0;

            for (var i = 0; i < window; i++)
            {
                var index = start + i;

                if (index < 0 || index >= values.Length)
                    break;

                double x = i;
                var y = values[index];
                sumX += x;
                sumY += y;
                sumXy += x * y;
                sumXx += x * x;
                count++;
            }

            if (count < 2)
                return 0;

            var denominator = (count * sumXx) - (sumX * sumX);

            return Math.Abs(denominator) < 1e-9
                ? 0
                : ((count * sumXy) - (sumX * sumY)) / denominator;
        }

        private static double MaxOf(double[] values)
        {
            if (values == null || values.Length == 0)
                return 0;

            var max = values[0];

            for (var i = 1; i < values.Length; i++)
            {
                if (values[i] > max)
                    max = values[i];
            }

            return max;
        }

        /// <summary>One-pole coefficient for an N-beat time constant.</summary>
        private static double SmoothingAlpha(int beats) =>
            1 - Math.Exp(-1.0 / Math.Max(1, beats));

        private static double Clamp01(double value) => Math.Max(0, Math.Min(1, value));
    }
}
