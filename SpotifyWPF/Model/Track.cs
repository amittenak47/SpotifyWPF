namespace SpotifyWPF.Model
{
    public class Track
    {
        public int Position { get; set; }

        public string TrackName { get; set; }

        public string Artists { get; set; }

        public string Album { get; set; }

        public int? DiscNumber { get; set; }

        public int? TrackNumber { get; set; }

        public int? DurationMs { get; set; }

        public string Duration { get; set; }

        public string SpotifyId { get; set; }

        public string ItemType { get; set; }

        public string StatusNote { get; set; }
    }
}
