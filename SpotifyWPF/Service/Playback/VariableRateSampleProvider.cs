using NAudio.Wave;

namespace SpotifyWPF.Service.Playback
{
    /// <summary>
    /// Simple hold-to-scan rate control: consumes source samples faster/slower (pitch shifts with rate).
    /// </summary>
    internal sealed class VariableRateSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;

        private float[] _scratch = new float[4096];

        private double _rate = 1.0;

        public VariableRateSampleProvider(ISampleProvider source)
        {
            _source = source ?? throw new System.ArgumentNullException(nameof(source));
        }

        public WaveFormat WaveFormat => _source.WaveFormat;

        public double Rate
        {
            get => _rate;
            set => _rate = value < 0.25 ? 0.25 : value > 4.0 ? 4.0 : value;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            if (count <= 0)
                return 0;

            if (System.Math.Abs(_rate - 1.0) < 0.02)
                return _source.Read(buffer, offset, count);

            var channels = WaveFormat.Channels;
            var framesNeeded = count / channels;
            var sourceFrames = (int)System.Math.Ceiling(framesNeeded * _rate) + 2;
            var sourceSamples = sourceFrames * channels;

            if (_scratch.Length < sourceSamples)
                _scratch = new float[sourceSamples];

            var read = _source.Read(_scratch, 0, sourceSamples);

            if (read <= 0)
                return 0;

            var availableFrames = read / channels;
            var outFrames = 0;

            for (var frame = 0; frame < framesNeeded; frame++)
            {
                var srcFrame = frame * _rate;

                if (srcFrame >= availableFrames - 1)
                    break;

                var i0 = (int)srcFrame;
                var frac = (float)(srcFrame - i0);
                var i1 = i0 + 1;

                for (var ch = 0; ch < channels; ch++)
                {
                    var a = _scratch[i0 * channels + ch];
                    var b = _scratch[i1 * channels + ch];
                    buffer[offset + outFrames * channels + ch] = a + (b - a) * frac;
                }

                outFrames++;
            }

            return outFrames * channels;
        }
    }
}
