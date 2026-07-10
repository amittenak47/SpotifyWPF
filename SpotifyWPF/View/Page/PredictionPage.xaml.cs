using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Navigation;using CommonServiceLocator;
using SpotifyWPF.Service.Playback;
using SpotifyWPF.ViewModel.Page;

namespace SpotifyWPF.View.Page
{
    /// <summary>
    /// Interaction logic for PredictionPage.xaml. The WebView2 player is a singleton owned by
    /// IWebPlaybackHost. It is shown on this page while Loop Lab is active and parked on
    /// MainWindow while other pages are visible so CoreWebView2 stays alive.
    /// </summary>
    public partial class PredictionPage
    {
        private bool _scrubStarted;

        public PredictionPage()
        {
            InitializeComponent();
        }

        private PredictionPageViewModel ViewModel => DataContext as PredictionPageViewModel;

        private async void PredictionPage_Loaded(object sender, RoutedEventArgs e)
        {
            // Fade the page in from black — after login this overlaps the login overlay's
            // fade-out, so the Infinite Jukebox appears underneath instead of popping in.
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(450))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
            });

            var host = ServiceLocator.Current.GetInstance<IWebPlaybackHost>();
            var view = host.GetOrCreateView();
            var mainWindow = Window.GetWindow(this) as MainWindow;

            mainWindow?.UnparkWebPlaybackView(view);

            if (!ReferenceEquals(PlayerHostBorder.Child, view))
            {
                if (view.Parent is Border previousParent)
                    previousParent.Child = null;

                PlayerHostBorder.Child = view;
            }

            // WebView2 must be in the visual tree before CoreWebView2 initializes.
            if (ViewModel != null)
                await ViewModel.OnPageLoadedAsync();
        }

        private void PredictionPage_Unloaded(object sender, RoutedEventArgs e)
        {
            if (ViewModel?.IsMiniPlayerMode == true)
                ViewModel.IsMiniPlayerMode = false;

            var view = PlayerHostBorder.Child;

            if (view == null)
                return;

            PlayerHostBorder.Child = null;
            (Window.GetWindow(this) as MainWindow)?.ParkWebPlaybackView(view);
        }

        private void Scrubber_Loaded(object sender, RoutedEventArgs e)
        {
            if (!(sender is Slider slider))
                return;

            slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(Scrubber_DragStarted), true);
            slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(Scrubber_DragCompleted), true);
        }

        private void Scrubber_DragStarted(object sender, DragStartedEventArgs e)
        {
            _scrubStarted = true;
            ViewModel?.BeginScrub();
        }

        private void Scrubber_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            EndScrubIfActive();
        }

        private void Scrubber_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _scrubStarted = true;
            ViewModel?.BeginScrub();
        }

        private void Scrubber_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            EndScrubIfActive();
        }

        private void Scrubber_LostMouseCapture(object sender, MouseEventArgs e)
        {
            EndScrubIfActive();
        }

        private void EndScrubIfActive()
        {
            if (!_scrubStarted)
                return;

            _scrubStarted = false;
            ViewModel?.EndScrub();
        }

        private void PaulLamereLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }
    }
}
