using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SpotifyWPF.ViewModel.Page;

namespace SpotifyWPF.View.Component
{
    public partial class TuningSliderRow : UserControl
    {
        public static readonly DependencyProperty LabelProperty =
            DependencyProperty.Register(nameof(Label), typeof(string), typeof(TuningSliderRow));

        public static readonly DependencyProperty ValueTextProperty =
            DependencyProperty.Register(nameof(ValueText), typeof(string), typeof(TuningSliderRow));

        public static readonly DependencyProperty MinimumProperty =
            DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(TuningSliderRow));

        public static readonly DependencyProperty MaximumProperty =
            DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(TuningSliderRow));

        public static readonly DependencyProperty ValueProperty =
            DependencyProperty.Register(
                nameof(Value),
                typeof(double),
                typeof(TuningSliderRow),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        public static readonly DependencyProperty InfoTipProperty =
            DependencyProperty.Register(nameof(InfoTip), typeof(string), typeof(TuningSliderRow),
                new PropertyMetadata(null, OnInfoTipChanged));

        private bool _dragActive;

        public TuningSliderRow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        public string Label
        {
            get => (string)GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }

        public string ValueText
        {
            get => (string)GetValue(ValueTextProperty);
            set => SetValue(ValueTextProperty, value);
        }

        public double Minimum
        {
            get => (double)GetValue(MinimumProperty);
            set => SetValue(MinimumProperty, value);
        }

        public double Maximum
        {
            get => (double)GetValue(MaximumProperty);
            set => SetValue(MaximumProperty, value);
        }

        public double Value
        {
            get => (double)GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        /// <summary>Hover (i) shows a fading tutorial popup with design + example.</summary>
        public string InfoTip
        {
            get => (string)GetValue(InfoTipProperty);
            set => SetValue(InfoTipProperty, value);
        }

        private static void OnInfoTipChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TuningSliderRow row)
                row.UpdateInfoVisibility();
        }

        private void UpdateInfoVisibility()
        {
            if (InfoButton != null)
                InfoButton.Visibility = string.IsNullOrWhiteSpace(InfoTip)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            UpdateInfoVisibility();

            if (ValueSlider == null)
                return;

            ValueSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnDragStarted), true);
            ValueSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnDragCompleted), true);
            ValueSlider.LostMouseCapture += OnLostMouseCapture;
        }

        private void InfoButton_MouseEnter(object sender, MouseEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(InfoTip) || InfoPopup == null || InfoPopupText == null)
                return;

            InfoPopupText.Text = InfoTip;
            InfoPopup.IsOpen = true;
            FadePopup(0, 1, 180);
        }

        private void InfoButton_MouseLeave(object sender, MouseEventArgs e)
        {
            if (InfoPopup == null || !InfoPopup.IsOpen)
                return;

            FadePopup(InfoPopup.Opacity, 0, 220, () => InfoPopup.IsOpen = false);
        }

        private void FadePopup(double from, double to, int ms, System.Action onComplete = null)
        {
            var anim = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            if (onComplete != null)
            {
                anim.Completed += (_, __) => onComplete();
            }

            InfoPopup.BeginAnimation(OpacityProperty, anim);
        }

        private void OnDragStarted(object sender, DragStartedEventArgs e)
        {
            _dragActive = true;
            FindViewModel()?.BeginSliderDrag();
        }

        private void OnDragCompleted(object sender, DragCompletedEventArgs e)
        {
            EndDragPersist();
        }

        private void OnLostMouseCapture(object sender, MouseEventArgs e)
        {
            EndDragPersist();
        }

        private void EndDragPersist()
        {
            if (!_dragActive)
                return;

            _dragActive = false;
            FindViewModel()?.EndSliderDrag();
        }

        private PredictionPageViewModel FindViewModel()
        {
            for (var source = (DependencyObject)this; source != null; source = GetParent(source))
            {
                if (source is FrameworkElement element && element.DataContext is PredictionPageViewModel viewModel)
                    return viewModel;
            }

            return null;
        }

        private static DependencyObject GetParent(DependencyObject child)
        {
            if (child is FrameworkElement || child is FrameworkContentElement)
            {
                var parent = VisualTreeHelper.GetParent(child);
                if (parent != null)
                    return parent;
            }

            return LogicalTreeHelper.GetParent(child);
        }
    }
}
