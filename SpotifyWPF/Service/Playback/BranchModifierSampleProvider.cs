using System;
using NAudio.Wave;

namespace SpotifyWPF.Service.Playback
{
    /// <summary>
    /// Lightweight Local-WAV effect chain for branch modifiers: 3-band shelving EQ approximation,
    /// gain, and soft-clip drive. Applied only on the Local WAV transport.
    /// </summary>
    internal sealed class BranchModifierSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly object _gate = new object();

        private bool _active;
        private float _gain = 1f;
        private float _drive;
        private float _lowGain = 1f;
        private float _midGain = 1f;
        private float _highGain = 1f;

        // One-pole low/high split state per channel (max 2).
        private readonly float[] _lpState = new float[2];
        private readonly float[] _hpState = new float[2];

        public BranchModifierSampleProvider(ISampleProvider source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
        }

        public WaveFormat WaveFormat => _source.WaveFormat;

        public void Clear()
        {
            lock (_gate)
            {
                _active = false;
                _gain = 1f;
                _drive = 0f;
                _lowGain = 1f;
                _midGain = 1f;
                _highGain = 1f;
            }
        }

        /// <summary>Apply modifier parameters. stretch (0–1) scales wetness of EQ/drive/gain.</summary>
        public void Apply(double eqLowDb, double eqMidDb, double eqHighDb, double gainDb, double drive,
            double stretch)
        {
            var wet = Clamp01(stretch);
            lock (_gate)
            {
                _active = wet > 0.01 || Math.Abs(eqLowDb) > 0.01 || Math.Abs(eqMidDb) > 0.01 ||
                          Math.Abs(eqHighDb) > 0.01 || Math.Abs(gainDb) > 0.01 || drive > 0.01;
                _lowGain = DbToLinear(eqLowDb * wet);
                _midGain = DbToLinear(eqMidDb * wet);
                _highGain = DbToLinear(eqHighDb * wet);
                _gain = DbToLinear(gainDb * wet);
                _drive = (float)(Clamp01(drive) * wet);
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            var read = _source.Read(buffer, offset, count);
            if (read <= 0)
                return read;

            bool active;
            float gain, drive, lowG, midG, highG;
            lock (_gate)
            {
                active = _active;
                gain = _gain;
                drive = _drive;
                lowG = _lowGain;
                midG = _midGain;
                highG = _highGain;
            }

            if (!active)
                return read;

            var channels = WaveFormat.Channels;
            if (channels <= 0)
                return read;

            // ~250 Hz / ~4 kHz crossover at 44.1k ≈ coefficients below; scale with sample rate.
            var sr = WaveFormat.SampleRate > 0 ? WaveFormat.SampleRate : 44100;
            var lpAlpha = 1f - (float)Math.Exp(-2.0 * Math.PI * 250.0 / sr);
            var hpAlpha = 1f - (float)Math.Exp(-2.0 * Math.PI * 4000.0 / sr);

            for (var i = 0; i < read; i++)
            {
                var ch = i % channels;
                if (ch >= 2)
                    ch = ch % 2;

                var x = buffer[offset + i];

                // Low shelf path
                _lpState[ch] += lpAlpha * (x - _lpState[ch]);
                var low = _lpState[ch];

                // High shelf path (one-pole high-pass approx)
                _hpState[ch] += hpAlpha * (x - _hpState[ch]);
                var high = x - _hpState[ch];
                var mid = x - low - high;

                var y = low * lowG + mid * midG + high * highG;
                y *= gain;

                if (drive > 0.001f)
                {
                    // Soft clip / mild saturation
                    var k = 1f + drive * 4f;
                    y = (float)Math.Tanh(y * k) / (float)Math.Tanh(k);
                }

                buffer[offset + i] = y;
            }

            return read;
        }

        private static float DbToLinear(double db) => (float)Math.Pow(10.0, db / 20.0);

        private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
    }
}
