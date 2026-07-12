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

        /// <summary>True when a complete capture WAV is on disk (not persisted — refreshed on load).</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasCapture { get; set; }

        /// <summary>True when analysis JSON exists (not persisted — refreshed on load).</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasAnalysis { get; set; }

        public string DisplayName =>
            string.IsNullOrWhiteSpace(Title) ? TrackId ?? "(unknown)" : Title;

        public string StatusText
        {
            get
            {
                if (AnalysisStatus == SessionAnalysisStatus.Analyzing)
                    return "Capturing / analyzing…";

                if (AnalysisStatus == SessionAnalysisStatus.Failed)
                    return "Failed";

                var capture = HasCapture ? "Capture" : "No capture";
                var analysis = HasAnalysis || AnalysisStatus == SessionAnalysisStatus.Ready
                    ? "Analysis"
                    : "No analysis";

                return $"{capture} · {analysis}";
            }
        }
    }
}
