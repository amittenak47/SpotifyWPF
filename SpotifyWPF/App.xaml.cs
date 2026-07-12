using System.Windows;
using System.Windows.Controls;
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

            // Kill OS ToolTip popups app-wide (they ignore FrameworkElement styles once a
            // control has its own Style). Custom hover (opacity zones, ring HUD) is unaffected.
            EventManager.RegisterClassHandler(
                typeof(FrameworkElement),
                ToolTipService.ToolTipOpeningEvent,
                new ToolTipEventHandler((_, args) => args.Handled = true));

            var themeStore = new AppThemeStore();
            AppThemeManager.Apply(themeStore.Get());
        }
    }
}
