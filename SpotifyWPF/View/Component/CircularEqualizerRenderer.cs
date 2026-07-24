using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using SpotifyWPF.Service.Visual;

namespace SpotifyWPF.View.Component
{
    /// <summary>
    /// Outer-ring music visualizer. Presets:
    /// <list type="bullet">
    /// <item><c>bars</c> — modern Winamp cascading segmented spectrum.</item>
    /// <item><c>wave-ring</c> — soft wave envelope with a single outer contour (no radial ticks).</item>
    /// </list>
    /// </summary>
    public sealed class CircularEqualizerRenderer
    {
        public const string PresetBars = "bars";

        public const string PresetWaveRing = "wave-ring";

        /// <summary>Overall opacity so beat bars stay readable underneath.</summary>
        private const double GlobalAlpha = 0.52;

        /// <summary>LED-style segments per bar (modern take on classic blocky spectrum).</summary>
        private const int SegmentCount = 12;

        private double[] _smoothScratch;

        private double[] _waveScratch;

        public void Render(DrawingContext dc, IVisualEnergyProvider energy, Point center,
            double innerRadius, double outerRadius, string preset = PresetBars,
            string trackId = null)
        {
            if (energy == null)
                return;

            var bars = energy.BarHeights;
            var peaks = energy.PeakHeights;

            if (bars == null || bars.Count == 0)
                return;

            var band = outerRadius - innerRadius;

            if (band <= 4)
                return;

            var spectrum = TrackColorPalette.SpectrumStops(trackId);
            var accent = TrackColorPalette.Accent(trackId);

            if (string.Equals(preset, PresetWaveRing, StringComparison.OrdinalIgnoreCase))
            {
                RenderWaveRing(dc, energy, bars, center, innerRadius, outerRadius, band, accent, spectrum);
                return;
            }

            RenderBars(dc, bars, peaks, center, innerRadius, band, spectrum);
        }

        private void RenderBars(DrawingContext dc, IReadOnlyList<double> bars,
            IReadOnlyList<double> peaks, Point center, double innerRadius, double band,
            Color[] spectrum)
        {
            var count = bars.Count;
            var barWidth = Math.Max(1.4, Math.Min(6.0, Math.PI * 2 * innerRadius / count * 0.55));
            var gap = Math.Max(0.6, band / SegmentCount * 0.22);
            var segmentLen = (band - gap * (SegmentCount - 1)) / SegmentCount;

            if (segmentLen < 0.8)
                return;

            for (var i = 0; i < count; i++)
            {
                var height = bars[i];
                var peak = peaks != null && i < peaks.Count ? peaks[i] : height;

                if (height <= 0.01 && peak <= 0.01)
                    continue;

                var angle = i / (double)count * Math.PI * 2 - Math.PI / 2;
                var cos = Math.Cos(angle);
                var sin = Math.Sin(angle);

                DrawSegmentedBar(dc, center, innerRadius, height, segmentLen, gap, SegmentCount,
                    cos, sin, barWidth, spectrum);

                if (peak > height + 0.02)
                    DrawPeakCap(dc, center, innerRadius, peak, band, cos, sin, barWidth, spectrum);
            }
        }

        /// <summary>
        /// Soft wave fill + single outer contour. No radial ticks or particle debris.
        /// </summary>
        private void RenderWaveRing(DrawingContext dc, IVisualEnergyProvider energy,
            IReadOnlyList<double> bars, Point center,
            double innerRadius, double outerRadius, double band,
            Color accent, Color[] spectrum)
        {
            var count = bars.Count;
            EnsureScratch(count);

            SmoothCircular(bars, _smoothScratch, radius: 1);
            for (var i = 0; i < count; i++)
            {
                var raw = Clamp01(bars[i]);
                var mixed = raw * 0.78 + Clamp01(_smoothScratch[i]) * 0.22;
                // Push amplitude so crests reach near the outer radius.
                _waveScratch[i] = Clamp01(Math.Pow(mixed, 0.78) * 1.45);
            }

            var pulse = 0.9 + 0.1 * Clamp01(energy.BeatPulse);
            var global = Clamp01(energy.GlobalEnergy);
            var floor = innerRadius;
            var maxR = outerRadius - 1.0;

            var geometry = new StreamGeometry();

            using (var ctx = geometry.Open())
            {
                for (var i = 0; i <= count; i++)
                {
                    var idx = i % count;
                    var angle = idx / (double)count * Math.PI * 2 - Math.PI / 2;
                    var h = _waveScratch[idx];
                    var r = Math.Min(maxR, floor + h * band * pulse);
                    var pt = new Point(
                        center.X + r * Math.Cos(angle),
                        center.Y + r * Math.Sin(angle));

                    if (i == 0)
                        ctx.BeginFigure(pt, true, true);
                    else
                        ctx.LineTo(pt, true, false);
                }
            }

            geometry.Freeze();

            var fill = new SolidColorBrush(Color.FromArgb(
                (byte)(18 + 32 * global), accent.R, accent.G, accent.B));
            fill.Freeze();
            dc.DrawGeometry(fill, null, geometry);

            var contour = spectrum != null && spectrum.Length > 0
                ? spectrum[spectrum.Length - 1]
                : accent;
            dc.DrawGeometry(null,
                MakePen(contour, Math.Max(1.4, band * 0.04),
                    0.55 + 0.35 * global),
                geometry);
        }

        private void EnsureScratch(int count)
        {
            if (_smoothScratch == null || _smoothScratch.Length != count)
                _smoothScratch = new double[count];

            if (_waveScratch == null || _waveScratch.Length != count)
                _waveScratch = new double[count];
        }

        private static void SmoothCircular(IReadOnlyList<double> source, double[] dest, int radius)
        {
            var n = source.Count;

            for (var i = 0; i < n; i++)
            {
                double sum = 0;
                double wsum = 0;

                for (var d = -radius; d <= radius; d++)
                {
                    var j = (i + d + n * 8) % n;
                    var w = radius + 1 - Math.Abs(d);
                    sum += source[j] * w;
                    wsum += w;
                }

                dest[i] = sum / wsum;
            }
        }

        private static void SmoothCircular(double[] source, double[] dest, int radius)
        {
            var n = source.Length;

            for (var i = 0; i < n; i++)
            {
                double sum = 0;
                double wsum = 0;

                for (var d = -radius; d <= radius; d++)
                {
                    var j = (i + d + n * 8) % n;
                    var w = radius + 1 - Math.Abs(d);
                    sum += source[j] * w;
                    wsum += w;
                }

                dest[i] = sum / wsum;
            }
        }

        private static void DrawSegmentedBar(DrawingContext dc, Point center, double inner,
            double height, double segmentLen, double gap, int segments,
            double cos, double sin, double barWidth, Color[] spectrum)
        {
            var lit = (int)Math.Ceiling(height * segments);
            lit = lit < 0 ? 0 : lit > segments ? segments : lit;

            for (var s = 0; s < lit; s++)
            {
                var t1 = (s + 1) / (double)segments;
                var r0 = inner + s * (segmentLen + gap);
                var r1 = r0 + segmentLen;
                var color = SpectrumColor(t1, spectrum);
                var alpha = GlobalAlpha * (0.55 + 0.45 * t1);

                DrawRadial(dc, center, r0, r1, cos, sin, MakePen(color, barWidth, alpha));
            }
        }

        private static void DrawPeakCap(DrawingContext dc, Point center, double inner, double peak,
            double band, double cos, double sin, double barWidth, Color[] spectrum)
        {
            var r = inner + peak * band;
            var half = Math.Max(1.2, barWidth * 0.55);
            var p0 = new Point(center.X + (r - half * 0.15) * cos, center.Y + (r - half * 0.15) * sin);
            var p1 = new Point(center.X + (r + half * 0.15) * cos, center.Y + (r + half * 0.15) * sin);

            var peakColor = spectrum != null && spectrum.Length > 0
                ? spectrum[spectrum.Length - 1]
                : Color.FromRgb(0xFF, 0xF0, 0xA0);
            dc.DrawLine(MakePen(peakColor, barWidth * 0.95, GlobalAlpha * 0.9), p0, p1);
            dc.DrawLine(MakePen(Colors.White, barWidth * 0.45, GlobalAlpha * 0.7), p0, p1);
        }

        private static Color SpectrumColor(double t, Color[] spectrum)
        {
            if (spectrum == null || spectrum.Length == 0)
                return Color.FromRgb(0x1D, 0xB9, 0x54);

            t = t < 0 ? 0 : t > 1 ? 1 : t;

            if (spectrum.Length == 1)
                return spectrum[0];

            var scaled = t * (spectrum.Length - 1);
            var i = (int)Math.Floor(scaled);
            if (i >= spectrum.Length - 1)
                return spectrum[spectrum.Length - 1];

            return Lerp(spectrum[i], spectrum[i + 1], scaled - i);
        }

        private static Color Lerp(Color a, Color b, double t)
        {
            t = t < 0 ? 0 : t > 1 ? 1 : t;
            return Color.FromRgb(
                (byte)(a.R + (b.R - a.R) * t),
                (byte)(a.G + (b.G - a.G) * t),
                (byte)(a.B + (b.B - a.B) * t));
        }

        private static void DrawRadial(DrawingContext dc, Point center, double r0, double r1,
            double cos, double sin, Pen pen)
        {
            dc.DrawLine(pen,
                new Point(center.X + r0 * cos, center.Y + r0 * sin),
                new Point(center.X + r1 * cos, center.Y + r1 * sin));
        }

        private static Pen MakePen(Color color, double thickness, double alpha)
        {
            thickness = Math.Max(0.5, thickness);
            alpha = alpha < 0 ? 0 : alpha > 1 ? 1 : alpha;
            var brush = new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), color.R, color.G, color.B));
            brush.Freeze();
            var pen = new Pen(brush, thickness)
            {
                StartLineCap = PenLineCap.Flat,
                EndLineCap = PenLineCap.Flat
            };
            pen.Freeze();
            return pen;
        }

        private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
    }
}
