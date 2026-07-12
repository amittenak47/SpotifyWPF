using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using SpotifyWPF.Model.Prediction;

namespace SpotifyWPF.Service.Prediction
{
    public interface ILoopRegionStore
    {
        LoopProfile Get(string trackId);

        void Save(LoopProfile profile);

        IReadOnlyDictionary<string, LoopProfile> GetAll();

        void ImportAll(IEnumerable<LoopProfile> profiles);

        /// <summary>Clears locks + saved tune presets for a track (leaves mode/enabled alone if profile exists).</summary>
        void ClearTuneAndBranches(string trackId);
    }

    /// <summary>
    /// Persists per-track LoopProfiles as a dictionary keyed by track ID in
    /// %LocalAppData%\SpotifyWPF\Prediction\loop-regions.json.
    /// </summary>
    public class LoopRegionStore : ILoopRegionStore
    {
        private readonly object _lock = new object();

        private Dictionary<string, LoopProfile> _profiles;

        public LoopProfile Get(string trackId)
        {
            if (string.IsNullOrEmpty(trackId))
                return null;

            lock (_lock)
            {
                EnsureLoaded();
                return _profiles.TryGetValue(trackId, out var profile) ? profile : null;
            }
        }

        public void Save(LoopProfile profile)
        {
            if (profile == null || string.IsNullOrEmpty(profile.TrackId))
                return;

            lock (_lock)
            {
                EnsureLoaded();
                _profiles[profile.TrackId] = profile;
                Persist();
            }
        }

        public IReadOnlyDictionary<string, LoopProfile> GetAll()
        {
            lock (_lock)
            {
                EnsureLoaded();
                return new Dictionary<string, LoopProfile>(_profiles);
            }
        }

        public void ImportAll(IEnumerable<LoopProfile> profiles)
        {
            if (profiles == null)
                return;

            lock (_lock)
            {
                EnsureLoaded();

                foreach (var profile in profiles)
                {
                    if (profile != null && !string.IsNullOrEmpty(profile.TrackId))
                        _profiles[profile.TrackId] = profile;
                }

                Persist();
            }
        }

        public void ClearTuneAndBranches(string trackId)
        {
            if (string.IsNullOrEmpty(trackId))
                return;

            lock (_lock)
            {
                EnsureLoaded();

                if (!_profiles.TryGetValue(trackId, out var profile) || profile == null)
                    return;

                profile.LockedBranches = new List<BranchLock>();
                profile.LockPresets = new List<BranchLockPreset>();
                profile.RandomBranches = true;
                _profiles[trackId] = profile;
                Persist();
            }
        }

        private void EnsureLoaded()
        {
            if (_profiles != null)
                return;

            _profiles = new Dictionary<string, LoopProfile>();

            var path = PredictionPaths.LoopRegionsPath;

            if (!File.Exists(path))
                return;

            try
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, LoopProfile>>(File.ReadAllText(path));

                if (loaded != null)
                    _profiles = loaded;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load loop-regions.json: {ex.Message}");
            }
        }

        private void Persist()
        {
            try
            {
                var path = PredictionPaths.LoopRegionsPath;
                PredictionPaths.EnsureDirectory(path);

                File.WriteAllText(path,
                    JsonSerializer.Serialize(_profiles, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to save loop-regions.json: {ex.Message}");
            }
        }
    }
}
