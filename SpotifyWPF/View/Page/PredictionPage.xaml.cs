using System.Windows;
using System.Windows.Controls;
using CommonServiceLocator;
using SpotifyWPF.Service.Playback;
using SpotifyWPF.ViewModel.Page;

namespace SpotifyWPF.View.Page
{
    /// <summary>
    /// Interaction logic for PredictionPage.xaml. The WebView2 player is a singleton owned by
    /// IWebPlaybackHost and is re-parented into this page on load so playback survives navigation.
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

            if (!ReferenceEquals(PlayerHostBorder.Child, view))
            {
                if (view.Parent is Border previousParent)
                    previousParent.Child = null;

                PlayerHostBorder.Child = view;
            }

            if (DataContext is PredictionPageViewModel viewModel)
                await viewModel.OnPageLoadedAsync();
        }

        private void PredictionPage_Unloaded(object sender, RoutedEventArgs e)
        {
            // Detach but do not dispose: the shared WebView2 keeps playing while other pages are shown.
            if (PlayerHostBorder.Child != null)
                PlayerHostBorder.Child = null;
        }
    }
}
