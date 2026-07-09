using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using SpotifyWPF.Model;

namespace SpotifyWPF.Service.Theme
{
    public static class AppThemeManager
    {
        private static readonly Regex HexPattern = new Regex("^#[0-9A-Fa-f]{6}$", RegexOptions.Compiled);

        public static bool TryNormalizeHex(string input, out string normalized)
        {
            normalized = null;
            if (string.IsNullOrWhiteSpace(input))
                return false;

            var value = input.Trim();
            if (!value.StartsWith("#", StringComparison.Ordinal))
                value = "#" + value;

            if (!HexPattern.IsMatch(value))
                return false;

            normalized = value.ToUpperInvariant();
            return true;
        }

        public static void Apply(AppThemePalette palette)
        {
            if (palette == null || Application.Current == null)
                return;

            var resources = Application.Current.Resources;
            SetBrush(resources, "AppBackgroundBrush", palette.Background);
            SetBrush(resources, "AppSurfaceBrush", palette.Surface);
            SetBrush(resources, "AppSurfaceAltBrush", palette.SurfaceAlt);
            SetBrush(resources, "AppBorderBrush", palette.Border);
            SetBrush(resources, "AppForegroundBrush", palette.Foreground);
            SetBrush(resources, "AppMutedForegroundBrush", palette.MutedForeground);
            SetBrush(resources, "AppDisabledForegroundBrush", palette.DisabledForeground);
            SetBrush(resources, "AppHoverBrush", palette.Hover);
            SetBrush(resources, "AppSelectedBrush", palette.Selected);
            SetBrush(resources, "AppAccentBrush", palette.Accent);
            SetBrush(resources, "SpotifyGreenBrush", palette.Border);
            SetBrush(resources, "SpotifyGreenLineBrush", palette.Border);

            SetBrush(resources, SystemColors.WindowBrushKey, palette.Surface);
            SetBrush(resources, SystemColors.WindowTextBrushKey, palette.Foreground);
            SetBrush(resources, SystemColors.ControlBrushKey, palette.SurfaceAlt);
            SetBrush(resources, SystemColors.ControlTextBrushKey, palette.Foreground);
            SetBrush(resources, SystemColors.GrayTextBrushKey, palette.DisabledForeground);
            SetBrush(resources, SystemColors.HighlightBrushKey, palette.Selected);
            SetBrush(resources, SystemColors.HighlightTextBrushKey, palette.Foreground);
            SetBrush(resources, SystemColors.InactiveSelectionHighlightBrushKey, palette.SurfaceAlt);
        }

        /// <summary>
        /// Replaces theme brushes in the resource dictionary so DynamicResource bindings refresh live.
        /// </summary>
        private static void SetBrush(ResourceDictionary root, object key, string hex)
        {
            if (!TryNormalizeHex(hex, out var normalized))
                return;

            var color = (Color)ColorConverter.ConvertFromString(normalized);
            var owner = FindResourceDictionary(root, key) ?? root;
            owner[key] = new SolidColorBrush(color);
        }

        public static void EnsureMutableThemeBrushes()
        {
            if (Application.Current == null)
                return;

            Apply(AppThemePalette.CreateDefaults());
        }

        private static ResourceDictionary FindResourceDictionary(ResourceDictionary dictionary, object key)
        {
            if (dictionary == null)
                return null;

            if (dictionary.Contains(key))
                return dictionary;

            foreach (var merged in dictionary.MergedDictionaries)
            {
                var found = FindResourceDictionary(merged, key);
                if (found != null)
                    return found;
            }

            return null;
        }

        public static Color ParseColorOrDefault(string hex, string fallbackHex)
        {
            if (!TryNormalizeHex(hex, out var normalized))
                TryNormalizeHex(fallbackHex, out normalized);

            return (Color)ColorConverter.ConvertFromString(normalized);
        }
    }
}
