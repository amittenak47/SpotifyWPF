using System;
using System.Collections.Generic;

namespace SpotifyWPF.Service.Visual
{
    /// <summary>
    /// Single source of truth for music-reactive visual state. The plasma equalizer, the fractal
    /// background, and any future effect all read the same smoothed energy values so they pulse in
    /// lockstep instead of each re-deriving its own envelope from the analysis data.
    /// </summary>
    public interface IVisualEnergyProvider
    {
        /// <summary>Overall track energy, 0–1, smoothed (fast attack, slow release).</summary>
        double GlobalEnergy { get; }

        /// <summary>Decaying spike raised on every beat crossing, 0–1 (amplitude-capped).</summary>
        double BeatPulse { get; }

        /// <summary>Per-sector bar heights around the ring, each 0–1, individually decayed
        /// (no global normalization).</summary>
        IReadOnlyList<double> BarHeights { get; }

        /// <summary>Raised after each batch update of the values above (~30 fps while active).</summary>
        event Action Updated;
    }
}
