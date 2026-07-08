using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace SpotifyWPF.View.Component
{
    /// <summary>
    /// Simplified Infinite Jukebox ring: segment wedges plus glow on the active branch jump
    /// (no chord web of all possible branches).
    /// </summary>
    public class JukeboxRingCanvas : FrameworkElement
    {
        public static readonly DependencyProperty DurationMsProperty =
            DependencyProperty.Register(nameof(DurationMs), typeof(long), typeof(JukeboxRingCanvas),
                new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty PositionMsProperty =
            DependencyProperty.Register(nameof(PositionMs), typeof(long), typeof(JukeboxRingCanvas),
                new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty SegmentStartsSecProperty =
            DependencyProperty.Register(nameof(SegmentStartsSec), typeof(IReadOnlyList<double>),
                typeof(JukeboxRingCanvas),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty GlowFromMsProperty =
            DependencyProperty.Register(nameof(GlowFromMs), typeof(long?), typeof(JukeboxRingCanvas),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty GlowToMsProperty =
            DependencyProperty.Register(nameof(GlowToMs), typeof(long?), typeof(JukeboxRingCanvas),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public long DurationMs
        {
            get => (long)GetValue(DurationMsProperty);
            set => SetValue(DurationMsProperty, value);
        }

        public long PositionMs
        {
            get => (long)GetValue(PositionMsProperty);
            set => SetValue(PositionMsProperty, value);
        }

        public IReadOnlyList<double> SegmentStartsSec
        {
            get => (IReadOnlyList<double>)GetValue(SegmentStartsSecProperty);
            set => SetValue(SegmentStartsSecProperty, value);
        }

        public long? GlowFromMs
        {
            get => (long?)GetValue(GlowFromMsProperty);
            set => SetValue(GlowFromMsProperty, value);
        }

        public long? GlowToMs
        {
            get => (long?)GetValue(GlowToMsProperty);
            set => SetValue(GlowToMsProperty, value);
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            var width = ActualWidth;
            var height = ActualHeight;

            if (width < 8 || height < 8)
                return;

            var center = new Point(width / 2, height / 2);
            var radius = Math.Min(width, height) / 2 - 6;
            var innerRadius = radius * 0.55;

            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1a)), null, center, radius, radius);

            var durationSec = DurationMs > 0 ? DurationMs / 1000.0 : 0;
            var segments = SegmentStartsSec;

            if (durationSec <= 0 || segments == null || segments.Count == 0)
            {
                dc.DrawEllipse(null, new Pen(Brushes.Gray, 1), center, radius, radius);
                return;
            }

            var glowFromIndex = FindSegmentIndex(segments, durationSec, GlowFromMs);
            var glowToIndex = FindSegmentIndex(segments, durationSec, GlowToMs);

            for (var i = 0; i < segments.Count; i++)
            {
                var startAngle = TimeToAngle(segments[i], durationSec);
                var endTime = i + 1 < segments.Count ? segments[i + 1] : durationSec;
                var endAngle = TimeToAngle(endTime, durationSec);

                var baseHue = (byte)(40 + (i * 37) % 120);
                var fill = new SolidColorBrush(Color.FromRgb((byte)(0x30 + baseHue / 4), (byte)(0x38 + baseHue / 3), (byte)(0x50 + baseHue / 2)));

                if (i == glowFromIndex || i == glowToIndex)
                    fill = new SolidColorBrush(Color.FromRgb(0xe0, 0x8a, 0x1a));

                var separator = new Pen(new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)), 0.75);
                DrawWedge(dc, center, innerRadius, radius, startAngle, endAngle, fill, separator);
            }

            if (PositionMs > 0 && durationSec > 0)
            {
                var playAngle = TimeToAngle(PositionMs / 1000.0, durationSec);
                DrawRadialLine(dc, center, innerRadius * 0.92, radius + 5, playAngle,
                    new Pen(new SolidColorBrush(Color.FromRgb(0x1d, 0xb9, 0x54)), 3));
            }

            dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)), 1.5),
                center, radius, radius);
            dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)), 1),
                center, innerRadius, innerRadius);
        }

        private static int FindSegmentIndex(IReadOnlyList<double> segments, double durationSec, long? timeMs)
        {
            if (!timeMs.HasValue || timeMs.Value < 0)
                return -1;

            var timeSec = timeMs.Value / 1000.0;

            for (var i = segments.Count - 1; i >= 0; i--)
            {
                if (timeSec >= segments[i])
                    return i;
            }

            return 0;
        }

        private static double TimeToAngle(double timeSec, double durationSec)
        {
            var fraction = durationSec > 0 ? timeSec / durationSec : 0;
            return fraction * 360 - 90;
        }

        private static void DrawWedge(DrawingContext dc, Point center, double inner, double outer,
            double startAngleDeg, double endAngleDeg, Brush fill, Pen pen)
        {
            if (endAngleDeg <= startAngleDeg)
                endAngleDeg = startAngleDeg + 0.5;

            var startOuter = PointOnCircle(center, outer, startAngleDeg);
            var endOuter = PointOnCircle(center, outer, endAngleDeg);
            var startInner = PointOnCircle(center, inner, endAngleDeg);
            var endInner = PointOnCircle(center, inner, startAngleDeg);

            var figure = new PathFigure { StartPoint = startOuter, IsClosed = true };
            figure.Segments.Add(new ArcSegment(endOuter, new Size(outer, outer), 0, false,
                SweepDirection.Clockwise, true));
            figure.Segments.Add(new LineSegment(startInner, true));
            figure.Segments.Add(new ArcSegment(endInner, new Size(inner, inner), 0, false,
                SweepDirection.Counterclockwise, true));

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            dc.DrawGeometry(fill, pen, geometry);
        }

        private static void DrawRadialLine(DrawingContext dc, Point center, double inner, double outer,
            double angleDeg, Pen pen)
        {
            dc.DrawLine(pen, PointOnCircle(center, inner, angleDeg), PointOnCircle(center, outer, angleDeg));
        }

        private static Point PointOnCircle(Point center, double radius, double angleDeg)
        {
            var radians = angleDeg * Math.PI / 180;
            return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
        }
    }
}
