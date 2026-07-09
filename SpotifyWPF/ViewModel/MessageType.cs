namespace SpotifyWPF.ViewModel
{
    public enum MessageType
    {
        LoginSuccessful,

        /// <summary>Payload: a spotify context/track URI to start playing on the Prediction page.</summary>
        OpenInLoopLab,

        /// <summary>Payload: true when Infinite Jukebox ring-only mini player mode is active.</summary>
        MiniPlayerModeChanged
    }
}
