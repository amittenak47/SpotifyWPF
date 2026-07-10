using System.Diagnostics;
using System.Windows.Navigation;

namespace SpotifyWPF.View.Page
{
    /// <summary>
    /// Interaction logic for LoginPage.xaml
    /// </summary>
    public partial class LoginPage
    {
        // Kept alive by the page; owns the phase-driven fade choreography.
        private readonly LoginTransitionController _transitions;

        public LoginPage()
        {
            InitializeComponent();
            _transitions = new LoginTransitionController(this, LoginFormPanel, LoadingOverlay,
                LoginStatusTextBlock);
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }
    }
}
