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
                // ButtonBase covers the transport buttons plus the ring-lock CheckBox.
                if (source is System.Windows.Controls.Primitives.ButtonBase)
                    return true;

                source = VisualTreeHelper.GetParent(source);
            }

            return false;
        }
    }
}
