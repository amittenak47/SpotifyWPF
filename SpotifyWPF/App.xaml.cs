using System.Windows;
using SpotifyWPF.Service.Theme;

namespace SpotifyWPF
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var themeStore = new AppThemeStore();
            AppThemeManager.Apply(themeStore.Get());
        }
    }
}
