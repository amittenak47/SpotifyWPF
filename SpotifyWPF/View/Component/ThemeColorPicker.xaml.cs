using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SpotifyWPF.Service.Theme;

namespace SpotifyWPF.View.Component
{
    public partial class ThemeColorPicker
    {
        public static readonly DependencyProperty HexProperty =
            DependencyProperty.Register(
                nameof(Hex),
                typeof(string),
                typeof(ThemeColorPicker),
                new FrameworkPropertyMetadata(
                    "#000000",
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                    OnHexChanged));

        public static readonly DependencyProperty SwatchBrushProperty =
            DependencyProperty.Register(
                nameof(SwatchBrush),
                typeof(Brush),
                typeof(ThemeColorPicker),
                new PropertyMetadata(new SolidColorBrush(Colors.Black)));

        private bool _suppressChannelUpdate;
        private bool _suppressHexUpdate;
        private bool _suppressHexDp;

        public ThemeColorPicker()
        {
            InitializeComponent();
            Loaded += (_, __) => SyncFromHex(Hex);
        }

        public string Hex
        {
            get => (string)GetValue(HexProperty);
            set => SetValue(HexProperty, value);
        }

        public Brush SwatchBrush
        {
            get => (Brush)GetValue(SwatchBrushProperty);
            set => SetValue(SwatchBrushProperty, value);
        }

        private static void OnHexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ThemeColorPicker picker && !picker._suppressHexDp)
                picker.SyncFromHex(e.NewValue as string);
        }

        private void SwatchButton_Click(object sender, RoutedEventArgs e)
        {
            SyncFromHex(Hex);
            PickerPopup.IsOpen = true;
        }

        private void ChannelSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressChannelUpdate || !IsLoaded)
                return;

            var color = Color.FromRgb(
                (byte)Math.Round(RedSlider.Value),
                (byte)Math.Round(GreenSlider.Value),
                (byte)Math.Round(BlueSlider.Value));

            ApplyColor(color, updateSliders: false);
        }

        private void PopupHexBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressHexUpdate)
                return;

            if (!AppThemeManager.TryNormalizeHex(PopupHexBox.Text, out var normalized))
                return;

            ApplyNormalizedHex(normalized, updateSliders: true);
        }

        private void SyncFromHex(string hex)
        {
            var color = AppThemeManager.ParseColorOrDefault(hex, "#000000");
            ApplyColor(color, updateSliders: true);
        }

        private void ApplyColor(Color color, bool updateSliders)
        {
            var hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            ApplyNormalizedHex(hex, updateSliders);
        }

        private void ApplyNormalizedHex(string hex, bool updateSliders)
        {
            var color = AppThemeManager.ParseColorOrDefault(hex, "#000000");
            SwatchBrush = new SolidColorBrush(color);

            _suppressHexUpdate = true;
            try
            {
                if (PopupHexBox != null && !string.Equals(PopupHexBox.Text, hex, StringComparison.OrdinalIgnoreCase))
                    PopupHexBox.Text = hex;
            }
            finally
            {
                _suppressHexUpdate = false;
            }

            if (updateSliders && RedSlider != null)
            {
                _suppressChannelUpdate = true;
                try
                {
                    RedSlider.Value = color.R;
                    GreenSlider.Value = color.G;
                    BlueSlider.Value = color.B;
                }
                finally
                {
                    _suppressChannelUpdate = false;
                }
            }

            if (!AppThemeManager.TryNormalizeHex(Hex, out var current) ||
                !string.Equals(current, hex, StringComparison.OrdinalIgnoreCase))
            {
                _suppressHexDp = true;
                try
                {
                    Hex = hex;
                }
                finally
                {
                    _suppressHexDp = false;
                }
            }
        }
    }
}
