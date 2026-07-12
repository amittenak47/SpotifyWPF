using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SpotifyWPF.View.Component
{
    public partial class MarqueeTextBlock : UserControl
    {
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register(nameof(Text), typeof(string), typeof(MarqueeTextBlock),
                new PropertyMetadata(string.Empty, OnTextChanged));

        public static readonly DependencyProperty AlwaysScrollProperty =
            DependencyProperty.Register(nameof(AlwaysScroll), typeof(bool), typeof(MarqueeTextBlock),
                new PropertyMetadata(false, OnTextChanged));

        private Storyboard _storyboard;

        public MarqueeTextBlock()
        {
            InitializeComponent();
            Loaded += (_, __) => UpdateMarquee();
            SizeChanged += (_, __) => UpdateMarquee();
        }

        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        public bool AlwaysScroll
        {
            get => (bool)GetValue(AlwaysScrollProperty);
            set => SetValue(AlwaysScrollProperty, value);
        }

        private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MarqueeTextBlock block)
                block.UpdateMarquee();
        }

        private void UpdateMarquee()
        {
            _storyboard?.Stop();
            _storyboard = null;
            ScrollTransform.X = 0;

            if (!IsLoaded || string.IsNullOrEmpty(Text))
                return;

            MeasureText.Text = Text;
            MeasureText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            ScrollText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            var textWidth = ScrollText.DesiredSize.Width;
            var hostWidth = ActualWidth;

            if (hostWidth <= 0)
                return;

            if (!AlwaysScroll && textWidth <= hostWidth + 4)
            {
                // Fit: left-align like a window readout (not centered).
                ScrollTransform.X = 0;
                return;
            }

            // Window-display scroll: enter from the right, exit to the left.
            var gap = Math.Max(24, hostWidth * 0.25);
            var animation = new DoubleAnimation
            {
                From = hostWidth,
                To = -textWidth - gap,
                Duration = TimeSpan.FromSeconds(Math.Max(8, (hostWidth + textWidth) / 40)),
                RepeatBehavior = RepeatBehavior.Forever
            };

            _storyboard = new Storyboard();
            _storyboard.Children.Add(animation);
            Storyboard.SetTarget(animation, ScrollTransform);
            Storyboard.SetTargetProperty(animation, new PropertyPath(TranslateTransform.XProperty));
            _storyboard.Begin();
        }
    }
}
