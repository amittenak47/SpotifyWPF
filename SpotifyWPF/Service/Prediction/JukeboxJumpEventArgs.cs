using System;

namespace SpotifyWPF.Service.Prediction
{
    public class JukeboxJumpEventArgs : EventArgs
    {
        public int FromBeatIndex { get; set; }

        public int ToBeatIndex { get; set; }

        public long FromMs { get; set; }

        public long ToMs { get; set; }

        public double BranchDistance { get; set; }

        public bool IsPlanned { get; set; }
    }

    /// <summary>A fired jukebox jump; the ring flashes the chord when a new instance is assigned.</summary>
    public class JukeboxJumpFlash
    {
        public JukeboxJumpFlash(int fromBeatIndex, int toBeatIndex)
        {
            FromBeatIndex = fromBeatIndex;
            ToBeatIndex = toBeatIndex;
        }

        public int FromBeatIndex { get; }

        public int ToBeatIndex { get; }
    }
}
