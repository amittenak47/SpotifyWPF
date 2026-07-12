using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NAudio.Wave;
using SpotifyWPF.Service.Prediction;

namespace SpotifyWPF.Service.Audio
{
    /// <summary>Guards against header-only or truncated WASAPI loopback captures.</summary>
    public static class WavCaptureValidator
    {
        public const long MinimumBytes = 8192;

        public const double MinimumDurationSec = 1.0;

        public static bool IsUsable(string wavPath)
        {
            if (string.IsNullOrWhiteSpace(wavPath) || !File.Exists(wavPath))
                return false;

            var length = new FileInfo(wavPath).Length;

            if (length < MinimumBytes)
                return false;

            if (!HasValidWavHeader(wavPath, length))
            {
                if (!TryRepairUnfinalizedHeader(wavPath, length))
                    return false;
            }

            try
            {
                using (var reader = new WaveFileReader(wavPath))
                    return reader.TotalTime.TotalSeconds >= MinimumDurationSec;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not read capture WAV {wavPath}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Patches RIFF/data chunk sizes when PCM was written but WaveFileWriter never finalized the header.
        /// Called from <see cref="IsUsable"/> during cache validation and from
        /// <see cref="WasapiLoopbackCaptureService.StopCaptureAsync"/> immediately after capture ends.
        /// </summary>
        public static bool TryRepairUnfinalizedHeader(string wavPath, long fileLength = 0)
        {
            if (string.IsNullOrWhiteSpace(wavPath) || !File.Exists(wavPath))
                return false;

            if (fileLength <= 0)
                fileLength = new FileInfo(wavPath).Length;

            if (fileLength < 44)
                return false;

            try
            {
                using (var stream = new FileStream(wavPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                using (var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true))
                using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
                {
                    if (ReadFourCc(reader) != "RIFF")
                        return false;

                    stream.Seek(8, SeekOrigin.Begin);

                    if (ReadFourCc(reader) != "WAVE")
                        return false;

                    var dataOffset = FindDataChunkOffset(stream);

                    if (dataOffset < 0)
                        return false;

                    var dataSize = fileLength - dataOffset;

                    if (dataSize < MinimumBytes - 44)
                        return false;

                    stream.Seek(4, SeekOrigin.Begin);
                    writer.Write((int)(fileLength - 8));

                    stream.Seek(dataOffset - 4, SeekOrigin.Begin);
                    writer.Write((int)dataSize);
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not repair capture WAV {wavPath}: {ex.Message}");
                return false;
            }
        }

        private static bool HasValidWavHeader(string wavPath, long fileLength)
        {
            try
            {
                using (var stream = File.OpenRead(wavPath))
                using (var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true))
                {
                    if (ReadFourCc(reader) != "RIFF")
                        return false;

                    var riffSize = reader.ReadInt32();

                    if (riffSize <= 0 || riffSize + 8 > fileLength)
                        return false;

                    if (ReadFourCc(reader) != "WAVE")
                        return false;

                    var dataOffset = FindDataChunkOffset(stream);

                    if (dataOffset < 0)
                        return false;

                    stream.Seek(dataOffset - 4, SeekOrigin.Begin);

                    var dataSize = reader.ReadInt32();

                    return dataSize > 0 && dataOffset + dataSize <= fileLength;
                }
            }
            catch
            {
                return false;
            }
        }

        private static int FindDataChunkOffset(Stream stream)
        {
            stream.Seek(12, SeekOrigin.Begin);

            using (var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true))
            {
                while (stream.Position + 8 <= stream.Length)
                {
                    var chunkId = ReadFourCc(reader);
                    var chunkSize = reader.ReadInt32();

                    if (chunkId == "data")
                        return (int)stream.Position;

                    stream.Seek(chunkSize + (chunkSize % 2), SeekOrigin.Current);
                }
            }

            return -1;
        }

        private static string ReadFourCc(BinaryReader reader)
        {
            var bytes = reader.ReadBytes(4);

            return bytes.Length == 4 ? Encoding.ASCII.GetString(bytes) : string.Empty;
        }

        private static int ReadInt32(BinaryReader reader)
        {
            return reader.ReadInt32();
        }

        public static void TryDeleteCapture(string trackId)
        {
            if (string.IsNullOrWhiteSpace(trackId))
                return;

            TryDeleteFile(PredictionPaths.GetAudioCachePath(trackId));
            TryDeleteFile(PredictionPaths.GetAudioCacheMetadataPath(trackId));

            var resolved = PredictionPaths.ResolveAudioCachePath(trackId);

            if (!string.IsNullOrWhiteSpace(resolved))
                TryDeleteFile(resolved);
        }

        /// <summary>Removes cached WAV + metadata and analysis JSON for a track.</summary>
        public static void DeleteTrackArtifacts(string trackId)
        {
            TryDeleteCapture(trackId);
            AnalysisCache.Delete(trackId);
        }

        /// <summary>
        /// Startup / abort sweep: delete orphan WAVs (no complete capture metadata) and
        /// unreadable / empty analysis JSON so a crashed playthrough does not linger.
        /// Returns how many artifact groups were removed.
        /// </summary>
        public static int PurgeIncompleteArtifacts()
        {
            var removed = 0;

            try
            {
                if (Directory.Exists(PredictionPaths.AudioCacheDirectory))
                {
                    foreach (var wav in Directory.GetFiles(PredictionPaths.AudioCacheDirectory, "*.wav"))
                    {
                        var trackId = Path.GetFileNameWithoutExtension(wav);

                        if (string.IsNullOrWhiteSpace(trackId))
                            continue;

                        if (HasCompleteCapture(trackId))
                            continue;

                        TryDeleteCapture(trackId);
                        removed++;
                    }

                    // Orphan metadata without a usable WAV.
                    foreach (var meta in Directory.GetFiles(PredictionPaths.AudioCacheDirectory, "*.capture.json"))
                    {
                        var name = Path.GetFileName(meta);
                        var trackId = name.EndsWith(".capture.json", StringComparison.OrdinalIgnoreCase)
                            ? name.Substring(0, name.Length - ".capture.json".Length)
                            : Path.GetFileNameWithoutExtension(meta);

                        if (string.IsNullOrWhiteSpace(trackId))
                            continue;

                        if (HasCompleteCapture(trackId))
                            continue;

                        TryDeleteCapture(trackId);
                        removed++;
                    }
                }

                if (Directory.Exists(PredictionPaths.AnalysisCacheDirectory))
                {
                    foreach (var json in Directory.GetFiles(PredictionPaths.AnalysisCacheDirectory, "*.json"))
                    {
                        var trackId = Path.GetFileNameWithoutExtension(json);

                        if (string.IsNullOrWhiteSpace(trackId))
                            continue;

                        var analysis = AnalysisCache.Load(trackId);

                        if (analysis?.Beats != null && analysis.Beats.Count > 0)
                            continue;

                        AnalysisCache.Delete(trackId);
                        removed++;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"PurgeIncompleteArtifacts failed: {ex.Message}");
            }

            return removed;
        }

        /// <summary>Track IDs that have a complete capture WAV on disk.</summary>
        public static IEnumerable<string> EnumerateCompleteCaptureTrackIds()
        {
            if (!Directory.Exists(PredictionPaths.AudioCacheDirectory))
                yield break;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var wav in Directory.GetFiles(PredictionPaths.AudioCacheDirectory, "*.wav"))
            {
                var trackId = Path.GetFileNameWithoutExtension(wav);

                if (string.IsNullOrWhiteSpace(trackId) || !seen.Add(trackId))
                    continue;

                if (HasCompleteCapture(trackId))
                    yield return trackId;
            }
        }

        /// <summary>
        /// True when a WAV exists, is readable, and spans the full track length recorded in capture metadata.
        /// </summary>
        public static bool HasCompleteCapture(string trackId, long expectedDurationMs = 0)
        {
            var wavPath = PredictionPaths.ResolveAudioCachePath(trackId);

            if (!IsUsable(wavPath))
                return false;

            var metaPath = PredictionPaths.GetAudioCacheMetadataPath(trackId);

            if (!File.Exists(metaPath))
                return expectedDurationMs <= 0 || MeetsDurationThreshold(wavPath, expectedDurationMs);

            try
            {
                using (var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(metaPath)))
                {
                    var root = document.RootElement;

                    if (root.TryGetProperty("durationMs", out var durationElement))
                    {
                        var metaDurationMs = durationElement.GetInt64();

                        if (metaDurationMs > 0)
                            return MeetsDurationThreshold(wavPath, metaDurationMs);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not read capture metadata for {trackId}: {ex.Message}");
            }

            return expectedDurationMs <= 0 || MeetsDurationThreshold(wavPath, expectedDurationMs);
        }

        /// <summary>Captured audio must reach at least 92% of the expected track length.</summary>
        public static bool MeetsDurationThreshold(string wavPath, long expectedDurationMs)
        {
            if (expectedDurationMs <= 0)
                return IsUsable(wavPath);

            try
            {
                using (var reader = new WaveFileReader(wavPath))
                {
                    var capturedMs = (long)reader.TotalTime.TotalMilliseconds;
                    var minimumMs = (long)(expectedDurationMs * 0.92) - 1500;

                    return capturedMs >= Math.Max(minimumMs, (long)(MinimumDurationSec * 1000));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not measure capture duration for {wavPath}: {ex.Message}");
                return false;
            }
        }

        private static void TryDeleteFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return;

            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not delete capture file {path}: {ex.Message}");
            }
        }
    }
}
