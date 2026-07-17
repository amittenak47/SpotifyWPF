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
    public partial class PlaylistsPage
    {
        private Dictionary<string, bool> _columnVisibility;

        public PlaylistsPage()
        {
            InitializeComponent();
            Loaded += PlaylistsPage_Loaded;
        }

        private void PlaylistsPage_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyColumnVisibility();
            AttachColumnHeaderContextMenu();

            Opacity = 0;
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(450))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
            });
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
