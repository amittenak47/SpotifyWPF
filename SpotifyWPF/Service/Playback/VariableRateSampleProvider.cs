using NAudio.Wave;

namespace SpotifyWPF.Service.Playback
{
    /// <summary>
    /// Variable-rate Local WAV playback: consumes source samples faster/slower (pitch shifts with rate).
    /// Always tries to fill the requested output buffer so WaveOut does not underrun.
    /// </summary>
    internal sealed class VariableRateSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;

        private float[] _scratch = new float[8192];

        private double _rate = 1.0;

        private double _srcCursor;

        private int _scratchFrames;

        private bool _sourceEnded;

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

            var channels = WaveFormat.Channels;

            if (channels <= 0)
                return 0;

            if (System.Math.Abs(_rate - 1.0) < 0.02 && _scratchFrames == 0 && _srcCursor < 0.01)
            {
                _sourceEnded = false;
                return _source.Read(buffer, offset, count);
            }

            var framesNeeded = count / channels;
            var outFrames = 0;

            while (outFrames < framesNeeded)
            {
                if (!EnsureScratch(channels))
                    break;

                var srcFrame = _srcCursor;

                if (srcFrame >= _scratchFrames - 1)
                {
                    _scratchFrames = 0;
                    _srcCursor = 0;
                    continue;
                }

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
                _srcCursor += _rate;
            }

            return outFrames * channels;
        }

        private bool EnsureScratch(int channels)
        {
            if (_scratchFrames >= 2 && _srcCursor < _scratchFrames - 1)
                return true;

            if (_sourceEnded)
                return false;

            // Keep a leftover frame for interpolation continuity when refilling.
            var keep = 0;

            if (_scratchFrames >= 2 && _srcCursor < _scratchFrames)
            {
                keep = 1;
                var from = (int)_srcCursor;
                for (var ch = 0; ch < channels; ch++)
                    _scratch[ch] = _scratch[from * channels + ch];
                _srcCursor -= from;
            }
            else
            {
                _srcCursor = 0;
            }

            var wantFrames = 2048;
            var wantSamples = wantFrames * channels;

            if (_scratch.Length < wantSamples + channels)
                _scratch = new float[wantSamples + channels * 4];

            var read = _source.Read(_scratch, keep * channels, wantSamples);

            if (read <= 0)
            {
                _sourceEnded = true;
                _scratchFrames = keep;
                return _scratchFrames >= 2;
            }

            _scratchFrames = keep + read / channels;
            return _scratchFrames >= 2;
        }
    }
}
