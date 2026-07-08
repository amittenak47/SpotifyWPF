using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SpotifyWPF.Model.Prediction;

namespace SpotifyWPF.Service.Prediction
{
    public interface ITrackAnalysisProvider
    {
        AnalysisSource Source { get; }

        bool IsCached(string trackId);

        /// <summary>
        /// Returns the (cached or freshly produced) analysis for a track. Progress strings are
        /// user-facing ("Capturing…", "Analyzing…"). May take a full track length on Path B.
        /// </summary>
        Task<TrackAnalysis> GetAnalysisAsync(string trackId, IProgress<string> progress,
            CancellationToken cancellationToken);
    }

    /// <summary>Shared JSON cache at Prediction\analysis-cache\{trackId}.json used by both paths.</summary>
    public static class AnalysisCache
    {
        private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            WriteIndented = false
        };

        public static bool Exists(string trackId)
        {
            return !string.IsNullOrEmpty(trackId) && File.Exists(PredictionPaths.GetAnalysisCachePath(trackId));
        }

        public static TrackAnalysis Load(string trackId)
        {
            var path = PredictionPaths.GetAnalysisCachePath(trackId);

            if (!File.Exists(path))
                return null;

            try
            {
                return JsonSerializer.Deserialize<TrackAnalysis>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load cached analysis for {trackId}: {ex.Message}");
                return null;
            }
        }

        public static void Save(TrackAnalysis analysis)
        {
            if (analysis == null || string.IsNullOrEmpty(analysis.TrackId))
                return;

            var path = PredictionPaths.GetAnalysisCachePath(analysis.TrackId);
            PredictionPaths.EnsureDirectory(path);

            File.WriteAllText(path, JsonSerializer.Serialize(analysis, SerializerOptions));
        }
    }

    /// <summary>
    /// Picks the provider matching the Phase 0 gate verdict. This is an explicit routing decision —
    /// not a fallback chain: the recorded AnalysisSource fully determines the pipeline.
    /// </summary>
    public interface IAnalysisProviderSelector
    {
        Task<ITrackAnalysisProvider> GetProviderAsync();
    }

    public class AnalysisProviderSelector : IAnalysisProviderSelector
    {
        private readonly IAnalysisGate _gate;

        private readonly SpotifyAnalysisProvider _spotifyProvider;

        private readonly LocalAnalysisProvider _localProvider;

        public AnalysisProviderSelector(IAnalysisGate gate, SpotifyAnalysisProvider spotifyProvider,
            LocalAnalysisProvider localProvider)
        {
            _gate = gate;
            _spotifyProvider = spotifyProvider;
            _localProvider = localProvider;
        }

        public async Task<ITrackAnalysisProvider> GetProviderAsync()
        {
            var source = await _gate.GetAnalysisSourceAsync();

            switch (source)
            {
                case AnalysisSource.Spotify:
                    return _spotifyProvider;
                case AnalysisSource.Local:
                    return _localProvider;
                default:
                    throw new InvalidOperationException(
                        "Analysis pipeline not decided yet — log in and let the probe run (see status bar).");
            }
        }
    }
}
