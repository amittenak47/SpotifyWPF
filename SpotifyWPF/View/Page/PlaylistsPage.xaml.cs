using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using SpotifyWPF.Service;
using SpotifyWPF.ViewModel.Page;

namespace SpotifyWPF.View.Page
{
    /// <summary>
    /// Interaction logic for PlaylistsPage.xaml
    /// </summary>
    public partial class PlaylistsPage
    {
        private Dictionary<string, bool> _columnVisibility;
        private bool _playlistsExpanded = true;

        public PlaylistsPage()
        {
            InitializeComponent();
            Loaded += PlaylistsPage_Loaded;
            DataContextChanged += (_, __) => WireViewModel();
        }

        private void PlaylistsPage_Loaded(object sender, RoutedEventArgs e)
        {
            WireViewModel();
            ApplyColumnVisibility();
            AttachColumnHeaderContextMenu();
            ApplySectionLayout(animate: false);

            Opacity = 0;
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(450))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
            });
        }

        private void WireViewModel()
        {
            if (DataContext is PlaylistsPageViewModel vm)
            {
                _playlistsExpanded = vm.IsPlaylistsSectionExpanded;
                vm.PropertyChanged -= ViewModel_PropertyChanged;
                vm.PropertyChanged += ViewModel_PropertyChanged;
            }
        }

        private void ViewModel_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PlaylistsPageViewModel.IsControlsPanelExpanded) ||
                e.PropertyName == nameof(PlaylistsPageViewModel.IsPlaylistsSectionExpanded) ||
                e.PropertyName == nameof(PlaylistsPageViewModel.FillControlsPanel))
            {
                ApplySectionLayout(animate: true);
            }
        }

        private void PlaylistsPage_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Keep fill-mode controls stretched when the page resizes.
            if (DataContext is PlaylistsPageViewModel vm && vm.FillControlsPanel)
                ApplySectionLayout(animate: false);
        }

        private void PlaylistsExpandToggle_Click(object sender, RoutedEventArgs e)
        {
            if (!(DataContext is PlaylistsPageViewModel vm))
                return;

            vm.IsPlaylistsSectionExpanded = !vm.IsPlaylistsSectionExpanded;
        }

        private void ApplySectionLayout(bool animate)
        {
            if (!(DataContext is PlaylistsPageViewModel vm))
                return;

            _playlistsExpanded = vm.IsPlaylistsSectionExpanded;
            PlaylistsChevron.Text = _playlistsExpanded ? "▼" : "▲";
            PlaylistsContent.Visibility = _playlistsExpanded ? Visibility.Visible : Visibility.Collapsed;

            if (vm.FillControlsPanel)
            {
                // Playlists collapsed + Controls open → Controls fills remaining space.
                PlaylistsRow.Height = GridLength.Auto;
                ControlsRow.Height = new GridLength(1, GridUnitType.Star);
            }
            else if (!vm.IsControlsPanelExpanded)
            {
                // Controls peeking → Playlists take the rest.
                PlaylistsRow.Height = new GridLength(1, GridUnitType.Star);
                ControlsRow.Height = GridLength.Auto;
            }
            else
            {
                // Both open → Playlists flex, Controls at ExpandedHeight.
                PlaylistsRow.Height = new GridLength(1, GridUnitType.Star);
                ControlsRow.Height = GridLength.Auto;
            }
        }

        private void PlaylistsDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DataContext is PlaylistsPageViewModel vm)
                vm.SetSelectedPlaylistItems(PlaylistsDataGrid.SelectedItems);
        }

        private void PlaylistsDataGrid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Delete && e.Key != Key.Back)
                return;

            if (!(DataContext is PlaylistsPageViewModel vm))
                return;

            var selected = PlaylistsDataGrid.SelectedItems;
            if (selected == null || selected.Count == 0)
                return;

            if (vm.EnqueueDeleteKeyCommand?.CanExecute(selected) == true)
            {
                vm.EnqueueDeleteKeyCommand.Execute(selected);
                e.Handled = true;
            }
        }

        private void ApplyColumnVisibility()
        {
            _columnVisibility = PlaylistGridColumnSettingsStore.Load();

            foreach (var column in PlaylistsDataGrid.Columns)
            {
                var header = column.Header as string;
                if (string.IsNullOrWhiteSpace(header))
                    continue;

                if (string.Equals(header, "Name", StringComparison.Ordinal))
                {
                    column.Visibility = Visibility.Visible;
                    continue;
                }

                var visible = !_columnVisibility.TryGetValue(header, out var flag) || flag;
                column.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void AttachColumnHeaderContextMenu()
        {
            var menu = new ContextMenu();

            foreach (var column in PlaylistsDataGrid.Columns)
            {
                var header = column.Header as string;
                if (string.IsNullOrWhiteSpace(header))
                    continue;

                var item = new MenuItem
                {
                    Header = header,
                    IsCheckable = true,
                    IsChecked = column.Visibility == Visibility.Visible,
                    IsEnabled = !string.Equals(header, "Name", StringComparison.Ordinal),
                    Tag = column
                };

                item.Click += ColumnVisibilityMenuItem_Click;
                menu.Items.Add(item);
            }

            var headerStyle = new Style(typeof(DataGridColumnHeader));
            if (TryFindResource(typeof(DataGridColumnHeader)) is Style basedOn)
                headerStyle.BasedOn = basedOn;
            headerStyle.Setters.Add(new Setter(FrameworkElement.ContextMenuProperty, menu));
            PlaylistsDataGrid.ColumnHeaderStyle = headerStyle;

            menu.Opened += (_, __) =>
            {
                foreach (MenuItem item in menu.Items)
                {
                    if (item.Tag is DataGridColumn col)
                        item.IsChecked = col.Visibility == Visibility.Visible;
                }
            };
        }

        private void ColumnVisibilityMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is MenuItem item) || !(item.Tag is DataGridColumn column))
                return;

            var header = column.Header as string;
            if (string.IsNullOrWhiteSpace(header) || string.Equals(header, "Name", StringComparison.Ordinal))
                return;

            column.Visibility = item.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

            if (_columnVisibility == null)
                _columnVisibility = PlaylistGridColumnSettingsStore.Load();

            _columnVisibility[header] = item.IsChecked == true;
            PlaylistGridColumnSettingsStore.Save(_columnVisibility);
        }
    }
}
