using System.Windows.Controls;
using System.Windows.Input;
using SpotifyWPF.Model;
using SpotifyWPF.ViewModel.Page;

namespace SpotifyWPF.View.Component
{
    public partial class LoopLabLibrarySidebar
    {
        public LoopLabLibrarySidebar()
        {
            InitializeComponent();
        }

        private void LibraryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (!(DataContext is PredictionPageViewModel vm))
                return;

            if (!(sender is ListBox list) || !(list.SelectedItem is PlaylistCacheItem playlist))
                return;

            if (vm.AddLibraryPlaylistToSessionCommand.CanExecute(playlist))
                vm.AddLibraryPlaylistToSessionCommand.Execute(playlist);
        }
    }
}
