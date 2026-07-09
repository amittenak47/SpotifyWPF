using System.Windows;
using System.Windows.Controls;

namespace SpotifyWPF.View.Component
{
    public partial class JukeboxRingView : UserControl
    {
        public static readonly DependencyProperty MiniPlayerModeProperty =
            DependencyProperty.Register(
                nameof(MiniPlayerMode),
                typeof(bool),
                typeof(JukeboxRingView),
                new PropertyMetadata(false));

        public bool MiniPlayerMode
        {
            get => (bool)GetValue(MiniPlayerModeProperty);
            set => SetValue(MiniPlayerModeProperty, value);
        }

        public JukeboxRingView()
        {
            InitializeComponent();
        }
    }
}
