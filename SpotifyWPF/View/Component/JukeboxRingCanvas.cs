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
        private const double RingInset = 6;
        private const double InnerRadiusRatio = 0.55;
        private const double SegmentHitWidth = 8;

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

        public static readonly DependencyProperty MiniPlayerModeProperty =
            DependencyProperty.Register(nameof(MiniPlayerMode), typeof(bool), typeof(JukeboxRingCanvas),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

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

        public bool MiniPlayerMode
        {
            get => (bool)GetValue(MiniPlayerModeProperty);
            set => SetValue(MiniPlayerModeProperty, value);
        }

        public JukeboxRingCanvas()
        {
        }

        protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters)
        {
            if (ActualWidth <= 0 || ActualHeight <= 0)
                return null;

            if (!MiniPlayerMode)
                return new PointHitTestResult(this, hitTestParameters.HitPoint);

            return IsInteractiveMiniPlayerPoint(hitTestParameters.HitPoint)
                ? new PointHitTestResult(this, hitTestParameters.HitPoint)
                : null;
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            var width = ActualWidth;
            var height = ActualHeight;

            if (width < 8 || height < 8)
                return;

            var center = GetCenter();
            var radius = GetOuterRadius();
            var innerRadius = radius * InnerRadiusRatio;

            if (!MiniPlayerMode)
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

                if (MiniPlayerMode)
                    DrawExtendedSegmentMarker(dc, center, radius, width, height, startAngle, endAngle, fill);
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

        private Point GetCenter() => new Point(ActualWidth / 2, ActualHeight / 2);

        private double GetOuterRadius() => Math.Min(ActualWidth, ActualHeight) / 2 - RingInset;

        private bool IsInteractiveMiniPlayerPoint(Point point)
        {
            var center = GetCenter();
            var outer = GetOuterRadius();
            var inner = outer * InnerRadiusRatio;
            var vector = point - center;
            var dist = vector.Length;

            if (dist <= outer)
                return true;

            return IsNearExtendedSegment(point, center, outer);
        }

        private bool IsNearExtendedSegment(Point point, Point center, double outer)
        {
            var segments = SegmentStartsSec;
            var durationSec = DurationMs > 0 ? DurationMs / 1000.0 : 0;

            if (segments == null || segments.Count == 0 || durationSec <= 0)
                return false;

            var vector = point - center;
            var dist = vector.Length;

            if (dist <= outer)
                return false;

            var edgeDist = DistanceToSquareEdge(center, ActualWidth, ActualHeight,
                Math.Atan2(vector.Y, vector.X) * 180 / Math.PI);

            if (dist > edgeDist + SegmentHitWidth)
                return false;

            var pointAngle = NormalizeAngle(Math.Atan2(vector.Y, vector.X) * 180 / Math.PI);

            for (var i = 0; i < segments.Count; i++)
            {
                var startAngle = NormalizeAngle(TimeToAngle(segments[i], durationSec));
                var endTime = i + 1 < segments.Count ? segments[i + 1] : durationSec;
                var endAngle = NormalizeAngle(TimeToAngle(endTime, durationSec));

                if (IsAngleWithinSegment(pointAngle, startAngle, endAngle))
                    return true;
            }

            return false;
        }

        private void DrawExtendedSegmentMarker(DrawingContext dc, Point center, double outer,
            double width, double height, double startAngleDeg, double endAngleDeg, Brush fill)
        {
            var midAngle = startAngleDeg + (endAngleDeg - startAngleDeg) / 2;
            var edgeDist = DistanceToSquareEdge(center, width, height, midAngle);
            var extensionPen = new Pen(fill, 3) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };

            DrawRadialLine(dc, center, outer, edgeDist, midAngle, extensionPen);

            Brush cornerFill = fill;
            if (fill is SolidColorBrush solid)
                cornerFill = new SolidColorBrush(Color.FromArgb(0xAA, solid.Color.R, solid.Color.G, solid.Color.B));

            DrawWedge(dc, center, outer, edgeDist, startAngleDeg, endAngleDeg, cornerFill,
                new Pen(new SolidColorBrush(Color.FromArgb(0x88, 0x55, 0x55, 0x55)), 0.5));
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

        private static double NormalizeAngle(double angleDeg)
        {
            angleDeg %= 360;
            if (angleDeg < 0)
                angleDeg += 360;

            return angleDeg;
        }

        private static bool IsAngleWithinSegment(double pointAngle, double startAngle, double endAngle)
        {
            if (startAngle <= endAngle)
                return pointAngle >= startAngle && pointAngle <= endAngle;

            return pointAngle >= startAngle || pointAngle <= endAngle;
        }

        private static double DistanceToSquareEdge(Point center, double width, double height, double angleDeg)
        {
            var radians = angleDeg * Math.PI / 180;
            var cos = Math.Cos(radians);
            var sin = Math.Sin(radians);
            double distX = double.PositiveInfinity;
            double distY = double.PositiveInfinity;

            if (Math.Abs(cos) > 0.0001)
                distX = Math.Abs(cos > 0 ? (width - center.X) / cos : -center.X / cos);

            if (Math.Abs(sin) > 0.0001)
                distY = Math.Abs(sin > 0 ? (height - center.Y) / sin : -center.Y / sin);

            return Math.Min(distX, distY);
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
