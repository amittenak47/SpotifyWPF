using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace SpotifyWPF.View.Page
{
    public partial class ArtistsPage
    {
        public ArtistsPage()
        {
            InitializeComponent();
            IsVisibleChanged += ArtistsPage_IsVisibleChanged;
        }

        private void ArtistsPage_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
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
