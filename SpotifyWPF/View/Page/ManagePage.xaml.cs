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
            {
                playlists.PropertyChanged -= PlaylistsVm_PropertyChanged;
                playlists.PropertyChanged += PlaylistsVm_PropertyChanged;
                await playlists.OnNavigatedToAsync();
            }
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

        private void PlaylistsVm_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Only react to controls expand/collapse — not FillControlsPanel (that is set by us).
            if (e.PropertyName == nameof(PlaylistsPageViewModel.IsControlsPanelExpanded))
                ApplySectionLayout();
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
        /// Exclusive accordion: one star-sized open section (or none). Content stays in the tree;
        /// closed sections are Collapsed (not unloaded) so toggling is cheap and stable.
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
                var playlistsVm = ServiceLocator.Current.GetInstance<PlaylistsPageViewModel>();
                var controlsOpen = playlistsVm?.IsControlsPanelExpanded == true;
                var noSectionOpen = manage.ActiveSection == ManageSection.None;

                PlaylistsChevron.Text = manage.IsPlaylistsOpen ? "▼" : "▲";
                AlbumsChevron.Text = manage.IsAlbumsOpen ? "▼" : "▲";
                ArtistsChevron.Text = manage.IsArtistsOpen ? "▼" : "▲";
                SearchChevron.Text = manage.IsSearchOpen ? "▼" : "▲";

                // Drive visibility in code: child pages set their own DataContext, so
                // {Binding IsXOpen} on the page element would bind to the wrong VM.
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

                if (playlistsVm != null)
                    playlistsVm.IsPlaylistsSectionExpanded = !noSectionOpen;

                if (noSectionOpen && controlsOpen)
                {
                    SetRowHeight(SectionsRow, GridLength.Auto);
                    SetRowHeight(ControlsRow, new GridLength(1, GridUnitType.Star));
                    if (ControlsPanel != null)
                        ControlsPanel.FillRemainingSpace = true;
                }
                else
                {
                    SetRowHeight(SectionsRow, new GridLength(1, GridUnitType.Star));
                    SetRowHeight(ControlsRow, GridLength.Auto);
                    if (ControlsPanel != null)
                        ControlsPanel.FillRemainingSpace = false;
                }
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
            SetRowHeight(row, open
                ? new GridLength(1, GridUnitType.Star)
                : GridLength.Auto);
        }

        private static void SetRowHeight(RowDefinition row, GridLength height)
        {
            if (row == null)
                return;

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
