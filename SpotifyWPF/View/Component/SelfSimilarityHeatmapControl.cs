using System;
using System.Collections;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SpotifyWPF.View.Component
{
    /// <summary>
    /// Slice 3 Observe: beats×beats self-similarity from Classic stacked features.
    /// Bright diagonal stripes = repeating sections; a bright row/column = an outlier beat.
    /// Click a cell to inspect that beat on the ring (via <see cref="SelectedBeatIndex"/>).
    /// </summary>
    public class SelfSimilarityHeatmapControl : FrameworkElement
    {
        private WriteableBitmap _bitmap;

        private int _beatCount;

        private int _hoverBeat = -1;

        public static readonly DependencyProperty StackedFeaturesProperty =
            DependencyProperty.Register(nameof(StackedFeatures), typeof(IList),
                typeof(SelfSimilarityHeatmapControl),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender,
                    OnFeaturesChanged));

        public static readonly DependencyProperty SelectedBeatIndexProperty =
            DependencyProperty.Register(nameof(SelectedBeatIndex), typeof(int),
                typeof(SelfSimilarityHeatmapControl),
                new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault |
                                                  FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty HighlightBeatIndexProperty =
            DependencyProperty.Register(nameof(HighlightBeatIndex), typeof(int),
                typeof(SelfSimilarityHeatmapControl),
                new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender));

        public IList StackedFeatures
        {
            get => (IList)GetValue(StackedFeaturesProperty);
            set => SetValue(StackedFeaturesProperty, value);
        }

        public int SelectedBeatIndex
        {
            get => (int)GetValue(SelectedBeatIndexProperty);
            set => SetValue(SelectedBeatIndexProperty, value);
        }

        public int HighlightBeatIndex
        {
            get => (int)GetValue(HighlightBeatIndexProperty);
            set => SetValue(HighlightBeatIndexProperty, value);
        }

        private static void OnFeaturesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (SelfSimilarityHeatmapControl)d;
            control.RebuildBitmap();
            control.InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            var w = ActualWidth;
            var h = ActualHeight;

            if (w < 8 || h < 8)
                return;

            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x14)), null,
                new Rect(0, 0, w, h));

            if (_bitmap == null || _beatCount <= 0)
            {
                var tip = new FormattedText("SSM needs Classic stacked features\n(re-Analyze with Classic metric)",
                    System.Globalization.CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"),
                    11,
                    new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)),
                    VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(tip, new Point(8, 8));
                return;
            }

            dc.DrawImage(_bitmap, new Rect(0, 0, w, h));

            var focus = HighlightBeatIndex >= 0 ? HighlightBeatIndex
                : _hoverBeat >= 0 ? _hoverBeat
                : SelectedBeatIndex;

            if (focus >= 0 && focus < _beatCount)
            {
                var cell = w / _beatCount;
                var rowY = focus * (h / _beatCount);
                var colX = focus * (w / _beatCount);
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xD1, 0x66)), 1.0);
                pen.Freeze();
                dc.DrawRectangle(null, pen, new Rect(0, rowY, w, Math.Max(1, h / _beatCount)));
                dc.DrawRectangle(null, pen, new Rect(colX, 0, Math.Max(1, w / _beatCount), h));
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var beat = BeatFromPoint(e.GetPosition(this));

            if (beat != _hoverBeat)
            {
                _hoverBeat = beat;
                InvalidateVisual();
            }

            Cursor = beat >= 0 ? Cursors.Cross : Cursors.Arrow;
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            _hoverBeat = -1;
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            var beat = BeatFromPoint(e.GetPosition(this));

            if (beat >= 0)
            {
                SelectedBeatIndex = beat;
                e.Handled = true;
            }
        }

        private int BeatFromPoint(Point point)
        {
            if (_beatCount <= 0 || ActualWidth <= 0 || ActualHeight <= 0)
                return -1;

            var i = (int)(point.Y / ActualHeight * _beatCount);
            var j = (int)(point.X / ActualWidth * _beatCount);

            if (i < 0 || i >= _beatCount || j < 0 || j >= _beatCount)
                return -1;

            // Prefer row (source beat) as the inspect target.
            return i;
        }

        private void RebuildBitmap()
        {
            _bitmap = null;
            _beatCount = 0;

            var features = StackedFeatures;

            if (features == null || features.Count < 2)
                return;

            var n = features.Count;
            var first = features[0] as IList;

            if (first == null || first.Count <= 0)
                return;

            var dim = first.Count;

            // Downsample for UI: max 256 so rebuild stays snappy on long tracks.
            var step = Math.Max(1, (int)Math.Ceiling(n / 256.0));
            var m = (n + step - 1) / step;
            var vectors = new double[m][];

            for (var i = 0; i < m; i++)
            {
                var src = features[Math.Min(i * step, n - 1)] as IList;

                if (src == null)
                    return;

                var v = new double[dim];

                for (var d = 0; d < dim && d < src.Count; d++)
                    v[d] = Convert.ToDouble(src[d]);

                vectors[i] = v;
            }

            var dist = new double[m, m];
            var max = 0.0;

            for (var i = 0; i < m; i++)
            {
                for (var j = i; j < m; j++)
                {
                    var d = Euclid(vectors[i], vectors[j]);
                    dist[i, j] = dist[j, i] = d;

                    if (d > max)
                        max = d;
                }
            }

            if (max <= 1e-9)
                max = 1;

            var bmp = new WriteableBitmap(m, m, 96, 96, PixelFormats.Bgra32, null);
            var pixels = new byte[m * m * 4];

            for (var i = 0; i < m; i++)
            {
                for (var j = 0; j < m; j++)
                {
                    // Similarity: close = bright. Stripes = repeating structure.
                    var sim = 1.0 - dist[i, j] / max;
                    sim = Math.Max(0, Math.Min(1, Math.Pow(sim, 1.35)));
                    var c = HeatColor(sim);
                    var o = (i * m + j) * 4;
                    pixels[o] = c.B;
                    pixels[o + 1] = c.G;
                    pixels[o + 2] = c.R;
                    pixels[o + 3] = 0xFF;
                }
            }

            bmp.WritePixels(new Int32Rect(0, 0, m, m), pixels, m * 4, 0);
            bmp.Freeze();
            _bitmap = bmp;
            _beatCount = n;
        }

        private static double Euclid(double[] a, double[] b)
        {
            double sum = 0;
            var len = Math.Min(a.Length, b.Length);

            for (var i = 0; i < len; i++)
            {
                var d = a[i] - b[i];
                sum += d * d;
            }

            return Math.Sqrt(sum / Math.Max(1, len));
        }

        private static Color HeatColor(double t)
        {
            // High-contrast heat: near-black → deep red → orange → bright yellow-white.
            if (t < 0.33)
            {
                var u = t / 0.33;
                return Color.FromRgb(
                    (byte)(8 + 120 * u),
                    (byte)(4 + 20 * u),
                    (byte)(18 + 30 * u));
            }

            if (t < 0.66)
            {
                var u = (t - 0.33) / 0.33;
                return Color.FromRgb(
                    (byte)(128 + 100 * u),
                    (byte)(24 + 100 * u),
                    (byte)(48 - 30 * u));
            }

            var v = (t - 0.66) / 0.34;
            return Color.FromRgb(
                (byte)(228 + 27 * v),
                (byte)(124 + 120 * v),
                (byte)(18 + 80 * v));
        }
    }
}
