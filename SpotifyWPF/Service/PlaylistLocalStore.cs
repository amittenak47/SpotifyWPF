using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SpotifyWPF.Model;

namespace SpotifyWPF.Service
{
    public class PlaylistLocalStore : IPlaylistLocalStore
    {
        private readonly string _playlistStoreRootDirectory;

        public PlaylistLocalStore()
        {
            _playlistStoreRootDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpotifyWPF", "Playlists");
            Directory.CreateDirectory(GetPlaylistStoreDirectory());
        }

        public event Action<string, bool> LogMessage;

        public Dictionary<string, PlaylistCacheItem> LoadAvailablePlaylists()
        {
            return LoadDictionary<PlaylistCacheItem>(GetAvailablePlaylistsPath());
        }

        public void SaveAvailablePlaylists(Dictionary<string, PlaylistCacheItem> playlists)
        {
            SaveDictionary(GetAvailablePlaylistsPath(), playlists);
        }

        public int AddOrUpdateAvailablePlaylists(IEnumerable<PlaylistCacheItem> playlists)
        {
            var availablePlaylists = LoadAvailablePlaylists();
            var deletionQueue = LoadDeletionQueue();
            var addedCount = 0;

            foreach (var playlist in playlists)
            {
                if (string.IsNullOrWhiteSpace(playlist.Id) || deletionQueue.ContainsKey(playlist.Id)) continue;

                var isNew = !availablePlaylists.ContainsKey(playlist.Id);
                availablePlaylists[playlist.Id] = playlist;

                if (isNew)
                    addedCount++;
            }

            SaveAvailablePlaylists(availablePlaylists);
            return addedCount;
        }

        public Dictionary<string, DeletionQueueItem> LoadDeletionQueue()
        {
            return LoadDictionary<DeletionQueueItem>(GetDeletionQueuePath());
        }

        public void SaveDeletionQueue(Dictionary<string, DeletionQueueItem> playlists)
        {
            SaveDictionary(GetDeletionQueuePath(), playlists);
        }

        public int GetKnownPlaylistCount()
        {
            return LoadAvailablePlaylists().Count + LoadDeletionQueue().Count;
        }

        public PlaylistPaginationState LoadPaginationState()
        {
            try
            {
                var path = GetPlaylistPaginationPath();
                if (!File.Exists(path))
                    return null;

                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<PlaylistPaginationState>(json);
            }
            catch (Exception ex)
            {
                Log($"Failed to load playlist pagination state: {ex.Message}");
                return null;
            }
        }

        public void SavePaginationState(PlaylistPaginationState state)
        {
            try
            {
                Directory.CreateDirectory(GetPlaylistStoreDirectory());
                var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(GetPlaylistPaginationPath(), json);
            }
            catch (Exception ex)
            {
                Log($"Failed to save playlist pagination state: {ex.Message}");
            }
        }

        private Dictionary<string, T> LoadDictionary<T>(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return new Dictionary<string, T>();

                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<Dictionary<string, T>>(json) ?? new Dictionary<string, T>();
            }
            catch (Exception ex)
            {
                Log($"Failed to read local playlist store {path}: {ex.Message}");
                Log($"Local playlist store read exception: {ex}", true);
                return new Dictionary<string, T>();
            }
        }

        private void SaveDictionary<T>(string path, Dictionary<string, T> values)
        {
            Directory.CreateDirectory(GetPlaylistStoreDirectory());

            var json = JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }

        // Resolved per call: the store follows the configured Spotify client id,
        // which can change after a re-login with a different id.
        private string GetPlaylistStoreDirectory()
        {
            return Path.Combine(_playlistStoreRootDirectory, GetSafeClientId());
        }

        private string GetAvailablePlaylistsPath()
        {
            return Path.Combine(GetPlaylistStoreDirectory(), "available-playlists.json");
        }

        private string GetDeletionQueuePath()
        {
            return Path.Combine(GetPlaylistStoreDirectory(), "deletion-queue.json");
        }

        private string GetPlaylistPaginationPath()
        {
            return Path.Combine(GetPlaylistStoreDirectory(), "playlist-pagination.json");
        }

        private static string GetSafeClientId()
        {
            var clientId = Properties.Settings.Default.SpotifyClientId ?? "default";
            var safeClientId = new string(clientId.Where(char.IsLetterOrDigit).ToArray());

            return string.IsNullOrWhiteSpace(safeClientId) ? "default" : safeClientId;
        }

        private void Log(string message, bool verbose = false)
        {
            LogMessage?.Invoke(message, verbose);
        }
    }
}
