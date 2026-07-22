using System;
using System.Windows.Media;

namespace SpotifyWPF.Service.Visual
{
    /// <summary>
    /// Stable per-track color family derived from the Spotify track id (or any string key).
    /// Ring section tints and the outer equalizer share the same hashed hues so each song
    /// reads as its own palette instead of a fixed green→red spectrum.
    /// </summary>
    public static class TrackColorPalette
    {
        /// <summary>Muted section hues for the coverage-bar ring (matches legacy sat/light).</summary>
        public static double[] SectionHues(string trackId, int count = 6)
        {
            count = Math.Max(3, count);
            var hash = StableHash(trackId);
            var baseHue = (hash % 3600) / 10.0;
            var step = 360.0 / count;
            var hues = new double[count];

            for (var i = 0; i < count; i++)
            {
                // Small per-slot jitter from hash bits so neighboring songs don't look identical
                // when they land near the same base hue.
                var jitter = ((hash >> (i * 3)) & 0x1F) - 15;
                hues[i] = NormalizeHue(baseHue + i * step + jitter);
            }

            return hues;
        }

        /// <summary>
        /// Brighter stops for the equalizer height gradient (low → high), same hue family as the ring.
        /// </summary>
        public static Color[] SpectrumStops(string trackId, int stopCount = 4)
        {
            stopCount = Math.Max(3, stopCount);
            var hues = SectionHues(trackId, stopCount);
            var colors = new Color[stopCount];

            for (var i = 0; i < stopCount; i++)
            {
                var t = stopCount == 1 ? 0 : i / (double)(stopCount - 1);
                // Low energy: deeper; high energy: brighter / slightly hotter.
                var sat = 0.42 + 0.28 * t;
                var light = 0.38 + 0.28 * t;
                colors[i] = FromHsl(hues[i], sat, light);
            }

            return colors;
        }

        /// <summary>Accent used for wave-ring fill / contour (first section hue, vibrant).</summary>
        public static Color Accent(string trackId)
        {
            var hue = SectionHues(trackId, 6)[0];
            return FromHsl(hue, 0.55, 0.52);
        }

        public static Color FromHsl(double h, double s, double l)
        {
            h = NormalizeHue(h);
            s = Clamp01(s);
            l = Clamp01(l);

            if (s <= 0)
            {
                var grey = (byte)Math.Round(l * 255);
                return Color.FromRgb(grey, grey, grey);
            }

            var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            var p = 2 * l - q;
            var hk = h / 360.0;

            var r = HueToRgb(p, q, hk + 1.0 / 3.0);
            var g = HueToRgb(p, q, hk);
            var b = HueToRgb(p, q, hk - 1.0 / 3.0);

            return Color.FromRgb(
                (byte)Math.Round(r * 255),
                (byte)Math.Round(g * 255),
                (byte)Math.Round(b * 255));
        }

        private static double HueToRgb(double p, double q, double t)
        {
            if (t < 0)
                t += 1;
            if (t > 1)
                t -= 1;
            if (t < 1.0 / 6.0)
                return p + (q - p) * 6 * t;
            if (t < 0.5)
                return q;
            if (t < 2.0 / 3.0)
                return p + (q - p) * (2.0 / 3.0 - t) * 6;
            return p;
        }

        /// <summary>FNV-1a 32-bit — stable across process runs (unlike string.GetHashCode).</summary>
        public static int StableHash(string key)
        {
            unchecked
            {
                var hash = (int)2166136261;

                if (string.IsNullOrEmpty(key))
                    return hash;

                foreach (var ch in key)
                {
                    hash ^= ch;
                    hash *= 16777619;
                }

                return hash == int.MinValue ? 0 : Math.Abs(hash);
            }
        }

        private static double NormalizeHue(double h)
        {
            h %= 360.0;
            if (h < 0)
                h += 360.0;
            return h;
        }

        private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
    }
}
