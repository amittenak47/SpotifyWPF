using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SpotifyWPF.Model.Prediction;
using SpotifyWPF.Service.Prediction;
using SpotifyWPF.Service.Visual;

namespace SpotifyWPF.View.Component
{
    /// <summary>
    /// Infinite Jukebox ring (Claude design reference, "coverage bars" family):
    /// beats are radial bars around a circle whose length grows as beats are replayed, the playhead
    /// beat burns hot with a fading trail, the planned jump is a pulsing chord through the middle,
    /// fired jumps flash, hovering a beat fans out its branches, and clicking locks the beat's best
    /// branch (gold rails). The full chord web is intentionally never drawn.
    /// Pure presentation: the beat graph is built service-side and handed in via <see cref="Graph"/>.
    ///
    /// In mini player mode the center disc stays transparent (the transport backdrop underneath
    /// remains visible and draggable), hit-testing is limited to the circular bar band, and
    /// beat bars stay within the ring (no square-edge extensions).
    /// </summary>
    public class JukeboxRingCanvas : FrameworkElement
    {
        private const int TrailLength = 28;

        private const double TrailFadeMs = 1400;

        private const double FlashFadeMs = 900;

        /// <summary>Forward gap (in beats) still treated as linear playback when counting plays.</summary>
        private const int LinearAdvanceMaxBeats = 8;

        /// <summary>Inner radius of the beat-bar band as a fraction of the outer radius.</summary>
        private const double BarBandInnerRatio = 0.62;

        /// <summary>
        /// Thin speed-dial band between the scrubber/section rim and the equalizer (px at canvas scale).
        /// </summary>
        private const double SpeedDialBand = 11.0;

        /// <summary>Equalizer inner edge as a fraction of canvas outer radius.</summary>
        private const double SpectrumInnerRatio = 0.78;

        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        private readonly DispatcherTimer _timer;

        private int[] _playCounts = Array.Empty<int>();

        private long _totalPlays;

        private int _lastBeatIndex = -1;

        private readonly List<(int BeatIndex, double AtMs)> _trail = new List<(int, double)>();

        private readonly List<(int From, int To, double AtMs)> _flashes = new List<(int, int, double)>();

        private int _hoverBeatIndex = -1;

        private int _hoverToBeatIndex = -1;

        private readonly int[] _hoverChain = { -1, -1, -1, -1 };

        private int[] _pinnedChain;

        private const int MaxPreviewHopDepth = 3;

        private long _lastPositionMs;

        private double _positionStampMs;

        private bool _isRingScrubbing;

        private bool _speedDialActive;

        /// <summary>0 = idle, 1 = pressed (dial track highlight).</summary>
        private double _centerPressAmount;

        /// <summary>Current Local WAV playback rate (0.5–2.5). Dial sits between scrubber and EQ.</summary>
        private double _playbackRate = 1.0;

        private double _speedDialLastAngle;

        /// <summary>True while dragging along hop arrows to build a multi-hop chain (not scrubbing).</summary>
        private bool _suppressInspectCallback;

        /// <summary>Manual hop authoring: press a beat, drag to a hop, release to queue; ✓ confirms.</summary>
        private bool _manualSelectActive;

        private int _manualTipBeat = -1;

        private readonly List<(int From, int To)> _manualChain = new List<(int, int)>();

        /// <summary>True while press-dragging from a pinned beat to choose a hop arrow.</summary>
        private bool _branchDragActive;

        private int _branchDragOrigin = -1;

        private int _tooltipFrom = -1;

        private int _tooltipTo = -1;

        private double _tooltipOpacity;

        private Point _tooltipPoint;

        private bool _tooltipIsPlanned;

        private string _lastHudText = string.Empty;

        private readonly CircularEqualizerRenderer _equalizer = new CircularEqualizerRenderer();

        public JukeboxRingCanvas()
        {
            _timer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(33)
            };
            _timer.Tick += (_, __) => OnAnimationTick();

            Loaded += (_, __) => _timer.Start();
            Unloaded += (_, __) => _timer.Stop();
        }

        #region Dependency properties

        public static readonly DependencyProperty DurationMsProperty =
            DependencyProperty.Register(nameof(DurationMs), typeof(long), typeof(JukeboxRingCanvas),
                new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty PositionMsProperty =
            DependencyProperty.Register(nameof(PositionMs), typeof(long), typeof(JukeboxRingCanvas),
                new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender,
                    OnPositionChanged));

        public static readonly DependencyProperty IsPausedProperty =
            DependencyProperty.Register(nameof(IsPaused), typeof(bool), typeof(JukeboxRingCanvas),
                new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender,
                    OnPositionChanged));

        public static readonly DependencyProperty GraphProperty =
            DependencyProperty.Register(nameof(Graph), typeof(BeatGraph), typeof(JukeboxRingCanvas),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender,
                    OnGraphChanged));

        public static readonly DependencyProperty SectionStartsSecProperty =
            DependencyProperty.Register(nameof(SectionStartsSec), typeof(IReadOnlyList<double>),
                typeof(JukeboxRingCanvas),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty PlannedFromBeatIndexProperty =
            DependencyProperty.Register(nameof(PlannedFromBeatIndex), typeof(int), typeof(JukeboxRingCanvas),
                new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty PlannedToBeatIndexProperty =
            DependencyProperty.Register(nameof(PlannedToBeatIndex), typeof(int), typeof(JukeboxRingCanvas),
                new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty JumpFlashProperty =
            DependencyProperty.Register(nameof(JumpFlash), typeof(JukeboxJumpFlash), typeof(JukeboxRingCanvas),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender,
                    OnJumpFlashChanged));

        public static readonly DependencyProperty LockedBranchesProperty =
            DependencyProperty.Register(nameof(LockedBranches), typeof(IReadOnlyList<BranchLock>),
                typeof(JukeboxRingCanvas),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ToggleLockCommandProperty =
            DependencyProperty.Register(nameof(ToggleLockCommand), typeof(ICommand), typeof(JukeboxRingCanvas),
                new FrameworkPropertyMetadata(null));

        public static readonly DependencyProperty ScrubToMsCommandProperty =
            DependencyProperty.Register(nameof(ScrubToMsCommand), typeof(ICommand), typeof(JukeboxRingCanvas),
                new FrameworkPropertyMetadata(null));

        public static readonly DependencyProperty EndScrubCommandProperty =
            DependencyProperty.Register(nameof(EndScrubCommand), typeof(ICommand), typeof(JukeboxRingCanvas),
                new FrameworkPropertyMetadata(null));

        public static readonly DependencyProperty PlaybackRateCommandProperty =
            DependencyProperty.Register(nameof(PlaybackRateCommand), typeof(ICommand), typeof(JukeboxRingCanvas),
                new FrameworkPropertyMetadata(null));

        public static readonly DependencyProperty ResetPlaysTokenProperty =
            DependencyProperty.Register(nameof(ResetPlaysToken), typeof(int), typeof(JukeboxRingCanvas),
                new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender,
                    OnResetPlaysTokenChanged));

        public static readonly DependencyProperty MiniPlayerModeProperty =
            DependencyProperty.Register(nameof(MiniPlayerMode), typeof(bool), typeof(JukeboxRingCanvas),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty PreviewHopDepthProperty =
            DependencyProperty.Register(nameof(PreviewHopDepth), typeof(int), typeof(JukeboxRingCanvas),
                new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty HudTextProperty =
            DependencyProperty.Register(nameof(HudText), typeof(string), typeof(JukeboxRingCanvas),
                new FrameworkPropertyMetadata(string.Empty));

        public static readonly DependencyProperty BeatFeaturesProperty =
            DependencyProperty.Register(nameof(BeatFeatures), typeof(System.Collections.IList),
                typeof(JukeboxRingCanvas),
                new FrameworkPropertyMetadata(null));

        public System.Collections.IList BeatFeatures
        {
            get => (System.Collections.IList)GetValue(BeatFeaturesProperty);
            set => SetValue(BeatFeaturesProperty, value);
        }

        public static readonly DependencyProperty InspectBeatIndexProperty =
            DependencyProperty.Register(nameof(InspectBeatIndex), typeof(int), typeof(JukeboxRingCanvas),
                new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                    OnInspectBeatIndexChanged));

        public static readonly DependencyProperty IsManualBranchSelectProperty =
            DependencyProperty.Register(nameof(IsManualBranchSelect), typeof(bool), typeof(JukeboxRingCanvas),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        public static readonly DependencyProperty ConfirmManualBranchCommandProperty =
            DependencyProperty.Register(nameof(ConfirmManualBranchCommand), typeof(ICommand),
                typeof(JukeboxRingCanvas));

        public static readonly DependencyProperty EnergyProviderProperty =
            DependencyProperty.Register(nameof(EnergyProvider), typeof(IVisualEnergyProvider),
                typeof(JukeboxRingCanvas),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty EqualizerPresetProperty =
            DependencyProperty.Register(nameof(EqualizerPreset), typeof(string), typeof(JukeboxRingCanvas),
                new FrameworkPropertyMetadata(CircularEqualizerRenderer.PresetBars,
                    FrameworkPropertyMetadataOptions.AffectsRender));

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

        public bool IsPaused
        {
            get => (bool)GetValue(IsPausedProperty);
            set => SetValue(IsPausedProperty, value);
        }

        public BeatGraph Graph
        {
            get => (BeatGraph)GetValue(GraphProperty);
            set => SetValue(GraphProperty, value);
        }

        public IReadOnlyList<double> SectionStartsSec
        {
            get => (IReadOnlyList<double>)GetValue(SectionStartsSecProperty);
            set => SetValue(SectionStartsSecProperty, value);
        }

        public int PlannedFromBeatIndex
        {
            get => (int)GetValue(PlannedFromBeatIndexProperty);
            set => SetValue(PlannedFromBeatIndexProperty, value);
        }

        public int PlannedToBeatIndex
        {
            get => (int)GetValue(PlannedToBeatIndexProperty);
            set => SetValue(PlannedToBeatIndexProperty, value);
        }

        public JukeboxJumpFlash JumpFlash
        {
            get => (JukeboxJumpFlash)GetValue(JumpFlashProperty);
            set => SetValue(JumpFlashProperty, value);
        }

        public IReadOnlyList<BranchLock> LockedBranches
        {
            get => (IReadOnlyList<BranchLock>)GetValue(LockedBranchesProperty);
            set => SetValue(LockedBranchesProperty, value);
        }

        /// <summary>Executed with the beat index whose best branch should be locked/unlocked.</summary>
        public ICommand ToggleLockCommand
        {
            get => (ICommand)GetValue(ToggleLockCommandProperty);
            set => SetValue(ToggleLockCommandProperty, value);
        }

        /// <summary>Seek the transport scrubber to the given position in milliseconds.</summary>
        public ICommand ScrubToMsCommand
        {
            get => (ICommand)GetValue(ScrubToMsCommandProperty);
            set => SetValue(ScrubToMsCommandProperty, value);
        }

        /// <summary>Commit the ring scrub and resume normal playback position updates.</summary>
        public ICommand EndScrubCommand
        {
            get => (ICommand)GetValue(EndScrubCommandProperty);
            set => SetValue(EndScrubCommandProperty, value);
        }

        /// <summary>Executed with a playback rate (0.5–2.5) from the center speed dial.</summary>
        public ICommand PlaybackRateCommand
        {
            get => (ICommand)GetValue(PlaybackRateCommandProperty);
            set => SetValue(PlaybackRateCommandProperty, value);
        }

        /// <summary>Increment to clear play-coverage bars (the "Reset plays" action).</summary>
        public int ResetPlaysToken
        {
            get => (int)GetValue(ResetPlaysTokenProperty);
            set => SetValue(ResetPlaysTokenProperty, value);
        }

        /// <summary>Transparent-center rendering + drag-passthrough hit-testing for the mini player.</summary>
        public bool MiniPlayerMode
        {
            get => (bool)GetValue(MiniPlayerModeProperty);
            set => SetValue(MiniPlayerModeProperty, value);
        }

        /// <summary>How many branch hops to preview when hovering (1–3).</summary>
        public int PreviewHopDepth
        {
            get => (int)GetValue(PreviewHopDepthProperty);
            set => SetValue(PreviewHopDepthProperty, value);
        }

        /// <summary>Coverage, play count, and hover hint for the status bar.</summary>
        public string HudText
        {
            get => (string)GetValue(HudTextProperty);
            private set => SetValue(HudTextProperty, value);
        }

        /// <summary>Beat currently under inspect (hover/chain/SSM). Two-way for heatmap sync.</summary>
        public int InspectBeatIndex
        {
            get => (int)GetValue(InspectBeatIndexProperty);
            set => SetValue(InspectBeatIndexProperty, value);
        }

        /// <summary>True while manually picking/chaining hops (confirm checkmark visible).</summary>
        public bool IsManualBranchSelect
        {
            get => (bool)GetValue(IsManualBranchSelectProperty);
            set => SetValue(IsManualBranchSelectProperty, value);
        }

        /// <summary>Executed with the pending manual chain when the user confirms.</summary>
        public ICommand ConfirmManualBranchCommand
        {
            get => (ICommand)GetValue(ConfirmManualBranchCommandProperty);
            set => SetValue(ConfirmManualBranchCommandProperty, value);
        }

        /// <summary>Shared music-energy source driving the plasma equalizer in the annulus.</summary>
        public IVisualEnergyProvider EnergyProvider
        {
            get => (IVisualEnergyProvider)GetValue(EnergyProviderProperty);
            set => SetValue(EnergyProviderProperty, value);
        }

        /// <summary>Outer-ring equalizer look: <c>bars</c> or <c>wave-ring</c>.</summary>
        public string EqualizerPreset
        {
            get => (string)GetValue(EqualizerPresetProperty);
            set => SetValue(EqualizerPresetProperty, value);
        }

        private static void OnPositionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var canvas = (JukeboxRingCanvas)d;
            canvas._lastPositionMs = canvas.PositionMs;
            canvas._positionStampMs = Clock.Elapsed.TotalMilliseconds;
        }

        private static void OnGraphChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((JukeboxRingCanvas)d).ResetPlayState();
        }

        private static void OnInspectBeatIndexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var canvas = (JukeboxRingCanvas)d;

            if (canvas._suppressInspectCallback)
                return;

            var beat = (int)e.NewValue;
            var graph = canvas.Graph;

            if (beat < 0 || graph == null || beat >= graph.Beats.Count)
                return;

            // External select (SSM heatmap click): pin that beat's branch fan.
            canvas.ClearChain(canvas._hoverChain);
            canvas._hoverChain[0] = beat;
            canvas._hoverBeatIndex = beat;
            canvas._pinnedChain = (int[])canvas._hoverChain.Clone();
            canvas.SyncHudText();
            canvas.InvalidateVisual();
        }

        private void PublishInspectBeat()
        {
            var chain = ActivePreviewChain();
            var inspect = chain != null && chain[0] >= 0
                ? chain[0]
                : _hoverBeatIndex;

            if (InspectBeatIndex == inspect)
                return;

            _suppressInspectCallback = true;

            try
            {
                InspectBeatIndex = inspect;
            }
            finally
            {
                _suppressInspectCallback = false;
            }
        }

        private static void OnResetPlaysTokenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((JukeboxRingCanvas)d).ResetPlayState();
        }

        private static void OnJumpFlashChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var canvas = (JukeboxRingCanvas)d;

            if (!(e.NewValue is JukeboxJumpFlash flash))
                return;

            canvas._flashes.Add((flash.FromBeatIndex, flash.ToBeatIndex, Clock.Elapsed.TotalMilliseconds));

            if (canvas._flashes.Count > 20)
                canvas._flashes.RemoveAt(0);
        }

        #endregion

        private void ResetPlayState()
        {
            var graph = Graph;
            _playCounts = new int[graph?.Beats.Count ?? 0];
            _totalPlays = 0;
            _lastBeatIndex = -1;
            _trail.Clear();
            _flashes.Clear();
            _hoverBeatIndex = -1;
            _hoverToBeatIndex = -1;
            ClearChain(_hoverChain);
            _pinnedChain = null;
        }

        private void OnAnimationTick()
        {
            var energy = EnergyProvider;
            var energyActive = energy != null && (energy.GlobalEnergy > 0.003 || energy.BeatPulse > 0.003);

            if (Graph == null && !energyActive)
                return;

            // Fade branch analysis tooltip in/out.
            var tipTarget = _tooltipFrom >= 0 && _tooltipTo >= 0 ? 1.0 : 0.0;
            var tipSpeed = tipTarget > _tooltipOpacity ? 0.22 : 0.14;
            _tooltipOpacity += (tipTarget - _tooltipOpacity) * tipSpeed;

            if (Math.Abs(_tooltipOpacity - tipTarget) < 0.02)
                _tooltipOpacity = tipTarget;

            if (Graph != null)
            {
                UpdatePlayCounts(EstimatedPositionMs());
                SyncHudText();
            }

            InvalidateVisual();
        }

        private void DrawAnalysisTooltip(DrawingContext dc, BeatGraph graph)
        {
            if (_tooltipOpacity < 0.05 || _tooltipFrom < 0 || MiniPlayerMode)
                return;

            var text = BuildTooltipText(graph, _tooltipFrom, _tooltipTo, _tooltipIsPlanned);

            if (string.IsNullOrEmpty(text))
                return;

            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var accent = _tooltipIsPlanned
                ? Color.FromRgb(0x1D, 0xB9, 0x54)
                : Color.FromRgb(0xFF, 0xD1, 0x66);
            var formatted = new FormattedText(text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface("Consolas"),
                9.5,
                new SolidColorBrush(Color.FromArgb((byte)(235 * _tooltipOpacity), 0xEC, 0xEC, 0xEC)),
                dpi)
            {
                MaxTextWidth = 200,
                LineHeight = 12
            };

            var pad = 7.0;
            var width = formatted.Width + pad * 2;
            var height = formatted.Height + pad * 2;
            var x = Math.Max(6, Math.Min(_tooltipPoint.X + 12, ActualWidth - width - 6));
            var y = Math.Max(6, Math.Min(_tooltipPoint.Y - height - 8, ActualHeight - height - 6));
            var rect = new Rect(x, y, width, height);

            dc.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromArgb((byte)(220 * _tooltipOpacity), 0x08, 0x08, 0x0A)),
                new Pen(new SolidColorBrush(Color.FromArgb((byte)(200 * _tooltipOpacity), accent.R, accent.G, accent.B)), 1),
                rect, 3, 3);
            dc.DrawText(formatted, new Point(x + pad, y + pad));
        }

        private string BuildTooltipText(BeatGraph graph, int from, int to, bool planned)
        {
            if (from < 0 || from >= graph.Beats.Count)
                return null;

            var a = graph.Beats[from];

            if (to < 0 || to == from || to >= graph.Beats.Count)
            {
                return $"beat {from}   {FormatBeatTime(a.StartMs)}\n" +
                       $"bar {a.IndexInBar}   {a.Neighbors.Count} hops\n" +
                       (_manualSelectActive
                           ? "press+drag another beat for more hops · ✓ saves locks"
                           : "press beat + drag to a hop");
            }

            var b = graph.Beats[to];
            var edge = FindEdge(a.Neighbors, to);
            var dist = edge?.Distance ?? double.NaN;

            // Enhanced edges are continuation-oriented: landing `to` should match the phase of
            // `from+1` (what would have played next), not `from`. Comparing from↔to often shows
            // "Δ1 off-sync" for a correct splice (e.g. leave beat-in-bar 2, land on beat-in-bar 3).
            // IndexInBar can grow past 0–3 in long undownbeat stretches — compare mod-4 phase.
            var expectPhaseBeat = from + 1 < graph.Beats.Count ? graph.Beats[from + 1] : a;
            var phaseDelta = CircularBarPhaseDelta(expectPhaseBeat.IndexInBar, b.IndexInBar);
            var rawPhaseGap = Math.Abs(expectPhaseBeat.IndexInBar - b.IndexInBar);
            var syncNote = phaseDelta == 0
                ? (rawPhaseGap == 0
                    ? "in phase (continues next)"
                    : $"in phase (mod-4; raw bar {expectPhaseBeat.IndexInBar}→{b.IndexInBar})")
                : phaseDelta == 1
                    ? "slightly off-sync (Δ1 vs next)"
                    : $"off-sync (Δ{phaseDelta} vs next)";

            var twinDelta = CircularBarPhaseDelta(a.IndexInBar, b.IndexInBar);
            if (twinDelta == 0 && phaseDelta != 0)
                syncNote += " · twin of origin";

            var quality = "—";

            if (!double.IsNaN(dist) && graph.BranchDistanceThreshold > 0)
            {
                var rel = dist / graph.BranchDistanceThreshold;
                quality = rel < 0.45 ? "very close"
                    : rel < 0.75 ? "good"
                    : rel < 1.0 ? "ok"
                    : "loose";
            }

            TryFeatureDeltas(from, to, out var chroma, out var mfcc, out var rms, out var totalFeat);
            var dir = to < from ? "earlier" : "later";
            var head = planned ? "RANDOM PLAN" : "HOP";
            return $"{head}  {from} → {to}  ({dir})\n" +
                   $"{FormatBeatTime(a.StartMs)} → {FormatBeatTime(b.StartMs)}\n" +
                   $"dist {dist:0.###}   {quality}   {syncNote}\n" +
                   $"bar {a.IndexInBar}→{b.IndexInBar} (expect bar {expectPhaseBeat.IndexInBar})\n" +
                   $"features  chroma {chroma:0.00}  mfcc {mfcc:0.00}  rms {rms:0.00}\n" +
                   $"(sum {totalFeat:0.00} · enhanced euclid on z-scored dims)";
        }

        private void TryFeatureDeltas(int from, int to,
            out double chroma, out double mfcc, out double rms, out double total)
        {
            chroma = mfcc = rms = total = 0;
            var features = BeatFeatures;

            if (features == null || from >= features.Count || to >= features.Count)
                return;

            var a = features[from] as System.Collections.IList;
            var b = features[to] as System.Collections.IList;

            if (a == null || b == null)
                return;

            // Classic beatFeatures layout: 12 chroma + 12 MFCC[1:] + 1 RMS.
            const int chromaN = 12;
            const int mfccN = 12;
            var n = Math.Min(a.Count, b.Count);
            double cSum = 0, mSum = 0, rSum = 0;

            for (var i = 0; i < n; i++)
            {
                var d = Convert.ToDouble(a[i]) - Convert.ToDouble(b[i]);
                var sq = d * d;

                if (i < chromaN)
                    cSum += sq;
                else if (i < chromaN + mfccN)
                    mSum += sq;
                else
                    rSum += sq;
            }

            chroma = Math.Sqrt(cSum / chromaN);
            mfcc = Math.Sqrt(mSum / Math.Max(1, Math.Min(mfccN, n - chromaN)));
            rms = Math.Sqrt(rSum);
            total = Math.Sqrt((cSum + mSum + rSum) / Math.Max(1, n));
        }

        private void SyncHudText()
        {
            if (MiniPlayerMode)
            {
                PublishHudText(string.Empty);
                return;
            }

            var graph = Graph;

            if (graph == null || graph.Beats.Count == 0)
            {
                PublishHudText(string.Empty);
                return;
            }

            var covered = 0;

            for (var i = 0; i < _playCounts.Length; i++)
            {
                if (_playCounts[i] > 0)
                    covered++;
            }

            var coverage = (int)Math.Round(covered * 100.0 / graph.Beats.Count);
            var baseLine = coverage + "% coverage · " + _totalPlays + " beats played";

            if (_manualSelectActive)
            {
                var hops = _manualChain.Count;
                PublishHudText(
                    $"{baseLine} · MANUAL SELECT beat {_manualTipBeat} · {hops} hop(s) queued · " +
                    (_branchDragActive
                        ? "drag onto a hop · release to queue"
                        : "press+drag a beat to add hops · ✓ confirm · ✕ cancel"));
                return;
            }

            var previewChain = ActivePreviewChain();
            string hover = null;

            if (previewChain[0] >= 0)
                hover = BuildChainPreviewText(graph, previewChain);
            else if (_hoverBeatIndex >= 0)
                hover = BuildHoverHint(graph, _hoverBeatIndex, previewChain);

            PublishHudText(hover != null
                ? baseLine + " · " + hover
                : baseLine + " · press a beat + drag to a hop");
        }

        private void PublishHudText(string text)
        {
            text = text ?? string.Empty;

            if (_lastHudText == text)
                return;

            _lastHudText = text;
            HudText = text;
        }

        /// <summary>
        /// Position interpolated between the coarse player updates so the playhead sweeps smoothly.
        /// </summary>
        private long EstimatedPositionMs()
        {
            var position = _lastPositionMs;

            if (!IsPaused)
                position += (long)(Clock.Elapsed.TotalMilliseconds - _positionStampMs);

            var total = TotalMs();
            return total > 0 ? Math.Max(0, Math.Min(position, total)) : Math.Max(0, position);
        }

        private long TotalMs()
        {
            var graph = Graph;
            var lastBeatEnd = graph != null && graph.Beats.Count > 0
                ? graph.Beats[graph.Beats.Count - 1].EndMs
                : 0;

            return Math.Max(DurationMs, lastBeatEnd);
        }

        private void UpdatePlayCounts(long positionMs)
        {
            var graph = Graph;

            if (graph == null || graph.Beats.Count == 0 || _playCounts.Length != graph.Beats.Count)
                return;

            var index = FindBeatIndex(graph, positionMs);

            if (index < 0 || index == _lastBeatIndex)
                return;

            var now = Clock.Elapsed.TotalMilliseconds;

            if (_lastBeatIndex >= 0 && index > _lastBeatIndex &&
                index - _lastBeatIndex <= LinearAdvanceMaxBeats)
            {
                // Linear playback: credit every beat passed since the last tick.
                for (var i = _lastBeatIndex + 1; i <= index; i++)
                    CountPlay(i, now);
            }
            else
            {
                // Jump or seek: only the landing beat plays.
                CountPlay(index, now);
            }

            _lastBeatIndex = index;
        }

        private void CountPlay(int beatIndex, double nowMs)
        {
            _playCounts[beatIndex]++;
            _totalPlays++;
            _trail.Add((beatIndex, nowMs));

            if (_trail.Count > TrailLength)
                _trail.RemoveAt(0);
        }

        private static int FindBeatIndex(BeatGraph graph, long positionMs)
        {
            var beats = graph.Beats;

            if (beats.Count == 0)
                return -1;

            var lo = 0;
            var hi = beats.Count - 1;

            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;

                if (beats[mid].StartMs <= positionMs)
                    lo = mid;
                else
                    hi = mid - 1;
            }

            return lo;
        }

        #region Hit testing / mouse interaction

        public static readonly DependencyProperty MiniPlayerHopModeProperty =
            DependencyProperty.Register(nameof(MiniPlayerHopMode), typeof(bool), typeof(JukeboxRingCanvas),
                new PropertyMetadata(false, OnMiniPlayerHopModeChanged));

        private static void OnMiniPlayerHopModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((JukeboxRingCanvas)d).InvalidateVisual();
        }

        public bool MiniPlayerHopMode
        {
            get => (bool)GetValue(MiniPlayerHopModeProperty);
            set => SetValue(MiniPlayerHopModeProperty, value);
        }

        protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters)
        {
            if (ActualWidth <= 0 || ActualHeight <= 0)
                return null;

            if (MiniPlayerMode)
            {
                if (!MiniPlayerHopMode || !IsHopInteractionPoint(hitTestParameters.HitPoint))
                    return null;

                return new PointHitTestResult(this, hitTestParameters.HitPoint);
            }

            return new PointHitTestResult(this, hitTestParameters.HitPoint);
        }

        /// <summary>
        /// Mini player drag host calls this to avoid stealing hop/chord clicks when hop mode is on.
        /// </summary>
        public bool WouldHandleHopClick(Point point)
        {
            if (!MiniPlayerMode || !MiniPlayerHopMode || !IsHopInteractionPoint(point))
                return false;

            var graph = Graph;

            if (graph == null)
                return false;

            UpdateHoverChain(point);
            return TryGetClickedBranch(point, graph, ActivePreviewChain(), out _, out _);
        }

        private bool IsHopInteractionPoint(Point point)
        {
            GetChordLayout(out var center, out var rim, out _);
            var rIn = rim * BarBandInnerRatio;
            return (point - center).Length <= rIn;
        }

        public bool IsInHopDisc(Point point) => IsHopInteractionPoint(point);

        /// <summary>
        /// Mini player: center disc passes clicks through; only the circular bar/spectrum band
        /// stays interactive (no square corner extensions).
        /// </summary>
        private bool IsInteractiveMiniPlayerPoint(Point point)
        {
            GetRingLayout(out var center, out var canvasOuter, out _, out _, out var rim, out _, out _);
            var rIn = rim * BarBandInnerRatio;
            var dist = (point - center).Length;
            return dist >= rIn && dist <= canvasOuter;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            var point = e.GetPosition(this);

            if (_isRingScrubbing && e.LeftButton == MouseButtonState.Pressed)
            {
                ScrubToPoint(point);
                return;
            }

            if (_speedDialActive && e.LeftButton == MouseButtonState.Pressed)
            {
                UpdateSpeedDial(point);
                return;
            }

            if (MiniPlayerMode && !MiniPlayerHopMode)
                return;

            var graph = Graph;

            if (graph == null)
            {
                ClearChain(_hoverChain);
                _hoverBeatIndex = -1;
                _hoverToBeatIndex = -1;
                Cursor = IsInSpeedDialZone(point) ? Cursors.ScrollAll : Cursors.Arrow;
                InvalidateVisual();
                return;
            }

            // Press-drag branch pick: origin stays pinned even when the cursor crosses other beats.
            if (_branchDragActive && _branchDragOrigin >= 0)
            {
                _hoverBeatIndex = _branchDragOrigin;
                var dest = HitTestBranchEdge(point, graph, _branchDragOrigin);
                UpdateBranchTooltip(graph, _branchDragOrigin, dest, point);
                SyncManualHoverChain(dest);
                PublishInspectBeat();
                SyncHudText();
                Cursor = dest >= 0 ? Cursors.Hand : Cursors.Cross;
                InvalidateVisual();
                return;
            }

            // Prefer coverage-bar hover so scrubbing the timeline still previews that beat's hops.
            var hit = HitTestBeat(point);

            if (hit < 0)
                hit = HitTestBranchOriginBeat(point);

            _hoverBeatIndex = hit;

            if (_manualSelectActive && _manualTipBeat >= 0)
            {
                // Sticky tip — do not retarget when the cursor drifts onto another beat.
                // Press+drag a new beat to switch origin.
                var dest = HitTestBranchEdge(point, graph, _manualTipBeat);
                UpdateBranchTooltip(graph, _manualTipBeat, dest, point);
                SyncManualHoverChain(dest);
                PublishInspectBeat();
                SyncHudText();
                Cursor = dest >= 0 || hit >= 0 ? Cursors.Hand : Cursors.Arrow;
                InvalidateVisual();
                return;
            }

            // Idle: fan potential branches from whatever beat is under the cursor.
            if (hit >= 0)
            {
                var inspect = NearestBeatWithBranches(graph, hit, 1);

                if (inspect >= 0)
                {
                    ClearChain(_hoverChain);
                    _hoverChain[0] = inspect;
                    var dest = HitTestBranchEdge(point, graph, inspect);
                    _hoverToBeatIndex = dest;

                    if (dest >= 0)
                        UpdateBranchTooltip(graph, inspect, dest, point);
                    else
                        UpdateIdleBeatTooltip(graph, hit, point);
                }
                else
                {
                    ClearChain(_hoverChain);
                    _hoverToBeatIndex = -1;
                    UpdateIdleBeatTooltip(graph, hit, point);
                }
            }
            else
            {
                ClearChain(_hoverChain);
                _hoverToBeatIndex = -1;
                UpdateIdleBeatTooltip(graph, -1, point);
            }

            PublishInspectBeat();
            SyncHudText();

            if (IsInSpeedDialZone(point))
                Cursor = Cursors.ScrollAll;
            else
                Cursor = hit >= 0 || _hoverToBeatIndex >= 0 ? Cursors.Hand : Cursors.Arrow;

            InvalidateVisual();
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);

            if (_isRingScrubbing)
                EndRingScrub();

            // Keep dial / branch-drag alive while mouse is captured outside the element.
            if ((_speedDialActive || _branchDragActive) && IsMouseCaptured)
                return;

            EndSpeedDial();
            EndBranchDrag(commit: false);

            _hoverBeatIndex = -1;
            _hoverToBeatIndex = -1;
            _tooltipFrom = -1;
            _tooltipTo = -1;
            ClearChain(_hoverChain);
            PublishInspectBeat();
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);

            var graph = Graph;
            var point = e.GetPosition(this);

            // Double-click a locked branch to unlock/remove it; double-click dial resets 1×.
            if (e.ClickCount > 1)
            {
                if (graph != null && TryHitLockedBranch(point, graph, out var from, out var to) &&
                    ToggleLockCommand != null)
                {
                    var click = new RingBranchClick { FromBeatIndex = from, ToBeatIndex = to };

                    if (ToggleLockCommand.CanExecute(click))
                    {
                        ToggleLockCommand.Execute(click);
                        InvalidateVisual();
                    }

                    e.Handled = true;
                    return;
                }

                if (IsInSpeedDialZone(point))
                {
                    _playbackRate = 1.0;
                    PublishPlaybackRate();
                    InvalidateVisual();
                    e.Handled = true;
                }

                return;
            }

            // Thin ring between scrubber and equalizer — press + rotate for Local WAV rate.
            if (TryBeginSpeedDial(point))
            {
                e.Handled = true;
                return;
            }

            if (graph == null || (MiniPlayerMode && !MiniPlayerHopMode))
            {
                if (TryBeginRingScrub(point))
                    e.Handled = MiniPlayerMode && !MiniPlayerHopMode;
                return;
            }

            // Press a beat (or its hop arrow) and drag to choose — origin stays sticky.
            if (TryBeginBranchDrag(point, graph))
            {
                e.Handled = true;
                return;
            }

            // Scrub on the coverage-bar ring when not starting a branch drag.
            if (TryBeginRingScrub(point))
            {
                e.Handled = true;
                return;
            }

            if (_manualSelectActive)
            {
                ExitManualSelection();
                e.Handled = true;
            }
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);

            if (_speedDialActive)
            {
                EndSpeedDial();
                e.Handled = true;
            }

            if (_branchDragActive)
            {
                EndBranchDrag(commit: true);
                e.Handled = true;
            }

            if (_isRingScrubbing)
                EndRingScrub();
        }

        /// <summary>
        /// Thin annulus just outside the scrubber/section rim and just inside the equalizer.
        /// </summary>
        private bool IsInSpeedDialZone(Point point)
        {
            GetRingLayout(out var center, out _, out _, out _, out _, out var dialInner, out var dialOuter);
            var dist = (point - center).Length;
            return dist >= dialInner && dist <= dialOuter;
        }

        private bool TryBeginSpeedDial(Point point)
        {
            if (!IsInSpeedDialZone(point))
                return false;

            GetChordLayout(out var center, out _, out _);
            _speedDialActive = true;
            _centerPressAmount = 1;
            _speedDialLastAngle = Math.Atan2(point.Y - center.Y, point.X - center.X);
            CaptureMouse();
            Cursor = Cursors.ScrollAll;
            InvalidateVisual();
            return true;
        }

        private void UpdateSpeedDial(Point point)
        {
            GetChordLayout(out var center, out _, out _);
            var angle = Math.Atan2(point.Y - center.Y, point.X - center.X);
            var delta = angle - _speedDialLastAngle;

            // Unwrap to shortest signed turn.
            while (delta > Math.PI)
                delta -= Math.PI * 2;
            while (delta < -Math.PI)
                delta += Math.PI * 2;

            _speedDialLastAngle = angle;

            // One full clockwise turn ≈ +1.0× rate.
            var next = _playbackRate + delta / (Math.PI * 2);
            next = Math.Max(0.5, Math.Min(2.5, next));

            // Magnetic snap to 1.00× so returning to normal is easy.
            if (Math.Abs(next - 1.0) < 0.09)
                next = 1.0;

            if (Math.Abs(next - _playbackRate) < 0.001)
                return;

            _playbackRate = next;
            PublishPlaybackRate();
            InvalidateVisual();
        }

        private void EndSpeedDial()
        {
            if (!_speedDialActive)
                return;

            // Final snap to 1.00× if still in the magnetic zone.
            if (Math.Abs(_playbackRate - 1.0) < 0.09)
            {
                _playbackRate = 1.0;
                PublishPlaybackRate();
            }

            _speedDialActive = false;
            _centerPressAmount = 0;

            if (IsMouseCaptured && !_branchDragActive && !_isRingScrubbing)
                ReleaseMouseCapture();

            Cursor = Cursors.Arrow;
            InvalidateVisual();
        }

        private void PublishPlaybackRate()
        {
            var rate = Math.Round(_playbackRate * 20.0) / 20.0; // 0.05 steps

            if (Math.Abs(rate - 1.0) < 0.06)
            {
                rate = 1.0;
                _playbackRate = 1.0;
            }

            if (PlaybackRateCommand != null && PlaybackRateCommand.CanExecute(rate))
                PlaybackRateCommand.Execute(rate);
        }

        /// <summary>
        /// Begin press-drag hop picking from a beat. Origin stays pinned until mouse up.
        /// </summary>
        private bool TryBeginBranchDrag(Point point, BeatGraph graph)
        {
            if (graph == null)
                return false;

            var origin = -1;

            // Chord vertices (just inside the bars): always start hop picking here.
            var originBeat = HitTestBranchOriginBeat(point);

            if (originBeat >= 0)
                origin = NearestBeatWithBranches(graph, originBeat, 1);

            // Grab a hop arrow that is already fanned from the hover / sticky tip.
            if (origin < 0 && _hoverChain[0] >= 0 &&
                HitTestBranchEdge(point, graph, _hoverChain[0]) >= 0)
                origin = _hoverChain[0];

            if (origin < 0 && _manualSelectActive && _manualTipBeat >= 0 &&
                HitTestBranchEdge(point, graph, _manualTipBeat) >= 0)
                origin = _manualTipBeat;

            // Coverage bars: start a drag when hops for this beat are already previewed,
            // or the press is on the inner edge of the bar. Outer bar stays free for scrubbing.
            if (origin < 0)
            {
                var beat = HitTestBeat(point);

                if (beat >= 0)
                {
                    var tip = NearestBeatWithBranches(graph, beat, 1);

                    if (tip >= 0)
                    {
                        GetChordLayout(out var center, out var rim, out _);
                        var rIn = rim * BarBandInnerRatio;
                        var barOuter = rIn + rim * 0.28;
                        var dist = (point - center).Length;
                        var onInnerBar = dist <= rIn + (barOuter - rIn) * 0.5;
                        var alreadyPreviewing = _hoverChain[0] == tip || _manualTipBeat == tip;

                        if (onInnerBar || alreadyPreviewing)
                            origin = tip;
                    }
                }
            }

            if (origin < 0)
                return false;

            _branchDragActive = true;
            _branchDragOrigin = origin;
            _manualTipBeat = origin;

            if (!_manualSelectActive)
            {
                _manualSelectActive = true;
                _manualChain.Clear();
                IsManualBranchSelect = true;
            }

            ClearChain(_hoverChain);
            _hoverChain[0] = origin;
            _pinnedChain = (int[])_hoverChain.Clone();
            _hoverBeatIndex = origin;
            _hoverToBeatIndex = -1;
            CaptureMouse();
            Cursor = Cursors.Cross;
            PublishInspectBeat();
            SyncHudText();
            InvalidateVisual();
            return true;
        }

        private void EndBranchDrag(bool commit)
        {
            if (!_branchDragActive)
                return;

            var graph = Graph;
            var origin = _branchDragOrigin;
            var dest = -1;

            if (commit && graph != null && origin >= 0)
            {
                var point = Mouse.GetPosition(this);
                dest = HitTestBranchEdge(point, graph, origin);
            }

            _branchDragActive = false;
            _branchDragOrigin = -1;

            if (IsMouseCaptured && !_speedDialActive && !_isRingScrubbing)
                ReleaseMouseCapture();

            Cursor = Cursors.Arrow;

            if (dest >= 0 && origin >= 0)
            {
                if (!_manualChain.Any(h => h.From == origin && h.To == dest))
                    _manualChain.Add((origin, dest));

                _manualTipBeat = origin;
                SyncManualHoverChain(-1);
                UpdateBranchTooltip(graph, -1, -1, default);
            }
            else
            {
                // Released off a hop: keep the beat pinned so hops stay visible.
                SyncManualHoverChain(-1);
            }

            PublishInspectBeat();
            SyncHudText();
            InvalidateVisual();
        }

        private void EnterManualSelection(int beatIndex)
        {
            _manualSelectActive = true;
            _manualTipBeat = beatIndex;
            _manualChain.Clear();
            IsManualBranchSelect = true;
            ClearChain(_hoverChain);
            _hoverChain[0] = beatIndex;
            _pinnedChain = (int[])_hoverChain.Clone();
            PublishInspectBeat();
            SyncHudText();
            InvalidateVisual();
        }

        public void ConfirmManualSelectionPublic() => ConfirmManualSelection();

        public void CancelManualSelectionPublic() => ExitManualSelection();

        private void ExitManualSelection()
        {
            EndBranchDrag(commit: false);
            _manualSelectActive = false;
            _manualTipBeat = -1;
            _manualChain.Clear();
            IsManualBranchSelect = false;
            _tooltipFrom = -1;
            _tooltipTo = -1;
            ClearChain(_hoverChain);
            _pinnedChain = null;
            PublishInspectBeat();
            SyncHudText();
            InvalidateVisual();
        }

        private void ConfirmManualSelection()
        {
            if (_manualChain.Count == 0)
            {
                ExitManualSelection();
                return;
            }

            var command = ConfirmManualBranchCommand ?? ToggleLockCommand;

            foreach (var hop in _manualChain)
            {
                var click = new RingBranchClick { FromBeatIndex = hop.From, ToBeatIndex = hop.To };

                if (command != null && command.CanExecute(click))
                    command.Execute(click);
            }

            ExitManualSelection();
        }

        private void SyncManualHoverChain(int hoveredDest)
        {
            ClearChain(_hoverChain);
            // Preview fan from the active tip only; committed multi-origin hops are drawn separately.
            if (_manualTipBeat >= 0)
                _hoverChain[0] = _manualTipBeat;

            _pinnedChain = (int[])_hoverChain.Clone();
            _hoverToBeatIndex = hoveredDest;
        }

        private void UpdateBranchTooltip(BeatGraph graph, int from, int to, Point point, bool planned = false)
        {
            if (from < 0 || to < 0 || graph == null ||
                from >= graph.Beats.Count || to >= graph.Beats.Count)
            {
                _tooltipFrom = -1;
                _tooltipTo = -1;
                _tooltipIsPlanned = false;
                return;
            }

            _tooltipFrom = from;
            _tooltipTo = to;
            _tooltipPoint = point;
            _tooltipIsPlanned = planned;
        }

        private void UpdateIdleBeatTooltip(BeatGraph graph, int beat, Point point)
        {
            if (graph == null || beat < 0 || beat >= graph.Beats.Count)
            {
                // Idle: still offer the planned random hop tip when hovering that chord.
                if (TryPlannedHopTooltip(graph, point))
                    return;

                _tooltipFrom = -1;
                _tooltipTo = -1;
                _tooltipIsPlanned = false;
                return;
            }

            _tooltipFrom = beat;
            _tooltipTo = beat;
            _tooltipPoint = point;
            _tooltipIsPlanned = false;
        }

        /// <summary>
        /// Shared ring radii. Layers outward: center → coverage/scrubber (≤ rim) → speed dial
        /// (dialInner..dialOuter) → equalizer (spectrumInner..spectrumOuter).
        /// </summary>
        private void GetRingLayout(out Point center, out double canvasOuter,
            out double spectrumInner, out double spectrumOuter,
            out double rim, out double dialInner, out double dialOuter)
        {
            center = new Point(ActualWidth / 2, ActualHeight / 2);
            canvasOuter = Math.Min(ActualWidth, ActualHeight) / 2 - 2;
            spectrumOuter = canvasOuter;
            spectrumInner = canvasOuter * SpectrumInnerRatio;
            dialOuter = spectrumInner - 1;
            dialInner = Math.Max(spectrumInner - SpeedDialBand, spectrumInner * 0.92);
            rim = dialInner - 1;
        }

        /// <summary>
        /// Radii used for both drawing and hit-testing chords. Must match RenderRing:
        /// rChord sits just inside the coverage-bar roots.
        /// </summary>
        private void GetChordLayout(out Point center, out double rim, out double rChord)
        {
            GetRingLayout(out center, out _, out _, out _, out rim, out _, out _);
            var rIn = rim * BarBandInnerRatio;
            // Keep branch vertices hugging the bar (was ~4% inset — looked detached).
            rChord = rIn - Math.Max(1.25, rim * 0.008);
        }

        /// <summary>Quadratic Bézier point at parameter t ∈ [0,1].</summary>
        private static Point BezierPoint(Point p0, Point p1, Point p2, double t)
        {
            var u = 1 - t;
            return new Point(
                u * u * p0.X + 2 * u * t * p1.X + t * t * p2.X,
                u * u * p0.Y + 2 * u * t * p1.Y + t * t * p2.Y);
        }

        private bool TryPlannedHopTooltip(BeatGraph graph, Point point)
        {
            if (graph == null || PlannedFromBeatIndex < 0 || PlannedToBeatIndex < 0)
                return false;

            GetChordLayout(out var center, out _, out var rChord);
            var total = TotalMs();
            var dist = DistanceToChord(point, center, rChord,
                BeatAngle(graph.Beats[PlannedFromBeatIndex], total),
                BeatAngle(graph.Beats[PlannedToBeatIndex], total));

            if (dist > 16)
                return false;

            UpdateBranchTooltip(graph, PlannedFromBeatIndex, PlannedToBeatIndex, point, planned: true);
            return true;
        }

        private bool TryBeginRingScrub(Point point)
        {
            if (!IsInScrubberBand(point))
                return false;

            var ms = PositionMsFromPoint(point);

            if (!ms.HasValue)
                return false;

            var scrubCommand = ScrubToMsCommand;

            if (scrubCommand == null || !scrubCommand.CanExecute(ms.Value))
                return false;

            _isRingScrubbing = true;
            CaptureMouse();
            scrubCommand.Execute(ms.Value);
            Cursor = Cursors.SizeWE;
            return true;
        }

        private void ScrubToPoint(Point point)
        {
            var ms = PositionMsFromPoint(point);
            var scrubCommand = ScrubToMsCommand;

            if (!ms.HasValue || scrubCommand == null || !scrubCommand.CanExecute(ms.Value))
                return;

            scrubCommand.Execute(ms.Value);
        }

        private void EndRingScrub()
        {
            if (!_isRingScrubbing)
                return;

            _isRingScrubbing = false;
            ReleaseMouseCapture();
            EndScrubCommand?.Execute(null);
            Cursor = Cursors.Arrow;
        }

        /// <summary>Map a screen point to a timeline position, or null if too close to center.</summary>
        private long? PositionMsFromPoint(Point point)
        {
            var total = TotalMs();

            if (total <= 0 || ActualWidth <= 0 || ActualHeight <= 0)
                return null;

            var center = new Point(ActualWidth / 2, ActualHeight / 2);
            var dx = point.X - center.X;
            var dy = point.Y - center.Y;

            if (dx * dx + dy * dy < 16)
                return null;

            var fraction = (Math.Atan2(dy, dx) + Math.PI / 2) / (Math.PI * 2);
            fraction = ((fraction % 1) + 1) % 1;
            return (long)Math.Round(fraction * total);
        }

        /// <summary>Beat under the cursor on the colored coverage-bar band.</summary>
        private int HitTestBeat(Point point)
        {
            var graph = Graph;
            var total = TotalMs();

            if (graph == null || graph.Beats.Count == 0 || total <= 0)
                return -1;

            GetChordLayout(out var center, out var rim, out _);
            var rIn = rim * BarBandInnerRatio;
            var barOuter = rIn + rim * 0.28;
            var dx = point.X - center.X;
            var dy = point.Y - center.Y;
            var radius = Math.Sqrt(dx * dx + dy * dy);

            if (radius < rIn || radius > barOuter)
                return -1;

            var fraction = (Math.Atan2(dy, dx) + Math.PI / 2) / (Math.PI * 2);
            fraction = ((fraction % 1) + 1) % 1;

            return FindBeatIndex(graph, (long)(fraction * total));
        }

        /// <summary>
        /// Beat near a chord vertex (just inside / at the bar root). Used to start branch
        /// authoring without stealing the scrubber band on the coverage bars.
        /// </summary>
        private int HitTestBranchOriginBeat(Point point)
        {
            var graph = Graph;
            var total = TotalMs();

            if (graph == null || graph.Beats.Count == 0 || total <= 0)
                return -1;

            GetChordLayout(out var center, out var rim, out var rChord);
            var rIn = rim * BarBandInnerRatio;
            var dist = (point - center).Length;

            // Thin annulus where branch arrows meet the bar.
            if (dist < rChord - 14 || dist > rIn + 10)
                return -1;

            var dx = point.X - center.X;
            var dy = point.Y - center.Y;
            var fraction = (Math.Atan2(dy, dx) + Math.PI / 2) / (Math.PI * 2);
            fraction = ((fraction % 1) + 1) % 1;

            return FindBeatIndex(graph, (long)(fraction * total));
        }

        /// <summary>
        /// Scrubber = coverage bars + section rim. Stops before the speed-dial gap under the EQ.
        /// </summary>
        private bool IsInScrubberBand(Point point)
        {
            GetChordLayout(out var center, out var rim, out _);
            var rIn = rim * BarBandInnerRatio;
            var dist = (point - center).Length;
            return dist >= rIn - 1 && dist <= rim + 0.5;
        }

        private bool TryHitLockedBranch(Point point, BeatGraph graph, out int fromBeat, out int toBeat)
        {
            fromBeat = -1;
            toBeat = -1;
            var locks = LockedBranches;

            if (graph == null || locks == null || locks.Count == 0)
                return false;

            GetChordLayout(out var center, out _, out var rChord);
            var total = TotalMs();
            var bestDist = 14.0;

            foreach (var branchLock in locks)
            {
                var from = branchLock.FromBeatIndex;
                var to = branchLock.ToBeatIndex;

                if (from < 0 || to < 0 || from >= graph.Beats.Count || to >= graph.Beats.Count)
                    continue;

                var dist = DistanceToChord(point, center, rChord,
                    BeatAngle(graph.Beats[from], total),
                    BeatAngle(graph.Beats[to], total));

                if (dist < bestDist)
                {
                    bestDist = dist;
                    fromBeat = from;
                    toBeat = to;
                }
            }

            return fromBeat >= 0;
        }

        private static int NearestBeatWithBranches(BeatGraph graph, int index, int searchRadius)
        {
            var count = graph.Beats.Count;

            for (var d = 0; d <= searchRadius; d++)
            {
                foreach (var candidate in new[] { index + d, index - d })
                {
                    var wrapped = ((candidate % count) + count) % count;

                    if (graph.Beats[wrapped].Neighbors.Count > 0)
                        return wrapped;
                }
            }

            return -1;
        }

        /// <summary>Branch chord under the cursor for a beat, or -1 if none is close enough.</summary>
        private int HitTestBranchEdge(Point point, BeatGraph graph, int fromBeatIndex)
        {
            var beats = graph.Beats;

            if (fromBeatIndex < 0 || fromBeatIndex >= beats.Count)
                return -1;

            GetChordLayout(out var center, out _, out var rChord);
            var total = TotalMs();
            var bestDest = -1;
            // Keep the hit corridor tight so dense fans pick the curve under the cursor,
            // not a distant sibling that still fell inside a wide threshold.
            var bestDist = 14.0;

            foreach (var edge in beats[fromBeatIndex].Neighbors)
            {
                var dest = edge.DestinationIndex;

                if (dest < 0 || dest >= beats.Count)
                    continue;

                var a1 = BeatAngle(beats[fromBeatIndex], total);
                var a2 = BeatAngle(beats[dest], total);
                var dist = DistanceToChord(point, center, rChord, a1, a2);

                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestDest = dest;
                }
            }

            return bestDest;
        }

        private static double DistanceToChord(Point point, Point center, double radius,
            double angle1, double angle2)
        {
            var p0 = Polar(center, radius, angle1);
            var p2 = Polar(center, radius, angle2);
            var p1 = ChordControlPoint(center, angle1, angle2, p0, p2);
            var best = double.MaxValue;
            const int samples = 64;

            for (var i = 0; i <= samples; i++)
            {
                var t = i / (double)samples;
                var oneMinus = 1 - t;
                var bx = oneMinus * oneMinus * p0.X + 2 * oneMinus * t * p1.X + t * t * p2.X;
                var by = oneMinus * oneMinus * p0.Y + 2 * oneMinus * t * p1.Y + t * t * p2.Y;
                var dx = point.X - bx;
                var dy = point.Y - by;
                var dist = Math.Sqrt(dx * dx + dy * dy);

                if (dist < best)
                    best = dist;
            }

            return best;
        }

        /// <summary>
        /// Branch distance → heat (Slice 3 Observe). Closest candidates are warm/bright;
        /// farther ones cool/dim — reads as a hop heatmap on the fan.
        /// </summary>
        private static Color DistanceBranchColor(double distance, double minDistance, double maxDistance)
        {
            var span = Math.Max(0.0001, maxDistance - minDistance);
            var t = Math.Max(0, Math.Min(1, (distance - minDistance) / span));
            // Striking heat: near = hot yellow-white, far = deep crimson-violet
            var near = Color.FromRgb(0xFF, 0xF0, 0xA8);
            var mid = Color.FromRgb(0xFF, 0x7A, 0x3D);
            var far = Color.FromRgb(0x5C, 0x2B, 0x8A);

            if (t < 0.5)
            {
                var u = t / 0.5;
                return Color.FromRgb(
                    (byte)(near.R + (mid.R - near.R) * u),
                    (byte)(near.G + (mid.G - near.G) * u),
                    (byte)(near.B + (mid.B - near.B) * u));
            }

            var v = (t - 0.5) / 0.5;
            return Color.FromRgb(
                (byte)(mid.R + (far.R - mid.R) * v),
                (byte)(mid.G + (far.G - mid.G) * v),
                (byte)(mid.B + (far.B - mid.B) * v));
        }

        private static void GetNeighborDistanceRange(IReadOnlyList<BeatEdge> neighbors,
            out double minDistance, out double maxDistance)
        {
            minDistance = double.MaxValue;
            maxDistance = double.MinValue;

            foreach (var edge in neighbors)
            {
                if (edge.Distance < minDistance)
                    minDistance = edge.Distance;

                if (edge.Distance > maxDistance)
                    maxDistance = edge.Distance;
            }

            if (minDistance == double.MaxValue)
            {
                minDistance = 0;
                maxDistance = 1;
            }
        }

        private static bool IsBranchLocked(IReadOnlyList<BranchLock> locks, int from, int to)
        {
            if (locks == null)
                return false;

            foreach (var branchLock in locks)
            {
                if (branchLock.FromBeatIndex == from && branchLock.ToBeatIndex == to)
                    return true;
            }

            return false;
        }

        private void ClearChain(int[] chain)
        {
            for (var i = 0; i < chain.Length; i++)
                chain[i] = -1;
        }

        private int EffectivePreviewHopDepth()
        {
            // Manual select: fan only from the active tip (committed hops drawn separately).
            if (_manualSelectActive)
                return 1;

            return Math.Max(1, Math.Min(MaxPreviewHopDepth, PreviewHopDepth));
        }

        private static string ChainSignature(int[] chain)
        {
            return chain == null ? string.Empty : string.Join(",", chain);
        }

        /// <summary>Hover chain wins; pinned chain keeps the fan visible after a lock click.</summary>
        private int[] ActivePreviewChain()
        {
            if (_hoverChain[0] >= 0)
                return _hoverChain;

            return _pinnedChain ?? _hoverChain;
        }

        /// <summary>Lock the branch chord under the cursor, not always the deepest hop.</summary>
        private bool TryGetClickedBranch(Point point, BeatGraph graph, int[] chain, out int fromBeat,
            out int toBeat)
        {
            fromBeat = -1;
            toBeat = -1;

            if (graph == null || chain == null)
                return false;

            var depth = EffectivePreviewHopDepth();

            for (var hop = 1; hop <= depth; hop++)
            {
                if (chain[hop] < 0 || chain[hop - 1] < 0)
                    break;

                var parent = chain[hop - 1];

                if (HitTestBranchEdge(point, graph, parent) == chain[hop])
                {
                    fromBeat = parent;
                    toBeat = chain[hop];
                    return true;
                }
            }

            if (chain[0] >= 0)
            {
                var dest = HitTestBranchEdge(point, graph, chain[0]);

                if (dest >= 0)
                {
                    fromBeat = chain[0];
                    toBeat = dest;
                    return true;
                }
            }

            return false;
        }

        private void UpdateHoverChain(Point point)
        {
            ClearChain(_hoverChain);
            _hoverToBeatIndex = -1;

            var graph = Graph;

            if (graph == null)
                return;

            var hit = HitTestBeat(point);

            if (hit < 0)
                return;

            var inspect = NearestBeatWithBranches(graph, hit, 2);

            if (inspect < 0)
                return;

            _hoverChain[0] = inspect;

            if (_pinnedChain != null && _pinnedChain[0] != inspect)
                _pinnedChain = null;

            var depth = EffectivePreviewHopDepth();
            var parent = inspect;

            for (var hop = 1; hop <= depth; hop++)
            {
                var to = HitTestBranchEdge(point, graph, parent);

                if (to < 0)
                    break;

                _hoverChain[hop] = to;
                parent = to;
            }

            if (_hoverChain[1] >= 0)
                _hoverToBeatIndex = _hoverChain[1];
        }

        private void DrawBranchLandmarks(DrawingContext dc, BeatGraph graph, Point center, double rChord,
            long total, IReadOnlyList<BranchLock> locks, int inspectBeat, int playheadBeat, int[] chain)
        {
            // Destination dots removed (Slice 3) — direction is shown by arrowheads on chords.
            // Locked hops still get a small gold tick at the destination for authoring.
            var beats = graph.Beats;

            if (locks == null)
                return;

            foreach (var branchLock in locks)
            {
                var dest = branchLock.ToBeatIndex;

                if (dest < 0 || dest >= beats.Count)
                    continue;

                var dot = Polar(center, rChord, BeatAngle(beats[dest], total));
                dc.DrawEllipse(MakeBrush(LockColor, 0.55), null, dot, 2.0, 2.0);
            }
        }

        private static bool TryGetLockProbability(IReadOnlyList<BranchLock> locks, int from, int to,
            out double probability)
        {
            probability = 1.0;

            if (locks == null)
                return false;

            foreach (var branchLock in locks)
            {
                if (branchLock.FromBeatIndex == from && branchLock.ToBeatIndex == to)
                {
                    probability = branchLock.Probability <= 0 ? 1.0 : branchLock.Probability;
                    return true;
                }
            }

            return false;
        }

        private static string FormatChainPreviewText(int inspect, int edgeCount, List<string> hopText)
        {
            var path = hopText.Count > 0 ? " · " + string.Join(" · ", hopText) : string.Empty;

            if (edgeCount == 0)
                return $"beat {inspect} · no branches — try another beat";

            return $"beat {inspect} · {edgeCount} branch{(edgeCount == 1 ? "" : "es")}{path}";
        }

        private string BuildChainPreviewText(BeatGraph graph, int[] chain)
        {
            if (chain == null || chain[0] < 0)
                return null;

            var beats = graph.Beats;
            var inspect = chain[0];
            var depth = EffectivePreviewHopDepth();
            var hopText = new List<string>();
            var ranked = RankNeighborSummary(beats[inspect].Neighbors, chain.Length > 1 ? chain[1] : -1);

            for (var hop = 1; hop <= depth; hop++)
            {
                if (hop > 1 && chain[hop - 1] < 0)
                    break;

                var parent = hop == 1 ? chain[0] : chain[hop - 1];

                if (parent < 0 || parent >= beats.Count)
                    break;

                if (beats[parent].Neighbors.Count == 0)
                    break;

                var highlight = chain[hop];

                if (highlight >= 0)
                {
                    var edge = FindEdge(beats[parent].Neighbors, highlight);
                    var dest = beats[highlight];
                    var distText = edge != null ? $" d={edge.Distance:0.###}" : string.Empty;
                    var timeText = $" @{FormatBeatTime(dest.StartMs)}";

                    if (TryGetLockProbability(LockedBranches, parent, highlight, out var lockProb))
                        hopText.Add($"hop{hop}: {parent}→{highlight}{distText}{timeText} lock {lockProb * 100:0}%");
                    else
                        hopText.Add($"hop{hop}: {parent}→{highlight}{distText}{timeText}");
                }

                if (highlight < 0)
                    break;
            }

            var inspectBeat = beats[inspect];
            var header =
                $"beat {inspect} @{FormatBeatTime(inspectBeat.StartMs)} · bar+{inspectBeat.IndexInBar} · " +
                $"{beats[inspect].Neighbors.Count} hop" +
                (beats[inspect].Neighbors.Count == 1 ? "" : "s");

            if (!string.IsNullOrEmpty(ranked))
                header += " · " + ranked;

            var path = hopText.Count > 0 ? " · " + string.Join(" · ", hopText) : string.Empty;
            return header + path + " · drag along an arrow to chain · click to lock";
        }

        private static string RankNeighborSummary(IReadOnlyList<BeatEdge> neighbors, int highlightedDest)
        {
            if (neighbors == null || neighbors.Count == 0)
                return string.Empty;

            var ordered = neighbors.OrderBy(e => e.Distance).Take(4).ToList();
            var parts = new List<string>();

            for (var i = 0; i < ordered.Count; i++)
            {
                var e = ordered[i];
                var mark = e.DestinationIndex == highlightedDest ? "*" : "";
                parts.Add($"#{i + 1}→{e.DestinationIndex}{mark}({e.Distance:0.##})");
            }

            return "candidates " + string.Join(" ", parts);
        }

        private static string FormatBeatTime(long ms)
        {
            var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }

        private void DrawChainedBranchPreview(DrawingContext dc, BeatGraph graph, Point center,
            double rChord, long total, IReadOnlyList<BranchLock> locks, int[] chain)
        {
            if (chain == null || chain[0] < 0)
                return;

            var beats = graph.Beats;
            var inspect = chain[0];
            var depth = EffectivePreviewHopDepth();
            var hopText = new List<string>();

            // Multi-origin picks: draw every committed hop (not only a linear chain).
            if (_manualSelectActive && _manualChain.Count > 0)
            {
                foreach (var hop in _manualChain)
                {
                    if (hop.From < 0 || hop.To < 0 ||
                        hop.From >= beats.Count || hop.To >= beats.Count)
                        continue;

                    var a1 = BeatAngle(beats[hop.From], total);
                    var a2 = BeatAngle(beats[hop.To], total);
                    DrawChord(dc, center, rChord, a1, a2,
                        MakePen(LockColor, 1.35, 0.9), arrowhead: true);
                    hopText.Add($"pick {hop.From}→{hop.To}");
                }
            }

            for (var hop = 1; hop <= depth; hop++)
            {
                if (hop > 1 && chain[hop - 1] < 0)
                    break;

                var parent = hop == 1 ? chain[0] : chain[hop - 1];

                if (parent < 0 || parent >= beats.Count)
                    break;

                var edges = beats[parent].Neighbors;

                if (edges.Count == 0)
                    break;

                GetNeighborDistanceRange(edges, out var minDist, out var maxDist);
                var layerAlpha = hop == 1 ? 0.40 : hop == 2 ? 0.26 : 0.16;
                var layerRadius = rChord * (1 - (hop - 1) * 0.06);
                var committedFromParent = _manualSelectActive
                    ? _manualChain.Where(h => h.From == parent).Select(h => h.To).ToList()
                    : new List<int>();
                var highlight = _manualSelectActive
                    ? (_hoverToBeatIndex >= 0 ? _hoverToBeatIndex : -1)
                    : chain[hop];

                foreach (var edge in edges)
                {
                    var dest = edge.DestinationIndex;

                    if (dest < 0 || dest >= beats.Count)
                        continue;

                    var a1 = BeatAngle(beats[parent], total);
                    var a2 = BeatAngle(beats[dest], total);
                    var branchColor = edge.IsBridge
                        ? BridgeColor
                        : DistanceBranchColor(edge.Distance, minDist, maxDist);
                    var locked = IsBranchLocked(locks, parent, dest);
                    var isCommitted = committedFromParent.Contains(dest);
                    var isHighlighted = dest == highlight && !isCommitted;

                    // Keep showing remaining candidates from this origin even after one pick
                    // (multi-select from same beat is allowed; dim committed ones as gold above).
                    if (isCommitted)
                        continue;

                    var width = isHighlighted ? 1.6 : locked ? 0.95 : 0.75;
                    var alpha = isHighlighted ? 0.95
                        : locked ? 0.28
                        : (highlight >= 0 ? layerAlpha * 0.22 : layerAlpha);

                    if (isHighlighted)
                    {
                        DrawChord(dc, center, layerRadius, a1, a2,
                            MakePen(branchColor, width, alpha),
                            arrowhead: true,
                            haloPen: MakePen(Colors.White, width + 2.6, 0.9));
                    }
                    else
                    {
                        DrawChord(dc, center, layerRadius, a1, a2,
                            MakePen(locked ? LockColor : branchColor, width, alpha),
                            arrowhead: true);
                    }
                }

                if (highlight >= 0 && !committedFromParent.Contains(highlight))
                {
                    var edge = FindEdge(edges, highlight);
                    var distText = edge != null ? $" d={edge.Distance:0.###}" : string.Empty;

                    if (TryGetLockProbability(locks, parent, highlight, out var lockProb))
                        hopText.Add($"hop{hop}: {parent}→{highlight}{distText} lock {lockProb * 100:0}%");
                    else
                        hopText.Add($"hop{hop}: {parent}→{highlight}{distText}");
                }

                // Idle mode: speculative next-hop preview only.
                if (_manualSelectActive)
                    break;

                if (highlight < 0)
                    break;
            }

            var origin = Polar(center, rChord, BeatAngle(beats[inspect], total));
            dc.DrawEllipse(MakeBrush(HotColor, 0.9), null, origin, 4, 4);
        }

        private static BeatEdge FindEdge(IReadOnlyList<BeatEdge> edges, int destination)
        {
            foreach (var edge in edges)
            {
                if (edge.DestinationIndex == destination)
                    return edge;
            }

            return null;
        }

        private string BuildHoverHint(BeatGraph graph, int hoveredBeatIndex, int[] chain)
        {
            if (hoveredBeatIndex < 0 || graph == null)
                return null;

            if (chain != null && chain[0] >= 0)
                return null;

            if (hoveredBeatIndex >= graph.Beats.Count)
                return null;

            if (graph.Beats[hoveredBeatIndex].Neighbors.Count == 0)
                return $"beat {hoveredBeatIndex} · no branches here";

            return "no branchable beat near cursor";
        }

        #endregion

        #region Rendering

        // Muted, dark-theme palette: green stays an accent (playhead/prediction), never a slab.
        private static readonly Color BackgroundColor = Color.FromRgb(0x0F, 0x0F, 0x0F);

        private static readonly Color RimColor = Color.FromRgb(0x3A, 0x3A, 0x3A);

        private static readonly Color AccentColor = Color.FromRgb(0x1D, 0xB9, 0x54);

        private static readonly Color LockColor = Color.FromRgb(0xFF, 0xD1, 0x66);

        /// <summary>Slice 4 inter-component / orphan bridges — cooler teal so they read as safety edges.</summary>
        private static readonly Color BridgeColor = Color.FromRgb(0x5E, 0xC8, 0xC8);

        private static readonly Color HotColor = Colors.White;

        private static readonly Color GhostColor = Color.FromRgb(0xA0, 0xB4, 0xDC);

        private static readonly Color TextColor = Color.FromRgb(0xE0, 0xE0, 0xE0);

        private static readonly Color MutedTextColor = Color.FromRgb(0x8A, 0x8A, 0x8A);

        /// <summary>Muted section hues (steel blue, sea green, violet, amber, teal, rose).</summary>
        private static readonly double[] SectionHues = { 210, 158, 278, 30, 190, 330 };

        // Render layer order (bottom → top):
        //   0. FractalBackgroundControl — Mandelbrot behind this canvas in the visual tree
        //   1. Center disc (skipped in mini player mode so the transport backdrop shows through)
        //   2. Winamp cascading spectrum — outer ring band (outside beat rim), translucent
        //   3. Thin speed-dial track (between scrubber and equalizer)
        //   4. Outer rim circle + section arcs
        //   5. Beat coverage bars, trail, headlights, playhead
        //   6. Branch landmarks/chords, planned-jump chord, jump flashes, locked branches
        // Spectrum sits behind beat bars so the map stays primary.
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            var width = ActualWidth;
            var height = ActualHeight;

            if (width < 24 || height < 24)
                return;

            GetRingLayout(out var center, out _, out var spectrumInner, out var spectrumOuter,
                out var rim, out var dialInner, out var dialOuter);
            var rIn = rim * BarBandInnerRatio;

            // Shade only the inner hole so Mandelbrot shows in the beat annulus + spectrum band.
            if (!MiniPlayerMode)
                DrawCenterDisc(dc, center, rIn);

            // Cascading Winamp bars live on the outer ring layer (outside beat coverage bars).
            _equalizer.Render(dc, EnergyProvider, center, spectrumInner, spectrumOuter, EqualizerPreset);

            // Quiet speed dial between scrubber/section rim and the equalizer floor.
            if (!MiniPlayerMode)
                DrawSpeedDialBand(dc, center, dialInner, dialOuter);

            dc.DrawEllipse(null, MakePen(RimColor, 1), center, rim, rim);

            var graph = Graph;

            if (graph == null || graph.Beats.Count == 0 || TotalMs() <= 0)
            {
                RenderFallback(dc, center, rim);
                return;
            }

            RenderRing(dc, graph, center, rim);
            DrawAnalysisTooltip(dc, graph);
        }

        /// <summary>No beat graph yet: plain progress ring plus a hint.</summary>
        private void RenderFallback(DrawingContext dc, Point center, double outer)
        {
            var duration = DurationMs;

            if (duration > 0)
            {
                var position = Math.Max(0, Math.Min(EstimatedPositionMs(), duration));
                var sweep = position / (double)duration * Math.PI * 2;
                DrawArc(dc, center, outer - 8, -Math.PI / 2, -Math.PI / 2 + sweep,
                    MakePen(AccentColor, 2.5));
                DrawRadialLine(dc, center, outer - 16, outer - 2, -Math.PI / 2 + sweep,
                    MakePen(AccentColor, 2.5));
            }

            // Mini player: keep a short center hint above the transport backdrop.
            if (MiniPlayerMode)
            {
                var baseOffset = -outer * 0.42;
                DrawCenteredText(dc, center, baseOffset - 8, duration > 0 ? "no beat map" : "no track",
                    14, TextColor, FontWeights.SemiBold);
                DrawCenteredText(dc, center, baseOffset + 12, "Analyze track to build the ring",
                    10, MutedTextColor, FontWeights.Normal);
            }
        }

        private void RenderRing(DrawingContext dc, BeatGraph graph, Point center, double outer)
        {
            var now = Clock.Elapsed.TotalMilliseconds;
            var beats = graph.Beats;
            var total = TotalMs();
            var rIn = outer * BarBandInnerRatio;
            var ext = outer * 0.28;
            var minLen = outer * 0.05;
            var rChord = rIn - Math.Max(1.25, outer * 0.008);

            var barWidth = Math.Max(1.0, Math.Min(6.0, 2 * Math.PI * rIn / beats.Count * 0.55));

            var maxPlay = 1;

            for (var i = 0; i < _playCounts.Length; i++)
            {
                if (_playCounts[i] > maxPlay)
                    maxPlay = _playCounts[i];
            }

            var sectionColors = BuildSectionColors();
            var currentIndex = FindBeatIndex(graph, EstimatedPositionMs());
            var locks = LockedBranches;

            DrawSectionRim(dc, center, outer - 4, total, sectionColors);

            // Coverage bars: length = play count; color blends section tint with play-frequency heat.
            for (var i = 0; i < beats.Count; i++)
            {
                var t = _playCounts.Length == beats.Count ? _playCounts[i] / (double)maxPlay : 0;
                var length = minLen + t * ext;
                var angle = BeatAngle(beats[i], total);
                DrawRadialLine(dc, center, rIn, rIn + length, angle,
                    MakePen(BeatColor(beats[i], sectionColors), barWidth));
            }

            // Fading trail of recently played beats.
            foreach (var (beatIndex, atMs) in _trail)
            {
                var alpha = 1 - (now - atMs) / TrailFadeMs;

                if (alpha <= 0 || beatIndex >= beats.Count)
                    continue;

                var t = _playCounts.Length == beats.Count ? _playCounts[beatIndex] / (double)maxPlay : 0;
                var angle = BeatAngle(beats[beatIndex], total);
                DrawRadialLine(dc, center, rIn, rIn + minLen + t * ext, angle,
                    MakePen(HotColor, barWidth, alpha * 0.4));
            }

            var plannedFrom = PlannedFromBeatIndex;
            var plannedTo = PlannedToBeatIndex;

            // Headlights: pre-illuminate the straight road between the playhead and the planned jump.
            if (plannedFrom >= 0 && plannedFrom < beats.Count && currentIndex >= 0 &&
                currentIndex <= plannedFrom)
            {
                var span = Math.Max(1, plannedFrom - currentIndex);

                for (var i = currentIndex; i <= plannedFrom; i++)
                {
                    var fade = 1 - (i - currentIndex) / (double)(span + 1);
                    var t = _playCounts.Length == beats.Count ? _playCounts[i] / (double)maxPlay : 0;
                    var angle = BeatAngle(beats[i], total);
                    DrawRadialLine(dc, center, rIn, rIn + minLen + t * ext + 4, angle,
                        MakePen(HotColor, barWidth, 0.08 + 0.3 * fade));
                }
            }

            // Playhead: continuous angle from clock time (not beat-start snap — that looked "stuck").
            if (currentIndex >= 0 && currentIndex < beats.Count)
            {
                var t = _playCounts.Length == beats.Count ? _playCounts[currentIndex] / (double)maxPlay : 0;
                var length = minLen + t * ext + 6;
                var angle = MsToAngle(EstimatedPositionMs(), total);
                DrawRadialLine(dc, center, rIn - 4, rIn + length, angle,
                    MakePen(HotColor, barWidth * 3, 0.22));
                DrawRadialLine(dc, center, rIn - 4, rIn + length, angle,
                    MakePen(HotColor, barWidth + 1.5));
            }

            // Planned / fired jump endpoints: bold glowing bars on origin and destination.
            if (plannedFrom >= 0 && plannedFrom < beats.Count)
                DrawJumpEndpointGlow(dc, center, rIn, minLen, ext, barWidth,
                    BeatAngle(beats[plannedFrom], total),
                    _playCounts.Length == beats.Count ? _playCounts[plannedFrom] / (double)maxPlay : 0,
                    AccentColor);

            if (plannedTo >= 0 && plannedTo < beats.Count)
                DrawJumpEndpointGlow(dc, center, rIn, minLen, ext, barWidth,
                    BeatAngle(beats[plannedTo], total),
                    _playCounts.Length == beats.Count ? _playCounts[plannedTo] / (double)maxPlay : 0,
                    AccentColor);

            foreach (var (from, to, atMs) in _flashes)
            {
                var alpha = 1 - (now - atMs) / FlashFadeMs;

                if (alpha <= 0)
                    continue;

                if (from >= 0 && from < beats.Count)
                    DrawJumpEndpointGlow(dc, center, rIn, minLen, ext, barWidth,
                        BeatAngle(beats[from], total),
                        _playCounts.Length == beats.Count ? _playCounts[from] / (double)maxPlay : 0,
                        HotColor, alpha);

                if (to >= 0 && to < beats.Count)
                    DrawJumpEndpointGlow(dc, center, rIn, minLen, ext, barWidth,
                        BeatAngle(beats[to], total),
                        _playCounts.Length == beats.Count ? _playCounts[to] / (double)maxPlay : 0,
                        HotColor, alpha);
            }

            // Chord / hop visuals only when hop mode is enabled in mini player.
            var previewChain = ActivePreviewChain();
            var inspectBeat = previewChain[0] >= 0 ? previewChain[0] : currentIndex;
            var showHopChords = !MiniPlayerMode || MiniPlayerHopMode;

            if (showHopChords)
            {
                DrawBranchLandmarks(dc, graph, center, rChord, total, locks, inspectBeat, currentIndex,
                    previewChain);

                if (previewChain[0] >= 0)
                    DrawChainedBranchPreview(dc, graph, center, rChord, total, locks, previewChain);
            }

            // The predictor's mind: one pulsing directed chord for the planned jump.
            if (showHopChords && plannedFrom >= 0 && plannedTo >= 0 && plannedFrom < beats.Count && plannedTo < beats.Count)
            {
                var pulse = 0.35 + 0.22 * Math.Sin(now / 140);
                DrawChord(dc, center, rChord, BeatAngle(beats[plannedFrom], total),
                    BeatAngle(beats[plannedTo], total), MakePen(AccentColor, 1.1, pulse), arrowhead: true);
            }

            // Fired jumps flash white and decay.
            if (showHopChords)
            {
                foreach (var (from, to, atMs) in _flashes)
                {
                    var alpha = 1 - (now - atMs) / FlashFadeMs;

                    if (alpha <= 0 || from >= beats.Count || to >= beats.Count)
                        continue;

                    DrawChord(dc, center, rChord, BeatAngle(beats[from], total),
                        BeatAngle(beats[to], total), MakePen(HotColor, 0.75 + 1.1 * alpha, alpha * 0.65));
                }
            }

            // Locked branches: thin rails with a flowing glow along the chord.
            if (showHopChords && locks != null)
            {
                var inspect = previewChain[0];

                foreach (var branchLock in locks)
                {
                    if (branchLock.FromBeatIndex >= beats.Count || branchLock.ToBeatIndex >= beats.Count ||
                        branchLock.FromBeatIndex < 0 || branchLock.ToBeatIndex < 0)
                        continue;

                    if (inspect >= 0 && branchLock.FromBeatIndex == inspect)
                        continue;

                    var a1 = BeatAngle(beats[branchLock.FromBeatIndex], total);
                    var a2 = BeatAngle(beats[branchLock.ToBeatIndex], total);
                    DrawLockedFlowChord(dc, center, rChord, a1, a2, now, inspect >= 0);
                }
            }
        }

        /// <summary>Inner stage disc only — play/pause sits on top in the page chrome.</summary>
        private void DrawCenterDisc(DrawingContext dc, Point center, double rIn)
        {
            var fill = ThemeColor("AppSurfaceBrush", Color.FromRgb(0x14, 0x14, 0x18));
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0xE8, fill.R, fill.G, fill.B)), null,
                center, rIn, rIn);
        }

        /// <summary>
        /// Quiet track between scrubber and equalizer. Press + drag to change Local WAV rate.
        /// </summary>
        private void DrawSpeedDialBand(DrawingContext dc, Point center, double inner, double outer)
        {
            var press = Math.Max(0, Math.Min(1, _centerPressAmount));
            var modified = Math.Abs(_playbackRate - 1.0) > 0.001;
            var mid = (inner + outer) * 0.5;
            var thickness = Math.Max(2.0, outer - inner - 0.5);

            var border = ThemeColor("AppBorderBrush", Color.FromRgb(0x3A, 0x3A, 0x42));
            var accent = ThemeColor("AppAccentBrush", Color.FromRgb(0x1D, 0xB9, 0x54));

            var alpha = press > 0.05 ? 0.5 : modified ? 0.36 : 0.2;
            var color = press > 0.05 || modified ? accent : border;
            dc.DrawEllipse(null, MakePen(color, thickness, alpha), center, mid, mid);
        }

        private Color ThemeColor(string resourceKey, Color fallback)
        {
            try
            {
                if (TryFindResource(resourceKey) is SolidColorBrush brush)
                    return brush.Color;
            }
            catch
            {
                // Fall through — design-time / missing theme key.
            }

            return fallback;
        }

        /// <summary>
        /// Thin locked-edge rail with a professional traveling glow along the quadratic chord.
        /// </summary>
        private void DrawLockedFlowChord(DrawingContext dc, Point center, double radius,
            double angle1, double angle2, double nowMs, bool dimmed)
        {
            var p1 = Polar(center, radius, angle1);
            var p2 = Polar(center, radius, angle2);
            var control = ChordControlPoint(center, angle1, angle2, p1, p2);

            var baseAlpha = dimmed ? 0.22 : 0.38;
            DrawQuadraticStroke(dc, p1, control, p2,
                MakePen(LockColor, 0.7, baseAlpha));

            var phase = (nowMs / 1400.0) % 1.0;
            const int packets = 2;

            for (var p = 0; p < packets; p++)
            {
                var tCenter = (phase + p / (double)packets) % 1.0;
                const double half = 0.07;
                var t0 = Math.Max(0.02, tCenter - half);
                var t1 = Math.Min(0.98, tCenter + half);

                if (t1 <= t0)
                    continue;

                var samples = 7;
                Point? prev = null;

                for (var i = 0; i <= samples; i++)
                {
                    var t = t0 + (t1 - t0) * (i / (double)samples);
                    var pt = BezierPoint(p1, control, p2, t);
                    var edge = 1.0 - Math.Abs((t - tCenter) / half);
                    edge = Math.Max(0, Math.Min(1, edge));
                    var alpha = (dimmed ? 0.35 : 0.75) * edge * edge;

                    if (prev.HasValue)
                    {
                        dc.DrawLine(MakePen(LockColor, 1.15 + 0.9 * edge, alpha), prev.Value, pt);
                        dc.DrawLine(MakePen(Colors.White, 0.55, alpha * 0.45), prev.Value, pt);
                    }

                    prev = pt;
                }

                var head = BezierPoint(p1, control, p2, tCenter);
                dc.DrawEllipse(MakeBrush(LockColor, dimmed ? 0.35 : 0.7), null, head, 1.8, 1.8);
                dc.DrawEllipse(MakeBrush(Colors.White, dimmed ? 0.2 : 0.45), null, head, 0.75, 0.75);
            }
        }

        private Color[] BuildSectionColors()
        {
            var sections = SectionStartsSec;
            var count = Math.Max(1, sections?.Count ?? 0);
            var colors = new Color[count];

            for (var i = 0; i < count; i++)
            {
                // Intro/outro grey like the reference; everything else cycles muted hues.
                colors[i] = sections != null && sections.Count >= 3 && (i == 0 || i == count - 1)
                    ? FromHsl(226, 0.10, 0.42)
                    : FromHsl(SectionHues[i % SectionHues.Length], 0.30, 0.48);
            }

            return colors;
        }

        private Color BeatColor(BeatNode beat, Color[] sectionColors)
        {
            var sections = SectionStartsSec;

            if (sections == null || sections.Count == 0)
                return FromHsl(215, 0.18, 0.52);

            var index = 0;

            for (var i = sections.Count - 1; i >= 0; i--)
            {
                if (beat.StartSec >= sections[i])
                {
                    index = i;
                    break;
                }
            }

            return sectionColors[Math.Min(index, sectionColors.Length - 1)];
        }

        private void DrawSectionRim(DrawingContext dc, Point center, double radius, long totalMs,
            Color[] sectionColors)
        {
            var sections = SectionStartsSec;

            if (sections == null || sections.Count < 2 || totalMs <= 0)
                return;

            for (var i = 0; i < sections.Count; i++)
            {
                var fromMs = (long)(sections[i] * 1000);
                var toMs = i + 1 < sections.Count ? (long)(sections[i + 1] * 1000) : totalMs;
                var a1 = MsToAngle(fromMs, totalMs) + 0.015;
                var a2 = MsToAngle(toMs, totalMs) - 0.015;

                if (a2 <= a1)
                    continue;

                DrawArc(dc, center, radius, a1, a2,
                    MakePen(MakeColor(sectionColors[i], 0.42), 1.15, roundCaps: true));
            }
        }

        private static double BeatAngle(BeatNode beat, long totalMs)
        {
            return MsToAngle(beat.StartMs, totalMs);
        }

        private static double MsToAngle(long ms, long totalMs)
        {
            var fraction = totalMs > 0 ? ms / (double)totalMs : 0;
            return fraction * Math.PI * 2 - Math.PI / 2;
        }

        private static Point Polar(Point center, double radius, double angleRad)
        {
            return new Point(center.X + radius * Math.Cos(angleRad),
                center.Y + radius * Math.Sin(angleRad));
        }

        /// <summary>Distance from the center to the element's rectangular edge along an angle.</summary>
        private static double DistanceToSquareEdge(Point center, double width, double height,
            double angleRad)
        {
            var cos = Math.Cos(angleRad);
            var sin = Math.Sin(angleRad);
            var distX = double.PositiveInfinity;
            var distY = double.PositiveInfinity;

            if (Math.Abs(cos) > 0.0001)
                distX = Math.Abs(cos > 0 ? (width - center.X) / cos : -center.X / cos);

            if (Math.Abs(sin) > 0.0001)
                distY = Math.Abs(sin > 0 ? (height - center.Y) / sin : -center.Y / sin);

            return Math.Min(distX, distY);
        }

        private static void DrawJumpEndpointGlow(DrawingContext dc, Point center, double rIn,
            double minLen, double ext, double barWidth, double angle, double playT, Color color,
            double alpha = 1.0)
        {
            var length = minLen + playT * ext + 10;
            var a = Math.Max(0, Math.Min(1, alpha));
            DrawRadialLine(dc, center, rIn - 2, rIn + length + 4, angle,
                MakePen(color, barWidth * 4.2, 0.18 * a));
            DrawRadialLine(dc, center, rIn - 1, rIn + length + 2, angle,
                MakePen(color, barWidth * 2.4, 0.55 * a));
            DrawRadialLine(dc, center, rIn, rIn + length, angle,
                MakePen(Colors.White, barWidth * 1.35, 0.92 * a));
        }

        private static void DrawRadialLine(DrawingContext dc, Point center, double inner, double outer,
            double angleRad, Pen pen)
        {
            dc.DrawLine(pen, Polar(center, inner, angleRad), Polar(center, outer, angleRad));
        }

        /// <summary>
        /// Quadratic chord pulled toward the center. Optional halo stops before the arrowhead
        /// so the outline does not swallow the tip.
        /// </summary>
        private static void DrawChord(DrawingContext dc, Point center, double radius,
            double angle1, double angle2, Pen pen, bool arrowhead = true, Pen haloPen = null)
        {
            var p1 = Polar(center, radius, angle1);
            var p2 = Polar(center, radius, angle2);
            var control = ChordControlPoint(center, angle1, angle2, p1, p2);
            // Leave room for the arrowhead so strokes meet the triangle base, not the tip.
            var shaftEnd = arrowhead ? BezierPoint(p1, control, p2, 0.86) : p2;

            if (haloPen != null)
                DrawQuadraticStroke(dc, p1, control, shaftEnd, haloPen);

            DrawQuadraticStroke(dc, p1, control, shaftEnd, pen);

            if (arrowhead)
                DrawArrowHead(dc, control, p2, pen);
        }

        private static void DrawQuadraticStroke(DrawingContext dc, Point p0, Point p1, Point p2, Pen pen)
        {
            var geometry = new StreamGeometry();

            using (var context = geometry.Open())
            {
                context.BeginFigure(p0, false, false);
                context.QuadraticBezierTo(p1, p2, true, false);
            }

            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);
        }

        private static void DrawArrowHead(DrawingContext dc, Point control, Point tip, Pen pen)
        {
            // Quadratic Bézier tangent at t=1 points toward tip from control.
            var tx = tip.X - control.X;
            var ty = tip.Y - control.Y;
            var len = Math.Sqrt(tx * tx + ty * ty);

            if (len < 1e-4)
                return;

            tx /= len;
            ty /= len;

            var size = Math.Max(5.0, pen.Thickness * 4.0);
            var baseX = tip.X - tx * size;
            var baseY = tip.Y - ty * size;
            var wing = size * 0.48;
            var px = -ty;
            var py = tx;

            var left = new Point(baseX + px * wing, baseY + py * wing);
            var right = new Point(baseX - px * wing, baseY - py * wing);
            var fill = pen.Brush as SolidColorBrush;
            var brush = fill != null
                ? new SolidColorBrush(fill.Color) { Opacity = fill.Opacity }
                : new SolidColorBrush(Colors.White) { Opacity = 0.85 };
            brush.Freeze();

            var head = new StreamGeometry();

            using (var context = head.Open())
            {
                context.BeginFigure(tip, true, true);
                context.LineTo(left, true, false);
                context.LineTo(right, true, false);
            }

            head.Freeze();
            dc.DrawGeometry(brush, null, head);
        }

        private static Point ChordControlPoint(Point center, double angle1, double angle2,
            Point p1, Point p2)
        {
            var delta = Math.Abs(angle1 - angle2) % (Math.PI * 2);

            if (delta > Math.PI)
                delta = Math.PI * 2 - delta;

            var pull = 0.18 + 0.72 * (delta / Math.PI);
            var midX = (p1.X + p2.X) / 2;
            var midY = (p1.Y + p2.Y) / 2;

            return new Point(center.X + (midX - center.X) * (1 - pull),
                center.Y + (midY - center.Y) * (1 - pull));
        }

        /// <summary>BeatThis IndexInBar can exceed 0–3; compare musical phase mod period.</summary>
        private static int CircularBarPhaseDelta(int barPosA, int barPosB, int period = 4)
        {
            var a = ((barPosA % period) + period) % period;
            var b = ((barPosB % period) + period) % period;
            var d = Math.Abs(a - b);
            return Math.Min(d, period - d);
        }

        private static Point ChordMidpoint(Point center, double radius, double angle1, double angle2)
        {
            var p1 = Polar(center, radius, angle1);
            var p2 = Polar(center, radius, angle2);
            var control = ChordControlPoint(center, angle1, angle2, p1, p2);

            // Quadratic Bézier at t = 0.5.
            return new Point(0.25 * p1.X + 0.5 * control.X + 0.25 * p2.X,
                0.25 * p1.Y + 0.5 * control.Y + 0.25 * p2.Y);
        }

        private static void DrawArc(DrawingContext dc, Point center, double radius,
            double fromAngle, double toAngle, Pen pen)
        {
            if (toAngle <= fromAngle)
                return;

            var geometry = new StreamGeometry();

            using (var context = geometry.Open())
            {
                context.BeginFigure(Polar(center, radius, fromAngle), false, false);
                context.ArcTo(Polar(center, radius, toAngle), new Size(radius, radius), 0,
                    toAngle - fromAngle > Math.PI, SweepDirection.Clockwise, true, false);
            }

            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);
        }

        private void DrawCenteredText(DrawingContext dc, Point center, double offsetY, string text,
            double size, Color color, FontWeight weight)
        {
            var formatted = new FormattedText(text, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal),
                size, new SolidColorBrush(color), VisualTreeHelper.GetDpi(this).PixelsPerDip);

            dc.DrawText(formatted, new Point(center.X - formatted.Width / 2,
                center.Y + offsetY - formatted.Height / 2));
        }

        private static Color MakeColor(Color color, double alpha)
        {
            return Color.FromArgb((byte)Math.Max(0, Math.Min(255, alpha * 255)),
                color.R, color.G, color.B);
        }

        private static Pen MakePen(Color color, double thickness, double alpha = 1,
            bool roundCaps = false)
        {
            var pen = new Pen(new SolidColorBrush(MakeColor(color, alpha)), thickness);

            if (roundCaps)
            {
                pen.StartLineCap = PenLineCap.Round;
                pen.EndLineCap = PenLineCap.Round;
            }

            pen.Freeze();
            return pen;
        }

        private static Brush MakeBrush(Color color, double alpha)
        {
            var brush = new SolidColorBrush(MakeColor(color, alpha));
            brush.Freeze();
            return brush;
        }

        private static Color FromHsl(double hue, double saturation, double lightness)
        {
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

        #endregion
    }
}
