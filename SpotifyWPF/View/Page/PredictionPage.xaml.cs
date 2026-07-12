using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using CommonServiceLocator;
using SpotifyWPF.Service.Playback;
using SpotifyWPF.View.Component;
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

            // Full-page zoom lives on StageRoot so search/wheel stay locked to the ring.
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

        private Point _ssmDragOffset;
        private bool _ssmDragging;

        private void SsmPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var panel = sender as Border;

            if (panel == null)
                return;

            // Don't start a drag when interacting with the heatmap image itself for click-select —
            // only drag from the title bar area (top ~22px) or empty chrome.
            var pos = e.GetPosition(panel);

            if (pos.Y > 28 && e.OriginalSource is SelfSimilarityHeatmapControl)
                return;

            _ssmDragging = true;
            _ssmDragOffset = e.GetPosition(panel);
            panel.CaptureMouse();
            e.Handled = true;
        }

        private void SsmPanel_MouseMove(object sender, MouseEventArgs e)
        {
            var panel = sender as Border;

            if (!_ssmDragging || panel == null || StageRoot == null)
                return;

            var parentPos = e.GetPosition(StageRoot);
            var left = Math.Max(0, Math.Min(parentPos.X - _ssmDragOffset.X, StageRoot.Width - panel.Width));
            var top = Math.Max(0, Math.Min(parentPos.Y - _ssmDragOffset.Y, StageRoot.Height - panel.Height));
            panel.Margin = new Thickness(left, top, 0, 0);
            panel.HorizontalAlignment = HorizontalAlignment.Left;
            panel.VerticalAlignment = VerticalAlignment.Top;
        }

        private void SsmPanel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var panel = sender as Border;

            if (!_ssmDragging || panel == null)
                return;

            _ssmDragging = false;
            panel.ReleaseMouseCapture();
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

        private void PaulLamereLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }
    }
}
