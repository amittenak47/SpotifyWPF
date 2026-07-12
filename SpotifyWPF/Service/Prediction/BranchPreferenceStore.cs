using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpotifyWPF.Service.Prediction
{
    /// <summary>
    /// Slice 6: lightweight pairwise preference memory. Chosen edges get positive counts;
    /// skip/scrub-after-jump and rejected alternatives get negatives. Scores only re-rank
    /// existing candidates — they never create new graph edges.
    /// </summary>
    public sealed class BranchPreferenceStore
    {
        private readonly string _path;
        private readonly object _gate = new object();
        private Dictionary<string, PreferenceEdgeStats> _edges =
            new Dictionary<string, PreferenceEdgeStats>(StringComparer.Ordinal);

        public BranchPreferenceStore(string path = null)
        {
            _path = path ?? Path.Combine(PredictionPaths.RootDirectory, "branch-preferences.json");
            Load();
        }

        public string StorePath => _path;

        public int EdgeCount
        {
            get
            {
                lock (_gate)
                    return _edges.Count;
            }
        }

        public static string PackKey(string trackId, int from, int to) =>
            $"{trackId ?? ""}|{from}->{to}";

        public void RecordChoice(string trackId, int from, int chosenTo, IEnumerable<int> alternatives)
        {
            if (string.IsNullOrEmpty(trackId) || chosenTo < 0)
                return;

            lock (_gate)
            {
                Bump(PackKey(trackId, from, chosenTo), chosen: 1, rejected: 0);

                if (alternatives != null)
                {
                    foreach (var alt in alternatives)
                    {
                        if (alt == chosenTo)
                            continue;

                        Bump(PackKey(trackId, from, alt), chosen: 0, rejected: 1);
                    }
                }

                SaveUnlocked();
            }
        }

        /// <summary>Negative label: user scrubbed/skipped shortly after taking this hop.</summary>
        public void RecordSkipAfterJump(string trackId, int from, int to)
        {
            if (string.IsNullOrEmpty(trackId) || from < 0 || to < 0)
                return;

            lock (_gate)
            {
                Bump(PackKey(trackId, from, to), chosen: 0, rejected: 2);
                SaveUnlocked();
            }
        }

        /// <summary>
        /// Preference score in roughly [-1, 1]: (chosen - rejected) / (chosen + rejected + 1).
        /// Missing edges score 0.
        /// </summary>
        public double Score(string trackId, int from, int to)
        {
            lock (_gate)
            {
                if (!_edges.TryGetValue(PackKey(trackId, from, to), out var stats))
                    return 0;

                var total = stats.Chosen + stats.Rejected;
                return (stats.Chosen - stats.Rejected) / (total + 1.0);
            }
        }

        public void ClearAll()
        {
            lock (_gate)
            {
                _edges.Clear();
                SaveUnlocked();
            }
        }

        public void ClearTrack(string trackId)
        {
            if (string.IsNullOrEmpty(trackId))
                return;

            var prefix = trackId + "|";

            lock (_gate)
            {
                var keys = _edges.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();

                foreach (var key in keys)
                    _edges.Remove(key);

                SaveUnlocked();
            }
        }

        private void Bump(string key, int chosen, int rejected)
        {
            if (!_edges.TryGetValue(key, out var stats))
            {
                stats = new PreferenceEdgeStats();
                _edges[key] = stats;
            }

            stats.Chosen += chosen;
            stats.Rejected += rejected;
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_path))
                    return;

                var json = File.ReadAllText(_path);
                var doc = JsonSerializer.Deserialize<PreferenceFile>(json);

                if (doc?.Edges == null)
                    return;

                _edges = doc.Edges.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value ?? new PreferenceEdgeStats(),
                    StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load branch-preferences.json: {ex.Message}");
            }
        }

        private void SaveUnlocked()
        {
            try
            {
                var dir = Path.GetDirectoryName(_path);

                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var doc = new PreferenceFile { Edges = _edges };
                var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_path, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to save branch-preferences.json: {ex.Message}");
            }
        }

        private sealed class PreferenceFile
        {
            [JsonPropertyName("edges")]
            public Dictionary<string, PreferenceEdgeStats> Edges { get; set; }
        }

        private sealed class PreferenceEdgeStats
        {
            [JsonPropertyName("chosen")]
            public int Chosen { get; set; }

            [JsonPropertyName("rejected")]
            public int Rejected { get; set; }
        }
    }
}
