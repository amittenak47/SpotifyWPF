using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using SpotifyWPF.Service.Visual;

namespace SpotifyWPF.View.Component
{
    /// <summary>
    /// Modern Winamp-style cascading spectrum on the *outer* ring band (outside the beat coverage
    /// bars): translucent radial bars with falling peak caps. Soft enough not to overwhelm the
    /// beat map underneath / inside.
    /// </summary>
    public sealed class CircularEqualizerRenderer
    {
        /// <summary>Overall opacity so beat bars stay readable underneath.</summary>
        private const double GlobalAlpha = 0.52;

        /// <summary>LED-style segments per bar (modern take on classic blocky spectrum).</summary>
        private const int SegmentCount = 12;

        public void Render(DrawingContext dc, IVisualEnergyProvider energy, Point center,
            double innerRadius, double outerRadius)
        {
            if (energy == null)
                return;

            var bars = energy.BarHeights;
            var peaks = energy.PeakHeights;

            if (bars == null || bars.Count == 0)
                return;

            var count = bars.Count;
            var band = outerRadius - innerRadius;

            if (band <= 4)
                return;

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
                    cos, sin, barWidth);

                if (peak > height + 0.02)
                    DrawPeakCap(dc, center, innerRadius, peak, band, cos, sin, barWidth);
            }
        }

        private static void DrawSegmentedBar(DrawingContext dc, Point center, double inner,
            double height, double segmentLen, double gap, int segments,
            double cos, double sin, double barWidth)
        {
            var lit = (int)Math.Ceiling(height * segments);
            lit = lit < 0 ? 0 : lit > segments ? segments : lit;

            for (var s = 0; s < lit; s++)
            {
                var t0 = s / (double)segments;
                var t1 = (s + 1) / (double)segments;
                var r0 = inner + s * (segmentLen + gap);
                var r1 = r0 + segmentLen;
                var color = SpectrumColor(t1);
                var alpha = GlobalAlpha * (0.55 + 0.45 * t1);

                DrawRadial(dc, center, r0, r1, cos, sin, MakePen(color, barWidth, alpha));
            }
        }

        private static void DrawPeakCap(DrawingContext dc, Point center, double inner, double peak,
            double band, double cos, double sin, double barWidth)
        {
            var r = inner + peak * band;
            var half = Math.Max(1.2, barWidth * 0.55);
            var p0 = new Point(center.X + (r - half * 0.15) * cos, center.Y + (r - half * 0.15) * sin);
            var p1 = new Point(center.X + (r + half * 0.15) * cos, center.Y + (r + half * 0.15) * sin);

            // Thin bright cap — classic Winamp falling peak, slightly soft for a modern look.
            dc.DrawLine(MakePen(Color.FromRgb(0xFF, 0xF0, 0xA0), barWidth * 0.95, GlobalAlpha * 0.9),
                p0, p1);
            dc.DrawLine(MakePen(Colors.White, barWidth * 0.45, GlobalAlpha * 0.7), p0, p1);
        }

        /// <summary>Classic spectrum ramp: green floor → yellow mid → red/orange peaks.</summary>
        private static Color SpectrumColor(double t)
        {
            if (t < 0.4)
                return Lerp(Color.FromRgb(0x1D, 0xB9, 0x54), Color.FromRgb(0xC8, 0xE0, 0x3A), t / 0.4);

            if (t < 0.7)
                return Lerp(Color.FromRgb(0xC8, 0xE0, 0x3A), Color.FromRgb(0xF0, 0xA0, 0x20),
                    (t - 0.4) / 0.3);

            return Lerp(Color.FromRgb(0xF0, 0xA0, 0x20), Color.FromRgb(0xE8, 0x3A, 0x3A),
                (t - 0.7) / 0.3);
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
    }
}
