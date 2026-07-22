using System;
using System.Collections.Generic;

namespace SpotifyWPF.Model
{
    public class AppThemePalette
    {
        public const string DefaultBackground = "#000000";
        public const string DefaultSurface = "#0A0A0A";
        public const string DefaultSurfaceAlt = "#121212";
        public const string DefaultBorder = "#1DB954";
        public const string DefaultForeground = "#FFFFFF";
        public const string DefaultMutedForeground = "#A0A0A0";
        public const string DefaultDisabledForeground = "#4A4A4A";
        public const string DefaultHover = "#1A1A1A";
        public const string DefaultSelected = "#169C46";
        public const string DefaultAccent = "#1DB954";

        public string Background { get; set; } = DefaultBackground;

        public string Surface { get; set; } = DefaultSurface;

        public string SurfaceAlt { get; set; } = DefaultSurfaceAlt;

        public string Border { get; set; } = DefaultBorder;

        public string Foreground { get; set; } = DefaultForeground;

        public string MutedForeground { get; set; } = DefaultMutedForeground;

        public string DisabledForeground { get; set; } = DefaultDisabledForeground;

        public string Hover { get; set; } = DefaultHover;

        public string Selected { get; set; } = DefaultSelected;

        public string Accent { get; set; } = DefaultAccent;

        public static AppThemePalette CreateDefaults() => new AppThemePalette();

        public AppThemePalette Clone()
        {
            return new AppThemePalette
            {
                Background = Background,
                Surface = Surface,
                SurfaceAlt = SurfaceAlt,
                Border = Border,
                Foreground = Foreground,
                MutedForeground = MutedForeground,
                DisabledForeground = DisabledForeground,
                Hover = Hover,
                Selected = Selected,
                Accent = Accent
            };
        }

        public static IReadOnlyList<ThemeColorDefinition> Definitions { get; } = new[]
        {
            new ThemeColorDefinition("Background", DefaultBackground, p => p.Background, (p, v) => p.Background = v),
            new ThemeColorDefinition("Surface", DefaultSurface, p => p.Surface, (p, v) => p.Surface = v),
            new ThemeColorDefinition("Surface (elevated)", DefaultSurfaceAlt, p => p.SurfaceAlt, (p, v) => p.SurfaceAlt = v),
            new ThemeColorDefinition("Border / lines", DefaultBorder, p => p.Border, (p, v) => p.Border = v),
            new ThemeColorDefinition("Text", DefaultForeground, p => p.Foreground, (p, v) => p.Foreground = v),
            new ThemeColorDefinition("Muted text", DefaultMutedForeground, p => p.MutedForeground, (p, v) => p.MutedForeground = v),
            new ThemeColorDefinition("Disabled text", DefaultDisabledForeground, p => p.DisabledForeground, (p, v) => p.DisabledForeground = v),
            new ThemeColorDefinition("Hover", DefaultHover, p => p.Hover, (p, v) => p.Hover = v),
            new ThemeColorDefinition("Selection", DefaultSelected, p => p.Selected, (p, v) => p.Selected = v),
            new ThemeColorDefinition("Accent", DefaultAccent, p => p.Accent, (p, v) => p.Accent = v)
        };
    }

    public sealed class ThemeColorDefinition
    {
        public ThemeColorDefinition(
            string label,
            string defaultHex,
            Func<AppThemePalette, string> get,
            Action<AppThemePalette, string> set)
        {
            Label = label;
            DefaultHex = defaultHex;
            Get = get;
            Set = set;
        }

        public string Label { get; }

        public string DefaultHex { get; }

        public Func<AppThemePalette, string> Get { get; }

        public Action<AppThemePalette, string> Set { get; }
    }
}
