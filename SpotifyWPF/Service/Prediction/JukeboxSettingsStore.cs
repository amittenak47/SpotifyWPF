using System;
using System.IO;
using System.Text.Json;
using SpotifyWPF.Model.Prediction;

namespace SpotifyWPF.Service.Prediction
{
    public interface IJukeboxSettingsStore
    {
        JukeboxSettings Get();

        void Save(JukeboxSettings settings);

        event EventHandler SettingsChanged;
    }

    public class JukeboxSettingsStore : IJukeboxSettingsStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = true };

        private JukeboxSettings _cached;

        public event EventHandler SettingsChanged;

        public JukeboxSettings Get()
        {
            if (_cached != null)
                return _cached;

            try
            {
                var path = PredictionPaths.JukeboxSettingsPath;

                if (File.Exists(path))
                {
                    _cached = JsonSerializer.Deserialize<JukeboxSettings>(File.ReadAllText(path)) ??
                              JukeboxSettings.CreateDefaults();
                }
                else
                {
                    _cached = JukeboxSettings.CreateDefaults();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load jukebox settings: {ex.Message}");
                _cached = JukeboxSettings.CreateDefaults();
            }

            return _cached;
        }

        public void Save(JukeboxSettings settings)
        {
            _cached = settings ?? JukeboxSettings.CreateDefaults();
            PredictionPaths.EnsureDirectory(PredictionPaths.JukeboxSettingsPath);

            File.WriteAllText(PredictionPaths.JukeboxSettingsPath,
                JsonSerializer.Serialize(_cached, JsonOptions));

            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
