using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using SpotifyWPF.Model;
using SpotifyWPF.Service.Theme;
using SpotifyWPF.View.Component;

namespace SpotifyWPF.View
{
    public partial class PreferencesWindow
    {
        private readonly IAppThemeStore _themeStore;
        private readonly AppThemePalette _workingPalette;
        private readonly List<ColorFieldBinding> _bindings = new List<ColorFieldBinding>();

        public PreferencesWindow(IAppThemeStore themeStore)
        {
            _themeStore = themeStore;
            _workingPalette = _themeStore.Get();
            InitializeComponent();
            BuildColorFields();
            LoadPaletteIntoFields();
        }

        private void BuildColorFields()
        {
            var row = 0;
            foreach (var definition in AppThemePalette.Definitions)
            {
                ColorFieldsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var label = new TextBlock
                {
                    Text = definition.Label,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 8)
                };
                Grid.SetRow(label, row);
                Grid.SetColumn(label, 0);
                ColorFieldsGrid.Children.Add(label);

                var picker = new ThemeColorPicker
                {
                    Margin = new Thickness(0, 0, 0, 8),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetRow(picker, row);
                Grid.SetColumn(picker, 1);
                ColorFieldsGrid.Children.Add(picker);

                _bindings.Add(new ColorFieldBinding(definition, picker));
                row++;
            }
        }

        private void LoadPaletteIntoFields()
        {
            foreach (var binding in _bindings)
                binding.Picker.Hex = binding.Definition.Get(_workingPalette);

            ValidationMessage.Visibility = Visibility.Collapsed;
        }

        private bool TryReadPaletteFromFields(out AppThemePalette palette, out string errorMessage)
        {
            palette = _workingPalette.Clone();
            errorMessage = null;

            foreach (var binding in _bindings)
            {
                if (!AppThemeManager.TryNormalizeHex(binding.Picker.Hex, out var normalized))
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
            DialogResult = true;
            Close();
        }

        private sealed class ColorFieldBinding
        {
            public ColorFieldBinding(ThemeColorDefinition definition, ThemeColorPicker picker)
            {
                Definition = definition;
                Picker = picker;
            }

            public ThemeColorDefinition Definition { get; }

            public ThemeColorPicker Picker { get; }
        }
    }
}
