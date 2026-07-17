using System;
using System.Globalization;
using System.Windows.Data;
using SpotifyWPF.Model.Prediction;

namespace SpotifyWPF.Converter
{
    /// <summary>
    /// values[0] = LoopLabSessionTrack, values[1] = active track id, values[2] = isPaused
    /// → play or pause glyph for that row.
    /// </summary>
    public class SessionPlayGlyphConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var track = values != null && values.Length > 0 ? values[0] as LoopLabSessionTrack : null;
            var activeId = values != null && values.Length > 1 ? values[1] as string : null;
            var isPaused = values != null && values.Length > 2 && values[2] is bool paused && paused;

            if (track == null || string.IsNullOrWhiteSpace(track.TrackId) || string.IsNullOrWhiteSpace(activeId))
                return "▶";

            var isCurrent = string.Equals(track.TrackId, activeId, StringComparison.Ordinal);
            return isCurrent && !isPaused ? "⏸" : "▶";
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>
    /// values[0] = LoopLabSessionTrack, values[1] = useLocalPlayback (bool)
    /// → "Local WAV · Capture · Analysis" style status line.
    /// </summary>
    public class SessionStatusMarqueeConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var track = values != null && values.Length > 0 ? values[0] as LoopLabSessionTrack : null;
            var useLocal = values != null && values.Length > 1 && values[1] is bool local && local;
            var source = useLocal ? "Local WAV" : "Spotify";
            var status = track?.StatusText;

            if (string.IsNullOrWhiteSpace(status))
                return source;

            return $"{source} · {status}";
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
