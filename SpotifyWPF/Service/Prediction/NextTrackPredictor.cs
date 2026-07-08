using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using SpotifyAPI.Web;
using SpotifyWPF.Model.Prediction;
using SpotifyWPF.Service.Playback;

namespace SpotifyWPF.Service.Prediction
{
    public interface INextTrackPredictor
    {
        /// <summary>Current weights (loaded from predictor-weights.json on first use).</summary>
        PredictorWeights GetWeights();

        void SaveWeights(PredictorWeights weights);

        /// <summary>Pins/unpins a track and persists the change. Returns the new pinned state.</summary>
        bool TogglePin(string trackId);

        /// <summary>
        /// Ranks candidate next tracks after lastTrackId using personal listening history —
        /// the replacement for Spotify's autoplay suggestions.
        /// </summary>
        Task<List<ScoredTrack>> PredictAsync(string lastTrackId, int count);
    }

    /// <summary>
    /// Interpretable next-track scoring over the append-only listening log. Builds transition
    /// counts (A→B), repeat/skip affinity per track, then scores a candidate pool drawn from the
    /// log, recently played, and the saved library. Tempo similarity uses cached analysis only —
    /// the deprecated audio-features endpoint is never called.
    /// </summary>
    public class NextTrackPredictor : INextTrackPredictor
    {
        private const int MaxCandidates = 250;

        private readonly IListeningLogService _listeningLog;

        private readonly ISpotifyPlaybackService _playbackService;

        private readonly ISpotify _spotify;

        private readonly object _weightsLock = new object();

        private PredictorWeights _weights;

        public NextTrackPredictor(IListeningLogService listeningLog, ISpotifyPlaybackService playbackService,
            ISpotify spotify)
        {
            _listeningLog = listeningLog;
            _playbackService = playbackService;
            _spotify = spotify;
        }

        public PredictorWeights GetWeights()
        {
            lock (_weightsLock)
            {
                if (_weights == null)
                    _weights = LoadWeights();

                return _weights;
            }
        }

        public void SaveWeights(PredictorWeights weights)
        {
            if (weights == null)
                return;

            lock (_weightsLock)
            {
                _weights = weights;
                PersistWeights(weights);
            }
        }

        public bool TogglePin(string trackId)
        {
            if (string.IsNullOrEmpty(trackId))
                return false;

            lock (_weightsLock)
            {
                var weights = GetWeights();
                bool pinned;

                if (weights.PinnedTrackIds.Contains(trackId))
                {
                    weights.PinnedTrackIds.Remove(trackId);
                    pinned = false;
                }
                else
                {
                    weights.PinnedTrackIds.Add(trackId);
                    pinned = true;
                }

                PersistWeights(weights);
                return pinned;
            }
        }

        public async Task<List<ScoredTrack>> PredictAsync(string lastTrackId, int count)
        {
            var events = _listeningLog.ReadAll();
            var weights = GetWeights();

            // --- History features -------------------------------------------------------------

            // Transitions: consecutive log entries where the previous play actually finished or was
            // deliberately skipped into the next one within a short window.
            var transitionsFromLast = new Dictionary<string, int>();
            var totalTransitionsFromLast = 0;

            for (var i = 1; i < events.Count; i++)
            {
                var previous = events[i - 1];
                var current = events[i];

                if (previous.TrackId != lastTrackId || current.TrackId == previous.TrackId)
                    continue;

                if ((current.StartedAt - previous.EndedAt) > TimeSpan.FromMinutes(10))
                    continue;

                transitionsFromLast.TryGetValue(current.TrackId, out var existing);
                transitionsFromLast[current.TrackId] = existing + 1;
                totalTransitionsFromLast++;
            }

            var playCounts = new Dictionary<string, int>();
            var naturalEndCounts = new Dictionary<string, int>();
            var skipCounts = new Dictionary<string, int>();
            var lastPlayedAt = new Dictionary<string, DateTime>();

            foreach (var playEvent in events)
            {
                Increment(playCounts, playEvent.TrackId);

                if (playEvent.EndedNaturally)
                    Increment(naturalEndCounts, playEvent.TrackId);

                if (playEvent.UserSkipped)
                    Increment(skipCounts, playEvent.TrackId);

                if (!lastPlayedAt.TryGetValue(playEvent.TrackId, out var seen) || playEvent.EndedAt > seen)
                    lastPlayedAt[playEvent.TrackId] = playEvent.EndedAt;
            }

            var maxNaturalEnds = naturalEndCounts.Count > 0 ? naturalEndCounts.Values.Max() : 1;

            // --- Candidate pool ---------------------------------------------------------------

            var candidates = new Dictionary<string, ScoredTrack>();

            foreach (var playEvent in events)
            {
                AddCandidate(candidates, playEvent.TrackId, playEvent.TrackName, playEvent.ArtistName);
            }

            try
            {
                foreach (var item in await _playbackService.GetRecentlyPlayedAsync(50))
                {
                    var track = item?.Track;

                    if (track != null)
                        AddCandidate(candidates, track.Id, track.Name, JoinArtists(track.Artists));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Recently-played fetch failed (predicting from log only): {ex.Message}");
            }

            try
            {
                var api = _spotify.Api;

                if (api != null && candidates.Count < MaxCandidates)
                {
                    var saved = await api.Library.GetTracks(new LibraryTracksRequest { Limit = 50 });

                    foreach (var item in saved?.Items ?? new List<SavedTrack>())
                    {
                        var track = item?.Track;

                        if (track != null)
                            AddCandidate(candidates, track.Id, track.Name, JoinArtists(track.Artists));
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Saved-library fetch failed (predicting without it): {ex.Message}");
            }

            candidates.Remove(lastTrackId ?? string.Empty);

            // --- Scoring ----------------------------------------------------------------------

            string lastArtist = null;
            double lastTempo = 0;

            if (lastTrackId != null)
            {
                var lastEvent = events.LastOrDefault(e => e.TrackId == lastTrackId);
                lastArtist = lastEvent?.ArtistName;
                lastTempo = AnalysisCache.Load(lastTrackId)?.Tempo ?? 0;
            }

            var now = DateTime.UtcNow;
            var scored = new List<ScoredTrack>();

            foreach (var candidate in candidates.Values)
            {
                double transitionProb = 0;

                if (totalTransitionsFromLast > 0 &&
                    transitionsFromLast.TryGetValue(candidate.TrackId, out var transitionCount))
                {
                    transitionProb = (double)transitionCount / totalTransitionsFromLast;
                }

                playCounts.TryGetValue(candidate.TrackId, out var plays);
                naturalEndCounts.TryGetValue(candidate.TrackId, out var naturalEnds);
                skipCounts.TryGetValue(candidate.TrackId, out var skips);

                var repeatAffinity = Clamp01((naturalEnds - 0.5 * skips) / maxNaturalEnds);

                var sameArtist = !string.IsNullOrEmpty(lastArtist) &&
                                 string.Equals(candidate.ArtistName, lastArtist, StringComparison.OrdinalIgnoreCase)
                    ? 1.0
                    : 0.0;

                double tempoSimilarity = 0;

                if (lastTempo > 0)
                {
                    var candidateTempo = AnalysisCache.Load(candidate.TrackId)?.Tempo ?? 0;

                    if (candidateTempo > 0)
                        tempoSimilarity = 1.0 - Math.Min(1.0, Math.Abs(lastTempo - candidateTempo) / 60.0);
                }

                double recencyPenalty = 0;

                if (lastPlayedAt.TryGetValue(candidate.TrackId, out var lastSeen))
                {
                    var minutesAgo = (now - lastSeen).TotalMinutes;
                    recencyPenalty = Clamp01(1.0 - minutesAgo / 120.0);
                }

                var pinned = GetWeights().PinnedTrackIds.Contains(candidate.TrackId) ? 1.0 : 0.0;

                candidate.IsPinned = pinned > 0;
                candidate.Score =
                    weights.Transition * transitionProb +
                    weights.RepeatAffinity * repeatAffinity +
                    weights.SameArtist * sameArtist +
                    weights.TempoSimilarity * tempoSimilarity -
                    weights.RecencyPenalty * recencyPenalty +
                    weights.Pinned * pinned;

                candidate.Reason = BuildReason(transitionProb, repeatAffinity, sameArtist, tempoSimilarity,
                    recencyPenalty, pinned);

                scored.Add(candidate);
            }

            return scored
                .OrderByDescending(t => t.Score)
                .ThenByDescending(t => playCounts.TryGetValue(t.TrackId, out var plays) ? plays : 0)
                .Take(count)
                .ToList();
        }

        private static string BuildReason(double transition, double repeat, double sameArtist,
            double tempo, double recency, double pinned)
        {
            var parts = new List<string>();

            if (pinned > 0)
                parts.Add("pinned");

            if (transition > 0)
                parts.Add($"follows it {transition:P0} of the time");

            if (repeat > 0.05)
                parts.Add($"repeat favorite {repeat:0.00}");

            if (sameArtist > 0)
                parts.Add("same artist");

            if (tempo > 0.05)
                parts.Add($"tempo match {tempo:0.00}");

            if (recency > 0.05)
                parts.Add($"played recently −{recency:0.00}");

            return parts.Count > 0 ? string.Join(" · ", parts) : "library candidate";
        }

        private static void AddCandidate(IDictionary<string, ScoredTrack> candidates, string trackId,
            string trackName, string artistName)
        {
            if (string.IsNullOrEmpty(trackId) || candidates.Count >= MaxCandidates)
                return;

            if (candidates.TryGetValue(trackId, out var existing))
            {
                // Prefer entries that carry metadata.
                if (string.IsNullOrEmpty(existing.TrackName) && !string.IsNullOrEmpty(trackName))
                {
                    existing.TrackName = trackName;
                    existing.ArtistName = artistName;
                }

                return;
            }

            candidates[trackId] = new ScoredTrack
            {
                TrackId = trackId,
                TrackName = string.IsNullOrEmpty(trackName) ? trackId : trackName,
                ArtistName = artistName
            };
        }

        private static string JoinArtists(IEnumerable<SimpleArtist> artists)
        {
            return artists == null ? null : string.Join(", ", artists.Select(a => a.Name));
        }

        private static void Increment(IDictionary<string, int> counts, string key)
        {
            if (string.IsNullOrEmpty(key))
                return;

            counts.TryGetValue(key, out var existing);
            counts[key] = existing + 1;
        }

        private static double Clamp01(double value)
        {
            if (value < 0)
                return 0;

            return value > 1 ? 1 : value;
        }

        private static PredictorWeights LoadWeights()
        {
            var path = PredictionPaths.PredictorWeightsPath;

            if (!File.Exists(path))
                return new PredictorWeights();

            try
            {
                return JsonSerializer.Deserialize<PredictorWeights>(File.ReadAllText(path))
                       ?? new PredictorWeights();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load predictor-weights.json: {ex.Message}");
                return new PredictorWeights();
            }
        }

        private static void PersistWeights(PredictorWeights weights)
        {
            try
            {
                var path = PredictionPaths.PredictorWeightsPath;
                PredictionPaths.EnsureDirectory(path);

                File.WriteAllText(path,
                    JsonSerializer.Serialize(weights, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to save predictor-weights.json: {ex.Message}");
            }
        }
    }
}
