using System.Windows;
using System.Windows.Controls;
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
        public PredictionPage()
        {
            InitializeComponent();
        }

        private async void PredictionPage_Loaded(object sender, RoutedEventArgs e)
        {
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
            if (DataContext is PredictionPageViewModel viewModel)
                await viewModel.OnPageLoadedAsync();
        }

        private void PredictionPage_Unloaded(object sender, RoutedEventArgs e)
        {
            var view = PlayerHostBorder.Child;

            if (view == null)
                return;

            PlayerHostBorder.Child = null;
            (Window.GetWindow(this) as MainWindow)?.ParkWebPlaybackView(view);
        }
    }
}
