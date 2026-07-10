using System;
using System.IO;
using System.Text.Json;
using SpotifyWPF.Model;

namespace SpotifyWPF.Service.Visual
{
    public interface IVisualEffectsStore
    {
        VisualEffectsSettings Get();

        void Save(VisualEffectsSettings settings);

        event EventHandler SettingsChanged;
    }

    /// <summary>Persists visual-effect preferences next to the theme palette (AppThemeStore pattern).</summary>
    public class VisualEffectsStore : IVisualEffectsStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = true };

        private VisualEffectsSettings _cached;

        public event EventHandler SettingsChanged;

        private static string SettingsPath
        {
            get
            {
                var root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SpotifyWPF");
                return Path.Combine(root, "visual-effects.json");
            }
        }

        public VisualEffectsSettings Get()
        {
            if (_cached != null)
                return _cached;

            try
            {
                var path = SettingsPath;

                if (File.Exists(path))
                {
                    _cached = JsonSerializer.Deserialize<VisualEffectsSettings>(File.ReadAllText(path)) ??
                              VisualEffectsSettings.CreateDefaults();
                }
                else
                {
                    _cached = VisualEffectsSettings.CreateDefaults();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load visual effects settings: {ex.Message}");
                _cached = VisualEffectsSettings.CreateDefaults();
            }

            return _cached;
        }

        public void Save(VisualEffectsSettings settings)
        {
            _cached = settings ?? VisualEffectsSettings.CreateDefaults();
            var path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? path);
            File.WriteAllText(path, JsonSerializer.Serialize(_cached, JsonOptions));

            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
