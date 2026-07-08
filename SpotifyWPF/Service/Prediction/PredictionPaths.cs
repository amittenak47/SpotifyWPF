using System;
using System.IO;
using System.Text.Json;

namespace SpotifyWPF.Service.Prediction
{
    /// <summary>
    /// Central definition of every file the experimental Prediction features persist under
    /// %LocalAppData%\SpotifyWPF\Prediction\.
    /// </summary>
    public static class PredictionPaths
    {
        public static string RootDirectory
        {
            get
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(localAppData, "SpotifyWPF", "Prediction");
            }
        }

        public static string ListeningLogPath => Path.Combine(RootDirectory, "listening-log.jsonl");

        public static string LoopRegionsPath => Path.Combine(RootDirectory, "loop-regions.json");

        public static string AnalysisSourcePath => Path.Combine(RootDirectory, "analysis-source.json");

        public static string PredictorWeightsPath => Path.Combine(RootDirectory, "predictor-weights.json");

        public static string JukeboxSettingsPath => Path.Combine(RootDirectory, "jukebox-settings.json");

        public static string AnalysisCacheDirectory => Path.Combine(RootDirectory, "analysis-cache");

        public static string AudioCacheDirectory => Path.Combine(RootDirectory, "audio-cache");

        public static string WebView2UserDataDirectory => Path.Combine(RootDirectory, "webview2");

        public static string GetAnalysisCachePath(string trackId)
        {
            return Path.Combine(AnalysisCacheDirectory, SanitizeFileName(trackId) + ".json");
        }

        public static string GetAudioCachePath(string trackId)
        {
            return Path.Combine(AudioCacheDirectory, SanitizeFileName(trackId) + ".wav");
        }

        public static string GetAudioCacheMetadataPath(string trackId)
        {
            return Path.Combine(AudioCacheDirectory, SanitizeFileName(trackId) + ".capture.json");
        }

        /// <summary>
        /// Resolves a captured WAV for <paramref name="trackId"/>, tolerating Windows path casing and
        /// lookup via <c>.capture.json</c> sidecars when the canonical filename is missing.
        /// </summary>
        public static string ResolveAudioCachePath(string trackId)
        {
            if (string.IsNullOrWhiteSpace(trackId))
                return GetAudioCachePath(trackId);

            var exact = GetAudioCachePath(trackId);

            if (File.Exists(exact))
                return exact;

            if (!Directory.Exists(AudioCacheDirectory))
                return exact;

            var sanitized = SanitizeFileName(trackId);

            foreach (var wav in Directory.EnumerateFiles(AudioCacheDirectory, "*.wav"))
            {
                var name = Path.GetFileNameWithoutExtension(wav);

                if (string.Equals(name, sanitized, StringComparison.OrdinalIgnoreCase))
                    return wav;
            }

            foreach (var metaPath in Directory.EnumerateFiles(AudioCacheDirectory, "*.capture.json"))
            {
                try
                {
                    using (var document = JsonDocument.Parse(File.ReadAllText(metaPath)))
                    {
                        var root = document.RootElement;

                        if (!root.TryGetProperty("trackId", out var idElement))
                            continue;

                        var metaTrackId = idElement.GetString();

                        if (!string.Equals(metaTrackId, trackId, StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (!root.TryGetProperty("wavFile", out var wavElement))
                            continue;

                        var resolved = Path.Combine(AudioCacheDirectory, wavElement.GetString());

                        if (File.Exists(resolved))
                            return resolved;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Could not read capture metadata {metaPath}: {ex.Message}");
                }
            }

            return exact;
        }

        public static void EnsureDirectory(string filePath)
        {
            var directory = Path.GetDirectoryName(filePath);

            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
        }

        private static string SanitizeFileName(string value)
        {
            var chars = (value ?? string.Empty).ToCharArray();

            for (var i = 0; i < chars.Length; i++)
            {
                if (!char.IsLetterOrDigit(chars[i]))
                    chars[i] = '_';
            }

            return new string(chars);
        }
    }
}
