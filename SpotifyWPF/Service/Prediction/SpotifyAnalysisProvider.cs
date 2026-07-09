using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SpotifyAPI.Web;
using SpotifyWPF.Model.Prediction;

namespace SpotifyWPF.Service.Prediction
{
    /// <summary>
    /// Path A: fetches Spotify's precomputed audio-analysis JSON and normalizes it into the shared
    /// TrackAnalysis schema, caching the result on disk. Only used when the Phase 0 probe recorded
    /// a working endpoint for this Client ID.
    /// </summary>
    public class SpotifyAnalysisProvider : ITrackAnalysisProvider
    {
        private readonly ISpotify _spotify;

        public SpotifyAnalysisProvider(ISpotify spotify)
        {
            _spotify = spotify;
        }

        public AnalysisSource Source => AnalysisSource.Spotify;

        public bool IsCached(string trackId)
        {
            return AnalysisCache.Exists(trackId);
        }

        public bool RequiresPlaybackCapture(string trackId) => false;

        public async Task<TrackAnalysis> GetAnalysisAsync(string trackId, IProgress<string> progress,
            CancellationToken cancellationToken)
        {
            var cached = AnalysisCache.Load(trackId);

            if (cached != null)
                return cached;

            var api = _spotify.Api;

            if (api == null)
                throw new InvalidOperationException("Not logged in to Spotify.");

            progress?.Report("Fetching Spotify audio analysis…");

            cancellationToken.ThrowIfCancellationRequested();

            var raw = await api.Tracks.GetAudioAnalysis(trackId);

            var analysis = Map(trackId, raw);

            AnalysisCache.Save(analysis);

            progress?.Report("Analysis cached.");

            return analysis;
        }

        private static TrackAnalysis Map(string trackId, TrackAudioAnalysis raw)
        {
            var analysis = new TrackAnalysis
            {
                TrackId = trackId,
                SourceType = "spotify",
                DurationSec = raw.Track?.Duration ?? 0,
                Tempo = raw.Track?.Tempo ?? 0,
                Key = raw.Track?.Key ?? -1,
                Mode = raw.Track?.Mode ?? -1,
                Loudness = raw.Track?.Loudness ?? 0
            };

            if (raw.Bars != null)
                analysis.Bars = raw.Bars.Select(MapInterval).ToList();

            if (raw.Beats != null)
                analysis.Beats = raw.Beats.Select(MapInterval).ToList();

            if (raw.Tatums != null)
                analysis.Tatums = raw.Tatums.Select(MapInterval).ToList();

            if (raw.Sections != null)
            {
                analysis.Sections = raw.Sections.Select(s => new AnalysisSection
                {
                    Start = s.Start,
                    Duration = s.Duration,
                    Confidence = s.Confidence,
                    Tempo = s.Tempo,
                    Key = s.Key,
                    Mode = s.Mode,
                    Loudness = s.Loudness
                }).ToList();
            }

            if (raw.Segments != null)
            {
                analysis.Segments = raw.Segments.Select(s => new AnalysisSegment
                {
                    Start = s.Start,
                    Duration = s.Duration,
                    Confidence = s.Confidence,
                    LoudnessStart = s.LoudnessStart,
                    LoudnessMax = s.LoudnessMax,
                    LoudnessMaxTime = s.LoudnessMaxTime,
                    Pitches = s.Pitches?.Select(p => (double)p).ToList() ?? new System.Collections.Generic.List<double>(),
                    Timbre = s.Timbre?.Select(t => (double)t).ToList() ?? new System.Collections.Generic.List<double>()
                }).ToList();
            }

            return analysis;
        }

        private static AnalysisInterval MapInterval(TimeInterval interval)
        {
            return new AnalysisInterval
            {
                Start = interval.Start,
                Duration = interval.Duration,
                Confidence = interval.Confidence
            };
        }
    }
}
