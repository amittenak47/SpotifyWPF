using System;
using System.IO;

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
