using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SpotifyWPF.View.Component
{
    public partial class MiniPlayerView
    {
        public MiniPlayerView()
        {
            InitializeComponent();
        }

        private void ControlsBackdrop_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (IsWithinButton(e.OriginalSource as DependencyObject))
                return;

            Window.GetWindow(this)?.DragMove();
        }

        private static bool IsWithinButton(DependencyObject source)
        {
            while (source != null)
            {
                if (source is Button)
                    return true;

                source = VisualTreeHelper.GetParent(source);
            }

            return false;
        }
    }
}
