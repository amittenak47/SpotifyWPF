using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
    /// remains visible and draggable), hit-testing is limited to the beat-bar band and window
    /// corners, and the bars extend to the square window edges so the transparent window has no
    /// abrupt circular clip.
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

        public static readonly DependencyProperty EnergyProviderProperty =
            DependencyProperty.Register(nameof(EnergyProvider), typeof(IVisualEnergyProvider),
                typeof(JukeboxRingCanvas),
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

        /// <summary>Shared music-energy source driving the plasma equalizer in the annulus.</summary>
        public IVisualEnergyProvider EnergyProvider
        {
            get => (IVisualEnergyProvider)GetValue(EnergyProviderProperty);
            set => SetValue(EnergyProviderProperty, value);
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

            if (Graph != null)
            {
                UpdatePlayCounts(EstimatedPositionMs());
                SyncHudText();
            }

            InvalidateVisual();
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
            var previewChain = ActivePreviewChain();
            string hover;

            if (previewChain[0] >= 0)
                hover = BuildChainPreviewText(graph, previewChain);
            else
                hover = BuildHoverHint(graph, _hoverBeatIndex, previewChain);

            PublishHudText(hover != null ? baseLine + " · " + hover : baseLine + " · click a branch line to lock that hop");
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

        /// <summary>
        /// Mini player: the center disc passes clicks through (the transport backdrop underneath
        /// handles window drag); the beat-bar band and the corner extensions stay interactive.
        /// </summary>
        private bool IsInteractiveMiniPlayerPoint(Point point)
        {
            var center = new Point(ActualWidth / 2, ActualHeight / 2);
            var outer = Math.Min(ActualWidth, ActualHeight) / 2 - 2;
            var vector = point - center;
            var dist = vector.Length;

            if (dist < outer * BarBandInnerRatio)
                return false;

            if (dist <= outer)
                return true;

            var edgeDist = DistanceToSquareEdge(center, ActualWidth, ActualHeight,
                Math.Atan2(vector.Y, vector.X));

            return dist <= edgeDist;
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

            var hit = HitTestBeat(point);
            var before = ChainSignature(_hoverChain);
            UpdateHoverChain(point);
            var after = ChainSignature(_hoverChain);

            if (hit != _hoverBeatIndex || before != after)
            {
                _hoverBeatIndex = hit;
                SyncHudText();
                InvalidateVisual();
            }

            Cursor = hit >= 0 ? Cursors.Cross : Cursors.Arrow;
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);

            if (_isRingScrubbing)
                EndRingScrub();

            _hoverBeatIndex = -1;
            _hoverToBeatIndex = -1;
            ClearChain(_hoverChain);
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);

            var graph = Graph;
            var command = ToggleLockCommand;
            var point = e.GetPosition(this);

            if (graph != null && command != null)
            {
                var chain = ActivePreviewChain();

                if (TryGetClickedBranch(point, graph, chain, out var fromBeat, out var toBeat))
                {
                    e.Handled = MiniPlayerMode;

                    _pinnedChain = (int[])chain.Clone();

                    var click = new RingBranchClick
                    {
                        FromBeatIndex = fromBeat,
                        ToBeatIndex = toBeat
                    };

                    if (command.CanExecute(click))
                        command.Execute(click);

                    return;
                }
            }

            if (TryBeginRingScrub(point))
                e.Handled = MiniPlayerMode;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);

            if (_isRingScrubbing)
                EndRingScrub();
        }

        private bool TryBeginRingScrub(Point point)
        {
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

        /// <summary>Beat under the cursor, or -1 outside the bar band.</summary>
        private int HitTestBeat(Point point)
        {
            var graph = Graph;
            var total = TotalMs();

            if (graph == null || graph.Beats.Count == 0 || total <= 0)
                return -1;

            var center = new Point(ActualWidth / 2, ActualHeight / 2);
            var outer = Math.Min(ActualWidth, ActualHeight) / 2 - 2;
            var inner = outer * 0.45;
            var dx = point.X - center.X;
            var dy = point.Y - center.Y;
            var radius = Math.Sqrt(dx * dx + dy * dy);

            if (radius < inner)
                return -1;

            if (radius > outer)
            {
                // Mini player: the corner extensions past the ring still map to their beat.
                if (!MiniPlayerMode ||
                    radius > DistanceToSquareEdge(center, ActualWidth, ActualHeight, Math.Atan2(dy, dx)))
                    return -1;
            }

            var fraction = (Math.Atan2(dy, dx) + Math.PI / 2) / (Math.PI * 2);
            fraction = ((fraction % 1) + 1) % 1;

            return FindBeatIndex(graph, (long)(fraction * total));
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

            var center = new Point(ActualWidth / 2, ActualHeight / 2);
            var outer = Math.Min(ActualWidth, ActualHeight) / 2 - 2;
            var rChord = outer * BarBandInnerRatio - outer * 0.04;
            var total = TotalMs();
            var bestDest = -1;
            var bestDist = 26.0;

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

            for (var i = 0; i <= 24; i++)
            {
                var t = i / 24.0;
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

        /// <summary>Closer branches are hotter (red/orange); farther branches are cooler (blue).</summary>
        private static Color DistanceBranchColor(double distance, double minDistance, double maxDistance)
        {
            var span = Math.Max(0.0001, maxDistance - minDistance);
            var t = Math.Max(0, Math.Min(1, (distance - minDistance) / span));
            var hue = t * 220;
            return FromHsl(hue, 0.78, 0.56);
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
            var beats = graph.Beats;
            var drawn = new HashSet<long>();

            void DrawDest(int from, int dest, double alpha, double size, Color color)
            {
                if (from < 0 || dest < 0 || from >= beats.Count || dest >= beats.Count)
                    return;

                var key = ((long)from << 32) | (uint)dest;

                if (!drawn.Add(key))
                    return;

                var dot = Polar(center, rChord, BeatAngle(beats[dest], total));
                dc.DrawEllipse(MakeBrush(color, alpha), null, dot, size, size);
            }

            if (locks != null)
            {
                foreach (var branchLock in locks)
                {
                    DrawDest(branchLock.FromBeatIndex, branchLock.ToBeatIndex, 0.9, 3.4, LockColor);
                }
            }

            if (inspectBeat >= 0)
            {
                foreach (var edge in beats[inspectBeat].Neighbors)
                    DrawDest(inspectBeat, edge.DestinationIndex, 0.42, 3.0, GhostColor);
            }

            if (playheadBeat >= 0 && playheadBeat != inspectBeat)
            {
                foreach (var edge in beats[playheadBeat].Neighbors)
                    DrawDest(playheadBeat, edge.DestinationIndex, 0.24, 2.6, GhostColor);
            }

            if (chain != null)
            {
                for (var hop = 1; hop < chain.Length; hop++)
                {
                    if (chain[hop - 1] < 0)
                        break;

                    foreach (var edge in beats[chain[hop - 1]].Neighbors)
                        DrawDest(chain[hop - 1], edge.DestinationIndex, 0.3, 2.8, GhostColor);
                }
            }
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
                    hopText.Add($"hop{hop}: {parent}→{highlight}");

                if (highlight < 0)
                    break;
            }

            return FormatChainPreviewText(inspect, beats[inspect].Neighbors.Count, hopText);
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
                var layerAlpha = hop == 1 ? 0.82 : hop == 2 ? 0.52 : 0.34;
                var layerRadius = rChord * (1 - (hop - 1) * 0.06);
                var highlight = chain[hop];

                foreach (var edge in edges)
                {
                    var dest = edge.DestinationIndex;

                    if (dest < 0 || dest >= beats.Count)
                        continue;

                    var a1 = BeatAngle(beats[parent], total);
                    var a2 = BeatAngle(beats[dest], total);
                    var branchColor = DistanceBranchColor(edge.Distance, minDist, maxDist);
                    var locked = IsBranchLocked(locks, parent, dest);
                    var isHighlighted = dest == highlight;
                    var width = isHighlighted ? 2.5 : locked ? 2.0 : 1.35;
                    var alpha = isHighlighted ? 1.0 : locked ? 0.88 : layerAlpha;

                    DrawChord(dc, center, layerRadius, a1, a2,
                        MakePen(locked ? LockColor : branchColor, width, alpha));

                    var dot = Polar(center, layerRadius, a2);
                    var dotAlpha = isHighlighted ? 1.0 : locked ? 0.9 : Math.Max(alpha, 0.42);
                    var dotSize = isHighlighted ? 4.2 : locked ? 3.4 : 3.0;
                    dc.DrawEllipse(MakeBrush(locked ? LockColor : branchColor, dotAlpha), null, dot,
                        dotSize, dotSize);
                }

                if (highlight >= 0)
                    hopText.Add($"hop{hop}: {parent}→{highlight}");

                if (highlight < 0)
                    break;
            }

            var origin = Polar(center, rChord, BeatAngle(beats[inspect], total));
            dc.DrawEllipse(MakeBrush(HotColor, 0.9), null, origin, 4, 4);
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

        private static readonly Color HotColor = Colors.White;

        private static readonly Color GhostColor = Color.FromRgb(0xA0, 0xB4, 0xDC);

        private static readonly Color TextColor = Color.FromRgb(0xE0, 0xE0, 0xE0);

        private static readonly Color MutedTextColor = Color.FromRgb(0x8A, 0x8A, 0x8A);

        /// <summary>Muted section hues (steel blue, sea green, violet, amber, teal, rose).</summary>
        private static readonly double[] SectionHues = { 210, 158, 278, 30, 190, 330 };

        // Render layer order (bottom → top):
        //   0. FractalBackgroundControl — separate element *behind* this canvas in the visual tree
        //   1. Center disc (skipped in mini player mode so the transport backdrop shows through)
        //   2. Plasma equalizer bars — annulus between the inner bar band and the outer rim;
        //      peaks intentionally overshoot the rim
        //   3. Outer rim circle + section arcs
        //   4. Beat coverage bars (+ mini-player square-edge extensions), trail, headlights, playhead
        //   5. Branch landmarks/chords, planned-jump chord, jump flashes, locked branches
        // Everything interactive (bars, chords, locks, scrub) stays on top of the equalizer.
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            var width = ActualWidth;
            var height = ActualHeight;

            if (width < 24 || height < 24)
                return;

            var center = new Point(width / 2, height / 2);
            var outer = Math.Min(width, height) / 2 - 2;

            // Mini player keeps the center transparent so the transport backdrop shows through.
            if (!MiniPlayerMode)
                dc.DrawEllipse(new SolidColorBrush(BackgroundColor), null, center, outer, outer);

            _equalizer.Render(dc, EnergyProvider, center, outer * BarBandInnerRatio, outer);

            dc.DrawEllipse(null, MakePen(RimColor, 1), center, outer, outer);

            var graph = Graph;

            if (graph == null || graph.Beats.Count == 0 || TotalMs() <= 0)
            {
                RenderFallback(dc, center, outer);
                return;
            }

            RenderRing(dc, graph, center, outer);
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

            // Mini player: shift the hint above the transport backdrop that covers the center.
            var baseOffset = MiniPlayerMode ? -outer * 0.42 : 0;

            DrawCenteredText(dc, center, baseOffset - 8, duration > 0 ? "no beat map" : "no track",
                14, TextColor, FontWeights.SemiBold);
            DrawCenteredText(dc, center, baseOffset + 12, "Analyze track to build the ring",
                10, MutedTextColor, FontWeights.Normal);
        }

        private void RenderRing(DrawingContext dc, BeatGraph graph, Point center, double outer)
        {
            var now = Clock.Elapsed.TotalMilliseconds;
            var beats = graph.Beats;
            var total = TotalMs();
            var rIn = outer * BarBandInnerRatio;
            var ext = outer * 0.28;
            var minLen = outer * 0.05;
            var rChord = rIn - outer * 0.04;

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

            // Mini player: extend beat bars to the square edges with section tint.
            if (MiniPlayerMode)
            {
                for (var i = 0; i < beats.Count; i++)
                {
                    var angle = BeatAngle(beats[i], total);
                    var edgeDist = DistanceToSquareEdge(center, ActualWidth, ActualHeight, angle);

                    if (edgeDist <= outer + 0.5)
                        continue;

                    DrawRadialLine(dc, center, outer - 1, edgeDist, angle,
                        MakePen(BeatColor(beats[i], sectionColors), barWidth, 0.45));
                }
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

            // Playhead beat: hot bar with a soft glow underlay.
            if (currentIndex >= 0 && currentIndex < beats.Count)
            {
                var t = _playCounts.Length == beats.Count ? _playCounts[currentIndex] / (double)maxPlay : 0;
                var length = minLen + t * ext + 6;
                var angle = BeatAngle(beats[currentIndex], total);
                DrawRadialLine(dc, center, rIn - 4, rIn + length, angle,
                    MakePen(HotColor, barWidth * 3, 0.22));
                DrawRadialLine(dc, center, rIn - 4, rIn + length, angle,
                    MakePen(HotColor, barWidth + 1.5));
            }

            // Chained branch preview (distance-colored); pinned after lock so options stay visible.
            var previewChain = ActivePreviewChain();
            var inspectBeat = previewChain[0] >= 0 ? previewChain[0] : currentIndex;

            DrawBranchLandmarks(dc, graph, center, rChord, total, locks, inspectBeat, currentIndex,
                previewChain);

            if (previewChain[0] >= 0)
                DrawChainedBranchPreview(dc, graph, center, rChord, total, locks, previewChain);

            // The predictor's mind: one pulsing chord for the planned jump.
            if (plannedFrom >= 0 && plannedTo >= 0 && plannedFrom < beats.Count && plannedTo < beats.Count)
            {
                var pulse = 0.55 + 0.3 * Math.Sin(now / 140);
                DrawChord(dc, center, rChord, BeatAngle(beats[plannedFrom], total),
                    BeatAngle(beats[plannedTo], total), MakePen(AccentColor, 2, pulse));
                var dot = Polar(center, rChord, BeatAngle(beats[plannedTo], total));
                dc.DrawEllipse(MakeBrush(AccentColor, 1), null, dot, 3.2, 3.2);
            }

            // Fired jumps flash white and decay.
            foreach (var (from, to, atMs) in _flashes)
            {
                var alpha = 1 - (now - atMs) / FlashFadeMs;

                if (alpha <= 0 || from >= beats.Count || to >= beats.Count)
                    continue;

                DrawChord(dc, center, rChord, BeatAngle(beats[from], total),
                    BeatAngle(beats[to], total), MakePen(HotColor, 1 + 2.4 * alpha, alpha * 0.9));
            }

            // Locked branches on beats not currently being inspected.
            if (locks != null)
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
                    var alpha = inspect >= 0 ? 0.55 : 1.0;
                    DrawChord(dc, center, rChord, a1, a2, MakePen(LockColor, inspect >= 0 ? 1.6 : 2.1, alpha));

                    if (inspect < 0)
                    {
                        var mid = ChordMidpoint(center, rChord, a1, a2);
                        dc.DrawEllipse(MakeBrush(LockColor, 1), null, mid, 3.4, 3.4);
                    }
                }
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
                    MakePen(MakeColor(sectionColors[i], 0.7), 2.5, roundCaps: true));
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

        private static void DrawRadialLine(DrawingContext dc, Point center, double inner, double outer,
            double angleRad, Pen pen)
        {
            dc.DrawLine(pen, Polar(center, inner, angleRad), Polar(center, outer, angleRad));
        }

        /// <summary>Quadratic chord pulled toward the center; far jumps bow deeper (reference look).</summary>
        private static void DrawChord(DrawingContext dc, Point center, double radius,
            double angle1, double angle2, Pen pen)
        {
            var p1 = Polar(center, radius, angle1);
            var p2 = Polar(center, radius, angle2);
            var control = ChordControlPoint(center, angle1, angle2, p1, p2);

            var geometry = new StreamGeometry();

            using (var context = geometry.Open())
            {
                context.BeginFigure(p1, false, false);
                context.QuadraticBezierTo(control, p2, true, false);
            }

            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);
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
