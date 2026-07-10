using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SpotifyWPF.Model;
using SpotifyWPF.Service.Theme;
using SpotifyWPF.Service.Visual;

namespace SpotifyWPF.View
{
    public partial class PreferencesWindow
    {
        private readonly IAppThemeStore _themeStore;
        private readonly IVisualEffectsStore _visualEffectsStore;
        private readonly AppThemePalette _workingPalette;
        private readonly List<ColorFieldBinding> _bindings = new List<ColorFieldBinding>();

        public PreferencesWindow(IAppThemeStore themeStore, IVisualEffectsStore visualEffectsStore)
        {
            _themeStore = themeStore;
            _visualEffectsStore = visualEffectsStore;
            _workingPalette = _themeStore.Get();
            InitializeComponent();
            BuildColorFields();
            LoadPaletteIntoFields();
            FractalBackgroundCheckBox.IsChecked = _visualEffectsStore.Get().FractalBackgroundEnabled;
        }

        private void BuildColorFields()
        {
            var row = 0;
            foreach (var definition in AppThemePalette.Definitions)
            {
                var label = new TextBlock
                {
                    Text = definition.Label,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                };
                Grid.SetRow(label, row);
                Grid.SetColumn(label, 0);
                ColorFieldsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                ColorFieldsGrid.Children.Add(label);

                var hexBox = new TextBox
                {
                    MaxLength = 7,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 6),
                    FontFamily = new FontFamily("Consolas")
                };
                Grid.SetRow(hexBox, row);
                Grid.SetColumn(hexBox, 1);
                ColorFieldsGrid.Children.Add(hexBox);

                var swatch = new Border
                {
                    Width = 28,
                    Height = 22,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 6),
                    BorderBrush = new SolidColorBrush(Colors.Gray),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(2)
                };
                Grid.SetRow(swatch, row);
                Grid.SetColumn(swatch, 2);
                ColorFieldsGrid.Children.Add(swatch);

                var binding = new ColorFieldBinding(definition, hexBox, swatch);
                hexBox.TextChanged += (_, __) => UpdateSwatch(binding);
                _bindings.Add(binding);
                row++;
            }
        }

        private void LoadPaletteIntoFields()
        {
            foreach (var binding in _bindings)
            {
                binding.HexBox.Text = binding.Definition.Get(_workingPalette);
                UpdateSwatch(binding);
            }

            ValidationMessage.Visibility = Visibility.Collapsed;
        }

        private static void UpdateSwatch(ColorFieldBinding binding)
        {
            var hex = binding.HexBox.Text;
            if (AppThemeManager.TryNormalizeHex(hex, out var normalized))
            {
                binding.Swatch.Background = new SolidColorBrush(
                    AppThemeManager.ParseColorOrDefault(normalized, binding.Definition.DefaultHex));
                return;
            }

            binding.Swatch.Background = new SolidColorBrush(
                AppThemeManager.ParseColorOrDefault(binding.Definition.DefaultHex, binding.Definition.DefaultHex));
        }

        private bool TryReadPaletteFromFields(out AppThemePalette palette, out string errorMessage)
        {
            palette = _workingPalette.Clone();
            errorMessage = null;

            foreach (var binding in _bindings)
            {
                if (!AppThemeManager.TryNormalizeHex(binding.HexBox.Text, out var normalized))
                {
                    errorMessage = $"\"{binding.Definition.Label}\" must be a six-digit hex color (for example, #750013).";
                    return false;
                }

                binding.Definition.Set(palette, normalized);
            }

            return true;
        }

        private void ResetToDefaults_Click(object sender, RoutedEventArgs e)
        {
            var defaults = AppThemePalette.CreateDefaults();
            foreach (var binding in _bindings)
                binding.Definition.Set(_workingPalette, binding.Definition.Get(defaults));

            LoadPaletteIntoFields();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (!TryReadPaletteFromFields(out var palette, out var errorMessage))
            {
                ValidationMessage.Text = errorMessage;
                ValidationMessage.Visibility = Visibility.Visible;
                return;
            }

            _themeStore.Save(palette);
            AppThemeManager.Apply(palette);

            var effects = _visualEffectsStore.Get();
            if (effects.FractalBackgroundEnabled != (FractalBackgroundCheckBox.IsChecked == true))
            {
                effects.FractalBackgroundEnabled = FractalBackgroundCheckBox.IsChecked == true;
                _visualEffectsStore.Save(effects);
            }

            DialogResult = true;
            Close();
        }

        private sealed class ColorFieldBinding
        {
            public ColorFieldBinding(ThemeColorDefinition definition, TextBox hexBox, Border swatch)
            {
                Definition = definition;
                HexBox = hexBox;
                Swatch = swatch;
            }

            public ThemeColorDefinition Definition { get; }

            public TextBox HexBox { get; }

            public Border Swatch { get; }
        }
    }
}
