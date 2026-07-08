using System.Windows.Input;
using SpotifyWPF.Model;
using SpotifyWPF.ViewModel.Page;

namespace SpotifyWPF.View.Page
{
    /// <summary>
    /// Interaction logic for PlaylistsPage.xaml
    /// </summary>
    public partial class PlaylistsPage
    {
        public PlaylistsPage()
        {
            InitializeComponent();
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
