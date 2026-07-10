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
    /// Optional generative background: a morphing Julia-set fractal rendered into a low-resolution
    /// WriteableBitmap and upscaled to the viewport. Palette hue, zoom pulse, and iteration depth
    /// follow the shared <see cref="IVisualEnergyProvider"/> so the fractal breathes with the
    /// equalizer. Disabled (default) it draws nothing — the plain black app background shows.
    ///
    /// Renders behind the jukebox ring but above the app background brush. Never hit-test visible.
    /// Performance: ~200px render target, Parallel.For pixel loop, ~30 fps; frames are skipped
    /// entirely while the hosting window is minimized.
    /// </summary>
    public class FractalBackgroundControl : FrameworkElement
    {
        private const int MaxRenderSize = 208;

        /// <summary>Accessibility: cap the beat zoom/hue kick so the background never strobes.</summary>
        private const double MaxBeatZoom = 0.10;

        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        private readonly DispatcherTimer _timer;

        private WriteableBitmap _bitmap;

        private int[] _pixels;

        private int _pixelWidth;

        private int _pixelHeight;

        private double _lastFrameMs;

        private double _theta = 2.05;

        private double _hueBase = 215;

        public FractalBackgroundControl()
        {
            IsHitTestVisible = false;
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(33)
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

        /// <summary>Bound to the "Fractal background" preference. Off ⇒ nothing is drawn.</summary>
        public bool IsActive
        {
            get => (bool)GetValue(IsActiveProperty);
            set => SetValue(IsActiveProperty, value);
        }

        /// <summary>Same energy source as the equalizer, so both pulse together.</summary>
        public IVisualEnergyProvider EnergyProvider
        {
            get => (IVisualEnergyProvider)GetValue(EnergyProviderProperty);
            set => SetValue(EnergyProviderProperty, value);
        }

        /// <summary>Feathers the fractal out over the transparent center disc of the mini player.</summary>
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

            // Minimized: skip work entirely; the timer keeps ticking cheaply.
            var window = Window.GetWindow(this);

            if (window != null && window.WindowState == WindowState.Minimized)
                return;

            var now = Clock.Elapsed.TotalMilliseconds;
            var dtSec = Math.Max(0.001, Math.Min(0.2, (now - _lastFrameMs) / 1000.0));
            _lastFrameMs = now;

            var energyProvider = EnergyProvider;
            var energy = energyProvider?.GlobalEnergy ?? 0;
            var pulse = energyProvider?.BeatPulse ?? 0;

            // Idle floor keeps a slow, dim drift alive when nothing is playing.
            var drive = Math.Max(0.18, energy);

            _theta += dtSec * (0.045 + 0.22 * energy);
            _hueBase = (_hueBase + dtSec * (3 + 22 * energy)) % 360;

            EnsureBuffers();
            RenderFractal(drive, pulse);

            _bitmap.WritePixels(new Int32Rect(0, 0, _pixelWidth, _pixelHeight), _pixels,
                _pixelWidth * 4, 0);
            InvalidateVisual();
        }

        private void EnsureBuffers()
        {
            var aspect = ActualWidth / ActualHeight;
            int pw, ph;

            if (aspect >= 1)
            {
                pw = MaxRenderSize;
                ph = Math.Max(16, (int)(MaxRenderSize / aspect));
            }
            else
            {
                ph = MaxRenderSize;
                pw = Math.Max(16, (int)(MaxRenderSize * aspect));
            }

            if (_bitmap != null && pw == _pixelWidth && ph == _pixelHeight)
                return;

            _pixelWidth = pw;
            _pixelHeight = ph;
            _pixels = new int[pw * ph];
            _bitmap = new WriteableBitmap(pw, ph, 96, 96, PixelFormats.Bgra32, null);
        }

        private void RenderFractal(double energy, double pulse)
        {
            var pw = _pixelWidth;
            var ph = _pixelHeight;
            var pixels = _pixels;

            // Julia constant orbits near the Mandelbrot boundary — rich, continuously morphing sets.
            var cRe = 0.7885 * Math.Cos(_theta);
            var cIm = 0.7885 * Math.Sin(_theta);

            // Beat: brief zoom-in pulse (capped) and a hue kick applied in the palette below.
            var zoom = 1.15 * (1 + Math.Min(MaxBeatZoom, pulse * MaxBeatZoom));
            var maxIter = 24 + (int)(18 * energy);

            var spanY = 2.9 / zoom;
            var spanX = spanY * pw / ph;
            var hue = _hueBase + pulse * 22;
            var saturation = 0.55 + 0.38 * energy;
            var brightness = 0.35 + 0.65 * energy;

            var mini = MiniPlayerMode;
            var holeOuter = Math.Min(pw, ph) * 0.5 * 0.66;
            var holeInner = Math.Min(pw, ph) * 0.5 * 0.52;
            var cx = pw / 2.0;
            var cy = ph / 2.0;

            Parallel.For(0, ph, y =>
            {
                var zy0 = (y / (double)ph - 0.5) * spanY;
                var row = y * pw;

                for (var x = 0; x < pw; x++)
                {
                    var zx = (x / (double)pw - 0.5) * spanX;
                    var zy = zy0;
                    var iter = 0;
                    var zx2 = zx * zx;
                    var zy2 = zy * zy;

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
                        // Interior stays near-black so the composition never washes out.
                        color = unchecked((int)0xFF060608);
                    }
                    else
                    {
                        // Smooth (non-banded) escape-time domain coloring.
                        var t = (iter + 1 - Math.Log(Math.Log(zx2 + zy2) / 2 / Math.Log(2)) / Math.Log(2)) / maxIter;
                        t = t < 0 ? 0 : t > 1 ? 1 : t;
                        color = HsvToBgra(hue + t * 210, saturation,
                            (0.16 + 0.84 * Math.Pow(t, 0.6)) * brightness);
                    }

                    if (mini)
                    {
                        // Feather out over the mini player's transparent center disc.
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
