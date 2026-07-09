using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
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

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (ValueSlider == null)
                return;

            ValueSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnDragStarted), true);
            ValueSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnDragCompleted), true);
            ValueSlider.LostMouseCapture += OnLostMouseCapture;
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

        private void OnLostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
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
