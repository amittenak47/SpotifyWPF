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

            if (hostWidth <= 0 || textWidth <= hostWidth + 4)
            {
                ScrollTransform.X = 0;
                return;
            }

            var animation = new DoubleAnimation
            {
                From = hostWidth,
                To = -textWidth,
                Duration = TimeSpan.FromSeconds(Math.Max(6, textWidth / 40)),
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
