using System;
using System.Globalization;
using System.Windows.Data;

namespace SpotifyWPF.Converter
{
    public class StatusActionConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var message = value as string;
            if (string.IsNullOrWhiteSpace(message))
                return "[Status] Ready";

            return $"[{GetAction(message)}] {message}";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }

        private static string GetAction(string message)
        {
            if (StartsWithAny(message, "Deleting", "Deleted", "Cannot delete", "Failed to delete", "Rate limited while deleting"))
                return "Delete";
            if (StartsWithAny(message, "Creating", "Created"))
                return "Add";
            if (StartsWithAny(message, "Cancelling", "Cancelled"))
                return "Abort";
            if (StartsWithAny(message, "Loading", "Load", "No additional", "Finished loading"))
                return "Load";
            if (StartsWithAny(message, "Refreshing", "Refreshed"))
                return "Refresh";
            if (StartsWithAny(message, "Removing", "Unfollowing"))
                return "Delete";
            if (StartsWithAny(message, "Searching"))
                return "Search";
            if (StartsWithAny(message, "Login required", "Authenticated"))
                return "Auth";
            if (StartsWithAny(message, "Failed", "Rate limited"))
                return "Error";

            return "Status";
        }

        private static bool StartsWithAny(string message, params string[] prefixes)
        {
            foreach (var prefix in prefixes)
            {
                if (message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}
