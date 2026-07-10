using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using SpotifyWPF.Model;
using SpotifyWPF.ViewModel.Component;
using SpotifyWPF.ViewModel.Page;

namespace SpotifyWPF.View.Component
{
    public partial class PlaylistsControlPanel
    {
        public static readonly DependencyProperty ActivityLogProperty =
            DependencyProperty.Register(
                nameof(ActivityLog),
                typeof(ActivityLogViewModel),
                typeof(PlaylistsControlPanel),
                new PropertyMetadata(null));

        public static readonly DependencyProperty IsExpandedProperty =
            DependencyProperty.Register(
                nameof(IsExpanded),
                typeof(bool),
                typeof(PlaylistsControlPanel),
                new FrameworkPropertyMetadata(
                    false,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                    OnIsExpandedChanged));

        public static readonly DependencyProperty ExpandedHeightProperty =
            DependencyProperty.Register(
                nameof(ExpandedHeight),
                typeof(double),
                typeof(PlaylistsControlPanel),
                new FrameworkPropertyMetadata(220.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        private const double PeekHeight = 10;
        private static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(220);

        public PlaylistsControlPanel()
        {
            InitializeComponent();
            Height = PeekHeight;
            Loaded += (_, __) =>
            {
                if (!IsExpanded)
                    Height = PeekHeight;
                else
                    Height = Math.Max(120, ExpandedHeight);
            };
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

        public double ExpandedHeight
        {
            get => (double)GetValue(ExpandedHeightProperty);
            set => SetValue(ExpandedHeightProperty, value);
        }

        private static void OnIsExpandedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is PlaylistsControlPanel panel)
                panel.AnimateToExpandedState((bool)e.NewValue);
        }

        private void PeekBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            IsExpanded = !IsExpanded;
            e.Handled = true;
        }

        private void AnimateToExpandedState(bool open)
        {
            var to = open ? Math.Max(120, ExpandedHeight) : PeekHeight;
            BeginAnimation(HeightProperty, null);
            var animation = new DoubleAnimation(Height, to, SlideDuration)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
            animation.Completed += (_, __) =>
            {
                BeginAnimation(HeightProperty, null);
                Height = to;
            };
            BeginAnimation(HeightProperty, animation, HandoffBehavior.SnapshotAndReplace);
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
