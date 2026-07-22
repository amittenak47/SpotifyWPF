using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using SpotifyWPF.ViewModel.Page;

namespace SpotifyWPF.View.Component
{
    public partial class LyricsPanel : UserControl
    {
        private PredictionPageViewModel _vm;

        public LyricsPanel()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
            Loaded += (_, __) => ScrollActiveIntoView();
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_vm != null)
                _vm.PropertyChanged -= OnVmPropertyChanged;

            _vm = e.NewValue as PredictionPageViewModel;
            if (_vm != null)
                _vm.PropertyChanged += OnVmPropertyChanged;
        }

        private void OnVmPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PredictionPageViewModel.ActiveLyricText) ||
                e.PropertyName == nameof(PredictionPageViewModel.LyricDisplayLines))
            {
                Dispatcher.BeginInvoke((Action)ScrollActiveIntoView,
                    System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        public void ScrollActiveIntoView()
        {
            if (LyricList?.Items == null || LyricList.Items.Count == 0)
                return;

            object active = null;
            foreach (var item in LyricList.Items)
            {
                if (item is Model.Lyrics.LyricDisplayLine line && line.IsActive)
                {
                    active = item;
                    break;
                }
            }

            if (active == null)
                return;

            LyricList.UpdateLayout();
            LyricList.ScrollIntoView(active);
        }
    }
}
