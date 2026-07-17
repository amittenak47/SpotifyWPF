using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SpotifyWPF.Service
{
    /// <summary>
    /// Persists Manage playlists grid column visibility under
    /// %LocalAppData%\SpotifyWPF\Playlists\grid-columns.json.
    /// </summary>
    public static class PlaylistGridColumnSettingsStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = true };

        private static string GetPath()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SpotifyWPF",
                "Playlists");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "grid-columns.json");
        }

        public static Dictionary<string, bool> Load()
        {
            try
            {
                var path = GetPath();
                if (!File.Exists(path))
                    return CreateDefaults();

                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<Dictionary<string, bool>>(json);
                if (loaded == null || loaded.Count == 0)
                    return CreateDefaults();

                var defaults = CreateDefaults();
                foreach (var key in defaults.Keys)
                {
                    if (!loaded.ContainsKey(key))
                        loaded[key] = defaults[key];
                }

                // Name is always visible.
                loaded["Name"] = true;
                return loaded;
            }
            catch
            {
                return CreateDefaults();
            }
        }

        public static void Save(Dictionary<string, bool> visibility)
        {
            try
            {
                if (visibility == null) return;
                visibility["Name"] = true;
                File.WriteAllText(GetPath(), JsonSerializer.Serialize(visibility, JsonOptions));
            }
            catch
            {
                // Best-effort persistence; ignore IO failures.
            }
        }

        public static Dictionary<string, bool> CreateDefaults()
        {
            return new Dictionary<string, bool>
            {
                ["Name"] = true,
                ["Owner"] = true,
                ["# Tracks"] = true,
                ["Loaded"] = true,
                ["Queue"] = true,
                ["Delete Status"] = true,
                ["Spotify ID"] = true
            };
        }
    }
}
