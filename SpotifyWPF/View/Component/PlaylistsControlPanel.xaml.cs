using System.Windows.Input;
using SpotifyWPF.Model;
using SpotifyWPF.ViewModel.Component;
using SpotifyWPF.ViewModel.Page;

namespace SpotifyWPF.View.Component
{
    public partial class PlaylistsControlPanel
    {
        public static readonly System.Windows.DependencyProperty ActivityLogProperty =
            System.Windows.DependencyProperty.Register(
                nameof(ActivityLog),
                typeof(ActivityLogViewModel),
                typeof(PlaylistsControlPanel),
                new System.Windows.PropertyMetadata(null));

        public static readonly System.Windows.DependencyProperty IsExpandedProperty =
            System.Windows.DependencyProperty.Register(
                nameof(IsExpanded),
                typeof(bool),
                typeof(PlaylistsControlPanel),
                new System.Windows.FrameworkPropertyMetadata(
                    true,
                    System.Windows.FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        public PlaylistsControlPanel()
        {
            InitializeComponent();
        }

        public ActivityLogViewModel ActivityLog
        {
            get => (ActivityLogViewModel)GetValue(ActivityLogProperty);
            set => SetValue(ActivityLogProperty, value);
        }

        public bool IsExpanded
        {
            get => (bool)GetValue(IsExpandedProperty);
            set => SetValue(IsExpandedProperty, value);
        }

        private void QueuedActionsTreeView_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Delete)
                return;

            var viewModel = DataContext as PlaylistsPageViewModel;
            if (viewModel == null)
                return;

            if (QueuedActionsTreeView.SelectedItem is QueuedActionDetailItem detail)
            {
                var parentAction = viewModel.FindQueuedActionForDetail(detail);
                if (parentAction != null)
                {
                    viewModel.RemoveQueuedActionDetail(parentAction, detail);
                    e.Handled = true;
                }

                return;
            }

            if (QueuedActionsTreeView.SelectedItem is QueuedPlaylistAction action)
            {
                viewModel.RemoveQueuedAction(action);
                e.Handled = true;
            }
        }
    }
}
