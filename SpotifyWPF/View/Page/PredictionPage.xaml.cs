using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using CommonServiceLocator;
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
        private const double HoverDimOpacity = 0.18;
        private const double HoverFullOpacity = 1.0;
        private const double HoverFadeMs = 180;
        private const double MinStageZoom = 0.75;
        private const double MaxStageZoom = 2.0;

        private bool _scrubStarted;
        private double _stageZoom = 1.0;

        public PredictionPage()
        {
            InitializeComponent();
        }

        private PredictionPageViewModel ViewModel => DataContext as PredictionPageViewModel;

        private async void PredictionPage_Loaded(object sender, RoutedEventArgs e)
        {
            // Fade the page in from black — after login this overlaps the login overlay's
            // fade-out, so the Infinite Jukebox appears underneath instead of popping in.
            Opacity = 0;
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(450))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
            });

            // Full-page zoom lives on StageRoot so search/scrubber/wheel stay locked to the ring.
            RingView.OwnsWheelZoom = false;
            ApplyStageZoom();

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

        private void StageHost_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Delta == 0)
                return;

            var factor = e.Delta > 0 ? 1.1 : 1.0 / 1.1;
            _stageZoom = Math.Max(MinStageZoom, Math.Min(MaxStageZoom, _stageZoom * factor));
            ApplyStageZoom();
            e.Handled = true;
        }

        private void ApplyStageZoom()
        {
            if (StageRoot == null)
                return;

            StageRoot.RenderTransform = new ScaleTransform(_stageZoom, _stageZoom);
        }

        private void HoverZone_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is UIElement element)
                FadeHoverZone(element, HoverFullOpacity);
        }

        private void HoverZone_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is UIElement element)
                FadeHoverZone(element, HoverDimOpacity);
        }

        private static void FadeHoverZone(UIElement element, double to)
        {
            element.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(to, TimeSpan.FromMilliseconds(HoverFadeMs))
                {
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                    FillBehavior = FillBehavior.HoldEnd
                },
                HandoffBehavior.SnapshotAndReplace);
        }

        private void TrackSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;

            if (ViewModel?.PlayFromInputCommand.CanExecute(null) == true)
                ViewModel.PlayFromInputCommand.Execute(null);

            e.Handled = true;
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
