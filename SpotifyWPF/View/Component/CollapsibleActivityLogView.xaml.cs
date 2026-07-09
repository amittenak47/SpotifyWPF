using System.Windows;
using System.Windows.Controls;
using SpotifyWPF.ViewModel.Component;

namespace SpotifyWPF.View.Component
{
    public partial class CollapsibleActivityLogView : UserControl
    {
        public static readonly DependencyProperty ActivityLogProperty =
            DependencyProperty.Register(nameof(ActivityLog), typeof(ActivityLogViewModel),
                typeof(CollapsibleActivityLogView), new PropertyMetadata(null));

        public static readonly DependencyProperty IsExpandedProperty =
            DependencyProperty.Register(nameof(IsExpanded), typeof(bool), typeof(CollapsibleActivityLogView),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        public CollapsibleActivityLogView()
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
    }
}
