using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SpotifyWPF.ViewModel.Page;

namespace SpotifyWPF.View.Component
{
    public partial class MiniPlayerView
    {
        private enum LockWheelAction
        {
            None,
            Clear,
            Reset,
            Maximize,
            Hops
        }

        private enum TransportWheelAction
        {
            None,
            Previous,
            Next
        }

        private const double WheelDragThreshold = 10;
        private const double WheelHoldDelayMs = 380;
        private const double WheelCenter = 42;

        private static readonly SolidColorBrush WheelGoldFill =
            new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xD1, 0x66));

        private static readonly SolidColorBrush WheelGoldStroke =
            new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xD1, 0x66));

        private static readonly SolidColorBrush WheelMutedLabel =
            new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF));

        private readonly DispatcherTimer _lockHoldTimer;
        private readonly DispatcherTimer _transportHoldTimer;

        private bool _lockHoldReady;
        private bool _transportHoldReady;
        private bool _hopModeEnabled;
        private bool _lockPressActive;
        private bool _lockWheelActive;
        private bool _transportPressActive;
        private bool _transportWheelActive;
        private bool _isDraggingWindow;
        private Point _wheelPressPoint;
        private Point _windowDragStartScreen;
        private Point _windowDragOrigin;
        private LockWheelAction _lockWheelAction = LockWheelAction.None;
        private TransportWheelAction _transportWheelAction = TransportWheelAction.None;

        public MiniPlayerView()
        {
            InitializeComponent();

            _lockHoldTimer = CreateHoldTimer(() =>
            {
                if (_lockPressActive)
                    _lockHoldReady = true;
            });

            _transportHoldTimer = CreateHoldTimer(() =>
            {
                if (_transportPressActive)
                    _transportHoldReady = true;
            });

            Loaded += (_, __) => UpdateHopModeChrome();
        }

        private static DispatcherTimer CreateHoldTimer(Action onTick)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(WheelHoldDelayMs) };
            timer.Tick += (_, __) =>
            {
                timer.Stop();
                onTick();
            };
            return timer;
        }

        private void DragRoot_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            RingView.ApplyWheelZoom(e.Delta);
            e.Handled = true;
        }

        private void DragRoot_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_lockPressActive || _lockWheelActive || _transportPressActive || _transportWheelActive
                || IsOverInteractiveChrome(e))
                return;

            if (IsOverRingHost(e))
            {
                if (_hopModeEnabled && IsOverHopDisc(e))
                    return;

                return;
            }

            StartWindowDrag(e);
        }

        private void RingDragPad_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_lockPressActive || _lockWheelActive || _transportPressActive || _transportWheelActive
                || IsOverInteractiveChrome(e))
                return;

            if (_hopModeEnabled && IsOverHopDisc(e))
                return;

            StartWindowDrag(e);
        }

        private void RingDragPad_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            ContinueWindowDrag(e);
        }

        private void RingDragPad_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            EndWindowDrag();
        }

        private void StartWindowDrag(MouseButtonEventArgs e)
        {
            var window = Window.GetWindow(this);
            if (window == null)
                return;

            _isDraggingWindow = true;
            _windowDragStartScreen = DragRoot.PointToScreen(e.GetPosition(DragRoot));
            _windowDragOrigin = new Point(window.Left, window.Top);
            DragRoot.CaptureMouse();
            e.Handled = true;
        }

        private void DragRoot_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_lockPressActive)
            {
                TrackLockWheelDrag();
                return;
            }

            if (_transportPressActive)
            {
                TrackTransportWheelDrag();
                return;
            }

            ContinueWindowDrag(e);
        }

        private void ContinueWindowDrag(MouseEventArgs e)
        {
            if (!_isDraggingWindow || e.LeftButton != MouseButtonState.Pressed)
                return;

            var window = Window.GetWindow(this);
            if (window == null)
                return;

            var currentScreen = DragRoot.PointToScreen(e.GetPosition(DragRoot));
            window.Left = _windowDragOrigin.X + (currentScreen.X - _windowDragStartScreen.X);
            window.Top = _windowDragOrigin.Y + (currentScreen.Y - _windowDragStartScreen.Y);
        }

        private void DragRoot_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            EndWindowDrag();
        }

        private void DragRoot_LostMouseCapture(object sender, MouseEventArgs e)
        {
            EndWindowDrag();
        }

        private void EndWindowDrag()
        {
            if (!_isDraggingWindow)
                return;

            _isDraggingWindow = false;
            if (DragRoot.IsMouseCaptured)
                DragRoot.ReleaseMouseCapture();
        }

        private bool IsOverInteractiveChrome(MouseButtonEventArgs e)
        {
            if (IsWithinButton(e.OriginalSource as DependencyObject))
                return true;

            var hit = VisualTreeHelper.HitTest(DragRoot, e.GetPosition(DragRoot));
            return hit != null && IsWithinButton(hit.VisualHit);
        }

        private bool IsOverRingHost(MouseEventArgs e)
        {
            if (RingHostGrid.ActualWidth <= 0 || RingHostGrid.ActualHeight <= 0)
                return false;

            var pos = e.GetPosition(RingHostGrid);
            return pos.X >= 0 && pos.Y >= 0 && pos.X <= RingHostGrid.ActualWidth
                   && pos.Y <= RingHostGrid.ActualHeight;
        }

        private bool IsOverHopDisc(MouseEventArgs e)
        {
            if (!_hopModeEnabled)
                return false;

            var canvas = FindRingCanvasFromEvent(e);
            return canvas != null && canvas.IsInHopDisc(e.GetPosition(canvas));
        }

        private JukeboxRingCanvas FindRingCanvasFromEvent(MouseEventArgs e)
        {
            var hit = VisualTreeHelper.HitTest(DragRoot, e.GetPosition(DragRoot));
            return FindRingCanvas(hit?.VisualHit);
        }

        private static JukeboxRingCanvas FindRingCanvas(DependencyObject source)
        {
            while (source != null)
            {
                if (source is JukeboxRingCanvas canvas)
                    return canvas;

                source = VisualTreeHelper.GetParent(source);
            }

            return null;
        }

        private void LockToggle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            BeginLockWheelPress();
            e.Handled = true;
        }

        private void LockToggle_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_lockPressActive)
                TrackLockWheelDrag();
        }

        private void LockToggle_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _lockHoldTimer.Stop();
            _lockPressActive = false;
            _lockHoldReady = false;
            ReleaseCapture(LockToggle);

            if (_lockWheelActive)
            {
                CommitLockWheelAction();
                HideLockWheel();
            }
            else
            {
                LockToggle.IsChecked = !LockToggle.IsChecked;
            }

            _lockWheelActive = false;
            e.Handled = true;
        }

        private void TransportButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            BeginTransportWheelPress();
            e.Handled = true;
        }

        private void TransportButton_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_transportPressActive)
                TrackTransportWheelDrag();
        }

        private void TransportButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _transportHoldTimer.Stop();
            _transportPressActive = false;
            _transportHoldReady = false;
            ReleaseCapture(TransportButton);

            if (_transportWheelActive)
            {
                CommitTransportWheelAction();
                HideTransportWheel();
            }
            else if (DataContext is PredictionPageViewModel vm
                     && vm.TogglePlayPauseCommand.CanExecute(null))
            {
                vm.TogglePlayPauseCommand.Execute(null);
            }

            _transportWheelActive = false;
            e.Handled = true;
        }

        private void BeginWheelPress(DispatcherTimer timer, ref bool pressActive, UIElement captureTarget)
        {
            pressActive = true;
            _wheelPressPoint = new Point(WheelCenter, WheelCenter);
            captureTarget.CaptureMouse();
            timer.Start();
        }

        private void BeginLockWheelPress()
        {
            _lockHoldReady = false;
            BeginWheelPress(_lockHoldTimer, ref _lockPressActive, LockToggle);
        }

        private void BeginTransportWheelPress()
        {
            _transportHoldReady = false;
            BeginWheelPress(_transportHoldTimer, ref _transportPressActive, TransportButton);
        }

        private void TrackLockWheelDrag()
        {
            if (!TryReadWheelDelta(LockWheelOverlay, ref _lockWheelActive, _lockHoldReady, ShowLockWheel,
                    horizontalOnly: false, out var delta))
                return;

            if (_lockWheelActive)
            {
                _lockWheelAction = PickLockWheelAction(delta);
                UpdateLockWheelHighlight();
            }
        }

        private void TrackTransportWheelDrag()
        {
            if (!TryReadWheelDelta(TransportWheelOverlay, ref _transportWheelActive, _transportHoldReady,
                    ShowTransportWheel, horizontalOnly: true, out var delta))
                return;

            if (_transportWheelActive)
            {
                _transportWheelAction = PickTransportWheelAction(delta);
                UpdateTransportWheelHighlight();
            }
        }

        private bool TryReadWheelDelta(FrameworkElement overlay, ref bool wheelActive, bool holdReady,
            Action showWheel, bool horizontalOnly, out Vector delta)
        {
            delta = default;

            if (Mouse.LeftButton != MouseButtonState.Pressed || !holdReady)
                return false;

            delta = (Vector)(Mouse.GetPosition(overlay) - _wheelPressPoint);

            if (!wheelActive)
            {
                if (horizontalOnly)
                {
                    if (Math.Abs(delta.X) < WheelDragThreshold
                        || Math.Abs(delta.X) < Math.Abs(delta.Y))
                        return false;
                }
                else if (Math.Abs(delta.X) < WheelDragThreshold && Math.Abs(delta.Y) < WheelDragThreshold)
                {
                    return false;
                }

                showWheel();
                wheelActive = true;
                delta = (Vector)(Mouse.GetPosition(overlay) - _wheelPressPoint);
            }

            return wheelActive;
        }

        private static void ReleaseCapture(UIElement element)
        {
            if (element.IsMouseCaptured)
                element.ReleaseMouseCapture();
        }

        private void ShowLockWheel()
        {
            _lockWheelActive = true;
            _lockWheelAction = LockWheelAction.None;
            LockWheelOverlay.Visibility = Visibility.Visible;
            Panel.SetZIndex(LockWheelOverlay, 0);
            Panel.SetZIndex(LockToggle, 1);
            UpdateLockWheelHighlight();
        }

        private void HideLockWheel()
        {
            LockWheelOverlay.Visibility = Visibility.Collapsed;
            _lockWheelAction = LockWheelAction.None;
            UpdateLockWheelHighlight();
        }

        private void ShowTransportWheel()
        {
            _transportWheelActive = true;
            _transportWheelAction = TransportWheelAction.None;
            TransportWheelOverlay.Visibility = Visibility.Visible;
            Panel.SetZIndex(TransportWheelOverlay, 0);
            Panel.SetZIndex(TransportButton, 1);
            UpdateTransportWheelHighlight();
        }

        private void HideTransportWheel()
        {
            TransportWheelOverlay.Visibility = Visibility.Collapsed;
            _transportWheelAction = TransportWheelAction.None;
            UpdateTransportWheelHighlight();
        }

        private static LockWheelAction PickLockWheelAction(Vector delta)
        {
            if (Math.Abs(delta.X) < WheelDragThreshold && Math.Abs(delta.Y) < WheelDragThreshold)
                return LockWheelAction.None;

            var angle = (Math.Atan2(delta.Y, delta.X) * 180.0 / Math.PI + 360.0) % 360.0;

            if (angle >= 315 || angle < 45)
                return LockWheelAction.Reset;

            if (angle < 135)
                return LockWheelAction.Maximize;

            if (angle < 225)
                return LockWheelAction.Hops;

            return LockWheelAction.Clear;
        }

        private static TransportWheelAction PickTransportWheelAction(Vector delta)
        {
            if (Math.Abs(delta.X) < WheelDragThreshold)
                return TransportWheelAction.None;

            return delta.X < 0 ? TransportWheelAction.Previous : TransportWheelAction.Next;
        }

        private void UpdateLockWheelHighlight()
        {
            ApplyWheelSegment(LockWheelClearArc, _lockWheelAction == LockWheelAction.Clear);
            ApplyWheelSegment(LockWheelResetArc, _lockWheelAction == LockWheelAction.Reset);
            ApplyWheelSegment(LockWheelMaximizeArc, _lockWheelAction == LockWheelAction.Maximize);
            ApplyWheelSegment(LockWheelHopsArc, _lockWheelAction == LockWheelAction.Hops);

            HighlightLabel(LockWheelClearLabel, _lockWheelAction == LockWheelAction.Clear);
            HighlightLabel(LockWheelResetLabel, _lockWheelAction == LockWheelAction.Reset);
            HighlightLabel(LockWheelMaximizeLabel, _lockWheelAction == LockWheelAction.Maximize);
            HighlightLabel(LockWheelHopsLabel, _lockWheelAction == LockWheelAction.Hops);
        }

        private void UpdateTransportWheelHighlight()
        {
            ApplyWheelSegment(TransportWheelPrevArc, _transportWheelAction == TransportWheelAction.Previous);
            ApplyWheelSegment(TransportWheelNextArc, _transportWheelAction == TransportWheelAction.Next);

            HighlightLabel(TransportWheelPrevLabel, _transportWheelAction == TransportWheelAction.Previous);
            HighlightLabel(TransportWheelNextLabel, _transportWheelAction == TransportWheelAction.Next);
        }

        private static void HighlightLabel(TextBlock label, bool selected)
        {
            label.Opacity = selected ? 1.0 : 0.45;
            label.Foreground = selected ? WheelGoldStroke : WheelMutedLabel;
        }

        private static void ApplyWheelSegment(System.Windows.Shapes.Path segment, bool selected)
        {
            if (selected)
            {
                segment.Fill = WheelGoldFill;
                segment.Stroke = WheelGoldStroke;
                segment.StrokeThickness = 2;
            }
            else
            {
                segment.Fill = Brushes.Transparent;
                segment.Stroke = Brushes.Transparent;
                segment.StrokeThickness = 1.5;
            }
        }

        private void CommitLockWheelAction()
        {
            if (!(DataContext is PredictionPageViewModel vm))
                return;

            switch (_lockWheelAction)
            {
                case LockWheelAction.Clear when vm.ClearRingLocksCommand.CanExecute(null):
                    vm.ClearRingLocksCommand.Execute(null);
                    break;
                case LockWheelAction.Reset when vm.ResetRingPlaysCommand.CanExecute(null):
                    vm.ResetRingPlaysCommand.Execute(null);
                    break;
                case LockWheelAction.Maximize when vm.ExitMiniPlayerCommand.CanExecute(null):
                    vm.ExitMiniPlayerCommand.Execute(null);
                    break;
                case LockWheelAction.Hops:
                    _hopModeEnabled = !_hopModeEnabled;
                    UpdateHopModeChrome();
                    break;
            }
        }

        private void CommitTransportWheelAction()
        {
            if (!(DataContext is PredictionPageViewModel vm))
                return;

            switch (_transportWheelAction)
            {
                case TransportWheelAction.Previous:
                    vm.PreviousSessionTrackCommand.Execute(null);
                    break;
                case TransportWheelAction.Next:
                    vm.NextSessionTrackCommand.Execute(null);
                    break;
            }
        }

        private void UpdateHopModeChrome()
        {
            RingView.RingCanvas.MiniPlayerHopMode = _hopModeEnabled;
            RingView.RingCanvas.InvalidateVisual();
            HopModeIndicator.Visibility = _hopModeEnabled ? Visibility.Visible : Visibility.Collapsed;
            LockWheelHopsLabel.Text = _hopModeEnabled ? "Hops on" : "Hops";

            Panel.SetZIndex(RingViewHost, _hopModeEnabled ? 2 : 0);
            Panel.SetZIndex(RingDragPad, _hopModeEnabled ? 0 : 2);

            if (LockWheelOverlay.Visibility != Visibility.Visible)
                UpdateLockWheelHighlight();
        }

        private static bool IsWithinButton(DependencyObject source)
        {
            while (source != null)
            {
                if (source is ButtonBase)
                    return true;

                source = VisualTreeHelper.GetParent(source);
            }

            return false;
        }
    }
}
