using System;

namespace SpotifyWPF.Service.Playback
{
    /// <summary>Transport for Loop Lab / jukebox seeks — Spotify SDK or local WAV.</summary>
    public enum JukeboxPlaybackSource
    {
        Spotify = 0,
        Local = 1
    }

    /// <summary>
    /// Subset of playback used by <see cref="Prediction.LoopController"/>: arm a seek at a
    /// position, track position, and report when the armed action fires. WebView2-specific
    /// lifecycle stays on <see cref="IWebPlaybackHost"/>.
    /// </summary>
    public interface IJukeboxTransport
    {
        void ArmAction(string actionId, long whenMs, long seekToMs);

        void DisarmAction();

        void Pause();

        void Resume();

        void Seek(long positionMs);

        void SetVolume(double volume);

        event EventHandler<PlayerStateSnapshot> StateChanged;

        event EventHandler<PositionSnapshot> PositionUpdated;

        event EventHandler<ArmedActionFiredEventArgs> ActionFired;

        event EventHandler<string> TrackEnded;
    }
}
