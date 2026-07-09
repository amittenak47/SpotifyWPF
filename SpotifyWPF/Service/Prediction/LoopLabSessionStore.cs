using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SpotifyWPF.Model.Prediction;

namespace SpotifyWPF.Service.Prediction
{
    public interface ILoopLabSessionStore
    {
        IReadOnlyList<LoopLabSessionTrack> Tracks { get; }

        void Load();

        void Save();

        void AddOrUpdate(LoopLabSessionTrack track);

        void Remove(string trackId);

        IReadOnlyCollection<string> GetTrackIds();
    }

    /// <summary>Persists the Loop Lab working set at %LocalAppData%/SpotifyWPF/Prediction/session-tracks.json.</summary>
    public class LoopLabSessionStore : ILoopLabSessionStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = true };

        private readonly List<LoopLabSessionTrack> _tracks = new List<LoopLabSessionTrack>();

        public LoopLabSessionStore()
        {
            Load();
        }

        public IReadOnlyList<LoopLabSessionTrack> Tracks => _tracks;

        public void Load()
        {
            _tracks.Clear();

            var path = PredictionPaths.SessionTracksPath;

            if (!File.Exists(path))
                return;

            try
            {
                var loaded = JsonSerializer.Deserialize<List<LoopLabSessionTrack>>(File.ReadAllText(path));

                if (loaded == null)
                    return;

                foreach (var track in loaded.Where(t => !string.IsNullOrEmpty(t?.TrackId)))
                    _tracks.Add(track);
            }
            catch (System.Exception ex)
            {
                System.Console.WriteLine($"Failed to load Loop Lab session: {ex.Message}");
            }
        }

        public void Save()
        {
            var path = PredictionPaths.SessionTracksPath;
            PredictionPaths.EnsureDirectory(path);
            File.WriteAllText(path, JsonSerializer.Serialize(_tracks, JsonOptions));
        }

        public void AddOrUpdate(LoopLabSessionTrack track)
        {
            if (track == null || string.IsNullOrEmpty(track.TrackId))
                return;

            var existing = _tracks.FirstOrDefault(t => t.TrackId == track.TrackId);

            if (existing == null)
            {
                _tracks.Add(track);
            }
            else
            {
                existing.Title = track.Title ?? existing.Title;
                existing.Artist = track.Artist ?? existing.Artist;
                existing.AnalysisStatus = track.AnalysisStatus;
            }

            Save();
        }

        public void Remove(string trackId)
        {
            if (string.IsNullOrEmpty(trackId))
                return;

            _tracks.RemoveAll(t => t.TrackId == trackId);
            Save();
        }

        public IReadOnlyCollection<string> GetTrackIds() =>
            _tracks.Select(t => t.TrackId).Where(id => !string.IsNullOrEmpty(id)).ToList();
    }
}
