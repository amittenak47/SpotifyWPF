using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using SpotifyWPF.Model;
using SpotifyWPF.ViewModel.Component;
using SpotifyWPF.ViewModel.Page;

namespace SpotifyWPF.View.Component
{
    public partial class PlaylistsControlPanel
    {
        public static readonly DependencyProperty ActivityLogProperty =
            DependencyProperty.Register(
                nameof(ActivityLog),
                typeof(ActivityLogViewModel),
                typeof(PlaylistsControlPanel),
                new PropertyMetadata(null));

        public static readonly DependencyProperty IsExpandedProperty =
            DependencyProperty.Register(
                nameof(IsExpanded),
                typeof(bool),
                typeof(PlaylistsControlPanel),
                new FrameworkPropertyMetadata(
                    true,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                    OnIsExpandedChanged));

        public static readonly DependencyProperty ExpandedHeightProperty =
            DependencyProperty.Register(
                nameof(ExpandedHeight),
                typeof(double),
                typeof(PlaylistsControlPanel),
                new FrameworkPropertyMetadata(280.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnExpandedHeightChanged));

        public static readonly DependencyProperty FillRemainingSpaceProperty =
            DependencyProperty.Register(
                nameof(FillRemainingSpace),
                typeof(bool),
                typeof(PlaylistsControlPanel),
                new PropertyMetadata(false, OnFillRemainingSpaceChanged));

        /// <summary>Collapsed, not hovered: tiny hit-strip so content fills the window.</summary>
        private const double PeekHitHeight = 10;
        /// <summary>Collapsed, hovered: header peek; content slides up to make room.</summary>
        private const double PeekHeight = 32;
        private const double MinExpandedHeight = 120;
        private const double MaxExpandedHeight = 720;
        private const double PeekDimOpacity = 0.0;
        private const double PeekFullOpacity = 1.0;
        private const double PeekFadeMs = 180;
        private static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(220);

        private bool _isResizing;
        private bool _isOpen;
        private bool _didResizeDrag;
        private double _resizeStartY;
        private double _resizeStartHeight;
        private double _frozenHeight;
        private double _previewHeight;

        public PlaylistsControlPanel()
        {
            InitializeComponent();
            Height = PeekHitHeight;
            Opacity = PeekFullOpacity;
            if (PeekVisual != null)
                PeekVisual.Opacity = PeekDimOpacity;
            Loaded += (_, __) =>
            {
                if (_isResizing)
                    return;

                if (IsExpanded)
                {
                    ApplyOpenImmediate();
                    return;
                }

                if (!_isOpen)
                    ApplyCollapsedPeek(IsMouseOver);
            };
            SizeChanged += (_, __) =>
            {
                if (_isResizing)
                    UpdateResizePreview();
            };
            PreviewMouseMove += OnPreviewMouseMoveWhileResizing;
            PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUpWhileResizing;
            LostMouseCapture += OnLostMouseCapture;
        }

        public ActivityLogViewModel ActivityLog
        {
            get => (ActivityLogViewModel)GetValue(ActivityLogProperty);
            set => SetValue(ActivityLogProperty, value);
        }

        public bool IsExpanded
        {
            get => (bool)GetValue(IsExpandedProperty);
            set => SetValue(IsExpandedProperty, value);
        }

        public double ExpandedHeight
        {
            get => (double)GetValue(ExpandedHeightProperty);
            set => SetValue(ExpandedHeightProperty, value);
        }

        public bool FillRemainingSpace
        {
            get => (bool)GetValue(FillRemainingSpaceProperty);
            set => SetValue(FillRemainingSpaceProperty, value);
        }

        private static void OnExpandedHeightChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is PlaylistsControlPanel panel) || panel._isResizing)
                return;

            if (panel._isOpen && !panel.FillRemainingSpace)
                panel.Height = ClampExpandedHeight(panel.ExpandedHeight);
        }

        private static void OnFillRemainingSpaceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is PlaylistsControlPanel panel)
                panel.ApplyLayoutMode();
        }

        private static void OnIsExpandedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is PlaylistsControlPanel panel) || panel._isResizing)
                return;

            var expanded = (bool)e.NewValue;
            if (expanded && !panel._isOpen)
                panel.ApplyOpenImmediate();
            else if (!expanded && panel._isOpen)
                panel.SlideClosed();
            else if (expanded && panel._isOpen)
                panel.ApplyLayoutMode();
        }

        private void ApplyLayoutMode()
        {
            if (!_isOpen)
                return;

            BeginAnimation(HeightProperty, null);
            AnimatePeekOpacity(PeekFullOpacity, immediate: true);
            Opacity = PeekFullOpacity;

            if (FillRemainingSpace)
            {
                Height = double.NaN;
                VerticalAlignment = VerticalAlignment.Stretch;
                if (ResizeBar != null)
                    ResizeBar.IsEnabled = false;
            }
            else
            {
                VerticalAlignment = VerticalAlignment.Bottom;
                Height = ClampExpandedHeight(ExpandedHeight);
                if (ResizeBar != null)
                    ResizeBar.IsEnabled = true;
            }
        }

        private void ApplyOpenImmediate()
        {
            _isOpen = true;
            if (!IsExpanded)
                IsExpanded = true;
            ApplyLayoutMode();
        }

        private void ApplyCollapsedPeek(bool hovered)
        {
            VerticalAlignment = VerticalAlignment.Bottom;
            Opacity = PeekFullOpacity;
            var targetHeight = hovered ? PeekHeight : PeekHitHeight;
            BeginAnimation(HeightProperty, null);
            Height = targetHeight;
            AnimatePeekOpacity(hovered ? PeekFullOpacity : PeekDimOpacity, immediate: true);
        }

        private void Root_MouseEnter(object sender, MouseEventArgs e)
        {
            if (_isOpen || _isResizing)
                return;

            AnimateHeight(PeekHeight);
            AnimatePeekOpacity(PeekFullOpacity);
        }

        private void Root_MouseLeave(object sender, MouseEventArgs e)
        {
            if (_isOpen || _isResizing)
                return;

            AnimateHeight(PeekHitHeight);
            AnimatePeekOpacity(PeekDimOpacity);
        }

        private void ExpandToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_isOpen)
                SlideClosed();
            else
                SlideOpen();
        }

        private void SlideOpen()
        {
            _isOpen = true;
            IsExpanded = true;
            AnimatePeekOpacity(PeekFullOpacity);
            if (FillRemainingSpace)
            {
                ApplyLayoutMode();
            }
            else
            {
                AnimateHeight(ClampExpandedHeight(ExpandedHeight));
            }
        }

        private void SlideClosed()
        {
            _isOpen = false;
            IsExpanded = false;
            VerticalAlignment = VerticalAlignment.Bottom;
            if (ResizeBar != null)
                ResizeBar.IsEnabled = true;
            // Restore a concrete height before peek animation when leaving fill mode.
            if (double.IsNaN(Height))
                Height = ClampExpandedHeight(ExpandedHeight);

            var hovered = IsMouseOver;
            AnimateHeight(hovered ? PeekHeight : PeekHitHeight);
            AnimatePeekOpacity(hovered ? PeekFullOpacity : PeekDimOpacity);
        }

        private void AnimatePeekOpacity(double to, bool immediate = false)
        {
            if (PeekVisual == null)
                return;

            PeekVisual.BeginAnimation(OpacityProperty, null);
            if (immediate)
            {
                PeekVisual.Opacity = to;
                return;
            }

            PeekVisual.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(to, TimeSpan.FromMilliseconds(PeekFadeMs))
                {
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                    FillBehavior = FillBehavior.HoldEnd
                },
                HandoffBehavior.SnapshotAndReplace);
        }

        private void AnimateHeight(double to)
        {
            BeginAnimation(HeightProperty, null);
            var animation = new DoubleAnimation(Height, to, SlideDuration)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
            animation.Completed += (_, __) =>
            {
                BeginAnimation(HeightProperty, null);
                Height = to;
            };
            BeginAnimation(HeightProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }

        private static double ClampExpandedHeight(double height)
        {
            return Math.Max(MinExpandedHeight, Math.Min(MaxExpandedHeight, height));
        }

        private void BeginResizePreview()
        {
            if (PanelContent != null)
                PanelContent.Effect = new BlurEffect { Radius = 8, RenderingBias = RenderingBias.Performance };
            UpdateResizePreview();
        }

        private void UpdateResizePreview()
        {
            if (ResizePreviewGhost == null)
                return;

            var panelWidth = Math.Max(0, ActualWidth);
            var top = ActualHeight - _previewHeight;

            System.Windows.Controls.Canvas.SetLeft(ResizePreviewGhost, 0);
            System.Windows.Controls.Canvas.SetTop(ResizePreviewGhost, top);
            ResizePreviewGhost.Width = panelWidth;
            ResizePreviewGhost.Height = _previewHeight;
            ResizePreviewGhost.Visibility = Visibility.Visible;
        }

        private void EndResizePreview()
        {
            if (PanelContent != null)
                PanelContent.Effect = null;
            if (ResizePreviewGhost != null)
                ResizePreviewGhost.Visibility = Visibility.Collapsed;
        }

        private void ResizeBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_isOpen || FillRemainingSpace)
                return;

            _isResizing = true;
            _didResizeDrag = false;
            _resizeStartY = e.GetPosition(this).Y;
            _resizeStartHeight = ExpandedHeight;
            _frozenHeight = Height;
            _previewHeight = _resizeStartHeight;

            BeginAnimation(HeightProperty, null);
            Height = _frozenHeight;
            BeginResizePreview();
            Mouse.Capture(this);
            e.Handled = true;
        }

        private void ResizeBar_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isResizing)
                return;

            UpdatePreviewFromMouse(e.GetPosition(this).Y);
            e.Handled = true;
        }

        private void OnPreviewMouseMoveWhileResizing(object sender, MouseEventArgs e)
        {
            if (!_isResizing)
                return;

            UpdatePreviewFromMouse(e.GetPosition(this).Y);
            e.Handled = true;
        }

        private void OnPreviewMouseLeftButtonUpWhileResizing(object sender, MouseButtonEventArgs e)
        {
            if (!_isResizing)
                return;

            FinishResize();
            e.Handled = true;
        }

        private void ResizeBar_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
        }

        private void OnLostMouseCapture(object sender, MouseEventArgs e)
        {
            if (_isResizing)
                FinishResize();
        }

        private void UpdatePreviewFromMouse(double currentY)
        {
            var delta = _resizeStartY - currentY;
            if (Math.Abs(delta) > 2)
                _didResizeDrag = true;

            _previewHeight = ClampExpandedHeight(_resizeStartHeight + delta);
            UpdateResizePreview();
        }

        private void FinishResize()
        {
            if (!_isResizing)
                return;

            _isResizing = false;
            Mouse.Capture(null);

            EndResizePreview();

            if (_didResizeDrag)
            {
                ExpandedHeight = _previewHeight;
                _isOpen = true;
                IsExpanded = true;
                BeginAnimation(OpacityProperty, null);
                Opacity = PeekFullOpacity;
                if (!FillRemainingSpace)
                    Height = ClampExpandedHeight(ExpandedHeight);
            }
        }

        private void QueuedActionsTreeView_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Delete)
                return;

            var viewModel = DataContext as PlaylistsPageViewModel;
            if (viewModel == null)
                return;

            if (QueuedActionsTreeView.SelectedItem is QueuedActionDetailItem detail)
            {
                var parentAction = viewModel.FindQueuedActionForDetail(detail);
                if (parentAction != null)
                {
                    viewModel.RemoveQueuedActionDetail(parentAction, detail);
                    e.Handled = true;
                }

                return;
            }

            if (QueuedActionsTreeView.SelectedItem is QueuedPlaylistAction action)
            {
                viewModel.RemoveQueuedAction(action);
                e.Handled = true;
            }
        }
    }
}
