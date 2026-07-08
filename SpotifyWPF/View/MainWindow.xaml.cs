using System.Windows;
using SpotifyWPF.View.Extension;

namespace SpotifyWPF.View
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow
    {
        public MainWindow()
        {
            InitializeComponent();
            DarkWindowChrome.Apply(this);
        }

        /// <summary>
        /// Keeps the shared WebView2 player in the visual tree while other pages are shown.
        /// Detaching WebView2 entirely causes crashes when Loop Lab is revisited.
        /// </summary>
        public void ParkWebPlaybackView(UIElement view)
        {
            if (view == null)
                return;

            WebPlaybackHiddenHost.Child = view;
        }

        public void UnparkWebPlaybackView(UIElement view)
        {
            if (view != null && ReferenceEquals(WebPlaybackHiddenHost.Child, view))
                WebPlaybackHiddenHost.Child = null;
        }
    }
}
