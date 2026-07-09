namespace SpotifyWPF.Model.Prediction
{
    public enum SessionAnalysisStatus
    {
        Pending,
        Analyzing,
        Ready,
        Failed
    }

    /// <summary>Track in the Loop Lab working session (local JSON under Prediction/).</summary>
    public class LoopLabSessionTrack
    {
        public string TrackId { get; set; }

        public string Title { get; set; }

        public string Artist { get; set; }

        public SessionAnalysisStatus AnalysisStatus { get; set; } = SessionAnalysisStatus.Pending;

        public string DisplayName =>
            string.IsNullOrWhiteSpace(Title) ? TrackId ?? "(unknown)" : Title;

        public string StatusText
        {
            get
            {
                switch (AnalysisStatus)
                {
                    case SessionAnalysisStatus.Ready:
                        return "Analysis ready";
                    case SessionAnalysisStatus.Analyzing:
                        return "Analyzing…";
                    case SessionAnalysisStatus.Failed:
                        return "Analysis failed";
                    default:
                        return "Not analyzed";
                }
            }
        }
    }
}
