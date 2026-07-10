using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace SpotifyWPF.View.Page
{
    /// <summary>
    /// Interaction logic for PlaylistsPage.xaml
    /// </summary>
    public partial class PlaylistsPage
    {
        public PlaylistsPage()
        {
            InitializeComponent();
            Loaded += PlaylistsPage_Loaded;
        }

        private void PlaylistsPage_Loaded(object sender, RoutedEventArgs e)
        {
            // Fade playlist chrome in from black — after login this overlaps the login overlay fade-out.
            Opacity = 0;
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(450))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
            });
        }
    }
}
