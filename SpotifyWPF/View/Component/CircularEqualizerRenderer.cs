using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using SpotifyWPF.Service.Visual;

namespace SpotifyWPF.View.Component
{
    /// <summary>
    /// Plasma-style circular equalizer drawn in the annulus between the ring's inner bar band and
    /// the outer rim. Bars radiate outward from the inner radius, glow, and may overshoot the rim
    /// slightly on peaks. Heights come pre-smoothed from <see cref="IVisualEnergyProvider"/> (per-bar
    /// decay, no global normalization); this class only maps them to geometry and color, so the same
    /// values render identically on the full page and in the mini player.
    /// </summary>
    public sealed class CircularEqualizerRenderer
    {
        /// <summary>Bars may cross the outer rim by this fraction of the annulus on peaks.</summary>
        private const double RimOvershoot = 1.18;

        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        public void Render(DrawingContext dc, IVisualEnergyProvider energy, Point center,
            double innerRadius, double outerRadius)
        {
            if (energy == null)
                return;

            var bars = energy.BarHeights;

            if (bars == null || bars.Count == 0)
                return;

            var count = bars.Count;
            var annulus = outerRadius - innerRadius;

            if (annulus <= 2)
                return;

            var barWidth = Math.Max(1.5, Math.Min(7.0, Math.PI * 2 * innerRadius / count * 0.5));

            // Slow base hue drift plus a capped kick on each beat (+~25° max — no strobing).
            var hueBase = Clock.Elapsed.TotalSeconds * (4 + 10 * energy.GlobalEnergy) % 360;
            var hueKick = energy.BeatPulse * 25;

            for (var i = 0; i < count; i++)
            {
                var height = bars[i];

                if (height <= 0.004)
                    continue;

                var angle = i / (double)count * Math.PI * 2 - Math.PI / 2;
                var length = height * annulus * RimOvershoot;
                var tip = innerRadius + length;
                var color = PlasmaColor(height, hueBase + hueKick);

                // Soft glow underlay, then the hot core bar.
                DrawRadial(dc, center, innerRadius, tip, angle,
                    MakePen(color, barWidth * 2.8, 0.10 + 0.16 * height));
                DrawRadial(dc, center, innerRadius, tip, angle,
                    MakePen(color, barWidth, 0.45 + 0.55 * height));

                // Bright tip cap on strong bars, so peaks visibly pierce the rim.
                if (height > 0.55)
                {
                    var tipPoint = new Point(center.X + tip * Math.Cos(angle),
                        center.Y + tip * Math.Sin(angle));
                    dc.DrawEllipse(MakeBrush(Colors.White, (height - 0.55) * 0.8), null,
                        tipPoint, barWidth * 0.5, barWidth * 0.5);
                }
            }
        }

        /// <summary>Plasma palette: quiet bars deep violet/blue, loud bars hot orange/pink.</summary>
        private static Color PlasmaColor(double height, double hue)
        {
            var mapped = (hue + 275 - height * 235) % 360;

            if (mapped < 0)
                mapped += 360;

            return FromHsl(mapped, 0.72 + 0.2 * height, 0.42 + 0.24 * height);
        }

        private static void DrawRadial(DrawingContext dc, Point center, double inner, double outer,
            double angle, Pen pen)
        {
            var cos = Math.Cos(angle);
            var sin = Math.Sin(angle);
            dc.DrawLine(pen,
                new Point(center.X + inner * cos, center.Y + inner * sin),
                new Point(center.X + outer * cos, center.Y + outer * sin));
        }

        private static Pen MakePen(Color color, double thickness, double alpha)
        {
            var pen = new Pen(MakeBrush(color, alpha), thickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            pen.Freeze();
            return pen;
        }

        private static Brush MakeBrush(Color color, double alpha)
        {
            alpha = alpha < 0 ? 0 : alpha > 1 ? 1 : alpha;
            var brush = new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), color.R, color.G, color.B));
            brush.Freeze();
            return brush;
        }

        private static Color FromHsl(double hue, double saturation, double lightness)
        {
            saturation = saturation > 1 ? 1 : saturation;
            lightness = lightness > 1 ? 1 : lightness;
            var c = (1 - Math.Abs(2 * lightness - 1)) * saturation;
            var hp = hue / 60.0;
            var x = c * (1 - Math.Abs(hp % 2 - 1));
            double r = 0, g = 0, b = 0;

            if (hp < 1) { r = c; g = x; }
            else if (hp < 2) { r = x; g = c; }
            else if (hp < 3) { g = c; b = x; }
            else if (hp < 4) { g = x; b = c; }
            else if (hp < 5) { r = x; b = c; }
            else { r = c; b = x; }

            var m = lightness - c / 2;
            return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
        }
    }
}
