using System;
using System.ComponentModel;
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
        private const double HoverDimOpacity = 0.35;
        private const double HoverFullOpacity = 1.0;
        private const double HoverFadeMs = 180;
        private const double MinStageZoom = 0.75;
        private const double MaxStageZoom = 2.0;
        private static readonly TimeSpan SearchPopupFadeMs = TimeSpan.FromMilliseconds(180);

        private double _stageZoom = 1.0;
        private PredictionPageViewModel _subscribedVm;

        public PredictionPage()
        {
            InitializeComponent();
            DataContextChanged += PredictionPage_DataContextChanged;
            Loaded += (_, __) => SyncSearchPopup(animate: false);
        }

        private PredictionPageViewModel ViewModel => DataContext as PredictionPageViewModel;

        private void PredictionPage_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_subscribedVm != null)
                _subscribedVm.PropertyChanged -= ViewModel_PropertyChanged;

            _subscribedVm = e.NewValue as PredictionPageViewModel;
            if (_subscribedVm != null)
                _subscribedVm.PropertyChanged += ViewModel_PropertyChanged;

            SyncSearchPopup(animate: false);
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PredictionPageViewModel.IsTrackSearchOpen))
                AnimateSearchPopup(_subscribedVm?.IsTrackSearchOpen == true);
        }

        private void SyncSearchPopup(bool animate)
        {
            if (ViewModel == null)
                return;

            if (animate)
                AnimateSearchPopup(ViewModel.IsTrackSearchOpen);
            else
                ApplySearchPopupImmediate(ViewModel.IsTrackSearchOpen);
        }

        private void ApplySearchPopupImmediate(bool open)
        {
            if (SearchResultsPopup == null)
                return;

            SearchResultsPopup.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            SearchResultsPopup.Opacity = open ? 1 : 0;
            SearchResultsPopup.IsHitTestVisible = open;
        }

        private void AnimateSearchPopup(bool open)
        {
            if (SearchResultsPopup == null)
                return;

            if (open)
            {
                SearchResultsPopup.Visibility = Visibility.Visible;
                SearchResultsPopup.IsHitTestVisible = true;
                SearchResultsPopup.BeginAnimation(OpacityProperty, new DoubleAnimation
                {
                    From = 0,
                    To = 1,
                    Duration = SearchPopupFadeMs,
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.HoldEnd
                }, HandoffBehavior.SnapshotAndReplace);
            }
            else
            {
                SearchResultsPopup.IsHitTestVisible = false;
                var fade = new DoubleAnimation
                {
                    To = 0,
                    Duration = TimeSpan.FromMilliseconds(140),
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseIn },
                    FillBehavior = FillBehavior.Stop
                };
                fade.Completed += (_, __) =>
                {
                    if (ViewModel?.IsTrackSearchOpen != true)
                    {
                        SearchResultsPopup.Visibility = Visibility.Collapsed;
                        SearchResultsPopup.Opacity = 0;
                    }
                };
                SearchResultsPopup.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
            }
        }

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
            SyncSearchPopup(animate: false);

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
            if (sender == SearchHoverZone && TrackSearchBox != null)
                FadeElementOpacity(TrackSearchBox, 1.0);
            else if (sender == WheelHoverZone && TransportWheel != null)
                FadeElementOpacity(TransportWheel, 1.0);
            else if (sender == StatusHoverZone)
                FadeHoverZone(StatusHoverZone, HoverFullOpacity);
        }

        private void HoverZone_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender == SearchHoverZone && TrackSearchBox != null)
                FadeElementOpacity(TrackSearchBox, 0.35);
            else if (sender == WheelHoverZone && TransportWheel != null)
                FadeElementOpacity(TransportWheel, 0.42);
            else if (sender == StatusHoverZone)
                FadeHoverZone(StatusHoverZone, HoverDimOpacity);
        }

        private static void FadeElementOpacity(UIElement element, double to)
        {
            element.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(to, TimeSpan.FromMilliseconds(HoverFadeMs))
                {
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                    FillBehavior = FillBehavior.HoldEnd
                },
                HandoffBehavior.SnapshotAndReplace);
        }

        private void ManualConfirm_Click(object sender, RoutedEventArgs e)
        {
            RingView?.RingCanvas?.ConfirmManualSelectionPublic();
        }

        private void ManualCancel_Click(object sender, RoutedEventArgs e)
        {
            RingView?.RingCanvas?.CancelManualSelectionPublic();
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
            if (e.Key == Key.Escape)
            {
                if (ViewModel != null)
                    ViewModel.IsTrackSearchOpen = false;
                e.Handled = true;
                return;
            }

            if (e.Key != Key.Enter)
                return;

            if (ViewModel?.IsTrackSearchOpen == true && ViewModel.TrackSearchResults.Count > 0)
            {
                ViewModel.SelectTrackSearchHitCommand.Execute(ViewModel.TrackSearchResults[0]);
                e.Handled = true;
                return;
            }

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
