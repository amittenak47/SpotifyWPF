using System;
using System.IO;
using System.Text.Json;
using SpotifyWPF.Model;

namespace SpotifyWPF.Service.Theme
{
    public class AppThemeStore : IAppThemeStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = true };

        private AppThemePalette _cached;

        private static string ThemePalettePath
        {
            get
            {
                var root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SpotifyWPF");
                return Path.Combine(root, "theme-palette.json");
            }
        }

        public AppThemePalette Get()
        {
            if (_cached != null)
                return _cached.Clone();

            try
            {
                var path = ThemePalettePath;
                if (File.Exists(path))
                {
                    _cached = JsonSerializer.Deserialize<AppThemePalette>(File.ReadAllText(path)) ??
                              AppThemePalette.CreateDefaults();
                }
                else
                {
                    _cached = AppThemePalette.CreateDefaults();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load theme palette: {ex.Message}");
                _cached = AppThemePalette.CreateDefaults();
            }

            return _cached.Clone();
        }

        public void Save(AppThemePalette palette)
        {
            _cached = palette?.Clone() ?? AppThemePalette.CreateDefaults();
            var path = ThemePalettePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? path);
            File.WriteAllText(path, JsonSerializer.Serialize(_cached, JsonOptions));
        }
    }
}
