using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using CommonServiceLocator;
using SpotifyWPF.ViewModel.Page;

namespace SpotifyWPF.View.Page
{
    public partial class ManagePage
    {
        private bool _applyingLayout;

        public ManagePage()
        {
            InitializeComponent();
            Loaded += ManagePage_Loaded;
            DataContextChanged += (_, __) => WireViewModel();
        }

        private async void ManagePage_Loaded(object sender, RoutedEventArgs e)
        {
            WireViewModel();
            ApplySectionLayout();

            var playlists = ServiceLocator.Current.GetInstance<PlaylistsPageViewModel>();
            if (playlists != null)
                await playlists.OnNavigatedToAsync();
        }

        private void WireViewModel()
        {
            if (DataContext is ManagePageViewModel vm)
            {
                vm.PropertyChanged -= ManageVm_PropertyChanged;
                vm.PropertyChanged += ManageVm_PropertyChanged;
            }
        }

        private void ManageVm_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ManagePageViewModel.ActiveSection) ||
                e.PropertyName == nameof(ManagePageViewModel.IsPlaylistsOpen) ||
                e.PropertyName == nameof(ManagePageViewModel.IsAlbumsOpen) ||
                e.PropertyName == nameof(ManagePageViewModel.IsArtistsOpen) ||
                e.PropertyName == nameof(ManagePageViewModel.IsSearchOpen))
            {
                ApplySectionLayout();
            }
        }

        private void PlaylistsHeader_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is ManagePageViewModel vm)
                vm.ToggleSection(ManageSection.Playlists);
        }

        private void AlbumsHeader_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is ManagePageViewModel vm)
                vm.ToggleSection(ManageSection.Albums);
        }

        private void ArtistsHeader_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is ManagePageViewModel vm)
                vm.ToggleSection(ManageSection.Artists);
        }

        private void SearchHeader_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is ManagePageViewModel vm)
                vm.ToggleSection(ManageSection.Search);
        }

        /// <summary>
        /// Exclusive accordion: one star-sized open section (or none). Content stays Collapsed, not unloaded.
        /// </summary>
        private void ApplySectionLayout()
        {
            if (_applyingLayout)
                return;

            if (!(DataContext is ManagePageViewModel manage))
                return;

            _applyingLayout = true;
            try
            {
                PlaylistsChevron.Text = manage.IsPlaylistsOpen ? "▼" : "▲";
                AlbumsChevron.Text = manage.IsAlbumsOpen ? "▼" : "▲";
                ArtistsChevron.Text = manage.IsArtistsOpen ? "▼" : "▲";
                SearchChevron.Text = manage.IsSearchOpen ? "▼" : "▲";

                SetContentVisible(PlaylistsContent, manage.IsPlaylistsOpen);
                SetContentVisible(AlbumsContent, manage.IsAlbumsOpen);
                SetContentVisible(ArtistsContent, manage.IsArtistsOpen);
                SetContentVisible(SearchContent, manage.IsSearchOpen);

                SetSectionRow(PlaylistsSectionRow, manage.IsPlaylistsOpen);
                SetSectionRow(AlbumsSectionRow, manage.IsAlbumsOpen);
                SetSectionRow(ArtistsSectionRow, manage.IsArtistsOpen);
                SetSectionRow(SearchSectionRow, manage.IsSearchOpen);

                SetSectionBorder(PlaylistsSectionBorder, manage.IsPlaylistsOpen);
                SetSectionBorder(AlbumsSectionBorder, manage.IsAlbumsOpen);
                SetSectionBorder(ArtistsSectionBorder, manage.IsArtistsOpen);
                SetSectionBorder(SearchSectionBorder, manage.IsSearchOpen);
            }
            finally
            {
                _applyingLayout = false;
            }
        }

        private static void SetContentVisible(UIElement element, bool open)
        {
            if (element == null)
                return;

            var visibility = open ? Visibility.Visible : Visibility.Collapsed;
            if (element.Visibility != visibility)
                element.Visibility = visibility;
        }

        private static void SetSectionRow(RowDefinition row, bool open)
        {
            if (row == null)
                return;

            var height = open
                ? new GridLength(1, GridUnitType.Star)
                : GridLength.Auto;

            if (row.Height.GridUnitType == height.GridUnitType &&
                row.Height.Value == height.Value)
                return;

            row.Height = height;
        }

        private static void SetSectionBorder(Border border, bool open)
        {
            if (border == null)
                return;

            var align = open ? VerticalAlignment.Stretch : VerticalAlignment.Top;
            if (border.VerticalAlignment != align)
                border.VerticalAlignment = align;
        }
    }
}
