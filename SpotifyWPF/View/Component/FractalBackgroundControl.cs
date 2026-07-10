using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SpotifyWPF.Service.Visual;

namespace SpotifyWPF.View.Component
{
    /// <summary>
    /// Optional Mandelbrot background: near-viewport resolution escape-time fractal with a slow
    /// continuous zoom. Soft indigo/violet palette. Disabled (default) it draws nothing.
    /// </summary>
    public class FractalBackgroundControl : FrameworkElement
    {
        /// <summary>
        /// Cap on the long edge. High enough that typical window sizes render 1:1 (no upscale
        /// pixelation); still bounded so a 4K monitor doesn't melt the CPU.
        /// </summary>
        private const int MaxRenderSize = 1440;

        private const int BaseMaxIter = 140;

        private const int ExtraMaxIter = 220;

        /// <summary>Seahorse-valley deep zoom target (classic Mandelbrot landmark).</summary>
        private const double TargetRe = -0.743643887037151;

        private const double TargetIm = 0.131825904205330;

        private const double StartScale = 2.6;

        private const double MinScale = 1e-8;

        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        private readonly DispatcherTimer _timer;

        private WriteableBitmap _bitmap;

        private int[] _pixels;

        private int _pixelWidth;

        private int _pixelHeight;

        private double _lastFrameMs;

        private double _scale = StartScale;

        private double _palettePhase;

        public FractalBackgroundControl()
        {
            IsHitTestVisible = false;
            // HighQuality softens any residual upscale; at 1:1 it is a no-op.
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(48)
            };
            _timer.Tick += (_, __) => OnFrame();

            Loaded += (_, __) => SyncTimer();
            Unloaded += (_, __) => _timer.Stop();
        }

        public static readonly DependencyProperty IsActiveProperty =
            DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(FractalBackgroundControl),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((FractalBackgroundControl)d).SyncTimer()));

        public static readonly DependencyProperty EnergyProviderProperty =
            DependencyProperty.Register(nameof(EnergyProvider), typeof(IVisualEnergyProvider),
                typeof(FractalBackgroundControl), new FrameworkPropertyMetadata(null));

        public static readonly DependencyProperty MiniPlayerModeProperty =
            DependencyProperty.Register(nameof(MiniPlayerMode), typeof(bool),
                typeof(FractalBackgroundControl), new FrameworkPropertyMetadata(false));

        public bool IsActive
        {
            get => (bool)GetValue(IsActiveProperty);
            set => SetValue(IsActiveProperty, value);
        }

        public IVisualEnergyProvider EnergyProvider
        {
            get => (IVisualEnergyProvider)GetValue(EnergyProviderProperty);
            set => SetValue(EnergyProviderProperty, value);
        }

        public bool MiniPlayerMode
        {
            get => (bool)GetValue(MiniPlayerModeProperty);
            set => SetValue(MiniPlayerModeProperty, value);
        }

        private void SyncTimer()
        {
            var shouldRun = IsActive && IsLoaded;

            if (shouldRun && !_timer.IsEnabled)
            {
                _lastFrameMs = Clock.Elapsed.TotalMilliseconds;
                _timer.Start();
            }
            else if (!shouldRun && _timer.IsEnabled)
            {
                _timer.Stop();
                _bitmap = null;
                _pixels = null;
                InvalidateVisual();
            }
        }

        private void OnFrame()
        {
            if (!IsActive || ActualWidth < 8 || ActualHeight < 8)
                return;

            var window = Window.GetWindow(this);

            if (window != null && window.WindowState == WindowState.Minimized)
                return;

            var now = Clock.Elapsed.TotalMilliseconds;
            var dtSec = Math.Max(0.001, Math.Min(0.2, (now - _lastFrameMs) / 1000.0));
            _lastFrameMs = now;

            var energy = EnergyProvider?.GlobalEnergy ?? 0;
            var pulse = EnergyProvider?.BeatPulse ?? 0;

            var zoomRate = 0.16 + 0.28 * energy + 0.10 * pulse;
            _scale *= Math.Exp(-zoomRate * dtSec);

            if (_scale < MinScale)
                _scale = StartScale;

            _palettePhase = (_palettePhase + dtSec * (0.6 + 1.8 * energy)) % 360;

            EnsureBuffers();
            RenderMandelbrot(Math.Max(0.12, energy), pulse);

            _bitmap.WritePixels(new Int32Rect(0, 0, _pixelWidth, _pixelHeight), _pixels,
                _pixelWidth * 4, 0);
            InvalidateVisual();
        }

        private void EnsureBuffers()
        {
            // Prefer near-native resolution so the fractal isn't upscaled into blocks.
            var targetW = (int)Math.Ceiling(ActualWidth);
            var targetH = (int)Math.Ceiling(ActualHeight);
            var longEdge = Math.Max(targetW, targetH);

            if (longEdge > MaxRenderSize)
            {
                var scale = MaxRenderSize / (double)longEdge;
                targetW = Math.Max(64, (int)(targetW * scale));
                targetH = Math.Max(64, (int)(targetH * scale));
            }

            // Snap to even sizes for slightly cleaner WriteableBitmap strides.
            targetW = Math.Max(64, targetW & ~1);
            targetH = Math.Max(64, targetH & ~1);

            if (_bitmap != null && targetW == _pixelWidth && targetH == _pixelHeight)
                return;

            _pixelWidth = targetW;
            _pixelHeight = targetH;
            _pixels = new int[targetW * targetH];
            _bitmap = new WriteableBitmap(targetW, targetH, 96, 96, PixelFormats.Bgra32, null);
        }

        private void RenderMandelbrot(double energy, double pulse)
        {
            var pw = _pixelWidth;
            var ph = _pixelHeight;
            var pixels = _pixels;
            var scale = _scale;
            var depth = Math.Min(1.0, Math.Log10(StartScale / scale + 1) / 6.0);
            var maxIter = BaseMaxIter + (int)(ExtraMaxIter * depth);
            maxIter = Math.Min(maxIter, 480);

            var spanY = scale;
            var spanX = spanY * pw / (double)ph;
            var centerRe = TargetRe;
            var centerIm = TargetIm;
            // Soft violet atmosphere — not neon rainbow.
            var brightness = 0.42 + 0.38 * energy + 0.06 * pulse;
            var hueShift = _palettePhase;

            var mini = MiniPlayerMode;
            var holeOuter = Math.Min(pw, ph) * 0.5 * 0.66;
            var holeInner = Math.Min(pw, ph) * 0.5 * 0.52;
            var cx = pw / 2.0;
            var cy = ph / 2.0;

            Parallel.For(0, ph, y =>
            {
                var cIm = centerIm + (y / (double)ph - 0.5) * spanY;
                var row = y * pw;

                for (var x = 0; x < pw; x++)
                {
                    var cRe = centerRe + (x / (double)pw - 0.5) * spanX;
                    double zx = 0, zy = 0;
                    double zx2 = 0, zy2 = 0;
                    var iter = 0;

                    while (iter < maxIter && zx2 + zy2 < 4)
                    {
                        zy = 2 * zx * zy + cIm;
                        zx = zx2 - zy2 + cRe;
                        zx2 = zx * zx;
                        zy2 = zy * zy;
                        iter++;
                    }

                    int color;

                    if (iter >= maxIter)
                    {
                        color = unchecked((int)0xFF05040A);
                    }
                    else
                    {
                        var logZn = Math.Log(zx2 + zy2) / 2;
                        var nu = Math.Log(logZn / Math.Log(2)) / Math.Log(2);
                        var t = (iter + 1 - nu) / maxIter;
                        t = t < 0 ? 0 : t > 1 ? 1 : t;

                        // Soft plasma: deep indigo → violet → muted magenta (no harsh banding).
                        var hue = 255 + t * 55 + hueShift * 0.05;
                        var sat = 0.42 + 0.22 * (1 - t);
                        var val = (0.05 + 0.90 * Math.Pow(t, 0.62)) * brightness;
                        color = HsvToBgra(hue, sat, val);
                    }

                    if (mini)
                    {
                        var dx = x - cx;
                        var dy = y - cy;
                        var dist = Math.Sqrt(dx * dx + dy * dy);

                        if (dist < holeInner)
                        {
                            color = 0;
                        }
                        else if (dist < holeOuter)
                        {
                            var fade = (dist - holeInner) / (holeOuter - holeInner);
                            var alpha = (byte)(((color >> 24) & 0xFF) * fade);
                            color = (color & 0x00FFFFFF) | (alpha << 24);
                        }
                    }

                    pixels[row + x] = color;
                }
            });
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            if (!IsActive || _bitmap == null)
                return;

            dc.DrawImage(_bitmap, new Rect(0, 0, ActualWidth, ActualHeight));
        }

        private static int HsvToBgra(double hue, double saturation, double value)
        {
            hue = hue % 360;

            if (hue < 0)
                hue += 360;

            saturation = saturation < 0 ? 0 : saturation > 1 ? 1 : saturation;
            value = value < 0 ? 0 : value > 1 ? 1 : value;

            var c = value * saturation;
            var hp = hue / 60.0;
            var x = c * (1 - Math.Abs(hp % 2 - 1));
            double r = 0, g = 0, b = 0;

            if (hp < 1) { r = c; g = x; }
            else if (hp < 2) { r = x; g = c; }
            else if (hp < 3) { g = c; b = x; }
            else if (hp < 4) { g = x; b = c; }
            else if (hp < 5) { r = x; b = c; }
            else { r = c; b = x; }

            var m = value - c;
            var rb = (byte)((r + m) * 255);
            var gb = (byte)((g + m) * 255);
            var bb = (byte)((b + m) * 255);

            return unchecked((int)0xFF000000 | (rb << 16) | (gb << 8) | bb);
        }
    }
}
