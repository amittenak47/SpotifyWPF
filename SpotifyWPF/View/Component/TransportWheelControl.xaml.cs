using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SpotifyWPF.ViewModel.Page;

namespace SpotifyWPF.View.Component
{
    public partial class TransportWheelControl
    {
        private enum WheelAction
        {
            None,
            Previous,
            Next,
            Stop,
            Minimize
        }

        private const double WheelDragThreshold = 8;
        private const double WheelCenter = 42;
        private const double WheelFadeMs = 140;

        private static readonly SolidColorBrush WheelGoldFill =
            new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xD1, 0x66));

        private static readonly SolidColorBrush WheelGoldStroke =
            new SolidColorBrush(Color.FromArgb(0xEE, 0xFF, 0xD1, 0x66));

        private static readonly SolidColorBrush WheelMutedLabel =
            new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));

        private bool _pressActive;
        private bool _wheelActive;
        private Point _pressPoint;
        private WheelAction _action = WheelAction.None;

        public TransportWheelControl()
        {
            InitializeComponent();
        }

        private PredictionPageViewModel ViewModel => DataContext as PredictionPageViewModel;

        private void CenterButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _pressActive = true;
            _wheelActive = false;
            _action = WheelAction.None;
            _pressPoint = new Point(WheelCenter, WheelCenter);
            CenterButton.CaptureMouse();
            e.Handled = true;
        }

        private void CenterButton_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_pressActive)
                TrackWheelDrag();
        }

        private void CenterButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_wheelActive)
            {
                CommitWheelAction();
                HideWheel();
            }
            else
            {
                FireBaseAction();
            }

            EndPress();
            e.Handled = true;
        }

        private void EndPress()
        {
            _pressActive = false;
            _wheelActive = false;
            _action = WheelAction.None;

            if (CenterButton.IsMouseCaptured)
                CenterButton.ReleaseMouseCapture();
        }

        private void FireBaseAction()
        {
            var vm = ViewModel;
            if (vm?.TogglePlayPauseCommand.CanExecute(null) == true)
                vm.TogglePlayPauseCommand.Execute(null);
        }

        private void TrackWheelDrag()
        {
            if (Mouse.LeftButton != MouseButtonState.Pressed)
                return;

            var delta = (Vector)(Mouse.GetPosition(WheelOverlay) - _pressPoint);

            if (!_wheelActive)
            {
                if (Math.Abs(delta.X) < WheelDragThreshold && Math.Abs(delta.Y) < WheelDragThreshold)
                    return;

                ShowWheel();
            }

            _action = PickAction(delta);
            UpdateHighlight();
        }

        private void ShowWheel()
        {
            _wheelActive = true;
            _action = WheelAction.None;
            FadeWheel(show: true);
            Panel.SetZIndex(WheelOverlay, 0);
            Panel.SetZIndex(CenterButton, 1);
            UpdateHighlight();
        }

        private void HideWheel()
        {
            FadeWheel(show: false);
            _action = WheelAction.None;
            UpdateHighlight();
        }

        private void FadeWheel(bool show)
        {
            WheelOverlay.BeginAnimation(UIElement.OpacityProperty, null);

            if (show)
            {
                WheelOverlay.Visibility = Visibility.Visible;
                var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(WheelFadeMs))
                {
                    FillBehavior = FillBehavior.HoldEnd
                };
                WheelOverlay.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                return;
            }

            var fadeOut = new DoubleAnimation(WheelOverlay.Opacity, 0, TimeSpan.FromMilliseconds(WheelFadeMs))
            {
                FillBehavior = FillBehavior.Stop
            };
            fadeOut.Completed += (_, __) =>
            {
                WheelOverlay.Visibility = Visibility.Collapsed;
                WheelOverlay.Opacity = 0;
            };
            WheelOverlay.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }

        // WPF Y grows downward: 0=right, 90=down, 180=left, 270=up.
        // Cardinal slices: left=Prev, top=Minimize, right=Next, bottom=Stop.
        private static WheelAction PickAction(Vector delta)
        {
            if (Math.Abs(delta.X) < WheelDragThreshold && Math.Abs(delta.Y) < WheelDragThreshold)
                return WheelAction.None;

            var angle = (Math.Atan2(delta.Y, delta.X) * 180.0 / Math.PI + 360.0) % 360.0;

            if (angle >= 315 || angle < 45)
                return WheelAction.Next;

            if (angle < 135)
                return WheelAction.Stop;

            if (angle < 225)
                return WheelAction.Previous;

            return WheelAction.Minimize;
        }

        private void UpdateHighlight()
        {
            ApplySegment(PrevArc, _action == WheelAction.Previous);
            ApplySegment(NextArc, _action == WheelAction.Next);
            ApplySegment(StopArc, _action == WheelAction.Stop);
            ApplySegment(MinimizeArc, _action == WheelAction.Minimize);

            HighlightLabel(PrevLabel, _action == WheelAction.Previous);
            HighlightLabel(NextLabel, _action == WheelAction.Next);
            HighlightLabel(StopLabel, _action == WheelAction.Stop);
            HighlightLabel(MinimizeLabel, _action == WheelAction.Minimize);
        }

        private static void HighlightLabel(TextBlock label, bool selected)
        {
            label.Opacity = selected ? 1.0 : 0.7;
            label.Foreground = selected ? WheelGoldStroke : WheelMutedLabel;
        }

        private static void ApplySegment(System.Windows.Shapes.Path segment, bool selected)
        {
            if (selected)
            {
                segment.Fill = WheelGoldFill;
                segment.Stroke = WheelGoldStroke;
                segment.StrokeThickness = 1.75;
            }
            else
            {
                segment.Fill = Brushes.Transparent;
                segment.Stroke = Brushes.Transparent;
                segment.StrokeThickness = 1.25;
            }
        }

        private void CommitWheelAction()
        {
            var vm = ViewModel;
            if (vm == null)
                return;

            switch (_action)
            {
                case WheelAction.Previous:
                    vm.PreviousSessionTrackCommand.Execute(null);
                    break;
                case WheelAction.Next:
                    vm.NextSessionTrackCommand.Execute(null);
                    break;
                case WheelAction.Stop when vm.StopPlaybackCommand.CanExecute(null):
                    vm.StopPlaybackCommand.Execute(null);
                    break;
                case WheelAction.Minimize when vm.EnterMiniPlayerCommand.CanExecute(null):
                    vm.EnterMiniPlayerCommand.Execute(null);
                    break;
            }
        }
    }
}
