using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using SpotifyWPF.ViewModel.Component;

namespace SpotifyWPF.View.Component
{
    public partial class LoopLabBottomPanel : UserControl
    {
        public static readonly DependencyProperty ActivityLogProperty =
            DependencyProperty.Register(nameof(ActivityLog), typeof(ActivityLogViewModel),
                typeof(LoopLabBottomPanel), new PropertyMetadata(null));

        public static readonly DependencyProperty IsExpandedProperty =
            DependencyProperty.Register(nameof(IsExpanded), typeof(bool), typeof(LoopLabBottomPanel),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        public static readonly DependencyProperty ExpandedHeightProperty =
            DependencyProperty.Register(nameof(ExpandedHeight), typeof(double), typeof(LoopLabBottomPanel),
                new FrameworkPropertyMetadata(220.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnExpandedHeightChanged));

        private const double PeekHeight = 10;
        private const double MinExpandedHeight = 120;
        private const double MaxExpandedHeight = 520;
        private static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(220);

        private bool _isResizing;
        private bool _isOpen;
        private double _resizeStartY;
        private double _resizeStartHeight;
        private double _frozenHeight;
        private double _previewHeight;

        public LoopLabBottomPanel()
        {
            InitializeComponent();
            Height = PeekHeight;
            Loaded += (_, __) =>
            {
                if (!_isOpen && !_isResizing)
                    Height = PeekHeight;
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

        private static void OnExpandedHeightChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LoopLabBottomPanel panel && panel._isOpen && !panel._isResizing)
                panel.Height = ClampExpandedHeight(panel.ExpandedHeight);
        }

        private void Root_MouseEnter(object sender, MouseEventArgs e)
        {
            SlideOpen();
        }

        private void Root_MouseLeave(object sender, MouseEventArgs e)
        {
            if (_isResizing)
                return;

            SlideClosed();
        }

        private void SlideOpen()
        {
            _isOpen = true;
            IsExpanded = true;
            AnimateHeight(ClampExpandedHeight(ExpandedHeight));
        }

        private void SlideClosed()
        {
            _isOpen = false;
            IsExpanded = false;
            AnimateHeight(PeekHeight);
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
            PanelContent.Effect = new BlurEffect { Radius = 8, RenderingBias = RenderingBias.Performance };
            UpdateResizePreview();
        }

        private void UpdateResizePreview()
        {
            var panelWidth = Math.Max(0, ActualWidth);
            var anchorHeight = Math.Max(_frozenHeight, ActualHeight);

            Canvas.SetLeft(ResizePreviewGhost, 0);
            Canvas.SetTop(ResizePreviewGhost, anchorHeight - _previewHeight);
            ResizePreviewGhost.Width = panelWidth;
            ResizePreviewGhost.Height = _previewHeight;
            ResizePreviewGhost.Visibility = Visibility.Visible;
        }

        private void EndResizePreview()
        {
            PanelContent.Effect = null;
            ResizePreviewGhost.Visibility = Visibility.Collapsed;
        }

        private void ResizeBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_isOpen)
                return;

            _isResizing = true;
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
            FinishResize();
            e.Handled = true;
        }

        private void OnLostMouseCapture(object sender, MouseEventArgs e)
        {
            if (_isResizing)
                FinishResize();
        }

        private void UpdatePreviewFromMouse(double currentY)
        {
            var delta = _resizeStartY - currentY;
            _previewHeight = ClampExpandedHeight(_resizeStartHeight + delta);
            UpdateResizePreview();
        }

        private void FinishResize()
        {
            if (!_isResizing)
                return;

            _isResizing = false;
            Mouse.Capture(null);
            ExpandedHeight = _previewHeight;
            EndResizePreview();

            if (_isOpen || IsMouseOver)
            {
                _isOpen = true;
                Height = ClampExpandedHeight(ExpandedHeight);
            }
            else
            {
                SlideClosed();
            }
        }
    }
}
