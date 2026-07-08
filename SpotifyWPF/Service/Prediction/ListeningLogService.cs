using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using SpotifyWPF.Model.Prediction;

namespace SpotifyWPF.Service.Prediction
{
    public interface IListeningLogService
    {
        void Append(PlayEvent playEvent);

        IReadOnlyList<PlayEvent> ReadAll();
    }

    /// <summary>
    /// Append-only JSONL log of plays (track, timestamps, skips, transitions) at
    /// %LocalAppData%\SpotifyWPF\Prediction\listening-log.jsonl.
    /// </summary>
    public class ListeningLogService : IListeningLogService
    {
        private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            WriteIndented = false
        };

        private readonly object _writeLock = new object();

        public void Append(PlayEvent playEvent)
        {
            if (playEvent == null || string.IsNullOrEmpty(playEvent.TrackId))
                return;

            var line = JsonSerializer.Serialize(playEvent, SerializerOptions);

            lock (_writeLock)
            {
                PredictionPaths.EnsureDirectory(PredictionPaths.ListeningLogPath);
                File.AppendAllText(PredictionPaths.ListeningLogPath, line + Environment.NewLine);
            }
        }

        public IReadOnlyList<PlayEvent> ReadAll()
        {
            var events = new List<PlayEvent>();
            var path = PredictionPaths.ListeningLogPath;

            if (!File.Exists(path))
                return events;

            string[] lines;

            lock (_writeLock)
            {
                lines = File.ReadAllLines(path);
            }

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    var playEvent = JsonSerializer.Deserialize<PlayEvent>(line);

                    if (playEvent != null && !string.IsNullOrEmpty(playEvent.TrackId))
                        events.Add(playEvent);
                }
                catch (JsonException)
                {
                    // Skip malformed lines rather than losing the whole log.
                }
            }

            return events;
        }
    }
}
