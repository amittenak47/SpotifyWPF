using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SpotifyWPF.View.Component
{
    public partial class JukeboxRingView : UserControl
    {
        private const double MinZoom = 0.75;

        private const double MaxZoom = 2.0;

        private double _zoom = 1.0;

        public static readonly DependencyProperty MiniPlayerModeProperty =
            DependencyProperty.Register(
                nameof(MiniPlayerMode),
                typeof(bool),
                typeof(JukeboxRingView),
                new PropertyMetadata(false, OnMiniPlayerModeChanged));

        public bool MiniPlayerMode
        {
            get => (bool)GetValue(MiniPlayerModeProperty);
            set => SetValue(MiniPlayerModeProperty, value);
        }

        public JukeboxRingCanvas RingCanvas => Ring;

        public JukeboxRingView()
        {
            InitializeComponent();
        }

        private static void OnMiniPlayerModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is JukeboxRingView view && (bool)e.NewValue)
                view.ResetZoom();
        }

        private void ZoomHost_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (MiniPlayerMode)
                return;

            var factor = e.Delta > 0 ? 1.1 : 1.0 / 1.1;
            _zoom = Math.Max(MinZoom, Math.Min(MaxZoom, _zoom * factor));
            ApplyZoom();
            e.Handled = true;
        }

        private void ApplyZoom()
        {
            ZoomContent.RenderTransform = new ScaleTransform(_zoom, _zoom);
        }

        private void ResetZoom()
        {
            _zoom = 1.0;
            ApplyZoom();
        }
    }
}
