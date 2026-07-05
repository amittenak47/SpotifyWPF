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
    }
}
