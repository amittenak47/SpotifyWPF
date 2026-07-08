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
}
