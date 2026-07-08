using System;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SpotifyAPI.Web;

namespace SpotifyWPF.Service.Prediction
{
    /// <summary>
    /// Which analysis pipeline this install uses (the Phase 0 gate).
    /// </summary>
    public enum AnalysisSource
    {
        /// <summary>Probe has not run successfully yet.</summary>
        Unknown = 0,

        /// <summary>Path A — Spotify's GET /audio-analysis/{id} works for this Client ID.</summary>
        Spotify = 1,

        /// <summary>Path B — endpoint returns 403 for this Client ID; analyze locally (WASAPI + sidecar).</summary>
        Local = 2
    }

    public interface IAnalysisGate
    {
        /// <summary>Last decided source, loaded from disk; Unknown until the probe has run once.</summary>
        AnalysisSource CachedSource { get; }

        /// <summary>
        /// Returns the decided analysis source, probing Spotify's audio-analysis endpoint once and
        /// persisting the verdict. Pass forceReprobe to re-run the probe (e.g. after switching Client ID).
        /// </summary>
        Task<AnalysisSource> GetAnalysisSourceAsync(bool forceReprobe = false);
    }

    /// <summary>
    /// Phase 0 gate: probes GET /audio-analysis/{id} with the app's token on a known track and records
    /// the result at %LocalAppData%\SpotifyWPF\Prediction\analysis-source.json. A 200 selects
    /// SpotifyAnalysisProvider (Path A); a 403 (endpoint deprecated for new dev apps since Nov 2024)
    /// selects LocalAnalysisProvider (Path B). The decision is explicit and persisted — no silent
    /// per-request fallback between the two paths.
    /// </summary>
    public class AnalysisGate : IAnalysisGate
    {
        /// <summary>The Killers — "Mr. Brightside": a stable catalog track used only to probe the endpoint.</summary>
        private const string ProbeTrackId = "003vvx7Niy0yvhvHt4a68B";

        private readonly ISpotify _spotify;

        private readonly SemaphoreSlim _probeSemaphore = new SemaphoreSlim(1, 1);

        private AnalysisSourceRecord _record;

        public AnalysisGate(ISpotify spotify)
        {
            _spotify = spotify;
            _record = LoadRecord();
        }

        public AnalysisSource CachedSource => ParseSource(_record?.Source);

        public async Task<AnalysisSource> GetAnalysisSourceAsync(bool forceReprobe = false)
        {
            if (!forceReprobe && CachedSource != AnalysisSource.Unknown)
                return CachedSource;

            await _probeSemaphore.WaitAsync();

            try
            {
                if (!forceReprobe && CachedSource != AnalysisSource.Unknown)
                    return CachedSource;

                var api = _spotify.Api;

                if (api == null)
                    return AnalysisSource.Unknown;

                AnalysisSource source;
                int? statusCode;

                try
                {
                    await api.Tracks.GetAudioAnalysis(ProbeTrackId);
                    source = AnalysisSource.Spotify;
                    statusCode = (int)HttpStatusCode.OK;
                }
                catch (APIException ex) when (ex.Response?.StatusCode == HttpStatusCode.Forbidden ||
                                              ex.Response?.StatusCode == HttpStatusCode.NotFound)
                {
                    // 403 is the documented deprecation response; some tokens see 404 for gated endpoints.
                    source = AnalysisSource.Local;
                    statusCode = ex.Response == null ? (int?)null : (int)ex.Response.StatusCode;
                }
                catch (Exception ex)
                {
                    // Transient failure (network, 5xx, expired auth): do not record a verdict.
                    Console.WriteLine($"Audio-analysis probe failed transiently: {ex.Message}");
                    return CachedSource;
                }

                _record = new AnalysisSourceRecord
                {
                    Source = source.ToString(),
                    ProbedAtUtc = DateTime.UtcNow,
                    StatusCode = statusCode,
                    ProbeTrackId = ProbeTrackId
                };

                SaveRecord(_record);

                return source;
            }
            finally
            {
                _probeSemaphore.Release();
            }
        }

        private static AnalysisSource ParseSource(string value)
        {
            switch (value)
            {
                case "Spotify":
                    return AnalysisSource.Spotify;
                case "Local":
                    return AnalysisSource.Local;
                default:
                    return AnalysisSource.Unknown;
            }
        }

        private static AnalysisSourceRecord LoadRecord()
        {
            var path = PredictionPaths.AnalysisSourcePath;

            if (!File.Exists(path))
                return null;

            try
            {
                return JsonSerializer.Deserialize<AnalysisSourceRecord>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load analysis-source.json: {ex.Message}");
                return null;
            }
        }

        private static void SaveRecord(AnalysisSourceRecord record)
        {
            var path = PredictionPaths.AnalysisSourcePath;
            PredictionPaths.EnsureDirectory(path);

            File.WriteAllText(path,
                JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
        }

        private class AnalysisSourceRecord
        {
            [JsonPropertyName("source")]
            public string Source { get; set; }

            [JsonPropertyName("probedAtUtc")]
            public DateTime ProbedAtUtc { get; set; }

            [JsonPropertyName("statusCode")]
            public int? StatusCode { get; set; }

            [JsonPropertyName("probeTrackId")]
            public string ProbeTrackId { get; set; }
        }
    }
}
