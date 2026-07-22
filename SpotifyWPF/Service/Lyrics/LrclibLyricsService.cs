using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SpotifyWPF.Model.Lyrics;
using SpotifyWPF.Service.Prediction;

namespace SpotifyWPF.Service.Lyrics
{
    public interface ILyricsService
    {
        /// <summary>Load cached lyrics or fetch from LRCLIB. Returns null when unavailable.</summary>
        Task<SyncedLyrics> GetSyncedLyricsAsync(
            string trackId,
            string title,
            string artist,
            string album = null,
            int durationSec = 0,
            CancellationToken cancellationToken = default);

        void ClearCache(string trackId);
    }

    /// <summary>
    /// Fetches synced LRC lyrics from lrclib.net and caches JSON under Prediction/lyrics-cache.
    /// Spotify Web API does not expose lyrics; this is the personal/experimental path.
    /// </summary>
    public sealed class LrclibLyricsService : ILyricsService
    {
        private static readonly HttpClient Http = CreateClient();

        private static readonly Regex LrcLineRegex = new Regex(
            @"^\[(\d{1,2}):(\d{2})(?:\.(\d{1,3}))?\]\s*(.*)$",
            RegexOptions.Compiled);

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };

        private static HttpClient CreateClient()
        {
            var client = new HttpClient
            {
                BaseAddress = new Uri("https://lrclib.net/"),
                Timeout = TimeSpan.FromSeconds(20)
            };
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "SpotifyWPF-LoopLab/1.0 (https://github.com/amittenak47/SpotifyWPF)");
            return client;
        }

        public async Task<SyncedLyrics> GetSyncedLyricsAsync(
            string trackId,
            string title,
            string artist,
            string album = null,
            int durationSec = 0,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(trackId))
                return null;

            var cached = TryLoadCache(trackId);
            if (cached != null)
                return cached;

            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
                return null;

            try
            {
                var lyrics = await FetchFromLrclibAsync(title, artist, album, durationSec, cancellationToken)
                    .ConfigureAwait(false);

                if (lyrics == null)
                {
                    // Fallback: search without duration/album constraint.
                    lyrics = await SearchLrclibAsync(title, artist, cancellationToken).ConfigureAwait(false);
                }

                if (lyrics == null)
                    return null;

                lyrics.TrackId = trackId;
                SaveCache(lyrics);
                return lyrics;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Lyrics fetch failed for {trackId}: {ex.Message}");
                return null;
            }
        }

        public void ClearCache(string trackId)
        {
            if (string.IsNullOrWhiteSpace(trackId))
                return;

            var path = PredictionPaths.GetLyricsCachePath(trackId);
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not delete lyrics cache {path}: {ex.Message}");
            }
        }

        private static SyncedLyrics TryLoadCache(string trackId)
        {
            var path = PredictionPaths.GetLyricsCachePath(trackId);
            if (!File.Exists(path))
                return null;

            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<SyncedLyrics>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not read lyrics cache {path}: {ex.Message}");
                return null;
            }
        }

        private static void SaveCache(SyncedLyrics lyrics)
        {
            if (lyrics == null || string.IsNullOrWhiteSpace(lyrics.TrackId))
                return;

            var path = PredictionPaths.GetLyricsCachePath(lyrics.TrackId);
            PredictionPaths.EnsureDirectory(path);
            var json = JsonSerializer.Serialize(lyrics, JsonOptions);
            File.WriteAllText(path, json);
        }

        private static async Task<SyncedLyrics> FetchFromLrclibAsync(
            string title, string artist, string album, int durationSec, CancellationToken ct)
        {
            var query = $"api/get?track_name={Uri.EscapeDataString(title)}" +
                        $"&artist_name={Uri.EscapeDataString(artist)}";

            if (!string.IsNullOrWhiteSpace(album))
                query += $"&album_name={Uri.EscapeDataString(album)}";

            if (durationSec > 0)
                query += $"&duration={durationSec}";

            using (var response = await Http.GetAsync(query, ct).ConfigureAwait(false))
            {
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return null;

                if ((int)response.StatusCode == 429)
                {
                    Console.WriteLine("LRCLIB rate limited (429).");
                    return null;
                }

                if (!response.IsSuccessStatusCode)
                    return null;

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return ParseLrclibPayload(json);
            }
        }

        private static async Task<SyncedLyrics> SearchLrclibAsync(
            string title, string artist, CancellationToken ct)
        {
            var query = $"api/search?track_name={Uri.EscapeDataString(title)}" +
                        $"&artist_name={Uri.EscapeDataString(artist)}";

            using (var response = await Http.GetAsync(query, ct).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                    return null;

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using (var doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Array)
                        return null;

                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        var synced = item.TryGetProperty("syncedLyrics", out var s) ? s.GetString() : null;
                        if (string.IsNullOrWhiteSpace(synced))
                            continue;

                        return ParseLrclibElement(item);
                    }
                }
            }

            return null;
        }

        private static SyncedLyrics ParseLrclibPayload(string json)
        {
            using (var doc = JsonDocument.Parse(json))
                return ParseLrclibElement(doc.RootElement);
        }

        private static SyncedLyrics ParseLrclibElement(JsonElement root)
        {
            var synced = root.TryGetProperty("syncedLyrics", out var s) ? s.GetString() : null;
            var plain = root.TryGetProperty("plainLyrics", out var p) ? p.GetString() : null;
            var instrumental = root.TryGetProperty("instrumental", out var i) && i.ValueKind == JsonValueKind.True;

            var lyrics = new SyncedLyrics
            {
                Title = root.TryGetProperty("trackName", out var t) ? t.GetString() : null,
                Artist = root.TryGetProperty("artistName", out var a) ? a.GetString() : null,
                Album = root.TryGetProperty("albumName", out var al) ? al.GetString() : null,
                DurationSec = root.TryGetProperty("duration", out var d) && d.TryGetInt32(out var ds) ? ds : 0,
                Instrumental = instrumental,
                PlainLyrics = plain,
                Source = "lrclib",
                Lines = ParseLrc(synced)
            };

            if (lyrics.Lines.Count == 0 && !string.IsNullOrWhiteSpace(plain) && !instrumental)
            {
                // Untimed fallback: one "line" so the panel can still show text.
                foreach (var line in plain.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    lyrics.Lines.Add(new LyricLine { StartMs = 0, Text = line.Trim() });
                }
            }

            return lyrics.HasSyncedLines || !string.IsNullOrWhiteSpace(plain) || instrumental
                ? lyrics
                : null;
        }

        internal static List<LyricLine> ParseLrc(string syncedLyrics)
        {
            var lines = new List<LyricLine>();
            if (string.IsNullOrWhiteSpace(syncedLyrics))
                return lines;

            foreach (var raw in syncedLyrics.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var match = LrcLineRegex.Match(raw.Trim());
                if (!match.Success)
                    continue;

                var minutes = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                var seconds = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                var frac = match.Groups[3].Success ? match.Groups[3].Value : "0";
                // LRC fractions may be 1–3 digits (tenths / hundredths / milliseconds).
                while (frac.Length < 3)
                    frac += "0";
                if (frac.Length > 3)
                    frac = frac.Substring(0, 3);

                var ms = minutes * 60000L + seconds * 1000L +
                         int.Parse(frac, CultureInfo.InvariantCulture);
                var text = match.Groups[4].Value.Trim();
                if (string.IsNullOrEmpty(text))
                    continue;

                lines.Add(new LyricLine { StartMs = ms, Text = text });
            }

            return lines.OrderBy(l => l.StartMs).ToList();
        }
    }

    /// <summary>Maps lyric line start times onto beat indices from a beat graph / analysis.</summary>
    public static class LyricBeatMapper
    {
        public static void MapToBeats(SyncedLyrics lyrics, IReadOnlyList<long> beatStartMs)
        {
            if (lyrics?.Lines == null || beatStartMs == null || beatStartMs.Count == 0)
                return;

            foreach (var line in lyrics.Lines)
                line.BeatIndex = FindBeatIndex(beatStartMs, line.StartMs);
        }

        public static HashSet<int> PhraseBoundaryBeats(SyncedLyrics lyrics)
        {
            var set = new HashSet<int>();
            if (lyrics?.Lines == null)
                return set;

            foreach (var line in lyrics.Lines)
            {
                if (line.BeatIndex >= 0)
                    set.Add(line.BeatIndex);
            }

            return set;
        }

        public static int FindActiveLineIndex(SyncedLyrics lyrics, long positionMs)
        {
            if (lyrics?.Lines == null || lyrics.Lines.Count == 0)
                return -1;

            var best = -1;
            for (var i = 0; i < lyrics.Lines.Count; i++)
            {
                if (lyrics.Lines[i].StartMs <= positionMs)
                    best = i;
                else
                    break;
            }

            return best;
        }

        private static int FindBeatIndex(IReadOnlyList<long> beatStartMs, long positionMs)
        {
            var best = 0;
            for (var i = 0; i < beatStartMs.Count; i++)
            {
                if (beatStartMs[i] <= positionMs)
                    best = i;
                else
                    break;
            }

            return best;
        }
    }
}
