namespace SpotifyWPF.Model.Prediction
{
    /// <summary>Lightweight Spotify track hit for the Loop Lab search picker.</summary>
    public class TrackSearchHit
    {
        public string Id { get; set; }

        public string Title { get; set; }

        public string Artist { get; set; }

        public string DisplayName =>
            string.IsNullOrWhiteSpace(Artist)
                ? Title ?? Id
                : $"{Title} — {Artist}";
    }
}
