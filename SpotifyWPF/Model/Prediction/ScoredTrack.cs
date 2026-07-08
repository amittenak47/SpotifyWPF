namespace SpotifyWPF.Model.Prediction
{
    /// <summary>One ranked next-track candidate shown on the Next Track tab.</summary>
    public class ScoredTrack
    {
        public string TrackId { get; set; }

        public string TrackName { get; set; }

        public string ArtistName { get; set; }

        public double Score { get; set; }

        public bool IsPinned { get; set; }

        /// <summary>Human-readable score breakdown ("transition 0.4 · repeat 0.2 · …").</summary>
        public string Reason { get; set; }

        public string DisplayName => string.IsNullOrEmpty(ArtistName)
            ? TrackName
            : $"{TrackName} — {ArtistName}";

        public string ScoreText => Score.ToString("0.000");
    }
}
