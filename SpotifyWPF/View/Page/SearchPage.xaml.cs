using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace SpotifyWPF.View.Page
{
    public partial class SearchPage
    {
        public SearchPage()
        {
            InitializeComponent();
            IsVisibleChanged += SearchPage_IsVisibleChanged;
        }

        private void SearchPage_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (IsVisible)
                FadeIn();
        }

        private void FadeIn()
        {
            Opacity = 0;
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(450))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
            });
        }
    }
}
